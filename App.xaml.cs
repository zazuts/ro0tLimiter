using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using NetDivert.UI;

namespace NetDivert
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Bindings parse/format numbers with the user's culture (e.g. "0,5" MB/s in pt-BR).
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

            DispatcherUnhandledException += OnUnhandled;
            Theme.Apply(UiSettings.Current.Theme);
            base.OnStartup(e);
        }

        private void CellHover(object sender, MouseEventArgs e)
        {
            if (sender is not DataGridCell cell)
                return;
            string? text = CellText(cell);
            cell.ToolTip = text != null && IsTrimmed(cell, text) ? text : null;
        }

        private void CellCopy(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGridCell cell)
                return;
            if (e.OriginalSource is DependencyObject source &&
                (FindParent<CheckBox>(source) != null || FindParent<TextBox>(source) != null || FindParent<Button>(source) != null))
                return;
            var row = FindParent<DataGridRow>(cell);
            var grid = FindParent<DataGrid>(cell);
            if (row == null || grid == null)
                return;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var column in grid.Columns)
            {
                try
                {
                    var content = column.GetCellContent(row);
                    parts.Add(content == null ? "" : Describe(content));
                }
                catch (InvalidOperationException)
                {
                    parts.Add("");
                }
            }
            string line = string.Join("\t", parts).Trim();
            if (line.Length > 0)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => CopyLine(line));
        }

        private static void CopyLine(string line)
        {
            try
            {
                Clipboard.SetDataObject(line, true);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    try { Clipboard.SetDataObject(line, true); } catch { }
                });
            }
            catch (Exception)
            {
                // The clipboard belongs to another program for the moment.
            }
        }

        private static string Describe(DependencyObject node)
        {
            var bits = new System.Collections.Generic.List<string>();
            CollectText(node, bits);
            return string.Join(" ", bits);
        }

        private static void CollectText(DependencyObject node, System.Collections.Generic.List<string> bits)
        {
            switch (node)
            {
                case TextBlock block when block.Text.Length > 0:
                    bits.Add(block.Text);
                    return;
                case TextBox box:
                    bits.Add(box.Text);
                    return;
                case CheckBox check:
                    bits.Add(check.IsChecked == true ? "1" : "0");
                    return;
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                CollectText(VisualTreeHelper.GetChild(node, i), bits);
        }

        private static string? CellText(DataGridCell cell)
        {
            var block = FindChild<TextBlock>(cell);
            return block == null || string.IsNullOrEmpty(block.Text) ? null : block.Text;
        }

        private static bool IsTrimmed(DataGridCell cell, string text)
        {
            var block = FindChild<TextBlock>(cell);
            if (block == null || block.ActualWidth <= 0)
                return false;
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
                block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);
            return formatted.Width > block.ActualWidth + 1;
        }

        private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found)
                    return found;
                var nested = FindChild<T>(child);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            for (var current = child; current != null; current = VisualTreeHelper.GetParent(current))
                if (current is T found)
                    return found;
            return null;
        }

        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.ToString(), "ro0tLimiter", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
