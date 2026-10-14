using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetDivert.Core;

namespace NetDivert.UI;

/// <summary>Small question mark. Hover shows the short tip; click opens the full text.</summary>
public sealed class HelpMark : Button
{
    public static readonly DependencyProperty TipKeyProperty =
        DependencyProperty.Register(nameof(TipKey), typeof(string), typeof(HelpMark),
            new PropertyMetadata(null, (d, _) => ((HelpMark)d).Refresh()));

    public string? TipKey
    {
        get => (string?)GetValue(TipKeyProperty);
        set => SetValue(TipKeyProperty, value);
    }

    public static readonly DependencyProperty HelpKeyProperty =
        DependencyProperty.Register(nameof(HelpKey), typeof(string), typeof(HelpMark));

    public string? HelpKey
    {
        get => (string?)GetValue(HelpKeyProperty);
        set => SetValue(HelpKeyProperty, value);
    }

    public HelpMark()
    {
        Width = 16;
        Height = 16;
        Margin = new Thickness(6, 0, 0, 0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Cursor = Cursors.Help;
        Focusable = false;
        VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetBetweenShowDelay(this, 0);
        Content = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
            Child = new TextBlock
            {
                Text = "?",
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            },
        };
        Click += (_, _) => Open();
        Loc.Instance.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (TipKey == null)
            return;
        ToolTip = Polish(Loc.Get(TipKey));
    }

    private void Open()
    {
        if (TipKey == null)
            return;
        string titleKey = TipKey.EndsWith(".tip", StringComparison.Ordinal) ? TipKey[..^4] : TipKey;
        string title = Loc.Get(titleKey);
        if (title == titleKey)
            title = Loc.Get(TipKey);
        string body = Polish(Loc.Get(HelpKey ?? TipKey));
        body = AppendDomainList(titleKey, body);
        new HelpWindow(title, body).Show();
    }

    /// <summary>Each sentence and each bullet starts with a capital and ends with punctuation.</summary>
    private static string Polish(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                lines[i] = "";
                continue;
            }
            bool bullet = line.StartsWith("•", StringComparison.Ordinal);
            string body = bullet ? line[1..].Trim() : line;
            if (body.Length == 0)
            {
                lines[i] = line;
                continue;
            }
            if (char.IsLetter(body[0]) && char.IsLower(body[0]))
                body = char.ToUpper(body[0]) + body[1..];
            char end = body[^1];
            if (end is not ('.' or '!' or '?' or ':'))
                body += ".";
            lines[i] = bullet ? "• " + body : body;
        }
        return string.Join("\n", lines);
    }

    private static string AppendDomainList(string titleKey, string body)
    {
        string[]? list = titleKey switch
        {
            "set.telemetry" => BuiltInLists.Telemetry,
            "set.updates" => BuiltInLists.Updates,
            "set.msapps" => BuiltInLists.MicrosoftApps,
            _ => null,
        };
        if (list == null)
            return body;
        var sb = new StringBuilder(body);
        sb.Append("\n\n");
        foreach (var domain in list)
            sb.Append('•').Append(' ').Append(domain).Append('\n');
        if (titleKey == "set.msapps")
        {
            sb.Append('\n').Append(Loc.Get("set.onedrive.note")).Append('\n');
            foreach (var domain in BuiltInLists.OneDrive)
                sb.Append('•').Append(' ').Append(domain).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Selectable help. The window title is the section it explains, and the size follows the text.</summary>
public sealed class HelpWindow : Window
{
    public HelpWindow(string title, string text)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 320;
        MaxWidth = 560;
        MaxHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = (Brush)Application.Current.Resources["PanelBrush"];
        Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

        var root = new StackPanel { Margin = new Thickness(16) };
        foreach (var block in text.Replace("\r", "").Split('\n'))
        {
            if (block.Length == 0)
            {
                root.Children.Add(new Border { Height = 8 });
                continue;
            }
            bool item = block.StartsWith("•", StringComparison.Ordinal);
            var box = new TextBox
            {
                Text = item ? block[1..].Trim() : block,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = (Brush)Application.Current.Resources["FgBrush"],
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Justify,
                FontSize = 13,
                Padding = new Thickness(item ? 8 : 0, 4, 0, 4),
                IsUndoEnabled = false,
            };
            if (item)
            {
                root.Children.Add(new Border
                {
                    BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 1, 0, 1),
                    Child = box,
                });
            }
            else
            {
                root.Children.Add(box);
            }
        }
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
