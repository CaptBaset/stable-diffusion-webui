using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HamRxBridge.Rig;

namespace HamRxBridge.Ui;

public static class UiKit
{
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static SolidColorBrush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static TextBlock Label(string text, double size = 13, string brush = "TextBrush", bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Res(brush),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0, 4, 0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    public static TextBlock GradientText(string text, double size, bool bold = true) => new()
    {
        Text = text,
        FontFamily = DisplayFont,
        FontSize = size,
        FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold,
        Foreground = Res("SignatureBrush"),
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>The operator's signature, shown on every screen.</summary>
    public static TextBlock Signature(double size = 13) => new()
    {
        Text = "By AP2FLY",
        FontSize = size,
        FontWeight = FontWeights.Bold,
        FontStyle = FontStyles.Italic,
        Foreground = Res("SignatureBrush"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 6, 0)
    };

    public static Button Btn(string text, RoutedEventHandler onClick, string? tip = null)
    {
        var b = new Button { Content = text };
        if (tip != null) b.ToolTip = tip;
        b.Click += onClick;
        return b;
    }

    /// <summary>Fluent system icons on Windows 11, MDL2 assets on Windows 10 (same code points).</summary>
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    public static class Glyph
    {
        public const string Home = "\uE80F", Settings = "\uE713", List = "\uE8FD", Refresh = "\uE72C",
            ZoomIn = "\uE8A3", ZoomOut = "\uE71F", Globe = "\uE774", Target = "\uE7B7",
            Contrast = "\uE793", Play = "\uE768", Pin = "\uE718", Folder = "\uE838", Search = "\uE721",
            Radio = "\uE704", Plug = "\uE83E", Star = "\uE734";
    }

    public static StackPanel IconText(string glyph, string text) 
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        if (text.Length > 0)
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    public static Button IconBtn(string glyph, string text, RoutedEventHandler onClick, string? tip = null)
    {
        var b = new Button { Content = IconText(glyph, text) };
        if (tip != null) b.ToolTip = tip;
        b.Click += onClick;
        return b;
    }

    public static Button AccentBtn(string text, RoutedEventHandler onClick)
    {
        var b = Btn(text, onClick);
        b.Background = Res("SignatureBrush");
        b.Foreground = Brushes.White;
        b.FontWeight = FontWeights.SemiBold;
        b.BorderThickness = new Thickness(0);
        return b;
    }

    public static ToggleButton Tog(string text, RoutedEventHandler onClick, Brush? accent = null)
    {
        var b = new ToggleButton { Content = text };
        if (accent != null) b.Tag = accent;
        b.Click += onClick;
        return b;
    }

    public static Border Card(UIElement child, double pad = 12) => new()
    {
        Background = Res("PanelBrush"),
        BorderBrush = Res("LineBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(pad),
        Margin = new Thickness(0, 0, 0, 8),
        Child = child
    };

    /// <summary>Small caps section title with a colour bar underneath.</summary>
    public static UIElement Section(string title)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var t = Label(title.ToUpperInvariant(), 11, "DimBrush", true);
        t.Margin = new Thickness(2, 0, 0, 3);
        sp.Children.Add(t);
        sp.Children.Add(new Border { Height = 2, Width = 36, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1), Background = Res("SignatureBrush"), Margin = new Thickness(2, 0, 0, 0) });
        return sp;
    }

    public static UniformGrid Cols(int columns, params UIElement[] items)
    {
        var g = new UniformGrid { Columns = columns };
        foreach (var i in items) g.Children.Add(i);
        return g;
    }

    public static Brush ModeBrush(RigMode m) => m switch
    {
        RigMode.LSB => Hex("#22D3EE"),
        RigMode.USB => Hex("#60A5FA"),
        RigMode.CW => Hex("#FBBF24"),
        RigMode.AM => Hex("#34D399"),
        _ => Hex("#F472B6")
    };

    public static string ModeName(RigMode m) => m == RigMode.NFM ? "FM" : m.ToString();

    public static string FormatHz(double hz)
    {
        long v = (long)Math.Round(hz);
        return $"{v / 1_000_000}.{v / 1000 % 1000:000}.{v % 1000:000}";
    }

    /// <summary>Accepts MHz (14.2) or kHz (14200). Anything up to 32 is read as MHz.</summary>
    public static bool TryParseFrequency(string text, out double hz)
    {
        hz = 0;
        text = text.Trim().ToLowerInvariant().Replace(',', '.').Replace("khz", "").Replace("mhz", "").Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) return false;
        hz = v <= 32 ? v * 1e6 : v * 1e3;
        return hz <= 32e6;
    }

    /// <summary>Decimal "lat, lon" or a 4/6 character Maidenhead locator (e.g. MM74 or MM74ab).</summary>
    public static bool TryParseLocation(string text, out double lat, out double lon)
    {
        lat = lon = 0;
        text = text.Trim();
        var parts = text.Replace(';', ',').Split(',');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lon))
            return Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;

        text = text.ToUpperInvariant();
        if (text.Length is 4 or 6 && text[0] is >= 'A' and <= 'R' && text[1] is >= 'A' and <= 'R' &&
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

    public static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ---------------------------------------------------------------- bands

    public sealed record Band(string Name, double LowHz, double HighHz, double DefaultHz, RigMode Mode, bool Ham);

    public static readonly Band[] Bands =
    {
        new("160m", 1_800_000, 2_000_000, 1_840_000, RigMode.LSB, true),
        new("80m", 3_500_000, 3_900_000, 3_700_000, RigMode.LSB, true),
        new("60m", 5_250_000, 5_450_000, 5_357_000, RigMode.USB, true),
        new("40m", 7_000_000, 7_300_000, 7_100_000, RigMode.LSB, true),
        new("30m", 10_100_000, 10_150_000, 10_136_000, RigMode.USB, true),
        new("20m", 14_000_000, 14_350_000, 14_200_000, RigMode.USB, true),
        new("17m", 18_068_000, 18_168_000, 18_130_000, RigMode.USB, true),
        new("15m", 21_000_000, 21_450_000, 21_300_000, RigMode.USB, true),
        new("12m", 24_890_000, 24_990_000, 24_950_000, RigMode.USB, true),
        new("10m", 28_000_000, 29_700_000, 28_500_000, RigMode.USB, true),
        new("WWV 10", 9_995_000, 10_005_000, 10_000_000, RigMode.AM, false),
        new("WWV 15", 14_995_000, 15_005_000, 15_000_000, RigMode.AM, false),
        new("MW", 530_000, 1_700_000, 1_000_000, RigMode.AM, false),
        new("49m", 5_900_000, 6_200_000, 6_000_000, RigMode.AM, false),
        new("31m", 9_400_000, 9_900_000, 9_600_000, RigMode.AM, false),
        new("25m", 11_600_000, 12_100_000, 11_800_000, RigMode.AM, false),
        new("19m", 15_100_000, 15_800_000, 15_400_000, RigMode.AM, false),
        new("13m", 21_450_000, 21_850_000, 21_600_000, RigMode.AM, false),
    };

    public static string BandName(double hz)
    {
        foreach (var b in Bands) if (hz >= b.LowHz && hz <= b.HighHz) return b.Name;
        return "";
    }
}
