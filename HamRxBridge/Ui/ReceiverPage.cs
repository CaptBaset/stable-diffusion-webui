using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HamRxBridge.Rig;
using HamRxBridge.Sdr;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge.Ui;

/// <summary>
/// Joins the control panel, one receiver view and the radio. This class owns the tuned state.
///
/// Three sources can change the frequency: our panel, the radio, and the receiver itself.
/// Echoes (a source repeating what we just told it) are ignored, using a small tolerance and
/// short hold off windows, so the three never fight.
/// </summary>
public sealed class ReceiverPage : UserControl, IDisposable
{
    readonly ReceiverEntry _rx;
    readonly RigHub _rig;
    readonly ControlPanel _panel = new();
    readonly IReceiverView _view;
    readonly TextBlock _title = Label("", 16, "TextBrush", true);
    readonly TextBlock _sub = Label("", 12, "DimBrush");
    readonly TextBlock _method = Label("", 11, "AccentBrush", true);
    readonly System.Windows.Controls.Primitives.ToggleButton _saveBtn;

    double _hz;
    RigMode _mode;
    double _bw;
    DateTime _rigHoldUntil = DateTime.MinValue;
    double _lastRigHz;
    RigMode? _lastRigMode;
    bool _lastTx;

    public event Action<string, StatusLevel>? StatusChanged;
    public event Action<ReceiverEntry>? SwitchToWebPage;

    public ReceiverEntry Receiver => _rx;
    public bool IsNative { get; }

