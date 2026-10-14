using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HamRxBridge.Ui;

/// <summary>Spectrum on top, frequency scale, scrolling waterfall below. Click or drag to tune.</summary>
public sealed class PanadapterControl : FrameworkElement
{
    const int Bins = 1024;
    const int WfHeight = 700;

    readonly float[] _spec = new float[Bins];
    readonly int[] _pixels = new int[Bins * WfHeight];
    readonly WriteableBitmap _bmp = new(Bins, WfHeight, 96, 96, PixelFormats.Bgra32, null);
    readonly int[] _palette = BuildPalette();
    bool _haveSpec;
    bool _dragging;

    public double CenterHz { get; private set; } = 15_000_000;
    public double SpanHz { get; private set; } = 30_000_000;
    public double TunedHz { get; set; } = 7_100_000;
    public double LowCutHz { get; set; } = 300;
    public double HighCutHz { get; set; } = 2700;
    public double MinDb { get; set; } = -120;
    public double MaxDb { get; set; } = -40;
    public double SnapHz { get; set; } = 100;
    public double LastRowMin { get; private set; }
    public double LastRowMax { get; private set; }

    public event Action<double>? TuneRequested;
    public event Action<int>? WheelStep;

    double ViewStart => CenterHz - SpanHz / 2;

    public PanadapterControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    public void PushRow(byte[] bins, double startHz, double spanHz)
    {
        double newCenter = startHz + spanHz / 2;
        bool viewChanged = Math.Abs(spanHz - SpanHz) > 1 || Math.Abs(newCenter - CenterHz) > spanHz / Bins;
        if (viewChanged)
        {
            Array.Clear(_pixels);
            _haveSpec = false;
        }
        CenterHz = newCenter;
        SpanHz = spanHz;

        double lo = 0, hi = -999;
        lo = 999;
        for (int i = 0; i < Bins; i++)
        {
            float db = bins[i] - 255f;
            if (db < lo) lo = db;
            if (db > hi) hi = db;
            _spec[i] = _haveSpec ? _spec[i] * 0.55f + db * 0.45f : db;
        }
        _haveSpec = true;
        LastRowMin = lo; LastRowMax = hi;

        Array.Copy(_pixels, 0, _pixels, Bins, Bins * (WfHeight - 1));
        double range = Math.Max(10, MaxDb - MinDb);
        for (int i = 0; i < Bins; i++)
        {
            double n = (bins[i] - 255.0 - MinDb) / range;
            int idx = (int)Math.Clamp(n * 255.0, 0, 255);
            _pixels[i] = _palette[idx];
        }
        _bmp.WritePixels(new Int32Rect(0, 0, Bins, WfHeight), _pixels, Bins * 4, 0);
        InvalidateVisual();
    }

    /// <summary>Set the dB window from what is currently on screen.</summary>
    public void AutoRange()
    {
        if (!_haveSpec) return;
        var sorted = _spec.OrderBy(x => x).ToArray();
        MinDb = Math.Round(sorted[(int)(Bins * 0.10)] - 6);
        MaxDb = Math.Round(Math.Max(MinDb + 30, sorted[(int)(Bins * 0.995)] + 8));
        InvalidateVisual();
    }

    public void RefreshOverlay() => InvalidateVisual();

    // ---- input ----------------------------------------------------------

    double HzAt(double x) => ViewStart + x / Math.Max(1, ActualWidth) * SpanHz;

