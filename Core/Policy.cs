using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace NetDivert.Core
{
    public sealed class SecurityPolicy
    {
        public bool BlockAllOutbound { get; set; }
        public bool BlockAllInbound { get; set; }

        /// <summary>0 = allow everything, 1 = block everything not allowed, 2 = ask for each program.</summary>
        public int NetMode { get; set; }
        public bool AllowLoopback { get; set; } = true;
        public bool Allow6to4 { get; set; } = true;
        public bool Stealth { get; set; }
        public bool FiltersOnStart { get; set; } = true;
        public bool VerifySignature { get; set; }
        public bool ComputeHash { get; set; }
        public bool ResolveAddresses { get; set; } = true;
        public bool IgnoreUacNoise { get; set; } = true;

        /// <summary>0 = off, 1 = always allow, 2 = block.</summary>
        public int Telemetry { get; set; }
        public int Updates { get; set; }
        public int MicrosoftApps { get; set; }

        public string BlockedDomains { get; set; } = "";
        public string AllowedDomains { get; set; } = "";
        public string BlockedIps { get; set; } = "";
        public string BlockedPorts { get; set; } = "";
        public string BlockedExes { get; set; } = "";
        public string BlockedLocalPorts { get; set; } = "";
        public string BlockedLocalIps { get; set; } = "";
        public bool AllowOneDrive { get; set; }
        public string RegexRules { get; set; } = "";
        public string ListUrls { get; set; } = "";

        public bool LogDrops { get; set; }
        public bool LogConnections { get; set; }
        public bool LogDebug { get; set; }

        /// <summary>0 off, 1 day, 2 week, 3 month, 4 half year, 5 year.</summary>
        public int LogRotate { get; set; }

        /// <summary>0 = keep a small config file, 1 = delete everything.</summary>
        public int UninstallMode { get; set; }
    }

    public static class PolicyStore
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        public static string FilePath => Path.Combine(Brand.DataDir, "policy.json");

        public static SecurityPolicy Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<SecurityPolicy>(File.ReadAllText(FilePath)) ?? new SecurityPolicy();
                    if (loaded.NetMode == 0 && (loaded.BlockAllOutbound || loaded.BlockAllInbound))
                        loaded.NetMode = 1;
                    return loaded;
                }
            }
            catch
            {
                // Fall through to defaults.
            }
            return new SecurityPolicy();
        }

        public static void Save(SecurityPolicy policy)
        {
            Directory.CreateDirectory(Brand.DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(policy, Json));
            File.Move(tmp, FilePath, overwrite: true);
            PolicyEngine.Reload();
        }
    }

    /// <summary>Immutable view of the policy used on the packet path.</summary>
    public sealed class PolicySnapshot
    {
        public static readonly PolicySnapshot Empty = new(new SecurityPolicy(), Array.Empty<string>());

        private readonly HashSet<string> _blockDomains;
        private readonly HashSet<string> _allowDomains;
        private readonly HashSet<int> _ports;
        private readonly HashSet<int> _localPorts;
        private readonly HashSet<string> _localIps = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(uint network, uint mask)> _localCidrs = new();
        private readonly HashSet<string> _exes;
        private readonly List<(uint network, uint mask)> _cidrs = new();
        private readonly HashSet<string> _ips = new(StringComparer.OrdinalIgnoreCase);
        private readonly Regex[] _blockRegex;
        private readonly Regex[] _allowRegex;

        public SecurityPolicy Policy { get; }
        public bool LogDrops => Policy.LogDrops;
        public bool LogConnections => Policy.LogConnections;
        public bool LogDebug => Policy.LogDebug;

        public PolicySnapshot(SecurityPolicy policy, IEnumerable<string> downloadedDomains)
        {
            Policy = policy;
            _blockDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _allowDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddDomains(_allowDomains, policy.AllowedDomains);
            AddDomains(_blockDomains, policy.BlockedDomains);
            AddDomains(_blockDomains, downloadedDomains);
            AddGroup(policy.Telemetry, BuiltInLists.Telemetry);
            AddGroup(policy.Updates, BuiltInLists.Updates);
            AddGroup(policy.MicrosoftApps, BuiltInLists.MicrosoftApps);
            if (policy.AllowOneDrive)
                foreach (var d in BuiltInLists.OneDrive)
                    _allowDomains.Add(d);

            _ports = new HashSet<int>();
            foreach (var line in Split(policy.BlockedPorts))
                if (int.TryParse(line, out int port) && port is > 0 and <= 65535)
                    _ports.Add(port);
            _localPorts = new HashSet<int>();
            foreach (var line in Split(policy.BlockedLocalPorts))
                if (int.TryParse(line, out int local) && local is > 0 and <= 65535)
                    _localPorts.Add(local);
            ParseAddressList(policy.BlockedLocalIps, _localIps, _localCidrs);

            _exes = new HashSet<string>(Split(policy.BlockedExes), StringComparer.OrdinalIgnoreCase);
            ParseAddressList(policy.BlockedIps, _ips, _cidrs);

            var block = new List<Regex>();
            var allow = new List<Regex>();
            foreach (var line in Split(policy.RegexRules))
            {
                bool isAllow = line.StartsWith('!');
                string body = isAllow ? line[1..] : line;
                if (body.Length == 0)
                    continue;
                try
                {
                    var rx = new Regex(body, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                    (isAllow ? allow : block).Add(rx);
                }
                catch (Exception ex)
                {
                    TrafficLog.Debug("regex ignorada: " + ex.Message);
                }
            }
            _blockRegex = block.ToArray();
            _allowRegex = allow.ToArray();
        }

        private void AddGroup(int mode, string[] domains)
        {
            if (mode == 2)
                foreach (var d in domains)
                    _blockDomains.Add(d);
            else if (mode == 1)
                foreach (var d in domains)
                    _allowDomains.Add(d);
        }

        public string? Reason(string? domain, string ip, int remotePort, string localIp, int localPort, string? exe, string appName)
        {
            if (domain != null && IsListed(_allowDomains, domain))
                return null;
            if (Matches(_allowRegex, domain, ip, exe, appName))
                return null;

            if (domain != null && IsListed(_blockDomains, domain))
                return "domain " + domain;
            if (_ips.Contains(ip) || InCidr(ip))
                return "ip " + ip;
            if (remotePort != 0 && _ports.Contains(remotePort))
                return "port " + remotePort;
            if (localPort != 0 && _localPorts.Contains(localPort))
                return "local-port " + localPort;
            if (_localIps.Contains(localIp) || InLocalCidr(localIp))
                return "local-ip " + localIp;
            if (exe != null && ExeBlocked(exe))
                return "exe " + Path.GetFileName(exe);
            if (Matches(_blockRegex, domain, ip, exe, appName))
                return "regex";
            return null;
        }

        private bool ExeBlocked(string exe)
        {
            string name = Path.GetFileName(exe);
            foreach (var item in _exes)
                if (string.Equals(item, exe, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool IsListed(HashSet<string> set, string domain)
        {
            if (set.Contains(domain))
                return true;
            int dot = domain.IndexOf('.');
            while (dot > 0 && dot < domain.Length - 1)
            {
                if (set.Contains(domain[(dot + 1)..]))
                    return true;
                dot = domain.IndexOf('.', dot + 1);
            }
            return false;
        }

        private static bool Matches(Regex[] list, string? domain, string ip, string? exe, string appName)
        {
            if (list.Length == 0)
                return false;
            string blob = $"{domain} {ip} {exe} {appName}";
            foreach (var rx in list)
            {
                try
                {
                    if (rx.IsMatch(blob))
                        return true;
                }
                catch (RegexMatchTimeoutException)
                {
                    // A slow pattern is skipped for this packet.
                }
            }
            return false;
        }

        private static void ParseAddressList(string text, HashSet<string> ips, List<(uint network, uint mask)> cidrs)
        {
            foreach (var line in Split(text))
            {
                int slash = line.IndexOf('/');
                if (slash < 0)
                {
                    ips.Add(line);
                    continue;
                }
                if (IPAddress.TryParse(line[..slash], out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && int.TryParse(line[(slash + 1)..], out int bits) && bits is >= 0 and <= 32)
                {
                    uint ip = ToUInt(addr);
                    uint mask = bits == 0 ? 0 : 0xFFFFFFFFu << (32 - bits);
                    cidrs.Add((ip & mask, mask));
                }
            }
        }

        private bool InCidr(string ip) => InCidr(ip, _cidrs);
        private bool InLocalCidr(string ip) => InCidr(ip, _localCidrs);

        private static bool InCidr(string ip, List<(uint network, uint mask)> cidrs)
        {
            if (cidrs.Count == 0 || !IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return false;
            uint value = ToUInt(addr);
            foreach (var (network, mask) in cidrs)
                if ((value & mask) == network)
                    return true;
            return false;
        }

        private static uint ToUInt(IPAddress addr)
        {
            var b = addr.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static void AddDomains(HashSet<string> set, string text)
        {
            foreach (var line in Split(text))
                set.Add(line.TrimStart('.'));
        }

        private static void AddDomains(HashSet<string> set, IEnumerable<string> domains)
        {
            foreach (var d in domains)
                if (d.Length > 0)
                    set.Add(d);
        }

        private static IEnumerable<string> Split(string text) =>
            text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static class BuiltInLists
    {
        public static readonly string[] Telemetry =
        {
            "vortex.data.microsoft.com", "vortex-win.data.microsoft.com", "telemetry.microsoft.com",
            "watson.telemetry.microsoft.com", "oca.telemetry.microsoft.com", "sqm.telemetry.microsoft.com",
            "telecommand.telemetry.microsoft.com", "watson.microsoft.com", "settings-win.data.microsoft.com",
            "events.data.microsoft.com", "telemetry.urs.microsoft.com", "data.microsoft.com",
        };

        public static readonly string[] Updates =
        {
            "windowsupdate.com", "update.microsoft.com", "download.microsoft.com", "wustat.windows.com",
        };

        public static readonly string[] MicrosoftApps =
        {
            "teams.microsoft.com", "teams.live.com", "teams.office.com", "teams.cloud.microsoft",
            "bing.com", "outlook.office.com", "outlook.office365.com", "officeapps.live.com",
            "onedrive.live.com", "onedrive.com", "storage.live.com",
        };

        public static readonly string[] OneDrive =
        {
            "onedrive.live.com", "onedrive.com", "storage.live.com",
        };
    }

    public static unsafe class PolicyEngine
    {
        private static readonly ConcurrentDictionary<string, string> IpToDomain = new(StringComparer.OrdinalIgnoreCase);
        private static PolicySnapshot _current = PolicySnapshot.Empty;
        private static DateTime _policyWriteUtc;
        private static int _downloading;

        public static PolicySnapshot Current => _current;

        public static void Reload()
        {
            var policy = PolicyStore.Load();
            _current = new PolicySnapshot(policy, ReadCachedLists(policy));
            try { _policyWriteUtc = File.GetLastWriteTimeUtc(PolicyStore.FilePath); }
            catch { _policyWriteUtc = DateTime.UtcNow; }
            RefreshListsInBackground(policy);
        }

        public static void ReloadIfStale()
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(PolicyStore.FilePath); }
            catch { return; }
            if (stamp != _policyWriteUtc)
                Reload();
        }

        /// <summary>Returns a block reason, or null to let the packet through this layer.</summary>
        public static string? Evaluate(byte* packet, int length, in PacketMeta meta, bool outbound, bool loopback,
            string? exe, string appName, bool allowOut, bool allowIn, bool localPortKnown)
        {
            var snap = _current;
            var policy = snap.Policy;

            if (loopback)
                return policy.AllowLoopback ? null : "loopback";
            if (meta.Protocol == 41 && !policy.Allow6to4)
                return "6to4";

            string ip = (outbound ? meta.Dst : meta.Src).ToString();
            string localIp = (outbound ? meta.Src : meta.Dst).ToString();
            int remotePort = meta.HasPorts ? (outbound ? meta.DstPort : meta.SrcPort) : 0;
            int localPort = meta.HasPorts ? (outbound ? meta.SrcPort : meta.DstPort) : 0;
            string? domain = null;

            if (meta.Protocol == PacketParser.Udp && meta.HasPorts && (meta.DstPort == 53 || meta.SrcPort == 53))
                domain = InspectDns(packet, length);

            if (domain == null && outbound && meta.Protocol == PacketParser.Tcp && meta.DstPort == 443)
                domain = InspectSni(packet, length);

            if (domain == null && IpToDomain.TryGetValue(ip, out var learned))
                domain = learned;

            string? reason = snap.Reason(domain, ip, remotePort, localIp, localPort, exe, appName);
            if (reason != null)
                return reason;

            if (policy.Stealth && !outbound && meta.HasPorts && !localPortKnown)
                return "stealth";

            if (outbound && policy.BlockAllOutbound && !allowOut)
                return "block-all-out";
            if (!outbound && policy.BlockAllInbound && !allowIn)
                return "block-all-in";
            return null;
        }

        private static string? InspectDns(byte* packet, int length)
        {
            if (!TryPayload(packet, length, udp: true, out var payload, out int payloadLen) || payloadLen < 12)
                return null;
            int offset = 12;
            if (!TryReadName(payload, payloadLen, ref offset, out string qname) || qname.Length == 0)
                return null;

            bool response = (payload[2] & 0x80) != 0;
            if (response)
                LearnAnswers(payload, payloadLen, offset, qname);
            return qname;
        }

        private static void LearnAnswers(byte* dns, int len, int offset, string qname)
        {
            ushort questions = Read16(dns + 4);
            ushort answers = Read16(dns + 6);
            for (int i = 1; i < questions && offset < len; i++)
            {
                if (!TryReadName(dns, len, ref offset, out _))
                    return;
                offset += 4;
            }
            for (int i = 0; i < answers && offset + 12 <= len; i++)
            {
                if (!TryReadName(dns, len, ref offset, out _))
                    return;
                if (offset + 10 > len)
                    return;
                ushort type = Read16(dns + offset);
                ushort rdlen = Read16(dns + offset + 8);
                offset += 10;
                if (offset + rdlen > len)
                    return;
                if (type == 1 && rdlen == 4)
                {
                    var ip = new IPAddress(new ReadOnlySpan<byte>(dns + offset, 4));
                    Remember(ip.ToString(), qname);
                }
                else if (type == 28 && rdlen == 16)
                {
                    var ip = new IPAddress(new ReadOnlySpan<byte>(dns + offset, 16).ToArray());
                    Remember(ip.ToString(), qname);
                }
                offset += rdlen;
            }
        }

        private static string? InspectSni(byte* packet, int length)
        {
            if (!TryPayload(packet, length, udp: false, out var p, out int len) || len < 5)
                return null;
            if (p[0] != 22)
                return null;
            int recLen = Read16(p + 3);
            if (recLen <= 0 || 5 + recLen > len || p[5] != 1)
                return null;
            int hs = Read24(p + 6);
            int o = 9;
            if (o + 34 > 5 + 4 + hs)
                return null;
            o += 34; // version + random
            if (o >= len)
                return null;
            o += 1 + p[o]; // session id
            if (o + 2 > len)
                return null;
            o += 2 + Read16(p + o); // cipher suites
            if (o + 1 > len)
                return null;
            o += 1 + p[o]; // compression
            if (o + 2 > len)
                return null;
            int extEnd = o + 2 + Read16(p + o);
            o += 2;
            while (o + 4 <= len && o + 4 <= extEnd)
            {
                ushort type = Read16(p + o);
                int elen = Read16(p + o + 2);
                o += 4;
                if (o + elen > len)
                    return null;
                if (type == 0 && elen >= 5)
                {
                    int nameLen = Read16(p + o + 3);
                    if (p[o + 2] == 0 && o + 5 + nameLen <= o + elen)
                        return Encoding.ASCII.GetString(p + o + 5, nameLen);
                }
                o += elen;
            }
            return null;
        }

        private static bool TryPayload(byte* packet, int length, bool udp, out byte* payload, out int payloadLen)
        {
            payload = null;
            payloadLen = 0;
            if (length < 1)
                return false;
            int off;
            int version = packet[0] >> 4;
            if (version == 4)
            {
                off = (packet[0] & 0x0F) * 4;
            }
            else if (version == 6)
            {
                off = 40;
                byte next = packet[6];
                while (next is 0 or 43 or 60 or 44)
                {
                    if (off + 2 > length)
                        return false;
                    next = packet[off];
                    off += (packet[off + 1] + 1) * 8;
                }
            }
            else
            {
                return false;
            }
            if (off + (udp ? 8 : 20) > length)
                return false;
            int header = udp ? 8 : ((packet[off + 12] >> 4) & 0x0F) * 4;
            if (header < (udp ? 8 : 20) || off + header > length)
                return false;
            payload = packet + off + header;
            payloadLen = length - off - header;
            return payloadLen > 0;
        }

        private static bool TryReadName(byte* dns, int len, ref int offset, out string name)
        {
            name = "";
            var sb = new StringBuilder();
            int jumps = 0;
            int cursor = offset;
            bool advanced = false;
            while (cursor < len && jumps < 8)
            {
                int lab = dns[cursor];
                if (lab == 0)
                {
                    if (!advanced)
                        offset = cursor + 1;
                    name = sb.ToString().TrimEnd('.');
                    return true;
                }
                if ((lab & 0xC0) == 0xC0)
                {
                    if (cursor + 1 >= len)
                        return false;
                    if (!advanced)
                        offset = cursor + 2;
                    advanced = true;
                    cursor = ((lab & 0x3F) << 8) | dns[cursor + 1];
                    jumps++;
                    continue;
                }
                cursor++;
                if (cursor + lab > len)
                    return false;
                if (sb.Length > 0)
                    sb.Append('.');
                sb.Append(Encoding.ASCII.GetString(dns + cursor, lab));
                cursor += lab;
                if (!advanced)
                    offset = cursor;
            }
            return false;
        }

        private static ushort Read16(byte* p) => (ushort)((p[0] << 8) | p[1]);
        private static int Read24(byte* p) => (p[0] << 16) | (p[1] << 8) | p[2];

        private static void Remember(string ip, string domain)
        {
            if (IpToDomain.Count > 20000)
                IpToDomain.Clear();
            IpToDomain[ip] = domain;
        }

        private static string CachePath(string url) =>
            Path.Combine(Brand.ListCacheDir, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16] + ".txt");

        private static IEnumerable<string> ReadCachedLists(SecurityPolicy policy)
        {
            var domains = new List<string>();
            foreach (var url in SplitUrls(policy))
            {
                string cache = CachePath(url);
                if (!File.Exists(cache))
                    continue;
                foreach (var line in File.ReadLines(cache))
                {
                    string domain = ParseListLine(line);
                    if (domain.Length > 0)
                        domains.Add(domain);
                }
            }
            return domains;
        }

        private static void RefreshListsInBackground(SecurityPolicy policy)
        {
            var urls = SplitUrls(policy);
            if (urls.Length == 0 || Interlocked.Exchange(ref _downloading, 1) == 1)
                return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool any = false;
                    Directory.CreateDirectory(Brand.ListCacheDir);
                    foreach (var url in urls)
                    {
                        string cache = CachePath(url);
                        bool fresh = File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromHours(12);
                        if (fresh)
                            continue;
                        TryDownload(url, cache);
                        any = true;
                    }
                    if (any)
                    {
                        var latest = PolicyStore.Load();
                        _current = new PolicySnapshot(latest, ReadCachedLists(latest));
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _downloading, 0);
                }
            });
        }

        private static string[] SplitUrls(SecurityPolicy policy) =>
            policy.ListUrls.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static void TryDownload(string url, string cache)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                string body = client.GetStringAsync(url).GetAwaiter().GetResult();
                File.WriteAllText(cache, body);
                TrafficLog.Debug("lista baixada: " + url);
            }
            catch (Exception ex)
            {
                TrafficLog.Debug("falha ao baixar lista " + url + ": " + ex.Message);
            }
        }

        private static string ParseListLine(string line)
        {
            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#') || t.StartsWith('!'))
                return "";
            if (t.StartsWith("||", StringComparison.Ordinal) && t.EndsWith('^'))
                return t[2..^1];
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && (parts[0] == "0.0.0.0" || parts[0] == "127.0.0.1"))
                return parts[1];
            if (parts.Length == 1 && t.Contains('.') && !t.Contains('/'))
                return t.TrimStart('*', '.');
            return "";
        }
    }
}
