using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DipWd;

sealed class TcpForgeSession
{
    public required ConnEntry Entry { get; init; }
    public uint ClientIsn { get; init; }
    public uint OurIsn { get; init; }
    public uint ClientNext { get; set; }
    public uint OurNext { get; set; }
    public bool Established { get; set; }
    public TcpClient? Socks { get; set; }
    public NetworkStream? SocksStream { get; set; }
    public readonly object Gate = new();
    public readonly List<byte[]> Pending = new();
    public long LastActivity { get; set; } = Environment.TickCount64;
}

/// <summary>
/// Userspace TCP edge: forge SYN-ACK, queue early data until SOCKS is up, splice.
/// </summary>
sealed class TcpForge : IDisposable
{
    const int MaxPayload = 1360;
    const int MaxPending = 64;

    readonly ConnMap _map;
    readonly AppLog _log;
    readonly string _socksHost;
    readonly int _socksPort;
    readonly IntPtr _sendHandle;
    readonly ConcurrentDictionary<int, TcpForgeSession> _sessions = new();
    readonly CancellationTokenSource _cts = new();
    readonly object _injectGate = new();
    long _accepts;

    static readonly Random Rng = new();

    public long Accepts => Interlocked.Read(ref _accepts);

    public TcpForge(ConnMap map, string socksHost, int socksPort, IntPtr sendHandle, uint loopbackIf, AppLog log)
    {
        _map = map;
        _socksHost = socksHost;
        _socksPort = socksPort;
        _sendHandle = sendHandle;
        _log = log;
        _ = loopbackIf;
    }

    public bool HandlesClientPort(int sport) => _sessions.ContainsKey(sport);

    public void OnClientPacket(byte[] packet, uint len, byte[] addr, IpInfo info, ConnEntry entry)
    {
        if (!info.IsTcp) return;

        if (PacketIp.IsSyn(info.TcpFlags))
        {
            HandleSyn(info, entry);
            return;
        }

        if (!_sessions.TryGetValue(entry.OrigSrcPort, out var sess))
            return;

        sess.LastActivity = Environment.TickCount64;

        if (!sess.Established && (info.TcpFlags & 0x10) != 0)
        {
            sess.Established = true;
            // Pure ACK consumes no payload; if ACK+data, ClientNext stays at ISN+1 until we take data
            if (info.Seq == sess.ClientNext || info.Seq == sess.ClientIsn + 1)
                sess.ClientNext = sess.ClientIsn + 1;
            _ = Task.Run(() => OpenSocksAsync(sess));
        }

        if (!sess.Established)
            return;

        if (PacketIp.IsFinOrRst(info.TcpFlags))
        {
            CloseSession(entry.OrigSrcPort, sendFin: (info.TcpFlags & 0x01) != 0);
            return;
        }

        var tcpOff = info.HeaderLen;
        var dataOff = tcpOff + (((packet[tcpOff + 12] >> 4) & 0xF) * 4);
        if (dataOff >= len) return;
        var payloadLen = (int)len - dataOff;
        if (payloadLen <= 0) return;

        if (info.Seq != sess.ClientNext)
        {
            InjectToClient(sess, 0x10, ReadOnlySpan<byte>.Empty);
            return;
        }

        var chunk = new byte[payloadLen];
        Buffer.BlockCopy(packet, dataOff, chunk, 0, payloadLen);
        sess.ClientNext = info.Seq + (uint)payloadLen;
        InjectToClient(sess, 0x10, ReadOnlySpan<byte>.Empty);

        NetworkStream? stream;
        lock (sess.Gate)
        {
            stream = sess.SocksStream;
            if (stream is null)
            {
                if (sess.Pending.Count < MaxPending)
                    sess.Pending.Add(chunk);
                else
                {
                    _log.Info("forge pending overflow sport=" + entry.OrigSrcPort);
                    CloseSession(entry.OrigSrcPort, sendFin: true);
                }
                return;
            }
        }

        try { stream.Write(chunk, 0, chunk.Length); }
        catch (Exception ex)
        {
            _log.Info("forge write socks: " + ex.Message);
            CloseSession(entry.OrigSrcPort, sendFin: true);
        }
    }

    void HandleSyn(IpInfo info, ConnEntry entry)
    {
        if (_sessions.TryGetValue(entry.OrigSrcPort, out var existing))
        {
            InjectToClient(existing, 0x12, ReadOnlySpan<byte>.Empty, existing.OurIsn, info.Seq + 1);
            return;
        }

        _map.Upsert(entry);
        var ourIsn = (uint)Rng.Next(1, int.MaxValue);
        var sess = new TcpForgeSession
        {
            Entry = entry,
            ClientIsn = info.Seq,
            OurIsn = ourIsn,
            ClientNext = info.Seq + 1,
            OurNext = ourIsn + 1
        };
        _sessions[entry.OrigSrcPort] = sess;
        Interlocked.Increment(ref _accepts);
        _log.Info("forge SYN-ACK pid=" + entry.Pid + " " + entry.OrigSrc + ":" + entry.OrigSrcPort +
                  " → " + entry.OrigDst + ":" + entry.OrigDstPort +
                  " via SOCKS " + _socksHost + ":" + _socksPort);

        InjectToClient(sess, 0x12, ReadOnlySpan<byte>.Empty, ourIsn, info.Seq + 1);
        sess.OurNext = ourIsn + 1;
    }

