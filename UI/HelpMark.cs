using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetDivert.UI
{
    /// <summary>A question mark. The tooltip is the short description; a click opens the full one.</summary>
    public class HelpMark : Button
    {
        public static readonly DependencyProperty TipKeyProperty =
            DependencyProperty.Register(nameof(TipKey), typeof(string), typeof(HelpMark),
                new PropertyMetadata("", (d, _) => ((HelpMark)d).Refresh()));

        public static readonly DependencyProperty HelpKeyProperty =
            DependencyProperty.Register(nameof(HelpKey), typeof(string), typeof(HelpMark), new PropertyMetadata(""));

        public string TipKey
        {
            get => (string)GetValue(TipKeyProperty);
            set => SetValue(TipKeyProperty, value);
        }

        public string HelpKey
        {
            get => (string)GetValue(HelpKeyProperty);
            set => SetValue(HelpKeyProperty, value);
        }

        public HelpMark()
        {
            Width = 22;
            Height = 22;
            Padding = new Thickness(0);
            Content = "?";
            FontWeight = FontWeights.Bold;
            FontSize = 12;
            VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(12, 0, 0, 0);
            Cursor = System.Windows.Input.Cursors.Help;
            ToolTipService.SetShowDuration(this, 30000);
            ToolTipService.SetInitialShowDelay(this, 250);
            Click += (_, _) =>
            {
                if (string.IsNullOrEmpty(HelpKey))
                    return;
                new HelpWindow(Loc.Get(TipKey), Loc.Get(HelpKey)) { Owner = Window.GetWindow(this) }.ShowDialog();
            };
            Loaded += (_, _) =>
            {
                Loc.Changed += Refresh;
                Refresh();
            };
            Unloaded += (_, _) => Loc.Changed -= Refresh;
        }

        private void Refresh()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(Refresh);
                return;
            }
            ToolTip = string.IsNullOrEmpty(TipKey) ? null : Loc.Get(TipKey);
        }
    }

    public class HelpWindow : Window
    {
        public HelpWindow(string title, string body)
        {
            Title = string.IsNullOrWhiteSpace(title) ? Loc.Get("set.title") : title;
            Width = 560;
            Height = 460;
            MinWidth = 440;
            MinHeight = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = (Brush)Application.Current.FindResource("BgBrush");
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13.5;
            Content = new ScrollViewer
            {
                Padding = new Thickness(22, 18, 22, 18),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                    Foreground = (Brush)Application.Current.FindResource("FgBrush"),
                },
            };
        }
    }
}
