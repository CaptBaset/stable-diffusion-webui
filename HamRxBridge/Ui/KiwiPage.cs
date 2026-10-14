using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using HamRxBridge.Rig;
using HamRxBridge.Sdr;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge.Ui;

/// <summary>Native "pro" view for a KiwiSDR: panadapter, waterfall, audio, S-meter.</summary>
public sealed class KiwiPage : UserControl, ITunable, IDisposable
{
    static readonly int[] Steps = { 10, 100, 500, 1000, 5000, 9000, 10000 };

    readonly ReceiverEntry _rx;
    readonly AudioOut _audio = new();
    readonly PanadapterControl _pan = new();
    readonly CancellationTokenSource _cts = new();
    readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    KiwiClient? _kiwi;

    double _hz = 7_100_000;
    RigMode _mode = RigMode.LSB;
    double _bw = 2400;
    int _zoom = 7;
    int _stepIdx = 3;
    int _rows, _silentSeconds;
    bool _disposed;

    readonly TextBlock _freqText = new();
    readonly TextBlock _sText = Label("S0", 16, "TextBrush", true);
    readonly TextBlock _dbmText = Label("", 12, "DimBrush");
    readonly TextBlock _status = Label("", 12, "DimBrush");
    readonly TextBlock _diag = Label("", 11, "DimBrush");
    readonly ProgressBar _meter = new() { Minimum = 0, Maximum = 100, Height = 12, Width = 220 };
    readonly TextBox _freqBox = new() { Width = 110, FontSize = 14 };
    readonly TextBox _pwBox = new() { Width = 120, Visibility = Visibility.Collapsed };
    readonly Slider _bwSlider = new() { Minimum = 0, Maximum = 1000, Width = 150 };
    readonly TextBlock _bwText = Label("", 12);
    readonly Slider _vol = new() { Minimum = 0, Maximum = 1, Width = 100 };
    readonly Button _stepBtn;
    readonly Button _reconnect;
    readonly Dictionary<RigMode, ToggleButton> _modeBtns = new();
    readonly ToggleButton _agcBtn, _muteBtn;

    public event Action<double, RigMode>? UserTuned;
    public event Action? OpenWebViewRequested;

    public KiwiPage(ReceiverEntry rx)
    {
        _rx = rx;
        _stepBtn = Btn("", (_, _) => { _stepIdx = (_stepIdx + 1) % Steps.Length; UpdateStep(); });
        _reconnect = Btn("Reconnect", async (_, _) => await ConnectAsync());
        _reconnect.Visibility = Visibility.Collapsed;
        _agcBtn = Tog("AGC", async (_, _) => { if (_kiwi != null) await _kiwi.SetAgc(_agcBtn!.IsChecked == true); });
        _agcBtn.IsChecked = true;
        _muteBtn = Tog("Mute", (_, _) => _audio.Muted = _muteBtn!.IsChecked == true);

        _mode = RigModeInfo.DefaultForFrequency(_hz);
        _bw = DefaultBw(_mode);
        BuildLayout();
        WireEvents();
        _audio.Volume = (float)AppSettings.Current.Volume;
        _vol.Value = AppSettings.Current.Volume;
        UpdateStep();
        UpdateModeButtons();
        UpdateBwUi();
        RefreshReadout();

        Loaded += async (_, _) => await ConnectAsync();
        _watchdog.Tick += (_, _) => Watchdog();
        _watchdog.Start();
    }

    // ---------------------------------------------------------------- layout

    void BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // row 0: big readout + S meter + server name
        _freqText.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _freqText.FontSize = 54;
        _freqText.FontWeight = FontWeights.Bold;
        _freqText.Foreground = Res("AccentBrush");
        _freqText.Margin = new Thickness(6, 0, 24, 0);

        _meter.Foreground = Res("GoodBrush");
        _meter.Background = Res("Panel2Brush");
        _meter.BorderBrush = Res("LineBrush");

