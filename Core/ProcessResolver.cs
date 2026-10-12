using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using NetDivert.Native;

namespace NetDivert.Core
{
    /// <summary>Maps PIDs to <see cref="AppEntry"/> instances, grouping processes by executable path.</summary>
    public sealed class ProcessResolver
    {
        public const string UnknownKey = "?unknown";
        public const string OtherKey = "?other";

        private readonly ConcurrentDictionary<int, AppEntry> _byPid = new();
        private readonly ConcurrentDictionary<string, AppEntry> _apps = new(StringComparer.OrdinalIgnoreCase);
        private readonly RuleStore _rules;

        public event Action<AppEntry>? AppCreated;

        public ProcessResolver(RuleStore rules)
        {
            _rules = rules;
            Unknown = GetOrCreateApp(UnknownKey, "Desconhecido", null);
            Other = GetOrCreateApp(OtherKey, "Sistema (ICMP / outros)", null);
        }

        /// <summary>Traffic whose local port has no known owner.</summary>
        public AppEntry Unknown { get; }

        /// <summary>Traffic without ports (ICMP, GRE, fragments...).</summary>
        public AppEntry Other { get; }

        public ConcurrentDictionary<string, AppEntry> Apps => _apps;

        public AppEntry Resolve(int pid)
        {
            if (pid <= 0)
                return Unknown;
            if (_byPid.TryGetValue(pid, out var app))
                return app;

            string key, name;
            string? path = null;
            if (pid == 4)
            {
                key = "?system";
                name = "System";
            }
            else
            {
                path = Win32.GetProcessPath(pid);
                if (path != null)
                {
                    key = path;
                    name = DescribeExecutable(path);
                }
                else
                {
                    string procName;
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        procName = p.ProcessName;
                    }
                    catch
                    {
                        procName = $"PID {pid}";
                    }
                    key = "?name:" + procName;
                    name = procName;
                }
            }

            app = GetOrCreateApp(key, name, path);
            app.AddPid(pid);
            return _byPid.GetOrAdd(pid, app);
        }

        private AppEntry GetOrCreateApp(string key, string name, string? path)
        {
            bool created = false;
            var app = _apps.GetOrAdd(key, k =>
            {
                created = true;
                return new AppEntry(k, name, path, _rules.GetOrCreate(k));
            });
            if (created)
                AppCreated?.Invoke(app);
            return app;
        }

        private static string DescribeExecutable(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                    return info.FileDescription.Trim();
            }
            catch
            {
                // Fall back to the file name.
            }
            return Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>Forgets PIDs of processes that have exited.</summary>
        public void PruneDeadProcesses()
        {
            foreach (var kv in _byPid)
            {
                if (!Win32.IsProcessAlive(kv.Key) && _byPid.TryRemove(kv.Key, out var app))
                    app.RemovePid(kv.Key);
            }
        }
    }
}