    async Task OpenSocksAsync(TcpForgeSession sess)
    {
        try
        {
            var client = await Socks5Client.ConnectClientAsync(
                _socksHost, _socksPort, sess.Entry.OrigDst, sess.Entry.OrigDstPort, _cts.Token);
            client.NoDelay = true;
            var stream = client.GetStream();

            List<byte[]> pending;
            lock (sess.Gate)
            {
                sess.Socks = client;
                sess.SocksStream = stream;
                pending = sess.Pending.ToList();
                sess.Pending.Clear();
            }

            foreach (var p in pending)
                stream.Write(p, 0, p.Length);

            _log.Info("forge SOCKS up sport=" + sess.Entry.OrigSrcPort +
                      " → " + sess.Entry.OrigDst + ":" + sess.Entry.OrigDstPort +
                      " via " + _socksHost + ":" + _socksPort +
                      (pending.Count > 0 ? " (flushed " + pending.Count + " early)" : ""));
            _ = Task.Run(() => PumpSocksToClient(sess));
        }
        catch (Exception ex)
        {
            _log.Info("forge SOCKS: " + ex.Message);
            CloseSession(sess.Entry.OrigSrcPort, sendFin: true);
        }
    }

    void PumpSocksToClient(TcpForgeSession sess)
    {
        var buf = new byte[MaxPayload];
        try
        {
            while (!_cts.IsCancellationRequested && sess.SocksStream is not null)
            {
                var n = sess.SocksStream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                InjectToClient(sess, 0x18, buf.AsSpan(0, n));
                sess.LastActivity = Environment.TickCount64;
            }
        }
        catch { }
        CloseSession(sess.Entry.OrigSrcPort, sendFin: true);
    }

    void InjectToClient(TcpForgeSession sess, byte flags, ReadOnlySpan<byte> payload,
        uint? seq = null, uint? ack = null)
    {
        lock (_injectGate)
        {
            var useSeq = seq ?? sess.OurNext;
            var useAck = ack ?? sess.ClientNext;
            var offset = 0;
            do
            {
                var chunk = payload.IsEmpty
                    ? ReadOnlySpan<byte>.Empty
                    : payload.Slice(offset, Math.Min(MaxPayload, payload.Length - offset));
                var pkt = BuildIpv4Tcp(sess.Entry.OrigDst, sess.Entry.OrigDstPort,
                    sess.Entry.OrigSrc, sess.Entry.OrigSrcPort, useSeq, useAck, flags, chunk);
                var addr = new byte[NativeWinDivert.AddressSize];
                NativeWinDivert.PrepareInjectAddr(addr);
                NativeWinDivert.SetOutbound(addr, false);
                NativeWinDivert.SetLoopback(addr, false);
                NativeWinDivert.SetImpostor(addr, true);
                NativeWinDivert.SetNetworkIf(addr, sess.Entry.IfIdx, sess.Entry.SubIfIdx);
                NativeWinDivert.WinDivertHelperCalcChecksums(pkt, (uint)pkt.Length, addr, 0);
                if (!NativeWinDivert.WinDivertSend(_sendHandle, pkt, (uint)pkt.Length, out _, addr))
                    _log.Verbose("forge Send err=" + Marshal.GetLastWin32Error());

                if (chunk.Length > 0)
                {
                    useSeq += (uint)chunk.Length;
                    offset += chunk.Length;
                    flags = 0x18;
                }
                else
                {
                    if ((flags & 0x03) != 0) useSeq += 1;
                    break;
                }
            } while (offset < payload.Length);

            if (seq is null)
                sess.OurNext = useSeq;
        }
    }

    static byte[] BuildIpv4Tcp(IPAddress src, int sport, IPAddress dst, int dport,
        uint seq, uint ack, byte flags, ReadOnlySpan<byte> payload)
    {
        var tcpLen = 20 + payload.Length;
        var total = 20 + tcpLen;
        var p = new byte[total];
        p[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)total);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), 0x4000);
        p[8] = 128;
        p[9] = NativeWinDivert.ProtoTcp;
        src.TryWriteBytes(p.AsSpan(12, 4), out _);
        dst.TryWriteBytes(p.AsSpan(16, 4), out _);
        var th = p.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(th, (ushort)sport);
        BinaryPrimitives.WriteUInt16BigEndian(th.Slice(2), (ushort)dport);
        BinaryPrimitives.WriteUInt32BigEndian(th.Slice(4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(th.Slice(8), ack);
        th[12] = 0x50;
        th[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(th.Slice(14), 65535);
        if (payload.Length > 0)
            payload.CopyTo(p.AsSpan(40));
        return p;
    }

    void CloseSession(int sport, bool sendFin)
    {
        if (!_sessions.TryRemove(sport, out var sess)) return;
        try
        {
            if (sendFin && sess.Established)
                InjectToClient(sess, 0x11, ReadOnlySpan<byte>.Empty);
        }
        catch { }
        try { sess.SocksStream?.Dispose(); } catch { }
        try { sess.Socks?.Dispose(); } catch { }
        _map.Remove(sport);
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var k in _sessions.Keys.ToArray())
            CloseSession(k, sendFin: false);
        _cts.Dispose();
    }
}
