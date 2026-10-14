using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DipWd;

sealed class LoopbackTemplate
{
    public required byte[] Addr { get; init; }
    public required byte[] Packet { get; init; }
}

/// <summary>
/// Capture a real loopback SYN (packet + address) by connecting to our own relay.
/// </summary>
static class LoopbackProbe
{
    public static LoopbackTemplate? Capture(int relayPort, AppLog log)
    {
        var filter = "loopback and outbound and tcp and tcp.DstPort == " + relayPort;
        var h = NativeWinDivert.WinDivertOpen(filter, NativeWinDivert.LayerNetwork, 0,
            NativeWinDivert.FlagSniff);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
        {
            log.Info("LoopbackProbe open fail err=" + Marshal.GetLastWin32Error());
            return null;
        }

        LoopbackTemplate? result = null;
        var done = new ManualResetEventSlim(false);
        var thr = new Thread(() =>
        {
            var packet = new byte[0xFFFF];
            var addr = new byte[NativeWinDivert.AddressSize];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000 && !done.IsSet)
            {
                if (!NativeWinDivert.WinDivertRecv(h, packet, (uint)packet.Length, out var len, addr))
                    continue;
                if (!PacketIp.TryParse(packet.AsSpan(0, (int)len), out var info)) continue;
                if (!info.IsTcp || !PacketIp.IsSyn(info.TcpFlags)) continue;
                var pkt = new byte[len];
                Buffer.BlockCopy(packet, 0, pkt, 0, (int)len);
                result = new LoopbackTemplate
                {
                    Addr = (byte[])addr.Clone(),
                    Packet = pkt
                };
                log.Info("LoopbackProbe SYN template len=" + len +
                         " out=" + NativeWinDivert.IsOutbound(addr) +
                         " lb=" + NativeWinDivert.IsLoopback(addr) +
                         " sport=" + info.SrcPort);
                done.Set();
                break;
            }
        }) { IsBackground = true, Name = "dip-wd-lb-probe" };
        thr.Start();

        try
        {
            Thread.Sleep(50);
            using var c = new TcpClient();
            c.Connect(new IPEndPoint(IPAddress.Loopback, relayPort));
            Thread.Sleep(100);
            c.Close();
        }
        catch (Exception ex)
        {
            log.Info("LoopbackProbe connect: " + ex.Message);
        }

        done.Wait(1500);
        try { NativeWinDivert.WinDivertClose(h); } catch { }
        try { thr.Join(500); } catch { }
        return result;
    }
}