    public ReceiverPage(ReceiverEntry rx, bool native, RigHub rig)
    {
        _rx = rx;
        _rig = rig;
        IsNative = native && rx.Kind == SdrKind.Kiwi;

        // Start where the radio is if it is online and syncing, else where we left off.
        _hz = AppSettings.Current.LastHz is > 1000 and < 32e6 ? AppSettings.Current.LastHz : 7_100_000;
        _mode = RigModeInfo.DefaultForFrequency(_hz);
        var b = rig.Backend;
        if (AppSettings.Current.Sync != SyncMode.Off && b.Link == RigLink.Online && b.FreqHz > 0)
        {
            _hz = b.FreqHz;
            _mode = b.Mode ?? RigModeInfo.DefaultForFrequency(_hz);
        }
        _bw = Passbands.DefaultWidth(_mode);

        if (IsNative)
        {
            var k = new KiwiNativeView(rx);
            k.OpenWebPageRequested += () => SwitchToWebPage?.Invoke(_rx);
            k.WheelStep += dir => FromPanel(_hz + dir * _panel.StepHz, _mode);
            _view = k;
        }
        else _view = new WebReceiverView(rx, _hz, _mode);

        // ---- layout
        var root = new Grid { Margin = new Thickness(12, 10, 12, 8) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_panel);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new StackPanel { Margin = new Thickness(2, 0, 0, 8) };
        _saveBtn = Tog("", (_, _) => ToggleSaved(), Res("WarnBrush"));
        _saveBtn.ToolTip = "Save this receiver to My receivers";
        UpdateSaveBtn();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(_saveBtn);
        titleRow.Children.Add(_title);
        titleRow.Children.Add(_method);
        head.Children.Add(titleRow);
        head.Children.Add(_sub);
        right.Children.Add(head);
        Grid.SetRow(_view.Element, 1);
        right.Children.Add(_view.Element);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;

        _title.Text = rx.Name;
        _sub.Text = string.Join("     ", new[]
        {
            rx.Location,
            rx.Kind == SdrKind.Kiwi ? $"KiwiSDR  {rx.Host}:{rx.Port}" : "WebSDR  " + rx.BaseUrl,
            rx.Antenna
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _method.Margin = new Thickness(14, 0, 0, 0);

        // ---- wiring
        _panel.FrequencyRequested += hz => FromPanel(hz, _mode);
        _panel.ModeRequested += m => { _bw = Passbands.DefaultWidth(m); FromPanel(_hz, m); };
        _panel.BandRequested += (hz, m) => { _bw = Passbands.DefaultWidth(m); FromPanel(hz, m); };
        _panel.BandwidthRequested += w => { _bw = w; PushToView(); _panel.ShowState(_hz, _mode, _bw); };
        _panel.VolumeChanged += v => _view.SetVolume(v);
        _panel.MuteChanged += m => _view.SetMuted(m);
        _panel.AgcChanged += a => _view.SetAgc(a);
        _panel.SyncChanged += _ => { OnRig(); };

        _view.ReceiverTuned += FromReceiver;
        _view.Signal += d => _panel.ShowSignal(d);
        _view.StatusChanged += (s, l) => { StatusChanged?.Invoke(s, l); _method.Text = _view.ControlMethod; };
        _view.CapsChanged += () => { _panel.ApplyCaps(_view.Caps); _method.Text = _view.ControlMethod; };

        _rig.Changed += OnRig;
        PreviewKeyDown += OnKey;

        _panel.ApplyCaps(_view.Caps);
        _panel.ShowState(_hz, _mode, _bw);
        _panel.ShowRig(_rig.Backend, AppSettings.Current.Sync);
        _method.Text = _view.ControlMethod;
        _view.SetVolume(AppSettings.Current.Volume);
        _lastRigHz = _rig.Backend.FreqHz;
        _lastRigMode = _rig.Backend.Mode;
        PushToView();
    }

    void ToggleSaved()
    {
        AppSettings.Current.ToggleFavorite(_rx);
        UpdateSaveBtn();
        StatusChanged?.Invoke(AppSettings.Current.IsFavorite(_rx) ? "Saved to My receivers" : "Removed from My receivers", StatusLevel.Good);
    }

    void UpdateSaveBtn()
    {
        bool saved = AppSettings.Current.IsFavorite(_rx);
        _saveBtn.IsChecked = saved;
        _saveBtn.Content = IconText(saved ? "\uE735" : "\uE734", saved ? "Saved" : "Save");
    }

    // ------------------------------------------------------------- the three sources

    /// <summary>Our own panel: tune the receiver, and the radio too in two way mode.</summary>
    void FromPanel(double hz, RigMode mode)
    {
        hz = Math.Clamp(Math.Round(hz), 1000, 32_000_000);
        _hz = hz; _mode = mode;
        _panel.ShowState(_hz, _mode, _bw);
        PushToView();
        PushToRig();
    }

    /// <summary>The operator tuned the receiver itself (waterfall or the web page).</summary>
    void FromReceiver(double hz, RigMode? mode)
    {
        if (Math.Abs(hz - _hz) < 1 && (!mode.HasValue || mode == _mode)) return;
        _hz = hz;
        if (mode.HasValue && mode != _mode) { _mode = mode.Value; _bw = Passbands.DefaultWidth(_mode); }
        _panel.ShowState(_hz, _mode, _bw);
        if (IsNative) PushToView();       // native view needs the server retuned; web pages already moved
        PushToRig();
    }

    /// <summary>The radio changed (or its link state did).</summary>
    void OnRig()
    {
        var b = _rig.Backend;
        var sync = AppSettings.Current.Sync;
        _panel.ShowRig(b, sync);

        if (b.Transmitting != _lastTx)
        {
            _lastTx = b.Transmitting;
            _view.SetTxMuted(_lastTx && AppSettings.Current.MuteOnTx);
            StatusChanged?.Invoke(_lastTx ? "Transmitting: receiver muted" : "Receiving", _lastTx ? StatusLevel.Warn : StatusLevel.Info);
        }

        if (b.Link != RigLink.Online || b.FreqHz <= 0) return;
        bool rigMoved = Math.Abs(b.FreqHz - _lastRigHz) >= 1 || b.Mode != _lastRigMode;
        _lastRigHz = b.FreqHz;
        _lastRigMode = b.Mode;
        if (sync == SyncMode.Off || !rigMoved) return;
        if (DateTime.UtcNow < _rigHoldUntil) return;          // echo of what we just sent the radio

        var mode = b.Mode ?? _mode;
        if (Math.Abs(b.FreqHz - _hz) < 1 && mode == _mode) return;
        if (mode != _mode) _bw = Passbands.DefaultWidth(mode);
        _hz = b.FreqHz;
        _mode = mode;
        _panel.ShowState(_hz, _mode, _bw);
        PushToView();
    }

    void PushToView()
    {
        var (lo, hi) = Passbands.Edges(_mode, _bw);
        _view.Tune(_hz, _mode, lo, hi);
        AppSettings.Current.LastHz = _hz;
    }

    void PushToRig()
    {
        if (AppSettings.Current.Sync != SyncMode.TwoWay || !_rig.Online) return;
        var b = _rig.Backend;
        _rigHoldUntil = DateTime.UtcNow.AddMilliseconds(700);
        if (Math.Abs(b.FreqHz - _hz) >= 1) b.SetFrequency(_hz);
        if (b.Mode != _mode) b.SetMode(_mode);
        _lastRigHz = _hz;
        _lastRigMode = _mode;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        int dir = e.Key switch { Key.Up or Key.Right => 1, Key.Down or Key.Left => -1, _ => 0 };
        int mult = e.Key is Key.PageUp ? 10 : e.Key is Key.PageDown ? -10 : 0;
        if (dir != 0) { FromPanel(_hz + dir * _panel.StepHz, _mode); e.Handled = true; }
        else if (mult != 0) { FromPanel(_hz + mult * _panel.StepHz, _mode); e.Handled = true; }
    }

    public void Dispose()
    {
        _rig.Changed -= OnRig;
        _view.Dispose();
        AppSettings.Current.Save();
    }
}
