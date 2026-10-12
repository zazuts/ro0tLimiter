using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace NetDivert.Core
{
    public sealed class AdapterInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public uint Ipv4Index { get; init; }
        public uint Ipv6Index { get; init; }
    }

    /// <summary>Maps a packet interface index to a friendly adapter name. Refreshed off the packet path.</summary>
    public static class AdapterTable
    {
        private sealed class Map
        {
            public static readonly Map Empty = new(Array.Empty<AdapterInfo>());
            private readonly Dictionary<uint, AdapterInfo> _byIndex = new();
            public IReadOnlyList<AdapterInfo> All { get; }

            public Map(IReadOnlyList<AdapterInfo> all)
            {
                All = all;
                foreach (var a in all)
                {
                    if (a.Ipv4Index != 0)
                        _byIndex[a.Ipv4Index] = a;
                    if (a.Ipv6Index != 0)
                        _byIndex[a.Ipv6Index] = a;
                }
            }

            public bool TryGet(uint index, out AdapterInfo info) => _byIndex.TryGetValue(index, out info!);
        }

        private static volatile Map _map = Map.Empty;

        public static IReadOnlyList<AdapterInfo> All => _map.All;

        public static void Refresh()
        {
            var list = new List<AdapterInfo>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    uint v4 = 0, v6 = 0;
                    try { v4 = (uint)ni.GetIPProperties().GetIPv4Properties().Index; } catch { }
                    try { v6 = (uint)ni.GetIPProperties().GetIPv6Properties().Index; } catch { }
                    list.Add(new AdapterInfo { Id = ni.Id, Name = ni.Name, Ipv4Index = v4, Ipv6Index = v6 });
                }
            }
            catch
            {
                return;
            }
            _map = new Map(list);
        }

        public static string NameOf(uint ifIndex)
        {
            if (ifIndex == 0)
                return "";
            return _map.TryGet(ifIndex, out var info) ? info.Name : $"if{ifIndex}";
        }

        public static string IdOf(uint ifIndex)
        {
            if (ifIndex == 0)
                return "";
            return _map.TryGet(ifIndex, out var info) ? info.Id : $"if{ifIndex}";
        }
    }
}
