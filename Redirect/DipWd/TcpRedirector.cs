using System.Net;
using System.Runtime.InteropServices;

namespace DipWd;

/// <summary>
/// WinDivert NETWORK: divert matching TCP to TcpForge (userspace SYN-ACK + SOCKS).
/// Original packets for matched flows are dropped (not reinjected).
/// </summary>
sealed class TcpRedirector : IDisposable
{
    readonly IntPtr _handle;
    readonly bool _ownsHandle;
    readonly ConnMap _map;
    readonly ProcessMatch _match;
    readonly TcpForge _forge;
    readonly AppLog _log;
    readonly int _socksPort;
    readonly bool _blockUdp;
    readonly bool _tcpOnly;
    readonly bool _passBrowsers;
    readonly CancellationTokenSource _cts = new();
    Thread? _thread;
    long _redirected;
    long _passed;
    long _dropped;
    long _recvCount;
    long _sendFail;

    static readonly IPAddress Loopback = IPAddress.Loopback;

    public long Redirected => Interlocked.Read(ref _redirected);
    public long Passed => Interlocked.Read(ref _passed);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Recv => Interlocked.Read(ref _recvCount);
    public long SendFail => Interlocked.Read(ref _sendFail);

    public TcpRedirector(ConnMap map, ProcessMatch match, TcpForge forge, IntPtr handle,
        int socksPort, string protocol, AppLog log, bool passBrowsers = false)
    {
        _map = map;
        _match = match;
        _forge = forge;
        _handle = handle;
        _ownsHandle = false;
        _socksPort = socksPort;
        _log = log;
        _blockUdp = protocol == "udp";
        _tcpOnly = protocol == "tcp";
        _passBrowsers = passBrowsers;
        _log.Info("WinDivert divert thread ready (forge mode" +
                  (passBrowsers ? ", browsers=pass/assist" : "") + ")");
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "dip-wd-divert" };
        _thread.Priority = ThreadPriority.AboveNormal;
        _thread.Start();
    }

    void Loop()
    {
        var packet = new byte[0xFFFF];
        var addr = new byte[NativeWinDivert.AddressSize];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!NativeWinDivert.WinDivertRecv(_handle, packet, (uint)packet.Length, out var len, addr))
                {
                    if (_cts.IsCancellationRequested) break;
                    var err = Marshal.GetLastWin32Error();
                    if (err is 995 or 6) break;
                    continue;
                }

                Interlocked.Increment(ref _recvCount);
                if (!PacketIp.TryParse(packet.AsSpan(0, (int)len), out var info))
                {
                    Pass(packet, len, addr);
                    continue;
                }

                if (info.IsUdp)
                {
                    HandleUdp(packet, len, addr, info);
                    continue;
                }

                if (!info.IsTcp)
                {
                    Pass(packet, len, addr);
                    continue;
                }

                HandleTcp(packet, len, addr, info);
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    _log.Info("divert loop: " + ex.Message);
            }
        }
    }

    void HandleUdp(byte[] packet, uint len, byte[] addr, IpInfo info)
    {
        if (IsExcludedEndpoint(info) || !NativeWinDivert.IsOutbound(addr))
        {
            Pass(packet, len, addr);
            return;
        }

        if (_tcpOnly || !_match.ShouldProxy(true, info.Src, info.SrcPort, info.Dst, info.DstPort, out _))
        {
            Pass(packet, len, addr);
            return;
        }

        if (_blockUdp)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        Pass(packet, len, addr);
    }

    void HandleTcp(byte[] packet, uint len, byte[] addr, IpInfo info)
    {
        if (IsExcludedEndpoint(info))
        {
            Pass(packet, len, addr);
            return;
        }

        if (!NativeWinDivert.IsOutbound(addr) || NativeWinDivert.IsLoopback(addr))
        {
            Pass(packet, len, addr);
            return;
        }

        if (_map.TryGetByClientPort(info.SrcPort, out var existing) &&
            (existing.OrigDst.Equals(info.Dst) && existing.OrigDstPort == info.DstPort ||
             _forge.HandlesClientPort(info.SrcPort)))
        {
            _forge.OnClientPacket(packet, len, addr, info, existing);
            Interlocked.Increment(ref _redirected);
            return; // drop
        }

        if (!_match.ShouldProxy(false, info.Src, info.SrcPort, info.Dst, info.DstPort, out var pid))
        {
            Pass(packet, len, addr);
            return;
        }

        // Chrome/Edge: HKCU SOCKS assist — nao forjar TCP (stack incompleto / AppContainer).
        if (_passBrowsers && IsBrowserPid(pid))
        {
            Pass(packet, len, addr);
            return;
        }

        var entry = new ConnEntry
        {
            OrigSrc = info.Src,
            OrigSrcPort = info.SrcPort,
            OrigDst = info.Dst,
            OrigDstPort = info.DstPort,
            Pid = pid,
            IfIdx = NativeWinDivert.GetIfIdx(addr),
            SubIfIdx = NativeWinDivert.GetSubIfIdx(addr)
        };
        _map.Upsert(entry);
        _forge.OnClientPacket(packet, len, addr, info, entry);
        Interlocked.Increment(ref _redirected);
    }

    bool IsBrowserPid(uint pid)
    {
        var path = ProcessPath.Of(pid);
        return ChromeAssist.IsBrowserName(path);
    }

    bool IsExcludedEndpoint(IpInfo info)
    {
        if (info.DstPort == _socksPort && info.Dst.Equals(Loopback)) return true;
        if (info.SrcPort == _socksPort && info.Src.Equals(Loopback)) return true;
        return false;
    }

    void Pass(byte[] packet, uint len, byte[] addr)
    {
        if (!NativeWinDivert.WinDivertSend(_handle, packet, len, out _, addr))
        {
            Interlocked.Increment(ref _sendFail);
            var err = Marshal.GetLastWin32Error();
            if (Interlocked.Read(ref _sendFail) <= 8)
                _log.Info("Pass Send err=" + err);
        }
        Interlocked.Increment(ref _passed);
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_ownsHandle)
        {
            try { NativeWinDivert.WinDivertClose(_handle); } catch { }
        }
        try { _thread?.Join(2000); } catch { }
        _cts.Dispose();
    }
}
