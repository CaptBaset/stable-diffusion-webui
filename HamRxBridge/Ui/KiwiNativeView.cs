using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HamRxBridge.Rig;
using HamRxBridge.Sdr;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge.Ui;

/// <summary>Native KiwiSDR view: our own panadapter, waterfall, audio and S-meter.</summary>
public sealed class KiwiNativeView : UserControl, IReceiverView
{
    readonly ReceiverEntry _rx;
    readonly PanadapterControl _pan = new();
    readonly AudioOut _audio = new();
    readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly TextBlock _viewText = Label("", 11, "DimBrush");
    readonly TextBox _pwBox = new() { Width = 140, Visibility = Visibility.Collapsed, ToolTip = "Receiver password, then press Enter" };
    readonly Button _retry;
    KiwiClient? _kiwi;
    CancellationTokenSource _cts = new();

    double _hz = 7_100_000;
    RigMode _mode = RigMode.LSB;
    int _lo = -2900, _hi = -200;
    bool _agc = true;
    int _zoom = AppSettings.Current.KiwiZoom;
    int _rows, _quiet, _retries;
    bool _disposed, _gotAudio;

    public FrameworkElement Element => this;
    public ReceiverCaps Caps { get; } = new() { Bandwidth = true, SMeter = true, Volume = true, Agc = true, Dsp = true };
    bool _nb, _nr, _an, _paused;
    Window? _window;
    public string ControlMethod => "Native KiwiSDR connection";

    public event Action<double, RigMode?>? ReceiverTuned;
    public event Action<double>? Signal;
    public event Action<string, StatusLevel>? StatusChanged;
    public event Action? CapsChanged { add { } remove { } }
    public event Action? OpenWebPageRequested;

