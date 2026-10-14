using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;

namespace DipWd;

/// <summary>
/// WINDIVERT_LAYER_SOCKET (sniff): learn ProcessId on connect before NETWORK packets.
/// </summary>
sealed class SocketWatch : IDisposable
{
    readonly IntPtr _handle;
    readonly ProcessMatch _match;
    readonly AppLog _log;
    readonly CancellationTokenSource _cts = new();
    Thread? _thread;

    // key: "tcp|lip:lport|rip:rport" or "tcp|lport|rip:rport"
    readonly ConcurrentDictionary<string, uint> _byTuple = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<int, uint> _byLocalPort = new();

    public SocketWatch(ProcessMatch match, AppLog log)
    {
        _match = match;
        _log = log;
        _handle = NativeWinDivert.WinDivertOpen(
            "true",
            NativeWinDivert.LayerSocket,
            -1000,
            NativeWinDivert.FlagSniff | NativeWinDivert.FlagRecvOnly);
        if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException("WinDivertOpen SOCKET falhou err=" + err);
        }
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "dip-wd-socket" };
        _thread.Start();
        _log.Info("SocketWatch ON (CONNECT/PID)");
    }

    public bool TryGetPid(bool udp, IPAddress localIp, int localPort, IPAddress remoteIp, int remotePort,
        out uint pid)
    {
        var proto = udp ? "udp" : "tcp";
        if (_byTuple.TryGetValue(Key(proto, localIp, localPort, remoteIp, remotePort), out pid))
            return true;
        if (_byTuple.TryGetValue(Key(proto, IPAddress.Any, localPort, remoteIp, remotePort), out pid))
            return true;
        if (!udp && _byLocalPort.TryGetValue(localPort, out pid))
            return true;
        pid = 0;
        return false;
    }

    void Loop()
    {
        var addr = new byte[NativeWinDivert.AddressSize];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // SOCKET layer: no packet payload
                if (!NativeWinDivert.WinDivertRecv(_handle, null, 0, out _, addr))
                {
                    if (_cts.IsCancellationRequested) break;
                    var err = Marshal.GetLastWin32Error();
                    if (err is 995 or 6) break;
                    continue;
                }

                var ev = NativeWinDivert.GetEvent(addr);
                if (ev is not (NativeWinDivert.EventSocketConnect or NativeWinDivert.EventSocketAccept))
                    continue;

                var pid = NativeWinDivert.GetSocketProcessId(addr);
                if (pid == 0 || !_match.MatchesPid(pid))
                    continue;

                var proto = NativeWinDivert.GetSocketProtocol(addr);
                var udp = proto == NativeWinDivert.ProtoUdp;
                var lip = NativeWinDivert.GetSocketLocalIp(addr);
                var rip = NativeWinDivert.GetSocketRemoteIp(addr);
                var lport = NativeWinDivert.GetSocketLocalPort(addr);
                var rport = NativeWinDivert.GetSocketRemotePort(addr);
                if (lport == 0 || rport == 0) continue;

                var p = udp ? "udp" : "tcp";
                _byTuple[Key(p, lip, lport, rip, rport)] = pid;
                if (!udp) _byLocalPort[lport] = pid;
                _log.Verbose("socket " + (ev == NativeWinDivert.EventSocketConnect ? "CONNECT" : "ACCEPT") +
                             " pid=" + pid + " " + lip + ":" + lport + " → " + rip + ":" + rport);
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    _log.Verbose("SocketWatch: " + ex.Message);
            }
        }
    }

    static string Key(string proto, IPAddress lip, int lport, IPAddress rip, int rport) =>
        proto + "|" + lip + ":" + lport + "|" + rip + ":" + rport;

    public void Dispose()
    {
        _cts.Cancel();
        try { NativeWinDivert.WinDivertClose(_handle); } catch { }
        try { _thread?.Join(1500); } catch { }
        _cts.Dispose();
    }
}
