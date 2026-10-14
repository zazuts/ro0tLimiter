using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace NetDivert.UI
{
    /// <summary>Draws the app/tray icon at runtime: a blue rounded tile with download/upload arrows.</summary>
    public static class AppIcon
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create(Color accent, int size = 32, Color? downColor = null, Color? upColor = null)
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float r = size * 0.22f;
                using var tile = new GraphicsPath();
                tile.AddArc(0, 0, r * 2, r * 2, 180, 90);
                tile.AddArc(size - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
                tile.AddArc(size - r * 2 - 1, size - r * 2 - 1, r * 2, r * 2, 0, 90);
                tile.AddArc(0, size - r * 2 - 1, r * 2, r * 2, 90, 90);
                tile.CloseFigure();
                using var bg = new LinearGradientBrush(new Rectangle(0, 0, size, size),
                    accent, Color.FromArgb(accent.A, (byte)(accent.R / 2), (byte)(accent.G / 2), (byte)(accent.B / 2)), 90f);
                g.FillPath(bg, tile);

                float s = size / 32f;
                using var down = new SolidBrush(downColor ?? Color.FromArgb(0x7C, 0xF5, 0xB0));
                using var up = new SolidBrush(upColor ?? Color.FromArgb(0xFF, 0xC0, 0x7A));
                g.FillPolygon(down, new[]
                {
                    new PointF(7 * s, 17 * s), new PointF(15 * s, 17 * s), new PointF(11 * s, 25 * s),
                });
                g.FillRectangle(down, 9.5f * s, 6 * s, 3 * s, 11.5f * s);
                g.FillPolygon(up, new[]
                {
                    new PointF(17 * s, 15 * s), new PointF(25 * s, 15 * s), new PointF(21 * s, 7 * s),
                });
                g.FillRectangle(up, 19.5f * s, 14.5f * s, 3 * s, 11.5f * s);
            }

            IntPtr h = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(h);
                return (Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(h);
            }
        }

        public static BitmapSource ToImageSource(Icon icon)
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
    }
}
