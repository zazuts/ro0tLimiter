using System.Buffers.Binary;
using System.Net;

namespace DipWd;

/// <summary>IPv4 + TCP/UDP header helpers for raw WinDivert packets (no Ethernet).</summary>
static class PacketIp
{
    public static bool TryParse(ReadOnlySpan<byte> pkt, out IpInfo info)
    {
        info = default;
        if (pkt.Length < 20) return false;
        var verIhl = pkt[0];
        if ((verIhl >> 4) != 4) return false;
        var ihl = (verIhl & 0x0F) * 4;
        if (ihl < 20 || pkt.Length < ihl) return false;

        var proto = pkt[9];
        var src = new IPAddress(pkt.Slice(12, 4));
        var dst = new IPAddress(pkt.Slice(16, 4));
        info = new IpInfo
        {
            HeaderLen = ihl,
            Protocol = proto,
            Src = src,
            Dst = dst,
            TotalLen = BinaryPrimitives.ReadUInt16BigEndian(pkt.Slice(2, 2))
        };

        if (proto == NativeWinDivert.ProtoTcp)
        {
            if (pkt.Length < ihl + 20) return false;
            var th = pkt.Slice(ihl);
            info.SrcPort = BinaryPrimitives.ReadUInt16BigEndian(th);
            info.DstPort = BinaryPrimitives.ReadUInt16BigEndian(th.Slice(2));
            info.TcpFlags = th[13];
            info.Seq = BinaryPrimitives.ReadUInt32BigEndian(th.Slice(4));
            info.Ack = BinaryPrimitives.ReadUInt32BigEndian(th.Slice(8));
            info.IsTcp = true;
            return true;
        }

        if (proto == NativeWinDivert.ProtoUdp)
        {
            if (pkt.Length < ihl + 8) return false;
            var uh = pkt.Slice(ihl);
            info.SrcPort = BinaryPrimitives.ReadUInt16BigEndian(uh);
            info.DstPort = BinaryPrimitives.ReadUInt16BigEndian(uh.Slice(2));
            info.IsUdp = true;
            return true;
        }

        return true;
    }

    public static void SetIpv4Addresses(Span<byte> pkt, IPAddress src, IPAddress dst)
    {
        src.TryWriteBytes(pkt.Slice(12, 4), out _);
        dst.TryWriteBytes(pkt.Slice(16, 4), out _);
    }

    public static void SetTcpPorts(Span<byte> pkt, int ipHeaderLen, int srcPort, int dstPort)
    {
        var th = pkt.Slice(ipHeaderLen);
        BinaryPrimitives.WriteUInt16BigEndian(th, (ushort)srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(th.Slice(2), (ushort)dstPort);
    }

    /// <summary>Build a minimal IPv4+TCP SYN (40 bytes) for loopback inject.</summary>
    public static byte[] BuildMinimalSyn(int srcPort, int dstPort, uint seq)
    {
        var p = new byte[40];
        p[0] = 0x45; // v4 ihl5
        p[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 40); // total length
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), 0); // id
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), 0x4000); // DF
        p[8] = 128; // ttl
        p[9] = NativeWinDivert.ProtoTcp;
        // checksum 10-11 zeroed
        // src/dst set by caller via SetIpv4Addresses
        var th = p.AsSpan(20);
        BinaryPrimitives.WriteUInt16BigEndian(th, (ushort)srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(th.Slice(2), (ushort)dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(th.Slice(4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(th.Slice(8), 0);
        th[12] = 0x50; // data offset 5 (20 bytes)
        th[13] = 0x02; // SYN
        BinaryPrimitives.WriteUInt16BigEndian(th.Slice(14), 65535); // window
        // checksum 16-17 zero
        // urg 18-19 zero
        return p;
    }

    public static bool IsSyn(byte tcpFlags) => (tcpFlags & 0x02) != 0 && (tcpFlags & 0x10) == 0;
    public static bool IsFinOrRst(byte tcpFlags) => (tcpFlags & 0x05) != 0;
}

struct IpInfo
{
    public int HeaderLen;
    public byte Protocol;
    public IPAddress Src;
    public IPAddress Dst;
    public int TotalLen;
    public int SrcPort;
    public int DstPort;
    public byte TcpFlags;
    public uint Seq;
    public uint Ack;
    public bool IsTcp;
    public bool IsUdp;
}