    public KiwiNativeView(ReceiverEntry rx)
    {
        _rx = rx;
        _retry = Btn("Reconnect", async (_, _) => { _retries = 0; await ConnectAsync(); });
        _retry.Visibility = Visibility.Collapsed;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };   // wraps on small screens
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(IconBtn(Glyph.ZoomOut, "Zoom out", (_, _) => ChangeZoom(-1)));
        left.Children.Add(IconBtn(Glyph.ZoomIn, "Zoom in", (_, _) => ChangeZoom(+1)));
        left.Children.Add(IconBtn(Glyph.Target, "Centre", (_, _) => _ = _kiwi?.SetView(_zoom, _hz)));
        left.Children.Add(IconBtn(Glyph.Contrast, "Auto contrast", (_, _) => _pan.AutoRange()));
        left.Children.Add(_viewText);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_pwBox);
        right.Children.Add(_retry);
        right.Children.Add(IconBtn(Glyph.Globe, "Open Kiwi web page", (_, _) => OpenWebPageRequested?.Invoke(),
            "Show this receiver's own KiwiSDR page instead (still controlled by this app)"));
        bar.Children.Add(left);
        bar.Children.Add(right);
        root.Children.Add(bar);

        var frame = new Border
        {
            BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = _pan, ClipToBounds = true
        };
        Grid.SetRow(frame, 1);
        root.Children.Add(frame);
        Content = root;

        _pan.TuneRequested += hz => { _hz = hz; _pan.TunedHz = hz; _pan.RefreshOverlay(); ReceiverTuned?.Invoke(hz, null); };
        _pan.WheelStep += dir => WheelStep?.Invoke(dir);
        _pwBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            AppSettings.Current.KiwiPassword = _pwBox.Text;
            AppSettings.Current.Save();
            _retries = 0;
            await ConnectAsync();
        };

        _audio.Volume = (float)AppSettings.Current.Volume;
        _watchdog.Tick += (_, _) => Watchdog();
        Loaded += async (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window != null) _window.StateChanged += OnWindowState;
            if (_kiwi == null) await ConnectAsync();
        };
        Unloaded += (_, _) => { if (_window != null) _window.StateChanged -= OnWindowState; };
    }

    /// <summary>Mouse wheel over the waterfall, handled by the page with the current step size.</summary>
    public event Action<int>? WheelStep;

    // ------------------------------------------------------------- IReceiverView

    public void Tune(double hz, RigMode mode, int lowCut, int highCut)
    {
        bool cwChanged = (mode == RigMode.CW) != (_mode == RigMode.CW);
        _hz = hz; _mode = mode; _lo = lowCut; _hi = highCut;
        if (cwChanged && _kiwi != null)
        {
            _ = _kiwi.SetAgc(_agc, mode == RigMode.CW);                   // CW has its own AGC threshold
            _ = _kiwi.SetDsp(_nb, _nr, _an, mode == RigMode.CW);          // and its own noise reduction
        }
        double off = Passbands.CarrierOffset(mode);
        // red line on the dial (signal) frequency, passband drawn where it really listens
        _pan.TunedHz = hz; _pan.LowCutHz = lowCut + off; _pan.HighCutHz = highCut + off;
        _pan.RefreshOverlay();
        if (_kiwi == null) return;
        _ = _kiwi.SetTuning(mode, hz + off, lowCut, highCut);
        double span = _kiwi.BandwidthHz / Math.Pow(2, _zoom);
        if (_rows == 0 || Math.Abs(hz - _pan.CenterHz) > span * 0.40) _ = _kiwi.SetView(_zoom, hz);
    }

    public void SetMuted(bool muted) => _audio.Muted = muted;
    public void SetTxMuted(bool muted) => _audio.TxMuted = muted;
    public void SetVolume(double v) { _audio.Volume = (float)v; }
    public void SetAgc(bool on) { _agc = on; if (_kiwi != null) _ = _kiwi.SetAgc(on, _mode == RigMode.CW); }

    public void SetDsp(bool noiseBlanker, bool noiseReduction, bool autoNotch)
    {
        _nb = noiseBlanker; _nr = noiseReduction; _an = autoNotch;
        if (_kiwi != null) _ = _kiwi.SetDsp(_nb, _nr, _an, _mode == RigMode.CW);
    }

    /// <summary>
    /// Idle mode: while the window is minimized nothing is drawn and the receiver sends the
    /// waterfall at its slowest rate. Audio keeps playing.
    /// </summary>
    void OnWindowState(object? sender, EventArgs e)
    {
        bool minimized = _window?.WindowState == WindowState.Minimized;
        if (minimized == _paused) return;
        _paused = minimized;
        if (_kiwi != null) _ = _kiwi.SetWaterfallSpeed(minimized ? 1 : 3);
        Log.Write(minimized ? "Idle: waterfall paused" : "Idle: waterfall resumed");
    }

    void ChangeZoom(int d)
    {
        _zoom = Math.Clamp(_zoom + d, 0, _kiwi?.ZoomMax ?? 14);
        AppSettings.Current.KiwiZoom = _zoom;
        if (_kiwi != null) _ = _kiwi.SetView(_zoom, _hz);
    }

    // ------------------------------------------------------------- connection

    async Task ConnectAsync()
    {
        if (_disposed) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _kiwi?.Dispose();
        _rows = 0; _quiet = 0; _gotAudio = false;
        _retry.Visibility = Visibility.Collapsed;
        _pwBox.Visibility = Visibility.Collapsed;

        var k = _kiwi = new KiwiClient();
        k.AudioReceived += pcm =>
        {
            _audio.Write(pcm, k.SampleRate);
            if (!_gotAudio) { _gotAudio = true; Dispatcher.BeginInvoke(() => Status("Connected, audio flowing", StatusLevel.Good)); }
        };
        k.SignalDbm += d => Dispatcher.BeginInvoke(() => Signal?.Invoke(d));
        k.WaterfallRow += (bins, start, span) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(k, _kiwi)) return;
            _rows++; _quiet = 0;
            if (_paused) return;                 // minimized: no drawing at all
            _pan.PushRow(bins, start, span);
            if (_rows == 3) _pan.AutoRange();
            if (_rows % 15 == 1)
                _viewText.Text = $"   zoom {_zoom}   span {span / 1000:0.#} kHz   noise floor {_pan.NoiseFloor:0} dBm";
        });
        k.Info += s => Log.Write($"kiwi {_rx.Host}: {s}");
        k.Disconnected += msg => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(k, _kiwi)) OnDisconnected(msg); });

        Status($"Connecting to {_rx.Host}:{_rx.Port} ...", StatusLevel.Info);
        Log.Write($"Connecting native Kiwi {_rx.Host}:{_rx.Port}");
        try
        {
            string ident = string.IsNullOrWhiteSpace(AppSettings.Current.Callsign) ? "HamRxBridge" : AppSettings.Current.Callsign;
            await k.ConnectAsync(_rx, AppSettings.Current.KiwiPassword, ident, AppSettings.Current.KiwiCompression, _cts.Token);
            if (_disposed) return;
            await k.SetAgc(_agc, _mode == RigMode.CW);
            await k.SetDsp(_nb, _nr, _an, _mode == RigMode.CW);
            await k.SetTuning(_mode, _hz + Passbands.CarrierOffset(_mode), _lo, _hi);
            await k.SetView(_zoom, _hz);
            Status("Connected, waiting for the receiver ...", StatusLevel.Info);
            _watchdog.Start();
        }
        catch (Exception ex)
        {
            OnDisconnected("Could not connect: " + ex.Message);
        }
    }

    void OnDisconnected(string msg)
    {
        if (_disposed) return;
        Log.Write($"Kiwi {_rx.Host} disconnected: {msg}");
        bool refused = _kiwi?.Refusal != null;
        if (msg.Contains("password", StringComparison.OrdinalIgnoreCase)) _pwBox.Visibility = Visibility.Visible;
        _retry.Visibility = Visibility.Visible;
        if (!refused && _retries < 2)
        {
            _retries++;
            Status($"{msg}. Reconnecting ({_retries}/2) ...", StatusLevel.Warn);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += async (_, _) => { t.Stop(); await ConnectAsync(); };
            t.Start();
        }
        else Status(msg, StatusLevel.Bad);
    }

    void Watchdog()
    {
        if (_disposed || _kiwi == null || _retry.Visibility == Visibility.Visible) return;
        if (_rows > 0 && _gotAudio) { _quiet = 0; return; }
        if (++_quiet == 12)
            Status(_rows == 0
                ? "Connected but no waterfall yet. If it stays empty, use 'Open Kiwi web page'."
                : "Waterfall works but no audio yet. Check the Windows sound output.", StatusLevel.Warn);
    }

    void Status(string s, StatusLevel l) => StatusChanged?.Invoke(s, l);

    public void Dispose()
    {
        _disposed = true;
        _watchdog.Stop();
        _cts.Cancel();
        _kiwi?.Dispose();
        _audio.Dispose();
    }
}
