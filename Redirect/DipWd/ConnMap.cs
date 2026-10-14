using System.Collections.Concurrent;
using System.Net;

namespace DipWd;

sealed class ConnEntry
{
    public required IPAddress OrigSrc { get; init; }
    public required int OrigSrcPort { get; init; }
    public required IPAddress OrigDst { get; init; }
    public required int OrigDstPort { get; init; }
    public uint Pid { get; init; }
    public uint IfIdx { get; set; }
    public uint SubIfIdx { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Maps redirected connections by client source port (on loopback after NAT).
/// Key: origSrcPort (preserved across rewrite to 127.0.0.1).
/// </summary>
sealed class ConnMap
{
    readonly ConcurrentDictionary<int, ConnEntry> _byClientPort = new();

    public void Upsert(ConnEntry e) => _byClientPort[e.OrigSrcPort] = e;

    public bool TryGetByClientPort(int clientPort, out ConnEntry entry) =>
        _byClientPort.TryGetValue(clientPort, out entry!);

    public void Touch(int clientPort)
    {
        if (_byClientPort.TryGetValue(clientPort, out var e))
            e.LastSeen = DateTime.UtcNow;
    }

    public void Remove(int clientPort) => _byClientPort.TryRemove(clientPort, out _);

    public void PurgeOld(TimeSpan maxAge)
    {
        var cut = DateTime.UtcNow - maxAge;
        foreach (var kv in _byClientPort)
        {
            if (kv.Value.LastSeen < cut)
                _byClientPort.TryRemove(kv.Key, out _);
        }
    }

    public int Count => _byClientPort.Count;
}
