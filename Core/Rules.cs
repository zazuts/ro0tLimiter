using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetDivert.Core
{
    public sealed class AppRule
    {
        public string Key { get; set; } = "";
        public bool BlockIn { get; set; }
        public bool BlockOut { get; set; }
        public bool LimitDownEnabled { get; set; }
        public int LimitDownKBps { get; set; } = 100;
        public bool LimitUpEnabled { get; set; }
        public int LimitUpKBps { get; set; } = 50;

        /// <summary>Used only while "bloquear tudo" is enabled: this app is allowed through.</summary>
        public bool AllowOut { get; set; }
        public bool AllowIn { get; set; }
        public List<AdapterRule> Adapters { get; set; } = new();

        [JsonIgnore]
        public bool IsDefault => !BlockIn && !BlockOut && !LimitDownEnabled && !LimitUpEnabled && !AllowOut && !AllowIn
            && Adapters.TrueForAll(a => a.IsDefault);

        [JsonIgnore]
        public long DownBytesPerSecond => LimitDownEnabled ? Math.Max(1, LimitDownKBps) * 1024L : 0;

        [JsonIgnore]
        public long UpBytesPerSecond => LimitUpEnabled ? Math.Max(1, LimitUpKBps) * 1024L : 0;

        public AppRule Clone()
        {
            var copy = (AppRule)MemberwiseClone();
            copy.Adapters = Adapters.ConvertAll(a => a.Clone());
            return copy;
        }

        public void CopyFrom(AppRule other)
        {
            BlockIn = other.BlockIn;
            BlockOut = other.BlockOut;
            LimitDownEnabled = other.LimitDownEnabled;
            LimitDownKBps = Math.Max(1, other.LimitDownKBps);
            LimitUpEnabled = other.LimitUpEnabled;
            LimitUpKBps = Math.Max(1, other.LimitUpKBps);
            AllowOut = other.AllowOut;
            AllowIn = other.AllowIn;
            Adapters = other.Adapters.ConvertAll(a => a.Clone());
        }

        public bool SameAs(AppRule o) =>
            BlockIn == o.BlockIn && BlockOut == o.BlockOut &&
            LimitDownEnabled == o.LimitDownEnabled && LimitDownKBps == o.LimitDownKBps &&
            LimitUpEnabled == o.LimitUpEnabled && LimitUpKBps == o.LimitUpKBps &&
            AllowOut == o.AllowOut && AllowIn == o.AllowIn &&
            SameAdapters(o);

        private bool SameAdapters(AppRule o)
        {
            if (Adapters.Count != o.Adapters.Count)
                return false;
            for (int i = 0; i < Adapters.Count; i++)
                if (!Adapters[i].SameAs(o.Adapters[i]))
                    return false;
            return true;
        }
    }

    public sealed class AdapterRule
    {
        public string AdapterId { get; set; } = "";
        public string AdapterName { get; set; } = "";
        public bool BlockIn { get; set; }
        public bool BlockOut { get; set; }
        public bool LimitDownEnabled { get; set; }
        public int LimitDownKBps { get; set; } = 100;
        public bool LimitUpEnabled { get; set; }
        public int LimitUpKBps { get; set; } = 50;

        [JsonIgnore]
        public bool IsDefault => !BlockIn && !BlockOut && !LimitDownEnabled && !LimitUpEnabled;

        [JsonIgnore]
        public long DownBytesPerSecond => LimitDownEnabled ? Math.Max(1, LimitDownKBps) * 1024L : 0;

        [JsonIgnore]
        public long UpBytesPerSecond => LimitUpEnabled ? Math.Max(1, LimitUpKBps) * 1024L : 0;

        public AdapterRule Clone() => (AdapterRule)MemberwiseClone();

        public bool SameAs(AdapterRule o) =>
            string.Equals(AdapterId, o.AdapterId, StringComparison.OrdinalIgnoreCase) &&
            BlockIn == o.BlockIn && BlockOut == o.BlockOut &&
            LimitDownEnabled == o.LimitDownEnabled && LimitDownKBps == o.LimitDownKBps &&
            LimitUpEnabled == o.LimitUpEnabled && LimitUpKBps == o.LimitUpKBps;
    }

    public sealed class RuleSet
    {
        public AppRule Global { get; set; } = new() { Key = "*", LimitDownKBps = 1024, LimitUpKBps = 512 };
        public List<AppRule> Apps { get; set; } = new();
    }

    /// <summary>
    /// Rules live in %ProgramData% so the Windows service (LocalSystem) and the UI share them.
    /// The folder is restricted to Administrators/SYSTEM since the rules drive a privileged engine.
    /// </summary>
    public sealed class RuleStore
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        private readonly object _lock = new();
        private readonly Dictionary<string, AppRule> _byKey = new(StringComparer.OrdinalIgnoreCase);

        public static string Directory => Brand.DataDir;

        public string FilePath { get; } = Path.Combine(Directory, "rules.json");
        public RuleSet Data { get; private set; } = new();

        public RuleStore()
        {
            Brand.MigrateLegacy();
            EnsureDirectory();
        }

        private static void EnsureDirectory()
        {
            if (System.IO.Directory.Exists(Directory))
                return;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Directory).Create(security);
        }

        public void Load()
        {
            lock (_lock)
            {
                string path = FilePath;
                string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetDivert", "rules.json");
                if (!File.Exists(path) && File.Exists(legacy))
                    path = legacy;

                try
                {
                    if (File.Exists(path))
                        Data = JsonSerializer.Deserialize<RuleSet>(File.ReadAllText(path)) ?? new RuleSet();
                }
                catch
                {
                    Data = new RuleSet();
                }
                _byKey.Clear();
                foreach (var r in Data.Apps)
                    _byKey[r.Key] = r;
                if (path == legacy)
                    Save();
            }
        }

        /// <summary>Returns the persisted rule for an app, or a new detached default rule.</summary>
        public AppRule GetOrCreate(string key)
        {
            lock (_lock)
                return _byKey.TryGetValue(key, out var r) ? r : new AppRule { Key = key };
        }

        public void Save()
        {
            lock (_lock)
            {
                Data.Apps.RemoveAll(r => r.IsDefault);
                try
                {
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
                    File.Move(tmp, FilePath, overwrite: true);
                }
                catch
                {
                    // Persisting rules is best effort; the engine keeps working with in-memory rules.
                }
            }
        }

        public void Commit(AppRule rule)
        {
            lock (_lock)
            {
                if (rule.IsDefault)
                {
                    _byKey.Remove(rule.Key);
                }
                else if (!_byKey.ContainsKey(rule.Key))
                {
                    _byKey[rule.Key] = rule;
                    Data.Apps.Add(rule);
                }
            }
            Save();
        }
    }
}
