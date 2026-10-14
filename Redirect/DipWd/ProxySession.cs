using System.Runtime.InteropServices;

namespace DipWd;

static class ProxySession
{
    public static int Run(CliArgs cli, AppLog log) => Run(cli, log, CancellationToken.None);

    public static int Run(CliArgs cli, AppLog log, CancellationToken cancel)
    {
        if (!NativeWinDivert.LoadDll())
            return 10;

        var map = new ConnMap();
        var match = new ProcessMatch(cli.ProcessPatterns, log);

        using var watch = new SocketWatch(match, log);
        match.AttachWatch(watch);
        watch.Start();

        // Sempre limpa policies SOCKS residuais (Chrome/Edge/Brave/Firefox) antes de reaplicar.
        ChromeAssist.CleanupStaleBeforeSession(log);

        ChromeAssist? chromeAssist = null;
        var wantChrome = !cli.NoChromeAssist && ChromeAssist.WantsAssist(cli.ProcessPatterns);
        if (wantChrome)
        {
            chromeAssist = new ChromeAssist(cli.SocksIp, cli.SocksPort, log);
            chromeAssist.Apply();
        }

        var filter =
            "(outbound and not loopback and tcp)" +
            " or (outbound and not loopback and udp)";
        // Priority below the limiter so a block or a limit is applied before the redirect.
        var handle = NativeWinDivert.WinDivertOpen(filter, NativeWinDivert.LayerNetwork, -1, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            chromeAssist?.Dispose();
            throw new InvalidOperationException("WinDivertOpen falhou err=" + Marshal.GetLastWin32Error());
        }
        log.Info("WinDivert ON filter=" + filter);

        try
        {
            var loopbackIf = NativeWinDivert.FindLoopbackIfIndex();
            using var forge = new TcpForge(map, cli.SocksIp, cli.SocksPort, handle, loopbackIf, log);
            using var divert = new TcpRedirector(
                map, match, forge, handle, cli.SocksPort, cli.Protocol, log,
                passBrowsers: wantChrome);
            divert.Start();

            log.Info("proxy ON · processos=" + string.Join(";", cli.ProcessPatterns) +
                     " · protocol=" + cli.Protocol +
                     " · engine=WinDivert · forge/SOCKS");
            log.Info("L2/apps: WinDivert drop + forge SYN-ACK → SOCKS " +
                     cli.SocksIp + ":" + cli.SocksPort);
            if (wantChrome)
                log.Info("Chrome/Edge: assist HKCU → " + cli.SocksIp + ":" + cli.SocksPort +
                         " (forge NAO toca o browser; reinicie o Chrome).");
            else
                log.Info("Chrome assist OFF — forge tambem tenta browsers.");

            while (!cancel.IsCancellationRequested)
            {
                cancel.WaitHandle.WaitOne(30_000);
                map.PurgeOld(TimeSpan.FromMinutes(10));
            }
            log.Info("Parando… recv=" + divert.Recv +
                     " redirected=" + divert.Redirected +
                     " passed=" + divert.Passed +
                     " sendFail=" + divert.SendFail +
                     " forgeSessions=" + forge.Accepts +
                     " conns=" + map.Count);
            return 0;
        }
        finally
        {
            try { NativeWinDivert.WinDivertClose(handle); } catch { }
            chromeAssist?.Dispose();
        }
    }
}
