using System;
using System.Buffers.Binary;
using System.Net;

namespace NetDivert.Core
{
    /// <summary>
    /// 128-bit address in the same normalized form WinDivert uses at the flow/socket layers:
    /// IPv4 is represented as the IPv4-mapped IPv6 address ::ffff:a.b.c.d.
    /// </summary>
    public readonly record struct Ip128(ulong Hi, ulong Lo)
    {
        private const ulong V4Prefix = 0x0000FFFF00000000UL;

        public static Ip128 FromV4(uint hostOrder) => new(0, V4Prefix | hostOrder);

        public static unsafe Ip128 FromV6(byte* networkOrder) => new(
            BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>(networkOrder, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>(networkOrder + 8, 8)));

        /// <summary>From WinDivert's UINT32[4] host-order representation (word 0 is least significant).</summary>
        public static unsafe Ip128 FromDivert(uint* a) => new(((ulong)a[3] << 32) | a[2], ((ulong)a[1] << 32) | a[0]);

        public bool IsV4 => Hi == 0 && (Lo >> 32) == 0xFFFF;

        public IPAddress ToIPAddress()
        {
            if (IsV4)
            {
                uint v = (uint)Lo;
                return new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
            }
            var bytes = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), Hi);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), Lo);
            return new IPAddress(bytes);
        }

        public override string ToString() => ToIPAddress().ToString();
    }

    public struct PacketMeta
    {
        public int Length;
        public byte Protocol;
        public bool HasPorts;
        public ushort SrcPort;
        public ushort DstPort;
        public Ip128 Src;
        public Ip128 Dst;
    }

    public static unsafe class PacketParser
    {
        public const byte Tcp = 6;
        public const byte Udp = 17;

        private static ushort Be16(byte* p) => (ushort)((p[0] << 8) | p[1]);
        private static uint Be32(byte* p) => ((uint)p[0] << 24) | ((uint)p[1] << 16) | ((uint)p[2] << 8) | p[3];

        /// <summary>Parses the IP header and, when present, TCP/UDP ports. Sets Length to the full IP packet length.</summary>
        public static bool TryParse(byte* p, int available, out PacketMeta m)
        {
            m = default;
            if (available < 1)
                return false;

            int off;
            int version = p[0] >> 4;
            if (version == 4)
            {
                if (available < 20)
                    return false;
                int ihl = (p[0] & 0x0F) * 4;
                int total = Be16(p + 2);
                if (ihl < 20 || total < ihl || total > available)
                    return false;
                m.Length = total;
                m.Protocol = p[9];
                m.Src = Ip128.FromV4(Be32(p + 12));
                m.Dst = Ip128.FromV4(Be32(p + 16));
                int fragOffset = ((p[6] & 0x1F) << 8) | p[7];
                if (fragOffset != 0)
                    return true;
                off = ihl;
            }
            else if (version == 6)
            {
                if (available < 40)
                    return false;
                int total = 40 + Be16(p + 4);
                if (total > available)
                    return false;
                m.Length = total;
                m.Src = Ip128.FromV6(p + 8);
                m.Dst = Ip128.FromV6(p + 24);
                byte next = p[6];
                off = 40;
                while (true)
                {
                    if (next is 0 or 43 or 60 or 44 or 51)
                    {
                        if (off + 8 > total)
                            break;
                        byte nn = p[off];
                        if (next == 44)
                        {
                            int fo = Be16(p + off + 2) >> 3;
                            off += 8;
                            next = nn;
                            if (fo != 0)
                            {
                                m.Protocol = next;
                                return true;
                            }
                        }
                        else
                        {
                            off += next == 51 ? (p[off + 1] + 2) * 4 : (p[off + 1] + 1) * 8;
                            next = nn;
                        }
                        continue;
                    }
                    break;
                }
                m.Protocol = next;
            }
            else
            {
                return false;
            }

            if ((m.Protocol == Tcp || m.Protocol == Udp) && off + 4 <= m.Length)
            {
                m.SrcPort = Be16(p + off);
                m.DstPort = Be16(p + off + 2);
                m.HasPorts = true;
            }
            return true;
        }

        public static string ProtocolName(byte proto) => proto switch
        {
            Tcp => "TCP",
            Udp => "UDP",
            1 => "ICMP",
            58 => "ICMPv6",
            _ => proto.ToString(),
        };
    }
}
