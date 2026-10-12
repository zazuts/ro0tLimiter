using System;
using System.Runtime.InteropServices;
using System.Threading;
using NetDivert.Native;

namespace NetDivert.Core
{
    /// <summary>
    /// Lock-free local-port -> PID map for TCP and UDP. Fed by WinDivert SOCKET/FLOW events and
    /// periodically re-seeded from the IP Helper tables (sockets opened before the engine started).
    /// </summary>
    public sealed unsafe class PortTable
    {
        private readonly int[] _tcp = new int[65536];
        private readonly int[] _udp = new int[65536];

        public int Get(byte protocol, ushort localPort) => protocol switch
        {
            PacketParser.Tcp => Volatile.Read(ref _tcp[localPort]),
            PacketParser.Udp => Volatile.Read(ref _udp[localPort]),
            _ => 0,
        };

        public void Set(byte protocol, ushort localPort, int pid)
        {
            if (localPort == 0 || pid < 0)
                return;
            if (protocol == PacketParser.Tcp)
                Volatile.Write(ref _tcp[localPort], pid);
            else if (protocol == PacketParser.Udp)
                Volatile.Write(ref _udp[localPort], pid);
        }

        private static ushort NetPort(uint dw) => (ushort)(((dw & 0xFF) << 8) | ((dw >> 8) & 0xFF));

        public void RefreshFromSystem()
        {
            Load(Win32.AF_INET, tcp: true, rowSize: 24, portOffset: 8, pidOffset: 20);
            Load(Win32.AF_INET6, tcp: true, rowSize: 56, portOffset: 20, pidOffset: 52);
            Load(Win32.AF_INET, tcp: false, rowSize: 12, portOffset: 4, pidOffset: 8);
            Load(Win32.AF_INET6, tcp: false, rowSize: 28, portOffset: 20, pidOffset: 24);
        }

        private void Load(int af, bool tcp, int rowSize, int portOffset, int pidOffset)
        {
            int size = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                uint rc = tcp
                    ? Win32.GetExtendedTcpTable(null, ref size, false, af, Win32.TCP_TABLE_OWNER_PID_ALL, 0)
                    : Win32.GetExtendedUdpTable(null, ref size, false, af, Win32.UDP_TABLE_OWNER_PID, 0);
                if (size <= 0)
                    return;
                size += 4096;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    rc = tcp
                        ? Win32.GetExtendedTcpTable((void*)buf, ref size, false, af, Win32.TCP_TABLE_OWNER_PID_ALL, 0)
                        : Win32.GetExtendedUdpTable((void*)buf, ref size, false, af, Win32.UDP_TABLE_OWNER_PID, 0);
                    if (rc == 122) // ERROR_INSUFFICIENT_BUFFER: table grew, retry
                        continue;
                    if (rc != 0)
                        return;

                    byte* p = (byte*)buf;
                    uint count = *(uint*)p;
                    byte* row = p + 4;
                    byte proto = tcp ? PacketParser.Tcp : PacketParser.Udp;
                    for (uint i = 0; i < count; i++, row += rowSize)
                    {
                        int pid = *(int*)(row + pidOffset);
                        if (pid != 0) // TIME_WAIT rows have no owner
                            Set(proto, NetPort(*(uint*)(row + portOffset)), pid);
                    }
                    return;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
    }
}
