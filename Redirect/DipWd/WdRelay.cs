using System.Net;
using System.Net.Sockets;

namespace DipWd;

/// <summary>
/// Loopback TCP relay: diverted app → 127.0.0.1:Port → SOCKS.
/// Destination from ConnMap by client source port.
/// </summary>
sealed class WdRelay : IDisposable
{
    readonly Socket _listen;
    readonly string _socksHost;
    readonly int _socksPort;
    readonly ConnMap _map;
    readonly AppLog _log;
    readonly CancellationTokenSource _cts = new();
    Thread? _acceptThread;
    long _sessions;
    long _accepts;

    public int Port { get; }
    public long Sessions => Interlocked.Read(ref _sessions);
    public long Accepts => Interlocked.Read(ref _accepts);

    public WdRelay(string socksHost, int socksPort, ConnMap map, AppLog log)
    {
        _socksHost = socksHost;
        _socksPort = socksPort;
        _map = map;
        _log = log;

        _listen = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listen.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listen.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listen.Listen(256);
        Port = ((IPEndPoint)_listen.LocalEndPoint!).Port;
    }

    public void Start()
    {
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "dip-wd-relay" };
        _acceptThread.Start();
        _log.Info("relay loopback 127.0.0.1:" + Port + " → SOCKS " + _socksHost + ":" + _socksPort);
    }

    void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = _listen.Accept();
                Interlocked.Increment(ref _accepts);
                Interlocked.Increment(ref _sessions);
                var peer = client.RemoteEndPoint?.ToString() ?? "?";
                _log.Info("relay ACCEPT #" + _accepts + " from " + peer);
                var sock = client;
                _ = Task.Run(() => HandleSocketAsync(sock, _cts.Token));
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    _log.Info("relay accept err: " + ex.Message);
            }
        }
    }

    async Task HandleSocketAsync(Socket appSock, CancellationToken ct)
    {
        using var app = new TcpClient { Client = appSock };
        TcpClient? remote = null;
        try
        {
            app.NoDelay = true;
            var peer = (IPEndPoint)appSock.RemoteEndPoint!;
            var clientPort = peer.Port;

            if (!_map.TryGetByClientPort(clientPort, out var entry))
            {
                _log.Info("relay: ConnMap miss sport=" + clientPort + " peer=" + peer);
                return;
            }

            _map.Touch(clientPort);
            _log.Info("relay tunnel → " + entry.OrigDst + ":" + entry.OrigDstPort +
                      " (pid=" + entry.Pid + " sport=" + clientPort + ") via SOCKS " +
                      _socksHost + ":" + _socksPort);

            remote = await Socks5Client.ConnectClientAsync(
                _socksHost, _socksPort, entry.OrigDst, entry.OrigDstPort, ct);
            remote.NoDelay = true;

            var appStream = app.GetStream();
            var remoteStream = remote.GetStream();
            var t1 = PumpAsync(appStream, remoteStream, ct);
            var t2 = PumpAsync(remoteStream, appStream, ct);
            await Task.WhenAny(t1, t2);
        }
        catch (Exception ex)
        {
            _log.Info("relay session: " + ex.Message);
        }
        finally
        {
            try { app.Dispose(); } catch { }
            try { remote?.Dispose(); } catch { }
        }
    }

    static async Task PumpAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buf = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            var n = await from.ReadAsync(buf, ct);
            if (n <= 0) break;
            await to.WriteAsync(buf.AsMemory(0, n), ct);
        }
        try { to.Socket.Shutdown(SocketShutdown.Send); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listen.Close(); } catch { }
        try { _acceptThread?.Join(1000); } catch { }
        _cts.Dispose();
    }
}
