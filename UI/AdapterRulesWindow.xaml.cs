using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using NetDivert.Core;

namespace NetDivert.UI
{
    public sealed class AdapterRuleRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool BlockIn { get; set; }
        public bool BlockOut { get; set; }
        public bool LimitDownEnabled { get; set; }
        public int LimitDownKBps { get; set; } = 100;
        public bool LimitUpEnabled { get; set; }
        public int LimitUpKBps { get; set; } = 50;
    }

    public partial class AdapterRulesWindow : Window
    {
        private readonly AppRule _rule;

        public ObservableCollection<AdapterRuleRow> Rows { get; } = new();

        public AdapterRulesWindow(AppRule rule)
        {
            _rule = rule;
            DataContext = this;
            AdapterTable.Refresh();
            var known = AdapterTable.All.Select(a => (a.Id, a.Name)).ToList();
            foreach (var extra in rule.Adapters)
                if (known.All(k => !string.Equals(k.Id, extra.AdapterId, System.StringComparison.OrdinalIgnoreCase)))
                    known.Add((extra.AdapterId, extra.AdapterName));
            foreach (var (id, name) in known.OrderBy(k => k.Name))
            {
                var existing = rule.Adapters.FirstOrDefault(a => string.Equals(a.AdapterId, id, System.StringComparison.OrdinalIgnoreCase));
                Rows.Add(new AdapterRuleRow
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? id : name,
                    BlockIn = existing?.BlockIn ?? false,
                    BlockOut = existing?.BlockOut ?? false,
                    LimitDownEnabled = existing?.LimitDownEnabled ?? false,
                    LimitDownKBps = existing?.LimitDownKBps ?? 100,
                    LimitUpEnabled = existing?.LimitUpEnabled ?? false,
                    LimitUpKBps = existing?.LimitUpKBps ?? 50,
                });
            }
            InitializeComponent();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _rule.Adapters = Rows
                .Where(r => r.BlockIn || r.BlockOut || r.LimitDownEnabled || r.LimitUpEnabled)
                .Select(r => new AdapterRule
                {
                    AdapterId = r.Id,
                    AdapterName = r.Name,
                    BlockIn = r.BlockIn,
                    BlockOut = r.BlockOut,
                    LimitDownEnabled = r.LimitDownEnabled,
                    LimitDownKBps = System.Math.Max(1, r.LimitDownKBps),
                    LimitUpEnabled = r.LimitUpEnabled,
                    LimitUpKBps = System.Math.Max(1, r.LimitUpKBps),
                })
                .ToList();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
