using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HamRxBridge.Ui;

/// <summary>Segmented S-meter: S1 to S9 on the left 60 percent, +10 to +60 dB on the right.</summary>
public sealed class SMeter : FrameworkElement
{
    const int Segments = 40;
    double _dbm = -127, _peak = -127;
    DateTime _peakAt;
    bool _available = true;

    static readonly Brush Bg = UiKit.Hex("#0A111C");
    static readonly Brush Off = UiKit.Hex("#16233A");
    static readonly Brush TextB = UiKit.Hex("#7F93AD");
    static readonly Brush[] SegBrush = Enumerable.Range(0, Segments).Select(i =>
    {
        var b = new SolidColorBrush(SegColor((i + 0.5) / Segments));
        b.Freeze();
        return (Brush)b;
    }).ToArray();

    public bool Available
    {
        get => _available;
        set { _available = value; InvalidateVisual(); }
    }

    public SMeter() { Height = 46; }

    public void Set(double dbm)
    {
        _dbm = dbm;
        if (dbm >= _peak || (DateTime.UtcNow - _peakAt).TotalSeconds > 1.5) { _peak = dbm; _peakAt = DateTime.UtcNow; }
        else _peak -= 0.6;
        InvalidateVisual();
    }

    /// <summary>0..1 position of a dBm value on the scale.</summary>
    static double Pos(double dbm)
    {
        if (dbm <= -73) return Math.Clamp((dbm + 127) / 54.0, 0, 1) * 0.6;
        return 0.6 + Math.Clamp((dbm + 73) / 60.0, 0, 1) * 0.4;
    }

    static Color SegColor(double t)
    {
        // green -> yellow over S1..S9, orange -> red above S9
        if (t < 0.6)
        {
            double u = t / 0.6;
            return Color.FromRgb((byte)(52 + (250 - 52) * u * u), (byte)(211 + (204 - 211) * u), (byte)(153 * (1 - u)));
        }
        double v = (t - 0.6) / 0.4;
        return Color.FromRgb(255, (byte)(150 * (1 - v) + 60 * v), (byte)(40 + 50 * v));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20) return;
        dc.DrawRoundedRectangle(Bg, null, new Rect(0, 0, w, h), 6, 6);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (!_available)
        {
            var t = new FormattedText("No signal meter on this receiver page", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, TextB, dip);
            dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
            return;
        }

        double pad = 8, barH = 14, segGap = 2;
        double segW = (w - 2 * pad - (Segments - 1) * segGap) / Segments;
        double lit = Pos(_dbm);
        for (int i = 0; i < Segments; i++)
        {
            double t = (i + 0.5) / Segments;
            var brush = t <= lit ? SegBrush[i] : Off;
            dc.DrawRoundedRectangle(brush, null, new Rect(pad + i * (segW + segGap), 8, segW, barH), 1.5, 1.5);
        }
        double px = pad + Pos(_peak) * (w - 2 * pad);
        dc.DrawRectangle(Brushes.White, null, new Rect(px - 1, 5, 2, barH + 6));

        string[] labels = { "1", "3", "5", "7", "9", "+20", "+40", "+60" };
        double[] at = { -121, -109, -97, -85, -73, -53, -33, -13 };
        for (int i = 0; i < labels.Length; i++)
        {
            var ft = new FormattedText(labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 10, i < 5 ? TextB : UiKit.Hex("#FF8A80"), dip);
            double x = pad + Pos(at[i]) * (w - 2 * pad) - ft.Width / 2;
            dc.DrawText(ft, new Point(Math.Clamp(x, 2, w - ft.Width - 2), 8 + barH + 4));
        }
    }
}
