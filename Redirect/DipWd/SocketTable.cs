using System.Net;
using System.Runtime.InteropServices;

namespace DipWd;

sealed class SocketTable
{
    const int AfInet = 2;
    const int TcpTableOwnerPidAll = 5;
    const int UdpTableOwnerPid = 1;

    readonly object _gate = new();
    Dictionary<string, uint> _tcp = new(StringComparer.Ordinal);
    Dictionary<string, uint> _udp = new(StringComparer.Ordinal);
    DateTime _nextRefresh = DateTime.MinValue;

    public void RefreshIfNeeded()
    {
        if (DateTime.UtcNow < _nextRefresh) return;
        lock (_gate)
        {
            if (DateTime.UtcNow < _nextRefresh) return;
            _tcp = ReadTcp();
            _udp = ReadUdp();
            _nextRefresh = DateTime.UtcNow.AddMilliseconds(250);
        }
    }

    public bool TryFind(bool udp, IPAddress localIp, int localPort, IPAddress remoteIp, int remotePort,
        out uint pid)
    {
        RefreshIfNeeded();
        lock (_gate)
        {
            if (!udp)
            {
                var k = TcpKey(localIp, localPort, remoteIp, remotePort);
                if (_tcp.TryGetValue(k, out pid)) return true;
                foreach (var kv in _tcp)
                {
                    if (kv.Key.StartsWith(LocalPart(localIp, localPort) + "|", StringComparison.Ordinal))
                    {
                        pid = kv.Value;
                        return true;
                    }
                }
                // SYN-SENT: match by local port only
                foreach (var kv in _tcp)
                {
                    if (kv.Key.Contains(":" + localPort + "|", StringComparison.Ordinal))
                    {
                        pid = kv.Value;
                        return true;
                    }
                }
            }
            else
            {
                var k = UdpKey(localIp, localPort);
                if (_udp.TryGetValue(k, out pid)) return true;
                if (_udp.TryGetValue(UdpKey(IPAddress.Any, localPort), out pid)) return true;
            }
        }
        pid = 0;
        return false;
    }

    static string LocalPart(IPAddress ip, int port) => ip + ":" + port;
    static string TcpKey(IPAddress lip, int lport, IPAddress rip, int rport) =>
        lip + ":" + lport + "|" + rip + ":" + rport;
    static string UdpKey(IPAddress lip, int lport) => lip + ":" + lport;

    static Dictionary<string, uint> ReadTcp()
    {
        var map = new Dictionary<string, uint>(StringComparer.Ordinal);
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0) return map;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, true, AfInet, TcpTableOwnerPidAll, 0) != 0)
                return map;
            var num = Marshal.ReadInt32(buf);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var ptr = buf + 4;
            for (var i = 0; i < num; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(ptr + i * rowSize);
                var lip = new IPAddress(BitConverter.GetBytes(row.LocalAddr));
                var rip = new IPAddress(BitConverter.GetBytes(row.RemoteAddr));
                var lport = Ntohs(row.LocalPort);
                var rport = Ntohs(row.RemotePort);
                map[TcpKey(lip, lport, rip, rport)] = row.OwningPid;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return map;
    }

    static Dictionary<string, uint> ReadUdp()
    {
        var map = new Dictionary<string, uint>(StringComparer.Ordinal);
        var size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, true, AfInet, UdpTableOwnerPid, 0);
        if (size <= 0) return map;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buf, ref size, true, AfInet, UdpTableOwnerPid, 0) != 0)
                return map;
            var num = Marshal.ReadInt32(buf);
            var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            var ptr = buf + 4;
            for (var i = 0; i < num; i++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(ptr + i * rowSize);
                var lip = new IPAddress(BitConverter.GetBytes(row.LocalAddr));
                var lport = Ntohs(row.LocalPort);
                map[UdpKey(lip, lport)] = row.OwningPid;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return map;
    }

    static int Ntohs(uint portField)
    {
        var b0 = (byte)(portField & 0xFF);
        var b1 = (byte)((portField >> 8) & 0xFF);
        return (b0 << 8) | b1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwSize, bool order, int ulAf,
        int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwSize, bool order, int ulAf,
        int tableClass, uint reserved);
}
