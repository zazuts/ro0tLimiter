using System.Runtime.InteropServices;

namespace DipWd;

static class NativeWinDivert
{
    public const int LayerNetwork = 0;
    public const int LayerFlow = 2;
    public const int LayerSocket = 3;

    public const ulong FlagSniff = 0x0001;
    public const ulong FlagDrop = 0x0002;
    public const ulong FlagRecvOnly = 0x0004;
    public const ulong FlagSendOnly = 0x0008;

    public const int EventSocketConnect = 4;
    public const int EventSocketListen = 5;
    public const int EventSocketAccept = 6;
    public const int EventSocketClose = 7;

    public const byte ProtoTcp = 6;
    public const byte ProtoUdp = 17;

    // INT64 Timestamp + UINT32 bitfields + UINT32 Reserved2 + union UINT8[64] = 80
    public const int AddressSize = 80;

    // Offsets into WINDIVERT_ADDRESS
    public const int OffBitfield = 8;
    public const int OffUnion = 16;
    // Socket / Flow union:
    public const int OffProcessId = OffUnion + 16; // after EndpointId(8)+Parent(8)
    public const int OffLocalAddr = OffUnion + 20;
    public const int OffRemoteAddr = OffUnion + 36;
    public const int OffLocalPort = OffUnion + 52;
    public const int OffRemotePort = OffUnion + 54;
    public const int OffProtocol = OffUnion + 56;

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern IntPtr WinDivertOpen(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        int layer,
        short priority,
        ulong flags);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertRecv(
        IntPtr handle,
        byte[]? pPacket,
        uint packetLen,
        out uint recvLen,
        byte[] pAddr);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertSend(
        IntPtr handle,
        byte[] pPacket,
        uint packetLen,
        out uint sendLen,
        byte[] pAddr);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern uint WinDivertHelperCalcChecksums(
        byte[] pPacket,
        uint packetLen,
        byte[]? pAddr,
        ulong flags);

    public static bool LoadDll()
    {
        var dir = AppContext.BaseDirectory;
        var dll = Path.Combine(dir, "WinDivert.dll");
        if (!File.Exists(dll))
        {
            Console.Error.WriteLine("WinDivert.dll ausente em " + dir);
            Console.Error.WriteLine("Coloque WinDivert.dll + WinDivert64.sys (v2.2.x) ao lado do exe.");
            return false;
        }
        return NativeLibrary.TryLoad(dll, out _);
    }

    // MSVC bitfield pack (low→high) in UINT32 at offset 8:
    // Layer:8 Event:8 Sniffed:1 Outbound:1 Loopback:1 Impostor:1 IPv6:1 IPChecksum:1 TCPChecksum:1 UDPChecksum:1 Reserved1:8
    public static int GetLayer(byte[] addr) => (int)(BitConverter.ToUInt32(addr, OffBitfield) & 0xFF);
    public static int GetEvent(byte[] addr) => (int)((BitConverter.ToUInt32(addr, OffBitfield) >> 8) & 0xFF);

    public static bool IsOutbound(byte[] addr) =>
        ((BitConverter.ToUInt32(addr, OffBitfield) >> 17) & 1) == 1;

    public static bool IsLoopback(byte[] addr) =>
        ((BitConverter.ToUInt32(addr, OffBitfield) >> 18) & 1) == 1;

    public static void SetOutbound(byte[] addr, bool outbound)
    {
        var word = BitConverter.ToUInt32(addr, OffBitfield);
        if (outbound) word |= 1u << 17;
        else word &= ~(1u << 17);
        BitConverter.TryWriteBytes(addr.AsSpan(OffBitfield), word);
    }

    public static void SetLoopback(byte[] addr, bool loopback)
    {
        var word = BitConverter.ToUInt32(addr, OffBitfield);
        if (loopback) word |= 1u << 18;
        else word &= ~(1u << 18);
        BitConverter.TryWriteBytes(addr.AsSpan(OffBitfield), word);
    }

    public static void SetImpostor(byte[] addr, bool impostor)
    {
        var word = BitConverter.ToUInt32(addr, OffBitfield);
        if (impostor) word |= 1u << 19;
        else word &= ~(1u << 19);
        BitConverter.TryWriteBytes(addr.AsSpan(OffBitfield), word);
    }

    public static void SetSniffed(byte[] addr, bool sniffed)
    {
        var word = BitConverter.ToUInt32(addr, OffBitfield);
        if (sniffed) word |= 1u << 16;
        else word &= ~(1u << 16);
        BitConverter.TryWriteBytes(addr.AsSpan(OffBitfield), word);
    }

    public static void PrepareInjectAddr(byte[] addr)
    {
        SetSniffed(addr, false);
        // Fresh timestamp
        BitConverter.TryWriteBytes(addr.AsSpan(0), DateTime.UtcNow.Ticks);
    }

    public static uint GetSocketProcessId(byte[] addr) =>
        BitConverter.ToUInt32(addr, OffProcessId);

    public static ushort GetSocketLocalPort(byte[] addr) =>
        BitConverter.ToUInt16(addr, OffLocalPort);

    public static ushort GetSocketRemotePort(byte[] addr) =>
        BitConverter.ToUInt16(addr, OffRemotePort);

    public static byte GetSocketProtocol(byte[] addr) => addr[OffProtocol];

    public static System.Net.IPAddress GetSocketLocalIp(byte[] addr) => ReadIpv4(addr, OffLocalAddr);

    public static System.Net.IPAddress GetSocketRemoteIp(byte[] addr) => ReadIpv4(addr, OffRemoteAddr);

    /// <summary>
    /// WinDivert stores IPv4 as UINT32 in network byte order; on LE memory the
    /// bytes are reversed vs IPAddress dotted form — swap to host presentation.
    /// </summary>
    public static System.Net.IPAddress ReadIpv4(byte[] addr, int offset)
    {
        return new System.Net.IPAddress(new byte[]
        {
            addr[offset + 3], addr[offset + 2], addr[offset + 1], addr[offset]
        });
    }

    public static void ClearNetworkIf(byte[] addr)
    {
        BitConverter.TryWriteBytes(addr.AsSpan(OffUnion), 0u);
        BitConverter.TryWriteBytes(addr.AsSpan(OffUnion + 4), 0u);
    }

    public static void SetNetworkIf(byte[] addr, uint ifIdx, uint subIfIdx = 0)
    {
        BitConverter.TryWriteBytes(addr.AsSpan(OffUnion), ifIdx);
        BitConverter.TryWriteBytes(addr.AsSpan(OffUnion + 4), subIfIdx);
    }

    public static uint GetIfIdx(byte[] addr) => BitConverter.ToUInt32(addr, OffUnion);
    public static uint GetSubIfIdx(byte[] addr) => BitConverter.ToUInt32(addr, OffUnion + 4);

    public static uint FindLoopbackIfIndex()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;
                var v4 = ni.GetIPProperties().GetIPv4Properties();
                if (v4 is not null) return (uint)v4.Index;
            }
        }
        catch { }
        return 1; // common fallback
    }
}