    double Snap(double hz) => SnapHz <= 0 ? Math.Round(hz) : Math.Round(hz / SnapHz) * SnapHz;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        TuneRequested?.Invoke(Snap(HzAt(e.GetPosition(this).X)));
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) TuneRequested?.Invoke(Snap(HzAt(e.GetPosition(this).X)));
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        WheelStep?.Invoke(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    // ---- drawing --------------------------------------------------------

    static readonly Brush Bg = Frozen(new SolidColorBrush(Color.FromRgb(6, 9, 14)));
    static readonly Pen GridPen = FrozenPen(Color.FromArgb(60, 120, 150, 190));
    static readonly Pen SpecPen = FrozenPen(Color.FromRgb(120, 220, 255), 1.2);
    static readonly Pen TunePen = FrozenPen(Color.FromRgb(255, 90, 95), 1.5);
    static readonly Brush BandBrush = Frozen(new SolidColorBrush(Color.FromArgb(55, 255, 90, 95)));
    static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(150, 170, 195)));

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Pen FrozenPen(Color c, double w = 1) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    FormattedText Text(string s, double size = 11) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));

        double specH = Math.Max(80, h * 0.34), scaleH = 20;
        double wfTop = specH + scaleH;

        // horizontal dB grid
        double range = Math.Max(10, MaxDb - MinDb);
        for (double db = Math.Ceiling(MinDb / 10) * 10; db <= MaxDb; db += 10)
        {
            double y = specH - (db - MinDb) / range * specH;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            var t = Text($"{db:0}", 10);
            dc.DrawText(t, new Point(3, y - 13));
        }

        // vertical frequency grid + scale
        double step = NiceStep(SpanHz / Math.Max(2, w / 110));
        double first = Math.Ceiling(ViewStart / step) * step;
        for (double f = first; f < ViewStart + SpanHz; f += step)
        {
            double x = (f - ViewStart) / SpanHz * w;
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, specH));
            var label = Text(FormatFreq(f, step), 11);
            dc.DrawText(label, new Point(x - label.Width / 2, specH + 3));
        }

        // spectrum
        if (_haveSpec)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, specH), true, true);
                for (int i = 0; i < Bins; i++)
                {
                    double x = (i + 0.5) / Bins * w;
                    double y = specH - (Math.Clamp(_spec[i], MinDb, MaxDb) - MinDb) / range * specH;
                    g.LineTo(new Point(x, y), true, false);
                }
                g.LineTo(new Point(w, specH), true, false);
            }
            geo.Freeze();
            var fill = new LinearGradientBrush(Color.FromArgb(150, 47, 181, 255), Color.FromArgb(10, 47, 181, 255), 90);
            fill.Freeze();
            dc.DrawGeometry(fill, SpecPen, geo);
        }

        // waterfall
        dc.DrawImage(_bmp, new Rect(0, wfTop, w, Math.Max(1, h - wfTop)));

        // passband and tuned marker
        double xt = (TunedHz - ViewStart) / SpanHz * w;
        double x1 = (TunedHz + Math.Min(LowCutHz, HighCutHz) - ViewStart) / SpanHz * w;
        double x2 = (TunedHz + Math.Max(LowCutHz, HighCutHz) - ViewStart) / SpanHz * w;
        dc.DrawRectangle(BandBrush, null, new Rect(x1, 0, Math.Max(2, x2 - x1), h));
        dc.DrawLine(TunePen, new Point(xt, 0), new Point(xt, h));
    }

    static double NiceStep(double raw)
    {
        double pow = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double m = raw / pow;
        double n = m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10;
        return n * pow;
    }

    static string FormatFreq(double hz, double step)
    {
        double mhz = hz / 1e6;
        if (step >= 1e6) return mhz.ToString("0", CultureInfo.InvariantCulture);
        if (step >= 1e5) return mhz.ToString("0.0", CultureInfo.InvariantCulture);
        if (step >= 1e4) return mhz.ToString("0.00", CultureInfo.InvariantCulture);
        return mhz.ToString("0.000", CultureInfo.InvariantCulture);
    }

    static int[] BuildPalette()
    {
        // black, deep blue, cyan, green, yellow, red, white
        (double p, Color c)[] stops =
        {
            (0.00, Color.FromRgb(0, 0, 8)),
            (0.20, Color.FromRgb(0, 20, 120)),
            (0.40, Color.FromRgb(0, 150, 220)),
            (0.58, Color.FromRgb(40, 220, 120)),
            (0.75, Color.FromRgb(250, 230, 40)),
            (0.90, Color.FromRgb(250, 70, 30)),
            (1.00, Color.FromRgb(255, 255, 255))
        };
        var pal = new int[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            int k = 0;
            while (k < stops.Length - 2 && t > stops[k + 1].p) k++;
            double u = (t - stops[k].p) / (stops[k + 1].p - stops[k].p);
            u = Math.Clamp(u, 0, 1);
            byte R = (byte)(stops[k].c.R + (stops[k + 1].c.R - stops[k].c.R) * u);
            byte G = (byte)(stops[k].c.G + (stops[k + 1].c.G - stops[k].c.G) * u);
            byte B = (byte)(stops[k].c.B + (stops[k + 1].c.B - stops[k].c.B) * u);
            pal[i] = (255 << 24) | (R << 16) | (G << 8) | B;
        }
        return pal;
    }
}
