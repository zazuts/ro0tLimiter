using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NetDivert.UI
{
    /// <summary>Lightweight auto-scaling download/upload chart.</summary>
    public sealed class TrafficGraph : FrameworkElement
    {
        private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1F)));
        private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2C, 0x31, 0x3A))), 1));
        private static readonly Brush DownFill = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x3D, 0xDC, 0x84)));
        private static readonly Pen DownPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84))), 1.5));
        private static readonly Pen UpPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x43))), 1.5));
        private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA3)));
        private static readonly Typeface Face = new("Segoe UI");

        public TrafficGraph()
        {
            IsVisibleChanged += (_, _) => InvalidateVisual();
        }

        private History? _down;
        private History? _up;

        public void SetData(History down, History up)
        {
            _down = down;
            _up = up;
            InvalidateVisual();
        }

        private static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
            if (_down == null || _up == null || w < 20 || h < 20)
                return;

            int n = _down.Capacity;
            double max = 1024;
            for (int i = 0; i < n; i++)
                max = Math.Max(max, Math.Max(_down[i], _up[i]));
            max = NiceCeiling(max * 1.1);

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int g = 1; g <= 3; g++)
            {
                double y = h - h * g / 4.0;
                dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
                var label = new FormattedText(Format.Rate(max * g / 4.0), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Face, 10, LabelBrush, dpi);
                dc.DrawText(label, new Point(4, y - label.Height));
            }

            double step = w / (n - 1);
            Point At(History hist, int i) => new(i * step, h - hist[i] / max * h);

            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                for (int i = 0; i < n; i++)
                    ctx.LineTo(At(_down, i), true, true);
                ctx.LineTo(new Point(w, h), true, false);
            }
            area.Freeze();
            dc.DrawGeometry(DownFill, null, area);

            dc.DrawGeometry(null, DownPen, Line(_down, n, At));
            dc.DrawGeometry(null, UpPen, Line(_up, n, At));
        }

        private static StreamGeometry Line(History hist, int n, Func<History, int, Point> at)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(at(hist, 0), false, false);
                for (int i = 1; i < n; i++)
                    ctx.LineTo(at(hist, i), true, true);
            }
            g.Freeze();
            return g;
        }

        private static double NiceCeiling(double v)
        {
            double exp = Math.Pow(2, Math.Ceiling(Math.Log2(v)));
            foreach (double f in new[] { 0.5, 0.625, 0.75, 1.0 })
                if (exp * f >= v)
                    return exp * f;
            return exp;
        }
    }
}
