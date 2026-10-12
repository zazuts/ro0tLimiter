using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace NetDivert.Core
{
    /// <summary>Opt-in text logs. Each line is one record of key=value fields.</summary>
    public static class TrafficLog
    {
        private static readonly object Gate = new();
        private const long MaxBytes = 16 * 1024 * 1024;

        public static void Drop(string reason, bool outbound, string proto, int length,
            string src, string dst, string app, string? exe, string adapter, int pid)
        {
            if (!PolicyEngine.Current.LogDrops || SkipUac(exe))
                return;
            Write("dropped.log", Record("DROP", reason, outbound, proto, length, src, dst, app, exe, adapter, pid));
        }

        public static void Connection(bool outbound, string proto, string src, string dst, string app, string? exe, string adapter, int pid)
        {
            if (!PolicyEngine.Current.LogConnections || SkipUac(exe))
                return;
            Write("connections.log", Record("OPEN", "", outbound, proto, 0, src, dst, app, exe, adapter, pid));
        }

        public static void Debug(string message)
        {
            if (!PolicyEngine.Current.LogDebug)
                return;
            Write("debug.log", $"time={Stamp()} level=debug msg={Q(message)}");
        }

        public static string Tail(string fileName, int maxChars = 24_000)
        {
            try
            {
                string path = Path.Combine(Brand.LogDir, fileName);
                if (!File.Exists(path))
                    return "";
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long start = Math.Max(0, fs.Length - maxChars);
                fs.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                string text = reader.ReadToEnd();
                int cut = text.IndexOf('\n');
                return start > 0 && cut >= 0 ? text[(cut + 1)..] : text;
            }
            catch
            {
                return "";
            }
        }

        private static string Record(string action, string reason, bool outbound, string proto, int length,
            string src, string dst, string app, string? exe, string adapter, int pid)
        {
            var sb = new StringBuilder(256);
            sb.Append("time=").Append(Stamp());
            sb.Append(" action=").Append(action);
            sb.Append(" dir=").Append(outbound ? "out" : "in");
            sb.Append(" proto=").Append(proto);
            if (length > 0)
                sb.Append(" len=").Append(length);
            sb.Append(" src=").Append(src);
            sb.Append(" dst=").Append(dst);
            sb.Append(" app=").Append(Q(app));
            sb.Append(" exe=").Append(Q(exe ?? ""));
            sb.Append(" adapter=").Append(Q(adapter));
            sb.Append(" pid=").Append(pid);
            if (reason.Length > 0)
                sb.Append(" reason=").Append(Q(reason));
            return sb.ToString();
        }

        private static bool SkipUac(string? exe) =>
            PolicyEngine.Current.Policy.IgnoreUacNoise
            && exe != null
            && exe.EndsWith("consent.exe", StringComparison.OrdinalIgnoreCase);

        private static string Stamp() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

        private static string Q(string value)
        {
            if (value.IndexOfAny(new[] { ' ', '"', '=' }) < 0)
                return value.Length == 0 ? "\"\"" : value;
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void Write(string fileName, string line)
        {
            try
            {
                Directory.CreateDirectory(Brand.LogDir);
                string path = Path.Combine(Brand.LogDir, fileName);
                lock (Gate)
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = path + ".1";
                        if (File.Exists(old))
                            File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never affect traffic.
            }
        }
    }
}
