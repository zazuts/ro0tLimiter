using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NetDivert.Native;

namespace NetDivert.Core
{
    /// <summary>
    /// WinDivert-based engine:
    ///  - NETWORK layer: every non-loopback IP packet is diverted, attributed, counted, blocked or shaped, then reinjected.
    ///  - SOCKET layer (sniff): bind/connect/listen/accept events give us localPort -> PID as soon as sockets appear.
    ///  - FLOW layer (sniff): established/deleted events drive the connection list.
    /// </summary>
    public sealed unsafe class TrafficEngine : IDisposable
    {
        private const int Batch = 64;
        private const int MaxPacket = 0xFFFF;
        private const int MaxConnections = 50_000;

        private readonly RuleStore _rules;
        private readonly PortTable _ports = new();
        private readonly ConcurrentDictionary<ConnKey, ConnectionInfo> _connections = new();
        private readonly object _pipesLock = new();
        private readonly HashSet<Pipe> _pipeSet = new();
        private volatile Pipe[] _pipes = Array.Empty<Pipe>();
        private readonly AutoResetEvent _refreshPorts = new(false);

        private WinDivertHandle? _net;
        private WinDivertHandle? _socket;
        private WinDivertHandle? _flow;
        private readonly List<Thread> _threads = new();
        private volatile bool _running;
        private int _refreshRequested;

        private long _totalIn;
        private long _totalOut;
        private long _packets;
        private long _blocked;
        private long _dropped;

        public TrafficEngine(RuleStore rules)
        {
            _rules = rules;
            Resolver = new ProcessResolver(rules);
            foreach (var app in Resolver.Apps.Values)
                RegisterApp(app);
            Resolver.AppCreated += RegisterApp;

            GlobalDown.SetRate(rules.Data.Global.DownBytesPerSecond);
            GlobalUp.SetRate(rules.Data.Global.UpBytesPerSecond);
            RegisterPipe(GlobalDown);
            RegisterPipe(GlobalUp);
        }

        public ProcessResolver Resolver { get; }
        public Pipe GlobalDown { get; } = new();
        public Pipe GlobalUp { get; } = new();
        public bool IsRunning => _running;

        public long TotalIn => Interlocked.Read(ref _totalIn);
        public long TotalOut => Interlocked.Read(ref _totalOut);
        public long Packets => Interlocked.Read(ref _packets);
        public long Blocked => Interlocked.Read(ref _blocked);
        public long Dropped => Interlocked.Read(ref _dropped);

        public long QueuedBytes
        {
            get
            {
                long sum = 0;
                foreach (var p in _pipes)
                    sum += p.QueuedBytes;
                return sum;
            }
        }

        public ICollection<ConnectionInfo> Connections => _connections.Values;

        public event Action<string>? Error;

        private void RegisterApp(AppEntry app)
        {
            app.DownPipe.Next = GlobalDown;
            app.UpPipe.Next = GlobalUp;
            ApplyRule(app);
            RegisterPipe(app.DownPipe);
            RegisterPipe(app.UpPipe);
        }

        private void RegisterPipe(Pipe pipe)
        {
            lock (_pipesLock)
            {
                if (_pipeSet.Add(pipe))
                    _pipes = new List<Pipe>(_pipeSet).ToArray();
            }
        }

        public void ApplyRule(AppEntry app)
        {
            var rule = app.Rule;
            app.DownPipe.SetRate(rule.DownBytesPerSecond);
            app.UpPipe.SetRate(rule.UpBytesPerSecond);
            foreach (var adapter in rule.Adapters)
            {
                if (adapter.LimitDownEnabled)
                    app.AdapterPipe(adapter.AdapterId, outbound: false, adapter.DownBytesPerSecond, RegisterPipe);
                if (adapter.LimitUpEnabled)
                    app.AdapterPipe(adapter.AdapterId, outbound: true, adapter.UpBytesPerSecond, RegisterPipe);
            }
        }

        public void ApplyGlobalRule()
        {
            var g = _rules.Data.Global;
            GlobalDown.SetRate(g.DownBytesPerSecond);
            GlobalUp.SetRate(g.UpBytesPerSecond);
        }

