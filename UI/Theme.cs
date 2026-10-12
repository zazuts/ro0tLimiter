using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace NetDivert.UI
{
    public static class Theme
    {
        private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static SolidColorBrush Brush(string hex)
        {
            if (!Cache.TryGetValue(hex, out var brush))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    brush = new SolidColorBrush(color);
                    brush.Freeze();
                }
                catch
                {
                    brush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4B));
                    brush.Freeze();
                }
                Cache[hex] = brush;
            }
            return brush;
        }
    }
}
