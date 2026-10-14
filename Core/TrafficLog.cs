using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace NetDivert.Core
{
    /// <summary>Opt-in text logs. Each line is one record of key=value fields.</summary>
    public static class TrafficLog
    {
        private const long MaxBytes = 16 * 1024 * 1024;
        private const int MaxQueued = 4000;
        private static readonly ConcurrentQueue<(string File, string Line)> Queue = new();
        private static int _queued;
        private static int _writer;
        private static long _rotatedAt;

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

        public static void Rotate(int mode)
        {
            if (mode <= 0)
                return;
            long now = Environment.TickCount64;
            if (now - _rotatedAt < 600_000)
                return;
            _rotatedAt = now;
            var maxAge = mode switch
            {
                1 => TimeSpan.FromDays(1),
                2 => TimeSpan.FromDays(7),
                3 => TimeSpan.FromDays(31),
                4 => TimeSpan.FromDays(182),
                _ => TimeSpan.FromDays(365),
            };
            var cutoff = DateTime.Now - maxAge;
            Purge(Brand.LogDir, "*.log", cutoff);
            Purge(Brand.LogDir, "*.log.1", cutoff);
            Purge(AppContext.BaseDirectory, "dip-wd-*.log", cutoff);
        }

        public static (int Deleted, int Busy) DeleteAll()
        {
            int deleted = 0, busy = 0;
            DeleteMatches(Brand.LogDir, "*.log", ref deleted, ref busy);
            DeleteMatches(Brand.LogDir, "*.log.1", ref deleted, ref busy);
            DeleteMatches(AppContext.BaseDirectory, "dip-wd-*.log", ref deleted, ref busy);
            return (deleted, busy);
        }

        private static void Purge(string dir, string pattern, DateTime cutoff)
        {
            if (!Directory.Exists(dir))
                return;
            foreach (string file in Directory.EnumerateFiles(dir, pattern))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    // A file the tunnel still has open stays until the next pass.
                }
            }
        }

        private static void DeleteMatches(string dir, string pattern, ref int deleted, ref int busy)
        {
            if (!Directory.Exists(dir))
                return;
            foreach (string file in Directory.EnumerateFiles(dir, pattern))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    busy++;
                }
            }
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
            if (Interlocked.Increment(ref _queued) > MaxQueued)
            {
                Interlocked.Decrement(ref _queued);
                return;
            }
            Queue.Enqueue((fileName, line));
            if (Interlocked.CompareExchange(ref _writer, 1, 0) == 0)
                ThreadPool.UnsafeQueueUserWorkItem(static _ => Drain(), null);
        }

        private static void Drain()
        {
            try
            {
                var batches = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
                while (Queue.TryDequeue(out var item))
                {
                    Interlocked.Decrement(ref _queued);
                    if (!batches.TryGetValue(item.File, out var sb))
                        batches[item.File] = sb = new StringBuilder();
                    sb.Append(item.Line).Append('\n');
                }
                if (batches.Count == 0)
                    return;
                Directory.CreateDirectory(Brand.LogDir);
                foreach (var kv in batches)
                {
                    string path = Path.Combine(Brand.LogDir, kv.Key);
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = path + ".1";
                        if (File.Exists(old))
                            File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, kv.Value.ToString());
                }
            }
            catch
            {
                // Logging must never affect traffic.
            }
            finally
            {
                Interlocked.Exchange(ref _writer, 0);
                if (!Queue.IsEmpty && Interlocked.CompareExchange(ref _writer, 1, 0) == 0)
                    ThreadPool.UnsafeQueueUserWorkItem(static _ => Drain(), null);
            }
        }
    }
}
