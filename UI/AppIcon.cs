using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace NetDivert.UI
{
    /// <summary>The ro0t mark. The tray tints it; the window keeps the original.</summary>
    public static class AppIcon
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Bitmap? _master;

        public static BitmapSource LogoSource()
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/Assets/ro0t.png", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }

        public static Icon Create(Color tint, int size = 32, Color? downColor = null, Color? upColor = null)
        {
            var color = downColor ?? upColor ?? tint;
            using var bmp = Render(size, color);
            using var png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            using var ico = new MemoryStream();
            WriteIco(ico, png.ToArray(), size);
            ico.Position = 0;
            using var icon = new Icon(ico);
            return (Icon)icon.Clone();
        }

        private static void WriteIco(Stream stream, byte[] png, int size)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(size >= 256 ? (byte)0 : (byte)size);
            writer.Write(size >= 256 ? (byte)0 : (byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)png.Length);
            writer.Write((uint)22);
            writer.Write(png);
        }

        private static Bitmap Render(int size, Color tint)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            using var master = (Bitmap)Master().Clone();
            g.DrawImage(master, new Rectangle(0, 0, size, size));
            Tint(bmp, tint);
            return bmp;
        }

        private static void Tint(Bitmap bmp, Color tint)
        {
            if (tint.R > 240 && tint.G > 240 && tint.B > 240)
                return;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.A == 0)
                        continue;
                    bmp.SetPixel(x, y, Color.FromArgb(p.A,
                        (byte)(p.R * tint.R / 255),
                        (byte)(p.G * tint.G / 255),
                        (byte)(p.B * tint.B / 255)));
                }
            }
        }

        private static Bitmap Master()
        {
            if (_master != null)
                return _master;
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ro0t.png", UriKind.Absolute))
                ?? throw new InvalidOperationException("Logo resource is missing.");
            var copy = new MemoryStream();
            info.Stream.CopyTo(copy);
            copy.Position = 0;
            _master = new Bitmap(copy);
            return _master;
        }
    }
}
