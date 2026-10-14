using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NetDivert.Native;

namespace NetDivert.Core
{
    /// <summary>Asks Windows to drop TCP connections owned by the given process ids.</summary>
    public static unsafe class TcpCloser
    {
        private const uint DeleteTcb = 12;

        public static int Close(int[] pids)
        {
            if (pids == null || pids.Length == 0)
                return 0;
            var owned = new HashSet<int>();
            foreach (int pid in pids)
                if (pid > 4)
                    owned.Add(pid);
            if (owned.Count == 0)
                return 0;
            int closed = Close4(owned);
            closed += Close6(owned);
            return closed;
        }

        private static int Close4(HashSet<int> pids)
        {
            int closed = 0;
            int size = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint rc = Win32.GetExtendedTcpTable(null, ref size, false, Win32.AF_INET, Win32.TCP_TABLE_OWNER_PID_ALL, 0);
                if (size <= 0)
                    return closed;
                size += 4096;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    rc = Win32.GetExtendedTcpTable((void*)buf, ref size, false, Win32.AF_INET, Win32.TCP_TABLE_OWNER_PID_ALL, 0);
                    if (rc == 122)
                        continue;
                    if (rc != 0)
                        return closed;
                    byte* p = (byte*)buf;
                    uint count = *(uint*)p;
                    byte* row = p + 4;
                    for (uint i = 0; i < count; i++, row += 24)
                    {
                        uint state = *(uint*)row;
                        int pid = *(int*)(row + 20);
                        if (state < 3 || state > 11 || !pids.Contains(pid))
                            continue;
                        var entry = new TcpRow
                        {
                            State = DeleteTcb,
                            LocalAddr = *(uint*)(row + 4),
                            LocalPort = *(uint*)(row + 8),
                            RemoteAddr = *(uint*)(row + 12),
                            RemotePort = *(uint*)(row + 16),
                        };
                        if (SetTcpEntry(ref entry) == 0)
                            closed++;
                    }
                    return closed;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            return closed;
        }

        private static int Close6(HashSet<int> pids)
        {
            var fn = V6Entry();
            if (fn == null)
                return 0;
            int closed = 0;
            int size = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint rc = Win32.GetExtendedTcpTable(null, ref size, false, Win32.AF_INET6, Win32.TCP_TABLE_OWNER_PID_ALL, 0);
                if (size <= 0)
                    return closed;
                size += 4096;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    rc = Win32.GetExtendedTcpTable((void*)buf, ref size, false, Win32.AF_INET6, Win32.TCP_TABLE_OWNER_PID_ALL, 0);
                    if (rc == 122)
                        continue;
                    if (rc != 0)
                        return closed;
                    byte* p = (byte*)buf;
                    uint count = *(uint*)p;
                    byte* row = p + 4;
                    for (uint i = 0; i < count; i++, row += 56)
                    {
                        uint state = *(uint*)(row + 48);
                        int pid = *(int*)(row + 52);
                        if (state < 3 || state > 11 || !pids.Contains(pid))
                            continue;
                        var entry = new Tcp6Row
                        {
                            LocalScope = *(uint*)(row + 16),
                            LocalPort = *(uint*)(row + 20),
                            RemoteScope = *(uint*)(row + 40),
                            RemotePort = *(uint*)(row + 44),
                            State = DeleteTcb,
                        };
                        Buffer.MemoryCopy(row, entry.LocalAddr, 16, 16);
                        Buffer.MemoryCopy(row + 24, entry.RemoteAddr, 16, 16);
                        if (fn(ref entry) == 0)
                            closed++;
                    }
                    return closed;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            return closed;
        }

        private static SetTcp6? _v6;
        private static bool _v6Looked;

        private static SetTcp6? V6Entry()
        {
            if (_v6Looked)
                return _v6;
            _v6Looked = true;
            IntPtr module = GetModuleHandle("iphlpapi.dll");
            if (module == IntPtr.Zero)
                return null;
            IntPtr proc = GetProcAddress(module, "SetTcp6Entry");
            if (proc == IntPtr.Zero)
                return null;
            _v6 = Marshal.GetDelegateForFunctionPointer<SetTcp6>(proc);
            return _v6;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TcpRow
        {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort;
            public uint RemoteAddr;
            public uint RemotePort;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Tcp6Row
        {
            public unsafe fixed byte LocalAddr[16];
            public uint LocalScope;
            public uint LocalPort;
            public unsafe fixed byte RemoteAddr[16];
            public uint RemoteScope;
            public uint RemotePort;
            public uint State;
        }

        private delegate uint SetTcp6(ref Tcp6Row row);

        [DllImport("iphlpapi.dll")]
        private static extern uint SetTcpEntry(ref TcpRow row);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
