using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace HamRxBridge.Ui;

/// <summary>
/// SDRuno style frequency readout. Hover a digit and turn the mouse wheel to tune that digit,
/// or click its upper half to step up and its lower half to step down.
/// </summary>
public sealed class VfoDisplay : FrameworkElement
{
    const string Layout = "00.000.000";
    static readonly int[] Powers = { 7, 6, -1, 5, 4, 3, -1, 2, 1, 0 };

    static readonly Typeface Face = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    static readonly Brush Lit = UiKit.Hex("#5CE1FF");
    static readonly Brush Dim = UiKit.Hex("#1F3A55");
    static readonly Brush HoverBg = UiKit.Hex("#16324D");
    static readonly Brush UnitBrush = UiKit.Hex("#7F93AD");

    double _hz = 7_100_000;
    int _hover = -1;
    bool _hoverUpper;

    public double FontSizePx { get; set; } = 46;
    public double MaxHz { get; set; } = 32_000_000;

    public double Hz
    {
        get => _hz;
        set { if (Math.Abs(value - _hz) >= 0.5) { _hz = value; InvalidateVisual(); } }
    }

    public event Action<double>? FrequencyEdited;

    public VfoDisplay()
    {
        Cursor = Cursors.SizeNS;
        Focusable = true;
        Effect = new DropShadowEffect { Color = Color.FromRgb(41, 197, 255), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.55 };
        ToolTip = "Mouse wheel over a digit tunes that digit. Click the top or bottom half of a digit to step it.";
    }

    double Dip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    FormattedText Glyph(string s, Brush b, double size) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b, Dip);

    double Cell => Glyph("0", Lit, FontSizePx).WidthIncludingTrailingWhitespace;
    double Line => Glyph("0", Lit, FontSizePx).Height;

    protected override Size MeasureOverride(Size availableSize)
    {
        var unit = Glyph(" MHz", UnitBrush, FontSizePx * 0.32);
        return new Size(Cell * Layout.Length + unit.Width + 8, Line + 4);
    }

    string Digits()
    {
        long v = (long)Math.Round(Math.Clamp(_hz, 0, 99_999_999));
        return $"{v / 1_000_000:00}.{v / 1000 % 1000:000}.{v % 1000:000}";
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        string s = Digits();
        double cell = Cell, line = Line;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool dim = i == 0 && c == '0';                   // a leading zero on the 10 MHz digit
            if (i == _hover)
            {
                dc.DrawRoundedRectangle(HoverBg, null, new Rect(i * cell, 2, cell, line), 4, 4);
                var arrow = new Pen(Lit, 1.4);
                double cx = i * cell + cell / 2;
                if (_hoverUpper) dc.DrawLine(arrow, new Point(cx - 4, 7), new Point(cx + 4, 7));
                else dc.DrawLine(arrow, new Point(cx - 4, line - 3), new Point(cx + 4, line - 3));
            }
            dc.DrawText(Glyph(c.ToString(), dim ? Dim : Lit, FontSizePx), new Point(i * cell, 2));
        }
        var unit = Glyph("MHz", UnitBrush, FontSizePx * 0.32);
        dc.DrawText(unit, new Point(cell * s.Length + 6, line - unit.Height - 4));
    }

    int HitIndex(Point p)
    {
        int i = (int)(p.X / Math.Max(1, Cell));
        return i >= 0 && i < Powers.Length && Powers[i] >= 0 ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        int i = HitIndex(p);
        bool upper = p.Y < Line / 2 + 2;
        if (i != _hover || upper != _hoverUpper) { _hover = i; _hoverUpper = upper; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    void Step(int index, int dir)
    {
        if (index < 0) return;
        double next = Math.Clamp(_hz + dir * Math.Pow(10, Powers[index]), 1_000, MaxHz);
        _hz = next;
        InvalidateVisual();
        FrequencyEdited?.Invoke(next);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int i = _hover >= 0 ? _hover : 5;      // default to the 1 kHz digit
        Step(i, e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        Step(HitIndex(p), p.Y < Line / 2 + 2 ? 1 : -1);
        Focus();
        e.Handled = true;
    }
}