        var meterBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var meterTop = new StackPanel { Orientation = Orientation.Horizontal };
        meterTop.Children.Add(_sText);
        meterTop.Children.Add(_dbmText);
        meterBox.Children.Add(meterTop);
        meterBox.Children.Add(_meter);

        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30, 0, 0, 0) };
        title.Children.Add(Label(_rx.Name, 15, "TextBrush", true));
        title.Children.Add(Label(string.Join("   ", new[] { _rx.Location, _rx.Host + ":" + _rx.Port }.Where(s => s.Length > 0)), 12, "DimBrush"));

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(_freqText);
        head.Children.Add(meterBox);
        head.Children.Add(title);
        root.Children.Add(Place(Card(head, 8), 0));

        // row 1: mode, bandwidth, step, agc, volume
        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var m in new[] { RigMode.LSB, RigMode.USB, RigMode.CW, RigMode.AM, RigMode.NFM })
        {
            var mm = m;
            var b = Tog(m == RigMode.NFM ? "FM" : m.ToString(), (_, _) => SetMode(mm, true));
            _modeBtns[m] = b;
            bar.Children.Add(b);
        }
        bar.Children.Add(Label("Bandwidth", 12, "DimBrush"));
        bar.Children.Add(_bwSlider);
        bar.Children.Add(_bwText);
        bar.Children.Add(_stepBtn);
        bar.Children.Add(Label("Go to", 12, "DimBrush"));
        _freqBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && TryParseFrequency(_freqBox.Text, out var hz)) { SetFrequency(hz, true); _freqBox.Text = ""; }
        };
        bar.Children.Add(_freqBox);
        bar.Children.Add(_agcBtn);
        bar.Children.Add(_muteBtn);
        bar.Children.Add(Label("Vol", 12, "DimBrush"));
        bar.Children.Add(_vol);
        bar.Children.Add(Btn("Zoom -", (_, _) => ChangeZoom(-1)));
        bar.Children.Add(Btn("Zoom +", (_, _) => ChangeZoom(+1)));
        bar.Children.Add(Btn("Auto range", (_, _) => _pan.AutoRange()));
        bar.Children.Add(Btn("Web view", (_, _) => OpenWebViewRequested?.Invoke()));
        root.Children.Add(Place(bar, 1));

        // row 2: band buttons
        var bands = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        foreach (var (name, khz) in new[] { ("160m", 1840), ("80m", 3700), ("60m", 5357), ("40m", 7100), ("30m", 10120),
                     ("20m", 14200), ("17m", 18130), ("15m", 21200), ("12m", 24950), ("10m", 28400) })
        {
            double hz = khz * 1000.0;
            bands.Children.Add(Btn(name, (_, _) =>
            {
                _mode = RigModeInfo.DefaultForFrequency(hz);
                _bw = DefaultBw(_mode);
                UpdateModeButtons(); UpdateBwUi();
                SetFrequency(hz, true);
            }));
        }
        root.Children.Add(Place(bands, 2));

        // row 3: panadapter
        var panCard = new Border { BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _pan, ClipToBounds = true };
        root.Children.Add(Place(panCard, 3));

        // row 4: status
        var st = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        _pwBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            AppSettings.Current.KiwiPassword = _pwBox.Text; AppSettings.Current.Save();
            await ConnectAsync();
        };
        right.Children.Add(_pwBox);
        right.Children.Add(_reconnect);
        DockPanel.SetDock(right, Dock.Right);
        st.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_status);
        left.Children.Add(_diag);
        st.Children.Add(left);
        root.Children.Add(Place(st, 4));

        Content = root;
        FrameworkElement Place(FrameworkElement e, int row) { Grid.SetRow(e, row); return e; }
    }

    void WireEvents()
    {
        _pan.TuneRequested += hz => SetFrequency(hz, true);
        _pan.WheelStep += dir => SetFrequency(_hz + dir * Steps[_stepIdx], true);
        _bwSlider.ValueChanged += (_, _) =>
        {
            _bw = Math.Round(100 * Math.Pow(160, _bwSlider.Value / 1000.0) / 50) * 50;
            _bwText.Text = $"{_bw:0} Hz";
            ApplyTune(false);
        };
        _vol.ValueChanged += (_, _) =>
        {
            _audio.Volume = (float)_vol.Value;
            AppSettings.Current.Volume = _vol.Value;
        };
    }

    // --------------------------------------------------------------- tuning

    static double DefaultBw(RigMode m) => m switch
    {
        RigMode.USB or RigMode.LSB => 2400,
        RigMode.CW => 400,
        RigMode.AM => 9000,
        _ => 12000
    };

    static (int lo, int hi) Passband(RigMode m, double bw) => m switch
    {
        RigMode.USB => (300, (int)(300 + bw)),
        RigMode.LSB => ((int)-(300 + bw), -300),
        RigMode.CW => ((int)(500 - bw / 2), (int)(500 + bw / 2)),
        _ => ((int)-(bw / 2), (int)(bw / 2))
    };

    void SetMode(RigMode m, bool fromUser)
    {
        _mode = m;
        _bw = DefaultBw(m);
        UpdateModeButtons();
        UpdateBwUi();
        ApplyTune(fromUser);
    }

    void SetFrequency(double hz, bool fromUser)
    {
        _hz = Math.Clamp(hz, 1000, 29_999_000);
        ApplyTune(fromUser);
    }

    void ApplyTune(bool fromUser)
    {
        var (lo, hi) = Passband(_mode, _bw);
        _pan.TunedHz = _hz;
        _pan.LowCutHz = lo;
        _pan.HighCutHz = hi;
        _pan.RefreshOverlay();
        RefreshReadout();
        if (_kiwi != null)
        {
            _ = _kiwi.SetTuning(_mode, _hz, lo, hi);
            double span = _kiwi.BandwidthHz / Math.Pow(2, _zoom);
            if (Math.Abs(_hz - _pan.CenterHz) > span * 0.40 || _rows == 0)
                _ = _kiwi.SetView(_zoom, _hz);
        }
        if (fromUser) UserTuned?.Invoke(_hz, _mode);
    }

    void ChangeZoom(int d)
    {
        _zoom = Math.Clamp(_zoom + d, 0, 14);
        if (_kiwi != null) _ = _kiwi.SetView(_zoom, _hz);
        RefreshReadout();
    }

    // ITunable
    public void TuneFromRig(double hz, RigMode mode)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => TuneFromRig(hz, mode)); return; }
        if (mode != _mode) { _mode = mode; _bw = DefaultBw(mode); UpdateModeButtons(); UpdateBwUi(); }
        SetFrequency(hz, false);
    }

    public void SetTxMute(bool muted) => Dispatcher.BeginInvoke(() => _audio.TxMuted = muted);

    // --------------------------------------------------------------- display

    void UpdateStep()
    {
        int s = Steps[_stepIdx];
        _stepBtn.Content = s >= 1000 ? $"Step {s / 1000} kHz" : $"Step {s} Hz";
        _pan.SnapHz = s;
    }

    void UpdateModeButtons()
    {
        foreach (var kv in _modeBtns) kv.Value.IsChecked = kv.Key == _mode;
    }

    void UpdateBwUi()
    {
        double x = 1000 * Math.Log(Math.Max(100, _bw) / 100.0) / Math.Log(160);
        _bwSlider.Value = Math.Clamp(x, 0, 1000);
        _bwText.Text = $"{_bw:0} Hz";
    }

    void RefreshReadout()
    {
        _freqText.Text = FormatHz(_hz);
        if (_kiwi != null || _rows > 0) _diag.Text = $"   zoom {_zoom}   view {_pan.SpanHz / 1000:0.#} kHz";
    }

    void ShowSignal(double dbm)
    {
        _meter.Value = Math.Clamp(dbm + 127, 0, 100);
        _dbmText.Text = $"{dbm:0} dBm";
        _sText.Text = dbm <= -73 ? $"S{Math.Max(0, Math.Round(9 + (dbm + 73) / 6)):0}" : $"S9+{dbm + 73:0} dB";
    }

    void SetStatus(string text, bool bad = false)
    {
        _status.Text = text;
        _status.Foreground = Res(bad ? "BadBrush" : "DimBrush");
    }

    // ------------------------------------------------------------ connection

    async Task ConnectAsync()
    {
        if (_disposed) return;
        _kiwi?.Dispose();
        _rows = 0; _silentSeconds = 0;
        _reconnect.Visibility = Visibility.Collapsed;
        _pwBox.Visibility = Visibility.Collapsed;
        var k = _kiwi = new KiwiClient();
        k.AudioReceived += pcm => _audio.Write(pcm);
        k.SignalDbm += d => Dispatcher.BeginInvoke(() => ShowSignal(d));
        k.WaterfallRow += (bins, start, span) => Dispatcher.BeginInvoke(() =>
        {
            _rows++; _silentSeconds = 0;
            _pan.PushRow(bins, start, span);
            if (_rows == 1) SetStatus("Connected");
            if (_rows % 10 == 0)
                _diag.Text = $"   zoom {_zoom}   view {span / 1000:0.#} kHz   raw {_pan.LastRowMin:0} to {_pan.LastRowMax:0} dBm";
        });
        k.Log += s => Dispatcher.BeginInvoke(() => SetStatus(s));
        k.Disconnected += msg => Dispatcher.BeginInvoke(() => OnDisconnected(msg));

        SetStatus($"Connecting to {_rx.Host}:{_rx.Port} ...");
        try
        {
            await k.ConnectAsync(_rx, AppSettings.Current.KiwiPassword, _cts.Token);
            if (_disposed) return;
            await k.SetAgc(_agcBtn.IsChecked == true);
            _rows = 0;
            ApplyTune(false);
            await k.SetView(_zoom, _hz);
            SetStatus("Waiting for data ...");
        }
        catch (Exception ex)
        {
            OnDisconnected("Could not connect: " + ex.Message);
        }
    }

    void OnDisconnected(string msg)
    {
        if (_disposed) return;
        SetStatus(msg, true);
        _reconnect.Visibility = Visibility.Visible;
        if (msg.Contains("password", StringComparison.OrdinalIgnoreCase)) _pwBox.Visibility = Visibility.Visible;
    }

    void Watchdog()
    {
        if (_kiwi == null || _rows > 0 || _reconnect.Visibility == Visibility.Visible) return;
        if (++_silentSeconds == 12)
            SetStatus("Connected but no waterfall data. This receiver may speak a different protocol version, so try Web view.", true);
    }

    public void Dispose()
    {
        _disposed = true;
        _watchdog.Stop();
        _cts.Cancel();
        _kiwi?.Dispose();
        _audio.Dispose();
        AppSettings.Current.Save();
    }
}
