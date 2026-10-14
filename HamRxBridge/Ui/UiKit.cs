using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace HamRxBridge.Ui;

public static class UiKit
{
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static TextBlock Label(string text, double size = 13, string brush = "TextBrush", bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Res(brush),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0, 4, 0)
    };

    public static Button Btn(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text };
        b.Click += onClick;
        return b;
    }

    public static ToggleButton Tog(string text, RoutedEventHandler onClick)
    {
        var b = new ToggleButton { Content = text };
        b.Click += onClick;
        return b;
    }

    public static Border Card(UIElement child, double pad = 12) => new()
    {
        Background = Res("PanelBrush"),
        BorderBrush = Res("LineBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(pad),
        Child = child
    };

    public static string FormatHz(double hz)
    {
        long v = (long)Math.Round(hz);
        return $"{v / 1_000_000}.{v / 1000 % 1000:000}.{v % 1000:000}";
    }

    /// <summary>Accepts MHz (14.2) or kHz (14200). Anything up to 30 is read as MHz.</summary>
    public static bool TryParseFrequency(string text, out double hz)
    {
        hz = 0;
        text = text.Trim().Replace(',', '.');
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0) return false;
        hz = v <= 30 ? v * 1e6 : v * 1e3;
        return true;
    }

    /// <summary>Parses "lat, lon" in decimal degrees, or a 4 or 6 character Maidenhead locator.</summary>
    public static bool TryParseLocation(string text, out double lat, out double lon)
    {
        lat = lon = 0;
        text = text.Trim();
        var parts = text.Replace(';', ',').Split(',');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lat) &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lon))
            return Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;

        text = text.ToUpperInvariant();
        if (text.Length is 4 or 6 &&
            text[0] is >= 'A' and <= 'R' && text[1] is >= 'A' and <= 'R' &&
            char.IsDigit(text[2]) && char.IsDigit(text[3]))
        {
            lon = (text[0] - 'A') * 20 - 180 + (text[2] - '0') * 2;
            lat = (text[1] - 'A') * 10 - 90 + (text[3] - '0');
            if (text.Length == 6 && text[4] is >= 'A' and <= 'X' && text[5] is >= 'A' and <= 'X')
            {
                lon += (text[4] - 'A') * (2.0 / 24) + 1.0 / 24;
                lat += (text[5] - 'A') * (1.0 / 24) + 0.5 / 24;
            }
            else { lon += 1; lat += 0.5; }
            return true;
        }
        return false;
    }
}
