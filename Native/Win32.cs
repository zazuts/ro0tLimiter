using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NetDivert.Native
{
    internal static unsafe class Win32
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint STILL_ACTIVE = 259;

        public const int AF_INET = 2;
        public const int AF_INET6 = 23;
        public const int TCP_TABLE_OWNER_PID_ALL = 5;
        public const int UDP_TABLE_OWNER_PID = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint period);

        [DllImport("iphlpapi.dll")]
        public static extern uint GetExtendedTcpTable(void* table, ref int size, bool order, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll")]
        public static extern uint GetExtendedUdpTable(void* table, ref int size, bool order, int af, int tableClass, uint reserved);

        public static string? GetProcessPath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>Returns false only when the process is known to have exited.</summary>
        public static bool IsProcessAlive(int pid)
        {
            if (pid <= 4)
                return true;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == 5; // access denied => exists
            try
            {
                return !GetExitCodeProcess(h, out uint code) || code == STILL_ACTIVE;
            }
            finally
            {
                CloseHandle(h);
            }
        }
    }
}
