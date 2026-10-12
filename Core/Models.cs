using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NetDivert.Core
{
    /// <summary>An application (grouped by executable path), aggregating all of its processes.</summary>
    public sealed class AppEntry
    {
        private readonly HashSet<int> _pids = new();
        private long _bytesIn;
        private long _bytesOut;
        private long _lastActivity;

        public AppEntry(string key, string name, string? path, AppRule rule)
        {
            Key = key;
            Name = name;
            Path = path;
            Rule = rule;
        }

        public string Key { get; }
        public string Name { get; }
        public string? Path { get; }

        /// <summary>Replaced atomically; read on the packet path.</summary>
        public volatile AppRule Rule;

        public Pipe DownPipe { get; } = new();
        public Pipe UpPipe { get; } = new();

        private readonly ConcurrentDictionary<string, byte> _adapterNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _adapterPipeLock = new();
        private readonly Dictionary<string, Pipe> _adapterDown = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Pipe> _adapterUp = new(StringComparer.OrdinalIgnoreCase);

        public void NoteAdapter(string name)
        {
            if (name.Length > 0)
                _adapterNames.TryAdd(name, 0);
        }

        public string AdapterText => string.Join(", ", _adapterNames.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        public Pipe AdapterPipe(string adapterId, bool outbound, long rate, Action<Pipe> register)
        {
            lock (_adapterPipeLock)
            {
                var table = outbound ? _adapterUp : _adapterDown;
                if (!table.TryGetValue(adapterId, out var pipe))
                {
                    pipe = new Pipe { Next = outbound ? UpPipe.Next : DownPipe.Next };
                    table[adapterId] = pipe;
                    register(pipe);
                }
                pipe.SetRate(rate);
                return pipe;
            }
        }

        public long BytesIn => Interlocked.Read(ref _bytesIn);
        public long BytesOut => Interlocked.Read(ref _bytesOut);
        public long LastActivity => Interlocked.Read(ref _lastActivity);

        public long BlockedPackets;

        private long _bytesDiscarded;

        /// <summary>Bytes dropped by the limiter or blocked by the firewall.</summary>
        public long BytesDiscarded => Interlocked.Read(ref _bytesDiscarded);

        public void AddTraffic(int bytes, bool outbound)
        {
            if (outbound)
                Interlocked.Add(ref _bytesOut, bytes);
            else
                Interlocked.Add(ref _bytesIn, bytes);
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
        }

        public void AddDiscarded(int bytes)
        {
            Interlocked.Add(ref _bytesDiscarded, bytes);
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
        }

        public void AddPid(int pid)
        {
            lock (_pids)
                _pids.Add(pid);
        }

        public void RemovePid(int pid)
        {
            lock (_pids)
                _pids.Remove(pid);
        }

        public int[] Pids
        {
            get
            {
                lock (_pids)
                    return _pids.OrderBy(p => p).ToArray();
            }
        }
    }

    public readonly record struct ConnKey(byte Protocol, ushort LocalPort, ushort RemotePort, Ip128 Remote);

    public sealed class ConnectionInfo
    {
        private long _bytesIn;
        private long _bytesOut;
        private long _lastSeen = Environment.TickCount64;

        public ConnectionInfo(ConnKey key, Ip128 local, AppEntry app)
        {
            Key = key;
            Local = local;
            App = app;
        }

        public ConnKey Key { get; }
        public Ip128 Local { get; set; }
        public volatile AppEntry App;
        public DateTime Started { get; } = DateTime.Now;

        /// <summary>True while WinDivert's FLOW layer reports the flow as established.</summary>
        public volatile bool FlowActive;
        public volatile bool FromFlow;
        public volatile bool Logged;
        public long ClosedAt;

        public long BytesIn => Interlocked.Read(ref _bytesIn);
        public long BytesOut => Interlocked.Read(ref _bytesOut);
        public long LastSeen => Interlocked.Read(ref _lastSeen);

        public void AddTraffic(int bytes, bool outbound)
        {
            if (outbound)
                Interlocked.Add(ref _bytesOut, bytes);
            else
                Interlocked.Add(ref _bytesIn, bytes);
            Interlocked.Exchange(ref _lastSeen, Environment.TickCount64);
        }

        public void Touch() => Interlocked.Exchange(ref _lastSeen, Environment.TickCount64);
    }
}