        public void Start()
        {
            if (_running)
                return;

            _ports.RefreshFromSystem();
            AdapterTable.Refresh();
            PolicyEngine.Reload();
            string filter = PolicyEngine.Current.Policy.AllowLoopback ? "!loopback" : "true";

            try
            {
                _net = WinDivertHandle.Open(filter, WinDivertLayer.Network, 0, WinDivertFlags.None);
                _net.SetParam(WinDivertParam.QueueLength, 16384);
                _net.SetParam(WinDivertParam.QueueTime, 2000);
                _net.SetParam(WinDivertParam.QueueSize, 33554432);

                _socket = WinDivertHandle.Open("true", WinDivertLayer.Socket, 1,
                    WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
                _flow = WinDivertHandle.Open("!loopback", WinDivertLayer.Flow, 1,
                    WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
            }
            catch
            {
                CloseHandles();
                throw;
            }

            _running = true;
            Win32.timeBeginPeriod(1);
            StartThread("ro0tLimiter network", NetworkLoop, ThreadPriority.Highest);
            StartThread("ro0tLimiter socket", () => EventLoop(_socket!), ThreadPriority.AboveNormal);
            StartThread("ro0tLimiter flow", () => EventLoop(_flow!), ThreadPriority.AboveNormal);
            StartThread("Shaper", SchedulerLoop, ThreadPriority.Highest);
            StartThread("Housekeeping", HousekeepingLoop, ThreadPriority.BelowNormal);
        }

        private void StartThread(string name, ThreadStart body, ThreadPriority priority)
        {
            var t = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    if (_running)
                        Error?.Invoke($"{name}: {ex.Message}");
                }
            })
            {
                IsBackground = true,
                Name = name,
                Priority = priority,
            };
            _threads.Add(t);
            t.Start();
        }

        public void Stop()
        {
            if (!_running)
                return;
            _running = false;

            _socket?.Shutdown();
            _flow?.Shutdown();
            if (_net != null)
                WinDivertNative.WinDivertShutdown(_net.Raw, WinDivertNative.ShutdownRecv);
            _refreshPorts.Set();

            foreach (var t in _threads)
                t.Join(2000);
            _threads.Clear();

            FlushAllPipes();
            CloseHandles();
            Win32.timeEndPeriod(1);
        }

        private void CloseHandles()
        {
            _net?.Dispose();
            _socket?.Dispose();
            _flow?.Dispose();
            _net = _socket = _flow = null;
        }

        public void Dispose() => Stop();

        // ------------------------------------------------------------------ NETWORK layer

