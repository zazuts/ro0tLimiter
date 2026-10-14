using System.Runtime.InteropServices;

namespace DipWd;

static class CaptureSession
{
    public static int Run(CliArgs cli, AppLog log)
    {
        if (!NativeWinDivert.LoadDll())
            return 10;

        var match = new ProcessMatch(cli.ProcessPatterns, log);
        var filter = "ip and (tcp or udp)";
        var handle = NativeWinDivert.WinDivertOpen(
            filter, NativeWinDivert.LayerNetwork, 0, NativeWinDivert.FlagSniff);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            log.Info("WinDivertOpen (sniff) falhou err=" + Marshal.GetLastWin32Error());
            return 11;
        }

        log.Info("capture ON (sniff) filter=" + filter);
        var packet = new byte[0xFFFF];
        var addr = new byte[NativeWinDivert.AddressSize];
        var exit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };

        while (!exit.IsSet)
        {
            if (!NativeWinDivert.WinDivertRecv(handle, packet, (uint)packet.Length, out var len, addr))
            {
                if (exit.IsSet) break;
                continue;
            }
            if (!PacketIp.TryParse(packet.AsSpan(0, (int)len), out var info))
                continue;
            if (!info.IsTcp && !info.IsUdp) continue;

            var ok = match.ShouldProxy(info.IsUdp, info.Src, info.SrcPort, info.Dst, info.DstPort, out var pid);
            if (!ok) continue;

            var dir = NativeWinDivert.IsOutbound(addr) ? "OUT" : "IN";
            var proto = info.IsTcp ? "TCP" : "UDP";
            log.Info(dir + " " + proto + " pid=" + pid + " " +
                     info.Src + ":" + info.SrcPort + " → " + info.Dst + ":" + info.DstPort +
                     " len=" + len);
        }

        NativeWinDivert.WinDivertClose(handle);
        return 0;
    }
}
