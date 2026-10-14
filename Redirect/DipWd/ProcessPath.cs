using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DipWd;

static class ProcessPath
{
    const uint QueryLimited = 0x1000;

    public static string Of(uint pid)
    {
        if (pid is 0 or 4) return "System";
        var win32 = Win32Path(pid);
        if (!string.IsNullOrWhiteSpace(win32)) return win32;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Contains('.') ? p.ProcessName : p.ProcessName + ".exe";
        }
        catch { return ""; }
    }

    public static string Win32Path(uint pid)
    {
        var h = OpenProcess(QueryLimited, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var size = 1024;
            var sb = new StringBuilder(size);
            if (QueryFullProcessImageName(h, 0, sb, ref size) && size > 0)
                return sb.ToString();
        }
        catch { }
        finally { CloseHandle(h); }
        return "";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
}