        private void NetworkLoop()
        {
            var handle = _net!;
            uint bufSize = (uint)(Batch * MaxPacket);
            byte* buffer = (byte*)NativeMemory.Alloc(bufSize);
            var addrs = (WinDivertAddress*)NativeMemory.Alloc((nuint)(Batch * sizeof(WinDivertAddress)));
            try
            {
                while (_running)
                {
                    uint addrLen = (uint)(Batch * sizeof(WinDivertAddress));
                    if (!WinDivertNative.WinDivertRecvEx(handle.Raw, buffer, bufSize, out uint recvLen, 0, addrs, &addrLen, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (!_running || err == 232 /* ERROR_NO_DATA: shutdown */ || err == 6 /* invalid handle */)
                            break;
                        continue;
                    }

                    int count = (int)(addrLen / (uint)sizeof(WinDivertAddress));
                    byte* p = buffer;
                    int remaining = (int)recvLen;
                    for (int i = 0; i < count && remaining > 0; i++)
                    {
                        if (!PacketParser.TryParse(p, remaining, out var meta))
                        {
                            // Unknown framing: reinject the rest untouched rather than dropping traffic.
                            handle.Send(p, remaining, &addrs[i]);
                            break;
                        }
                        HandlePacket(p, ref meta, &addrs[i]);
                        p += meta.Length;
                        remaining -= meta.Length;
                    }
                }
            }
            finally
            {
                NativeMemory.Free(buffer);
                NativeMemory.Free(addrs);
            }
        }

        private void HandlePacket(byte* packet, ref PacketMeta meta, WinDivertAddress* addr)
        {
            bool outbound = addr->Outbound;
            int len = meta.Length;
            Interlocked.Increment(ref _packets);

            AppEntry app;
            ConnectionInfo? conn = null;
            if (meta.HasPorts)
            {
                ushort localPort = outbound ? meta.SrcPort : meta.DstPort;
                ushort remotePort = outbound ? meta.DstPort : meta.SrcPort;
                var key = new ConnKey(meta.Protocol, localPort, remotePort, outbound ? meta.Dst : meta.Src);

                if (_connections.TryGetValue(key, out conn))
                {
                    app = conn.App;
                    if (app == Resolver.Unknown)
                    {
                        int pid = _ports.Get(meta.Protocol, localPort);
                        if (pid != 0)
                            conn.App = app = Resolver.Resolve(pid);
                    }
                }
                else
                {
                    int pid = _ports.Get(meta.Protocol, localPort);
                    if (pid == 0 && Interlocked.Exchange(ref _refreshRequested, 1) == 0)
                        _refreshPorts.Set();
                    app = Resolver.Resolve(pid);
                    if (_connections.Count < MaxConnections)
                    {
                        var local = outbound ? meta.Src : meta.Dst;
                        var owner = app;
                        conn = _connections.GetOrAdd(key, k => new ConnectionInfo(k, local, owner));
                    }
                }
            }
            else
            {
                app = Resolver.Other;
            }

            string adapterName = AdapterTable.NameOf(addr->InterfaceIndex);
            string adapterId = AdapterTable.IdOf(addr->InterfaceIndex);
            app.NoteAdapter(adapterName);

            ushort seenPort = meta.HasPorts ? (outbound ? meta.SrcPort : meta.DstPort) : (ushort)0;
            bool portKnown = meta.HasPorts && _ports.Get(meta.Protocol, seenPort) != 0;
            var rule = app.Rule;
            string? reason = PolicyEngine.Evaluate(packet, len, meta, outbound, addr->Loopback, app.Path, app.Name,
                rule.AllowOut, rule.AllowIn, portKnown);

            AdapterRule? adapterRule = null;
            if (reason == null && adapterId.Length > 0)
            {
                foreach (var candidate in rule.Adapters)
                {
                    if (string.Equals(candidate.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase))
                    {
                        adapterRule = candidate;
                        break;
                    }
                }
                if (adapterRule != null && (outbound ? adapterRule.BlockOut : adapterRule.BlockIn))
                    reason = "adapter " + adapterName;
            }

            if (reason == null && (outbound ? rule.BlockOut : rule.BlockIn))
                reason = outbound ? "block-out" : "block-in";

            if (reason != null)
            {
                app.AddDiscarded(len);
                conn?.Touch();
                Interlocked.Increment(ref app.BlockedPackets);
                Interlocked.Increment(ref _blocked);
                TrafficLog.Drop(reason, outbound, PacketParser.ProtocolName(meta.Protocol), len,
                    Endpoint(outbound ? meta.Src : meta.Dst, meta.HasPorts ? (outbound ? meta.SrcPort : meta.DstPort) : (ushort)0),
                    Endpoint(outbound ? meta.Dst : meta.Src, meta.HasPorts ? (outbound ? meta.DstPort : meta.SrcPort) : (ushort)0),
                    app.Name, app.Path, adapterName, 0);
                return;
            }

            if (conn != null && !conn.Logged)
            {
                conn.Logged = true;
                TrafficLog.Connection(outbound, PacketParser.ProtocolName(meta.Protocol),
                    Endpoint(outbound ? meta.Src : meta.Dst, outbound ? meta.SrcPort : meta.DstPort),
                    Endpoint(outbound ? meta.Dst : meta.Src, outbound ? meta.DstPort : meta.SrcPort),
                    app.Name, app.Path, adapterName, 0);
            }

            bool adapterLimit = adapterRule != null && (outbound ? adapterRule.LimitUpEnabled : adapterRule.LimitDownEnabled);
            Pipe pipe = adapterLimit
                ? app.AdapterPipe(adapterId, outbound, outbound ? adapterRule!.UpBytesPerSecond : adapterRule!.DownBytesPerSecond, RegisterPipe)
                : outbound ? app.UpPipe : app.DownPipe;
            Submit(packet, len, addr, pipe, app, conn);
        }

        private static string Endpoint(Ip128 ip, ushort port) => metaPort(ip, port);

        private static string metaPort(Ip128 ip, ushort port)
        {
            string text = ip.ToString();
            return port == 0 ? text : text.Contains(':') ? $"[{text}]:{port}" : $"{text}:{port}";
        }

        /// <summary>Runs the packet through the pipe chain; traffic is accounted only when actually reinjected.</summary>
        private void Submit(byte* packet, int len, WinDivertAddress* addr, Pipe? pipe, AppEntry app, ConnectionInfo? conn)
        {
            while (pipe != null)
            {
                var verdict = pipe.Offer(packet, len, *addr, app, conn);
                if (verdict == PipeVerdict.Queued)
                    return;
                if (verdict == PipeVerdict.Drop)
                {
                    app.AddDiscarded(len);
                    conn?.Touch();
                    Interlocked.Increment(ref _dropped);
                    return;
                }
                pipe = pipe.Next;
            }

            bool outbound = addr->Outbound;
            if (outbound)
                Interlocked.Add(ref _totalOut, len);
            else
                Interlocked.Add(ref _totalIn, len);
            app.AddTraffic(len, outbound);
            conn?.AddTraffic(len, outbound);
            _net?.Send(packet, len, addr);
        }

        // ------------------------------------------------------------------ shaper

        private void SchedulerLoop()
        {
            var ready = new List<QueuedPacket>(256);
            while (_running)
            {
                foreach (var pipe in _pipes)
                {
                    pipe.Release(ready);
                    if (ready.Count > 0)
                        Forward(ready, pipe.Next);
                }
                Thread.Sleep(1);
            }
        }

        private void Forward(List<QueuedPacket> ready, Pipe? next)
        {
            foreach (var q in ready)
            {
                var addr = q.Address;
                fixed (byte* p = q.Buffer)
                    Submit(p, q.Length, &addr, next, q.App, q.Connection);
                ArrayPool<byte>.Shared.Return(q.Buffer);
            }
            ready.Clear();
        }

        private void FlushAllPipes()
        {
            var ready = new List<QueuedPacket>();
            // App pipes first so their packets still pass through (and get flushed from) the global pipes.
            foreach (var pipe in _pipes)
            {
                if (pipe == GlobalDown || pipe == GlobalUp)
                    continue;
                pipe.Release(ready, all: true);
                Forward(ready, pipe.Next);
            }
            GlobalDown.Release(ready, all: true);
            GlobalUp.Release(ready, all: true);
            Forward(ready, null);
        }

        // ------------------------------------------------------------------ SOCKET / FLOW layers

        private void EventLoop(WinDivertHandle handle)
        {
            WinDivertAddress addr;
            while (_running)
            {
                if (!WinDivertNative.WinDivertRecv(handle.Raw, null, 0, out _, &addr))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (!_running || err == 232 || err == 6)
                        break;
                    continue;
                }

                var data = (WinDivertDataFlow*)addr.Data;
                int pid = (int)data->ProcessId;
                byte proto = data->Protocol;

                switch (addr.Event)
                {
                    case WinDivertEvent.SocketBind:
                    case WinDivertEvent.SocketConnect:
                    case WinDivertEvent.SocketListen:
                    case WinDivertEvent.SocketAccept:
                        _ports.Set(proto, data->LocalPort, pid);
                        break;

                    case WinDivertEvent.FlowEstablished:
                    {
                        _ports.Set(proto, data->LocalPort, pid);
                        var key = new ConnKey(proto, data->LocalPort, data->RemotePort, Ip128.FromDivert(data->RemoteAddr));
                        var app = Resolver.Resolve(pid);
                        var local = Ip128.FromDivert(data->LocalAddr);
                        var conn = _connections.GetOrAdd(key, k => new ConnectionInfo(k, local, app));
                        conn.App = app;
                        conn.Local = local;
                        conn.FromFlow = true;
                        conn.FlowActive = true;
                        break;
                    }

                    case WinDivertEvent.FlowDeleted:
                    {
                        var key = new ConnKey(proto, data->LocalPort, data->RemotePort, Ip128.FromDivert(data->RemoteAddr));
                        if (_connections.TryGetValue(key, out var conn))
                        {
                            conn.FromFlow = true;
                            conn.FlowActive = false;
                            Interlocked.Exchange(ref conn.ClosedAt, Environment.TickCount64);
                        }
                        break;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ housekeeping

        private void HousekeepingLoop()
        {
            long lastPrune = Environment.TickCount64;
            while (_running)
            {
                _refreshPorts.WaitOne(2000);
                if (!_running)
                    break;
                Interlocked.Exchange(ref _refreshRequested, 0);
                _ports.RefreshFromSystem();
                AdapterTable.Refresh();
                PolicyEngine.ReloadIfStale();

                long now = Environment.TickCount64;
                if (now - lastPrune >= 5000)
                {
                    lastPrune = now;
                    PruneConnections(now);
                    Resolver.PruneDeadProcesses();
                }
                Thread.Sleep(250); // rate-limit on-demand refreshes
            }
        }

        private void PruneConnections(long now)
        {
            foreach (var kv in _connections)
            {
                var c = kv.Value;
                bool expired = c.FromFlow
                    ? (c.FlowActive ? now - c.LastSeen > 600_000 : now - Interlocked.Read(ref c.ClosedAt) > 5000)
                    : now - c.LastSeen > 30000;
                if (expired)
                    _connections.TryRemove(kv);
            }
        }
    }
}
