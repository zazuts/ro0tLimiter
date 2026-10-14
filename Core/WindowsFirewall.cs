using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace NetDivert.Core
{
    public sealed class FirewallSyncResult
    {
        public string? Error { get; set; }
        public bool NothingToBlock { get; set; }
        public bool MissingPath { get; set; }
        public int OutRules { get; set; }
        public int InRules { get; set; }
        public int ProgramRules { get; set; }
        public int PortRules { get; set; }
        public int AddressRules { get; set; }
        public int ExeRules { get; set; }
        public List<string> SkippedNames { get; } = new();
    }

    /// <summary>Copies block rules into the Windows Firewall. Rate limits have no equivalent there.</summary>
    public static class WindowsFirewall
    {
        private const string Prefix = "ro0tLimiter:";
        private const int DirIn = 1;
        private const int DirOut = 2;
        private const int ActionBlock = 0;
        private const int ProtoTcp = 6;
        private const int ProtoUdp = 17;
        private const int ProtoAny = 256;
        private const int AllProfiles = 0x7FFFFFFF;

        public static FirewallSyncResult ApplyApp(string displayName, string? path, bool blockIn, bool blockOut)
        {
            var result = new FirewallSyncResult();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                result.MissingPath = true;
                return result;
            }
            try
            {
                dynamic rules = Rules();
                Func<string, bool> none = _ => false;
                Remove(rules, none, GroupFor(path));
                if (!blockIn && !blockOut)
                {
                    result.NothingToBlock = true;
                    return result;
                }
                string label = Trim(displayName) + " " + Tag(path);
                if (blockOut)
                {
                    Add(rules, $"{Prefix} out | {label}", path, DirOut, ProtoAny, null, null, GroupFor(path));
                    result.OutRules++;
                }
                if (blockIn)
                {
                    Add(rules, $"{Prefix} in | {label}", path, DirIn, ProtoAny, null, null, GroupFor(path));
                    result.InRules++;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        public static FirewallSyncResult ApplySaved(string portsText, string ipsText, string exesText)
        {
            var result = new FirewallSyncResult();
            try
            {
                var store = new RuleStore();
                store.Load();
                dynamic rules = Rules();
                Func<string, bool> ours = name => name.StartsWith(Prefix, StringComparison.Ordinal);
                Remove(rules, ours, null);

                foreach (var app in store.Data.Apps)
                {
                    if (!app.BlockIn && !app.BlockOut)
                        continue;
                    if (!IsFile(app.Key))
                        continue;
                    string label = Trim(Path.GetFileNameWithoutExtension(app.Key)) + " " + Tag(app.Key);
                    if (app.BlockOut)
                    {
                        Add(rules, $"{Prefix} out | {label}", app.Key, DirOut, ProtoAny, null, null, GroupFor(app.Key));
                        result.ProgramRules++;
                    }
                    if (app.BlockIn)
                    {
                        Add(rules, $"{Prefix} in | {label}", app.Key, DirIn, ProtoAny, null, null, GroupFor(app.Key));
                        result.ProgramRules++;
                    }
                }

                int chunkIndex = 0;
                foreach (var chunk in Chunk(ParsePorts(portsText), 40))
                {
                    chunkIndex++;
                    string list = string.Join(",", chunk);
                    string suffix = chunkIndex == 1 ? "" : " " + chunkIndex;
                    Add(rules, $"{Prefix} ports TCP out{suffix}", null, DirOut, ProtoTcp, list, null, "ro0tLimiter|policy");
                    Add(rules, $"{Prefix} ports TCP in{suffix}", null, DirIn, ProtoTcp, list, null, "ro0tLimiter|policy");
                    Add(rules, $"{Prefix} ports UDP out{suffix}", null, DirOut, ProtoUdp, list, null, "ro0tLimiter|policy");
                    Add(rules, $"{Prefix} ports UDP in{suffix}", null, DirIn, ProtoUdp, list, null, "ro0tLimiter|policy");
                    result.PortRules += 4;
                }

                int addressIndex = 0;
                foreach (var chunk in Chunk(ParseAddresses(ipsText), 15))
                {
                    addressIndex++;
                    string suffix = addressIndex == 1 ? "" : " " + addressIndex;
                    string list = string.Join(",", chunk);
                    Add(rules, $"{Prefix} addresses out{suffix}", null, DirOut, ProtoAny, null, list, "ro0tLimiter|policy");
                    Add(rules, $"{Prefix} addresses in{suffix}", null, DirIn, ProtoAny, null, list, "ro0tLimiter|policy");
                    result.AddressRules += 2;
                }

                foreach (var line in Split(exesText))
                {
                    if (IsFile(line))
                    {
                        string label = Trim(Path.GetFileName(line)) + " " + Tag(line);
                        Add(rules, $"{Prefix} exe out | {label}", line, DirOut, ProtoAny, null, null, GroupFor(line));
                        Add(rules, $"{Prefix} exe in | {label}", line, DirIn, ProtoAny, null, null, GroupFor(line));
                        result.ExeRules += 2;
                    }
                    else
                    {
                        result.SkippedNames.Add(line);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        public static int RemoveCreated()
        {
            dynamic rules = Rules();
            int count = (int)rules.Count;
            var doomed = new List<string>();
            for (int i = 1; i <= count; i++)
            {
                dynamic rule = rules.Item(i);
                string name = (string)rule.Name;
                if (name.StartsWith(Prefix, StringComparison.Ordinal))
                    doomed.Add(name);
            }
            foreach (var name in doomed)
            {
                try { rules.Remove(name); } catch { }
            }
            return doomed.Count;
        }

        private static dynamic Rules()
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)
                ?? throw new InvalidOperationException("HNetCfg.FwPolicy2");
            return policy.Rules;
        }

        private static void Remove(dynamic rules, Func<string, bool> byName, string? group)
        {
            int count = (int)rules.Count;
            var doomed = new List<string>();
            for (int i = 1; i <= count; i++)
            {
                dynamic rule = rules.Item(i);
                string name = (string)rule.Name;
                string grouping = "";
                try { grouping = (string)(rule.Grouping ?? ""); } catch { }
                bool sameGroup = group != null && string.Equals(grouping, group, StringComparison.OrdinalIgnoreCase);
                if (sameGroup || byName(name))
                    doomed.Add(name);
            }
            foreach (var name in doomed)
            {
                try { rules.Remove(name); } catch { }
            }
        }

        private static void Add(dynamic rules, string name, string? program, int direction, int protocol,
            string? ports, string? addresses, string group)
        {
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)
                ?? throw new InvalidOperationException("HNetCfg.FWRule");
            rule.Name = name.Length > 230 ? name[..230] : name;
            rule.Description = "ro0tLimiter";
            rule.Enabled = true;
            rule.Profiles = AllProfiles;
            rule.Action = ActionBlock;
            rule.Direction = direction;
            rule.Protocol = protocol;
            rule.Grouping = group;
            if (!string.IsNullOrEmpty(program))
                rule.ApplicationName = program;
            if (!string.IsNullOrEmpty(ports))
                rule.RemotePorts = ports;
            if (!string.IsNullOrEmpty(addresses))
                rule.RemoteAddresses = addresses;
            rules.Add(rule);
        }

        private static string GroupFor(string path) => "ro0tLimiter|" + path.ToLowerInvariant();

        private static string Tag(string path)
        {
            uint hash = 2166136261;
            foreach (char c in path.ToLowerInvariant())
                hash = (hash ^ c) * 16777619;
            return hash.ToString("x8");
        }

        private static string Trim(string name)
        {
            name = name.Trim();
            return name.Length > 40 ? name[..40] : name;
        }

        private static bool IsFile(string path) =>
            path.Contains('\\') && File.Exists(path);

        private static List<int> ParsePorts(string text)
        {
            var ports = new List<int>();
            foreach (var line in Split(text))
                if (int.TryParse(line, out int port) && port is > 0 and <= 65535 && !ports.Contains(port))
                    ports.Add(port);
            return ports;
        }

        private static List<string> ParseAddresses(string text)
        {
            var list = new List<string>();
            foreach (var line in Split(text))
            {
                int slash = line.IndexOf('/');
                if (slash < 0)
                {
                    if (IPAddress.TryParse(line, out _))
                        list.Add(line);
                    continue;
                }
                if (IPAddress.TryParse(line[..slash], out var addr)
                    && addr.AddressFamily == AddressFamily.InterNetwork
                    && int.TryParse(line[(slash + 1)..], out int bits)
                    && bits is >= 0 and <= 32)
                    list.Add(line);
            }
            return list;
        }

        private static IEnumerable<string> Split(string text) =>
            text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static IEnumerable<List<T>> Chunk<T>(List<T> items, int size)
        {
            for (int i = 0; i < items.Count; i += size)
                yield return items.GetRange(i, Math.Min(size, items.Count - i));
        }
    }
}
