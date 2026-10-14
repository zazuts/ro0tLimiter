using System;
using System.Text.Json;
using NetDivert.Core;

namespace NetDivert.UI
{
    /// <summary>One file with everything the user can personalize.</summary>
    public sealed class SettingsBackup
    {
        public int Version { get; set; } = 1;
        public UiSettings? Ui { get; set; }
        public SecurityPolicy? Policy { get; set; }
        public RuleSet? Rules { get; set; }
        public RedirectSettings? Redirect { get; set; }

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static string Export(UiSettings ui, SecurityPolicy policy, RuleSet rules, RedirectSettings redirect) =>
            JsonSerializer.Serialize(new SettingsBackup
            {
                Ui = ui,
                Policy = policy,
                Rules = rules,
                Redirect = redirect,
            }, Json);

        public static SettingsBackup Import(string json) =>
            JsonSerializer.Deserialize<SettingsBackup>(json) ?? throw new InvalidOperationException("empty");
    }
}
