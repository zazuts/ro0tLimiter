using System.Windows;
using System.Windows.Media;

namespace NetDivert.UI;

/// <summary>Shared colors, plus the light and dark palettes.</summary>
public static class Theme
{
    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static void Apply(string name)
    {
        bool light = string.Equals(name, "light", StringComparison.OrdinalIgnoreCase);
        Set("BgBrush", light ? "#F3F3F3" : "#1E2128");
        Set("PanelBrush", light ? "#FFFFFF" : "#252932");
        Set("AltRowBrush", light ? "#F7F7F7" : "#22262E");
        Set("BorderBrush", light ? "#D0D0D0" : "#353B46");
        Set("FgBrush", light ? "#1A1A1A" : "#E3E6EB");
        Set("MutedBrush", light ? "#5A5A5A" : "#8A93A3");
        Set("AccentBrush", light ? "#0F6CBD" : "#3D8BFD");
        Set("SelectBrush", light ? "#D6E6F8" : "#2F4A72");
        Set("DownBrush", light ? "#0B6A0B" : "#3DDC84");
        Set("UpBrush", light ? "#C05600" : "#FF9F43");
        Set("BlockBrush", light ? "#C42B1C" : "#FF5C5C");
        Set("InputBrush", light ? "#FFFFFF" : "#16191F");
        Set("ScrollTrackBrush", light ? "#F3F3F3" : "#1E2128");
        Set("ScrollThumbBrush", light ? "#C4C4C4" : "#6B6B6B");
        Set("ScrollThumbHoverBrush", light ? "#A6A6A6" : "#8A8A8A");
    }

    private static void Set(string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }
}
