using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
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
        public bool Exclusive { get; set; }
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
                    Exclusive = existing?.Exclusive ?? false,
                });
            }
            InitializeComponent();
            Loaded += (_, _) => FitToColumns();
        }

        private void FitToColumns()
        {
            string[] headers =
            {
                Loc.Get("col.adapter"), Loc.Get("col.blockDown"), Loc.Get("col.blockUp"), Loc.Get("col.limitDown"),
                "KB/s", Loc.Get("col.limitUp"), "KB/s", Loc.Get("col.exclusive"),
            };
            double total = 16;
            for (int i = 0; i < Grid.Columns.Count && i < headers.Length; i++)
            {
                double width = TextWidth(headers[i]) + 32;
                if (i == 0)
                {
                    foreach (var row in Rows)
                        width = Math.Max(width, TextWidth(row.Name) + 24);
                }
                else if (headers[i] == "KB/s")
                    width = Math.Max(width, 72);
                else
                    width = Math.Max(width, 88);
                Grid.Columns[i].Width = width;
                total += width;
            }
            var area = SystemParameters.WorkArea;
            Width = Math.Min(Math.Max(680, total + 36), area.Width - 40);
            Height = Math.Min(area.Height - 40, 168 + Math.Max(1, Rows.Count) * 28);
            MinWidth = 520;
            MinHeight = 260;
        }

        private double TextWidth(string text)
        {
            if (text.Length == 0)
                return 0;
            double dpi = PresentationSource.FromVisual(this) == null ? 1 : VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12.5,
                Brushes.Black,
                dpi);
            return formatted.WidthIncludingTrailingWhitespace;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _rule.Adapters = Rows
                .Where(r => r.BlockIn || r.BlockOut || r.LimitDownEnabled || r.LimitUpEnabled || r.Exclusive)
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
                    Exclusive = r.Exclusive,
                })
                .ToList();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
