using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NetDivert.Native;

namespace NetDivert.Core
{
    public enum PipeVerdict
    {
        Pass,
        Queued,
        Drop,
    }

    public readonly struct QueuedPacket
    {
        public readonly byte[] Buffer;
        public readonly int Length;
        public readonly WinDivertAddress Address;
        public readonly AppEntry App;
        public readonly ConnectionInfo? Connection;

        public QueuedPacket(byte[] buffer, int length, in WinDivertAddress address, AppEntry app, ConnectionInfo? connection)
        {
            Buffer = buffer;
            Length = length;
            Address = address;
            App = app;
            Connection = connection;
        }
    }

    /// <summary>
    /// Token-bucket shaper with a bounded FIFO. Packets that exceed the rate are delayed; once the
    /// queue is full they are dropped, which is what makes TCP senders back off (same idea as NetLimiter).
    /// </summary>
    public sealed class Pipe
    {
        private readonly object _lock = new();
        private readonly Queue<QueuedPacket> _queue = new();
        private long _rate;
        private double _tokens;
        private double _burst;
        private long _maxQueue;
        private long _queuedBytes;
        private long _lastTicks = Stopwatch.GetTimestamp();
        private long _dropped;

        public Pipe? Next { get; set; }

        public long Rate => Volatile.Read(ref _rate);
        public long QueuedBytes => Volatile.Read(ref _queuedBytes);
        public long Dropped => Volatile.Read(ref _dropped);

        /// <param name="bytesPerSecond">0 disables the limit.</param>
        public void SetRate(long bytesPerSecond)
        {
            lock (_lock)
            {
                _rate = Math.Max(0, bytesPerSecond);
                _burst = Math.Max(_rate * 0.05, 3000);
                _maxQueue = Math.Max(_rate / 2, 64 * 1024);
                _tokens = Math.Min(_tokens, _burst);
                _lastTicks = Stopwatch.GetTimestamp();
            }
        }

        private void Refill()
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;
            _tokens = Math.Min(_burst, _tokens + dt * _rate);
        }

        public unsafe PipeVerdict Offer(byte* packet, int length, in WinDivertAddress addr, AppEntry app, ConnectionInfo? connection)
        {
            if (Volatile.Read(ref _rate) == 0)
                return PipeVerdict.Pass;

            lock (_lock)
            {
                if (_rate == 0)
                    return PipeVerdict.Pass;

                Refill();
                if (_queue.Count == 0 && _tokens >= length)
                {
                    _tokens -= length;
                    return PipeVerdict.Pass;
                }
                if (_queuedBytes + length > _maxQueue)
                {
                    _dropped++;
                    return PipeVerdict.Drop;
                }

                byte[] buf = ArrayPool<byte>.Shared.Rent(length);
                new ReadOnlySpan<byte>(packet, length).CopyTo(buf);
                _queue.Enqueue(new QueuedPacket(buf, length, addr, app, connection));
                _queuedBytes += length;
                return PipeVerdict.Queued;
            }
        }

        /// <summary>Moves packets whose tokens are available into <paramref name="output"/>.</summary>
        public void Release(List<QueuedPacket> output, bool all = false)
        {
            if (Volatile.Read(ref _queuedBytes) == 0)
                return;

            lock (_lock)
            {
                if (_rate == 0 || all)
                {
                    while (_queue.Count > 0)
                        output.Add(_queue.Dequeue());
                    _queuedBytes = 0;
                    return;
                }

                Refill();
                while (_queue.Count > 0)
                {
                    int len = _queue.Peek().Length;
                    // Packets larger than the burst (e.g. reassembled datagrams) go out once the bucket is full.
                    if (_tokens < len && _tokens < _burst)
                        break;
                    _tokens -= len;
                    _queuedBytes -= len;
                    output.Add(_queue.Dequeue());
                }
            }
        }
    }
}
