using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using NetDivert.Native;

namespace NetDivert.Core
{
    public sealed class ListenerSnapshot
    {
        public string Id { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string Local { get; set; } = "";
        public int Port { get; set; }
        public int Pid { get; set; }
        public string Process { get; set; } = "";
        public string Path { get; set; } = "";
    }

    /// <summary>TCP listeners and UDP sockets currently bound on this machine.</summary>
    public static unsafe class ListenerQuery
    {
        public static List<ListenerSnapshot> Query()
        {
            var list = new List<ListenerSnapshot>();
            Read(Win32.AF_INET, tcp: true, rowSize: 24, stateOffset: 0, addrOffset: 4, portOffset: 8, pidOffset: 20, v6: false, list);
            Read(Win32.AF_INET6, tcp: true, rowSize: 56, stateOffset: 48, addrOffset: 0, portOffset: 20, pidOffset: 52, v6: true, list);
            Read(Win32.AF_INET, tcp: false, rowSize: 12, stateOffset: -1, addrOffset: 0, portOffset: 4, pidOffset: 8, v6: false, list);
            Read(Win32.AF_INET6, tcp: false, rowSize: 28, stateOffset: -1, addrOffset: 0, portOffset: 20, pidOffset: 24, v6: true, list);
            return list;
        }

        private static void Read(int af, bool tcp, int rowSize, int stateOffset, int addrOffset, int portOffset, int pidOffset, bool v6, List<ListenerSnapshot> list)
        {
            int size = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint rc = tcp
                    ? Win32.GetExtendedTcpTable(null, ref size, false, af, Win32.TCP_TABLE_OWNER_PID_ALL, 0)
                    : Win32.GetExtendedUdpTable(null, ref size, false, af, Win32.UDP_TABLE_OWNER_PID, 0);
                if (size <= 0)
                    return;
                size += 4096;
                IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                try
                {
                    rc = tcp
                        ? Win32.GetExtendedTcpTable((void*)buf, ref size, false, af, Win32.TCP_TABLE_OWNER_PID_ALL, 0)
                        : Win32.GetExtendedUdpTable((void*)buf, ref size, false, af, Win32.UDP_TABLE_OWNER_PID, 0);
                    if (rc == 122)
                        continue;
                    if (rc != 0)
                        return;
                    byte* p = (byte*)buf;
                    uint count = *(uint*)p;
                    byte* row = p + 4;
                    for (uint i = 0; i < count; i++, row += rowSize)
                    {
                        if (stateOffset >= 0 && *(uint*)(row + stateOffset) != 2)
                            continue;
                        int pid = *(int*)(row + pidOffset);
                        ushort port = NetPort(*(uint*)(row + portOffset));
                        if (port == 0)
                            continue;
                        string local = v6 ? FormatV6(row + addrOffset) : FormatV4(*(uint*)(row + addrOffset));
                        string path = Win32.GetProcessPath(pid) ?? "";
                        string name = path.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(path) : pid.ToString();
                        list.Add(new ListenerSnapshot
                        {
                            Id = $"{(tcp ? "tcp" : "udp")}|{local}|{port}|{pid}",
                            Protocol = tcp ? "TCP" : "UDP",
                            Local = local,
                            Port = port,
                            Pid = pid,
                            Process = name,
                            Path = path,
                        });
                    }
                    return;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
                }
            }
        }

        private static ushort NetPort(uint dw) => (ushort)(((dw & 0xFF) << 8) | ((dw >> 8) & 0xFF));

        private static string FormatV4(uint addr) =>
            addr == 0 ? "0.0.0.0" : new IPAddress(new[] { (byte)addr, (byte)(addr >> 8), (byte)(addr >> 16), (byte)(addr >> 24) }).ToString();

        private static string FormatV6(byte* addr)
        {
            var bytes = new byte[16];
            for (int i = 0; i < 16; i++)
                bytes[i] = addr[i];
            bool any = true;
            foreach (var b in bytes)
                any &= b == 0;
            return any ? "::" : new IPAddress(bytes).ToString();
        }
    }
}
