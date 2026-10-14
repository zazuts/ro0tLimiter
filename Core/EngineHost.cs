using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NetDivert.Core
{
    public sealed class AppSnapshot
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Path { get; set; }
        public string Adapters { get; set; } = "";
        public int[] Pids { get; set; } = Array.Empty<int>();
        public long BytesIn { get; set; }
        public long BytesOut { get; set; }
        public long Discarded { get; set; }
        public long IdleMs { get; set; }
        public int Connections { get; set; }
        public AppRule Rule { get; set; } = new();
        public bool Ruled { get; set; }
    }

    public sealed class ConnSnapshot
    {
        public string Id { get; set; } = "";
        public byte Protocol { get; set; }
        public string LocalIp { get; set; } = "";
        public ushort LocalPort { get; set; }
        public string RemoteIp { get; set; } = "";
        public ushort RemotePort { get; set; }
        public long BytesIn { get; set; }
        public long BytesOut { get; set; }
        public string State { get; set; } = "";
        public DateTime Started { get; set; }
    }

    public sealed class EngineSnapshot
    {
        public bool Running { get; set; }
        public string? Error { get; set; }
        public long TotalIn { get; set; }
        public long TotalOut { get; set; }
        public long Packets { get; set; }
        public long Blocked { get; set; }
        public long Dropped { get; set; }
        public long Queued { get; set; }
        public AppRule Global { get; set; } = new();
        public List<AppSnapshot> Apps { get; set; } = new();
        public List<ConnSnapshot> Connections { get; set; } = new();
        public List<ListenerSnapshot>? Listeners { get; set; }
    }

    /// <summary>What the UI talks to: either an in-process engine or the Windows service.</summary>
    public interface IEngineHost : IDisposable
    {
        bool IsService { get; }
        EngineSnapshot GetSnapshot(string? connectionsForKey);
        void SetRule(AppRule rule);
        void SetGlobal(AppRule rule);
        void Start();
        void Stop();
    }

    /// <summary>Runs the engine in the current process. Used by the UI (local mode) and by the service.</summary>
    public sealed class LocalEngineHost : IEngineHost
    {
        private readonly object _lock = new();
        private readonly RuleStore _rules;
        private string? _error;
        private List<ListenerSnapshot>? _listeners;
        private long _listenersAt;

        public LocalEngineHost()
        {
            _rules = new RuleStore();
            _rules.Load();
            Engine = new TrafficEngine(_rules);
            Engine.Error += msg => _error = msg;
        }

        public TrafficEngine Engine { get; }
        public bool IsService => false;

        public void Start()
        {
            lock (_lock)
            {
                try
                {
                    Engine.Start();
                    _error = null;
                }
                catch (Exception ex)
                {
                    _error = ex.Message;
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
                Engine.Stop();
        }

        public void SetRule(AppRule rule)
        {
            lock (_lock)
            {
                Engine.Resolver.Apps.TryGetValue(rule.Key, out var app);
                var target = app?.Rule ?? _rules.GetOrCreate(rule.Key);
                target.CopyFrom(rule);
                if (app != null)
                    Engine.ApplyRule(app);
                _rules.Commit(target);
            }
        }

        public void SetGlobal(AppRule rule)
        {
            lock (_lock)
            {
                _rules.Data.Global.CopyFrom(rule);
                Engine.ApplyGlobalRule();
                _rules.Save();
            }
        }

        private List<ListenerSnapshot>? TakeListeners(long now)
        {
            if (_listeners != null && now - _listenersAt < 8000)
                return null;
            _listeners = ListenerQuery.Query();
            _listenersAt = now;
            return _listeners;
        }

        public EngineSnapshot GetSnapshot(string? connectionsForKey)
        {
            long now = Environment.TickCount64;
            var connCounts = new Dictionary<AppEntry, int>();
            var selected = new List<ConnectionInfo>();
            foreach (var c in Engine.Connections)
            {
                bool open = c.FromFlow ? c.FlowActive : now - c.LastSeen < 30_000;
                if (open)
                {
                    connCounts.TryGetValue(c.App, out int n);
                    connCounts[c.App] = n + 1;
                }
                if (open && selected.Count < 400 && connectionsForKey != null
                    && string.Equals(c.App.Key, connectionsForKey, StringComparison.OrdinalIgnoreCase))
                    selected.Add(c);
            }

            var snap = new EngineSnapshot
            {
                Running = Engine.IsRunning,
                Error = _error,
                TotalIn = Engine.TotalIn,
                TotalOut = Engine.TotalOut,
                Packets = Engine.Packets,
                Blocked = Engine.Blocked,
                Dropped = Engine.Dropped,
                Queued = Engine.QueuedBytes,
                Global = _rules.Data.Global.Clone(),
                Listeners = TakeListeners(now),
            };

            foreach (var app in Engine.Resolver.Apps.Values)
            {
                long last = app.LastActivity;
                snap.Apps.Add(new AppSnapshot
                {
                    Key = app.Key,
                    Name = app.Name,
                    Path = app.Path,
                    Adapters = app.AdapterText,
                    Pids = app.Pids,
                    BytesIn = app.BytesIn,
                    BytesOut = app.BytesOut,
                    Discarded = app.BytesDiscarded,
                    IdleMs = last == 0 ? long.MaxValue : now - last,
                    Connections = connCounts.TryGetValue(app, out int n) ? n : 0,
                    Rule = app.Rule.Clone(),
                    Ruled = app.Ruled,
                });
            }

            foreach (var c in selected)
            {
                var k = c.Key;
                snap.Connections.Add(new ConnSnapshot
                {
                    Id = $"{k.Protocol}|{k.LocalPort}|{k.RemotePort}|{k.Remote.Hi:x}|{k.Remote.Lo:x}",
                    Protocol = k.Protocol,
                    LocalIp = c.Local.ToString(),
                    LocalPort = k.LocalPort,
                    RemoteIp = k.Remote.ToString(),
                    RemotePort = k.RemotePort,
                    BytesIn = c.BytesIn,
                    BytesOut = c.BytesOut,
                    State = c.FromFlow ? (c.FlowActive ? "Ativa" : "Encerrada") : "Observada",
                    Started = c.Started,
                });
            }
            return snap;
        }

        public void Dispose()
        {
            Engine.Dispose();
            _rules.Save();
        }
    }
}
