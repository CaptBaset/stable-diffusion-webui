using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HamRxBridge.Rig;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge.Ui;

/// <summary>The left hand RX control column. It only raises requests; the receiver page decides.</summary>
public sealed class ControlPanel : UserControl
{
    static readonly int[] Steps = { 10, 100, 500, 1000, 5000, 9000, 10000, 100000 };

    readonly VfoDisplay _vfo = new();
    readonly TextBlock _bandBadge = Label("", 12, "TextBrush", true);
    readonly Border _bandPill = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
    readonly TextBlock _modeBadge = Label("", 12, "TextBrush", true);
    readonly Border _modePill = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2) };
    readonly TextBox _entry = new() { ToolTip = "Type 14.205 (MHz) or 7074 (kHz) and press Enter" };
    readonly SMeter _meter = new();
    readonly TextBlock _sText = Label("S0", 18, "TextBrush", true);
    readonly TextBlock _dbm = Label("", 12, "DimBrush");
    readonly Dictionary<RigMode, ToggleButton> _modes = new();
    readonly UniformGrid _presets = new() { Columns = 4 };
    readonly Slider _bwSlider = new() { Minimum = 0, Maximum = 1000 };
    readonly TextBlock _bwText = Label("", 12, "DimBrush");
    readonly List<ToggleButton> _stepBtns = new();
    readonly Slider _vol = new() { Minimum = 0, Maximum = 1 };
    readonly ToggleButton _mute, _agc;
    readonly Ellipse _led = new() { Width = 11, Height = 11, Margin = new Thickness(2, 0, 6, 0) };
    readonly TextBlock _rigName = Label("", 12);
    readonly TextBlock _rigFreq = Label("", 12, "DimBrush");
    readonly ToggleButton _syncOff, _syncOne, _syncTwo;
    readonly CheckBox _muteTx = new() { Content = "Mute receiver while I transmit" };
    readonly UIElement _bwCard, _audioCard;
    bool _suppress;
    RigMode _mode = RigMode.LSB;
    double _bw = 2700;
    int _step = 3;

    public event Action<double>? FrequencyRequested;
    public event Action<RigMode>? ModeRequested;
    public event Action<double, RigMode>? BandRequested;
    public event Action<double>? BandwidthRequested;
    public event Action<double>? VolumeChanged;
    public event Action<bool>? MuteChanged;
    public event Action<bool>? AgcChanged;
    public event Action<bool, bool, bool>? DspChanged;
    readonly System.Windows.Controls.Primitives.ToggleButton _nb, _nr, _an;
    public event Action<SyncMode>? SyncChanged;
    public event Action? MuteOnTxChanged;

    public int StepHz => Steps[_step];

    public ControlPanel()
    {
        Width = 352;
        _mute = Tog("Mute", (_, _) => MuteChanged?.Invoke(_mute!.IsChecked == true), Res("BadBrush"));
        _agc = Tog("AGC", (_, _) => AgcChanged?.Invoke(_agc!.IsChecked == true), Res("GoodBrush"));
        _nb = Tog("NB", (_, _) => DspToggled(), Res("WarnBrush"));
        _nr = Tog("NR", (_, _) => DspToggled(), Res("WarnBrush"));
        _an = Tog("Notch", (_, _) => DspToggled(), Res("WarnBrush"));
        _nb.ToolTip = "Noise blanker: removes impulse noise (ignition, power line clicks). Runs on the receiver.";
        _nr.ToolTip = "Noise reduction: lowers background hiss. Spectral denoiser on its own, LMS when Notch is also on. Runs on the receiver, no PC load.";
        _an.ToolTip = "Autonotch: removes steady carriers and heterodyne whistles. Runs on the receiver.";
        _nb.IsChecked = AppSettings.Current.NoiseBlanker;
        _nr.IsChecked = AppSettings.Current.NoiseReduction;
        _an.IsChecked = AppSettings.Current.AutoNotch;
        _agc.IsChecked = true;
        _syncOff = Tog("Off", (_, _) => PickSync(SyncMode.Off));
        _syncOne = Tog("Radio → SDR", (_, _) => PickSync(SyncMode.RadioToSdr));
        _syncTwo = Tog("Two way", (_, _) => PickSync(SyncMode.TwoWay));
        _syncOne.ToolTip = "Your radio's dial tunes the receiver";
        _syncTwo.ToolTip = "Radio tunes the receiver, and tuning the receiver also moves your radio";

        var stack = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };

        // ---- VFO
        var vfo = new StackPanel();
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _bandPill.Child = _bandBadge;
        _modePill.Child = _modeBadge;
        badges.Children.Add(Label("VFO", 11, "DimBrush", true));
        badges.Children.Add(_bandPill);
        badges.Children.Add(_modePill);
        vfo.Children.Add(badges);
        vfo.Children.Add(_vfo);
        var entryRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var go = Btn("Go", (_, _) => SubmitEntry());
        DockPanel.SetDock(go, Dock.Right);
        entryRow.Children.Add(go);
        entryRow.Children.Add(_entry);
        vfo.Children.Add(entryRow);
        stack.Children.Add(Card(vfo));
        _vfo.FrequencyEdited += hz => FrequencyRequested?.Invoke(hz);
        _entry.KeyDown += (_, e) => { if (e.Key == Key.Enter) SubmitEntry(); };

        // ---- signal
        var sig = new StackPanel();
        var sRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        sRow.Children.Add(Label("SIGNAL", 11, "DimBrush", true));
        sRow.Children.Add(_sText);
        sRow.Children.Add(_dbm);
        sig.Children.Add(sRow);
        sig.Children.Add(_meter);
        stack.Children.Add(Card(sig));

        // ---- mode
        var modeCard = new StackPanel();
        modeCard.Children.Add(Section("Mode"));
        var modeGrid = new UniformGrid { Columns = 5 };
        foreach (var m in new[] { RigMode.LSB, RigMode.USB, RigMode.CW, RigMode.AM, RigMode.NFM })
        {
            var mm = m;
            var b = Tog(ModeName(m), (_, _) => { ModeRequested?.Invoke(mm); }, ModeBrush(m));
            b.FontWeight = FontWeights.SemiBold;
            b.Foreground = ModeBrush(m);
            _modes[m] = b;
            modeGrid.Children.Add(b);
        }
        modeCard.Children.Add(modeGrid);
        stack.Children.Add(Card(modeCard));

        // ---- filter
        var bw = new StackPanel();
        var bwHead = new DockPanel();
        DockPanel.SetDock(_bwText, Dock.Right);
        bwHead.Children.Add(_bwText);
        bwHead.Children.Add(Section("Filter"));
        bw.Children.Add(bwHead);
        bw.Children.Add(_presets);
        bw.Children.Add(_bwSlider);
        _bwCard = Card(bw);
        stack.Children.Add(_bwCard);
        _bwSlider.ValueChanged += (_, _) =>
        {
            if (_suppress) return;
            _bw = Math.Round(100 * Math.Pow(160, _bwSlider.Value / 1000.0) / 50) * 50;
            _bwText.Text = $"{_bw:N0} Hz";
            BandwidthRequested?.Invoke(_bw);
            UpdatePresetChecks();
        };

        // ---- step
        var step = new StackPanel();
        step.Children.Add(Section("Tuning step (wheel on waterfall)"));
        var stepGrid = new UniformGrid { Columns = 4 };
        for (int i = 0; i < Steps.Length; i++)
        {
            int idx = i;
            int s = Steps[i];
            var b = Tog(s >= 1000 ? $"{s / 1000} kHz" : $"{s} Hz", (_, _) => SetStep(idx), Res("VioletBrush"));
            _stepBtns.Add(b);
            stepGrid.Children.Add(b);
        }
        step.Children.Add(stepGrid);
        stack.Children.Add(Card(step));

        // ---- bands
        var bands = new StackPanel();
        bands.Children.Add(Section("Bands"));
        var hamGrid = new UniformGrid { Columns = 5 };
        var swGrid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var b in Bands)
        {
            var band = b;
            var btn = Btn(b.Name, (_, _) => BandRequested?.Invoke(band.DefaultHz, band.Mode));
            btn.Padding = new Thickness(4, 5, 4, 5);
            if (!b.Ham) btn.Foreground = Res("DimBrush");
            (b.Ham ? hamGrid : swGrid).Children.Add(btn);
        }
        bands.Children.Add(hamGrid);
        bands.Children.Add(swGrid);
        stack.Children.Add(Card(bands));

        // ---- audio
        var audio = new StackPanel();
        audio.Children.Add(Section("Audio"));
        var aRow = new DockPanel();
        var aBtns = new StackPanel { Orientation = Orientation.Horizontal };
        aBtns.Children.Add(_mute);
        aBtns.Children.Add(_agc);
        DockPanel.SetDock(aBtns, Dock.Right);
        aRow.Children.Add(aBtns);
        aRow.Children.Add(_vol);
        audio.Children.Add(aRow);
        var dspRow = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
        dspRow.Children.Add(_nb);
        dspRow.Children.Add(_nr);
        dspRow.Children.Add(_an);
        audio.Children.Add(dspRow);
        _audioCard = Card(audio);
        stack.Children.Add(_audioCard);
        _vol.Value = AppSettings.Current.Volume;
        _vol.ValueChanged += (_, _) => { AppSettings.Current.Volume = _vol.Value; VolumeChanged?.Invoke(_vol.Value); };

        // ---- radio
        var radio = new StackPanel();
        radio.Children.Add(Section("Your radio (CAT)"));
        var rigRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        rigRow.Children.Add(_led);
        rigRow.Children.Add(_rigName);
        radio.Children.Add(rigRow);
        radio.Children.Add(_rigFreq);
        radio.Children.Add(Cols(3, _syncOff, _syncOne, _syncTwo));
        _muteTx.IsChecked = AppSettings.Current.MuteOnTx;
        _muteTx.Click += (_, _) => { AppSettings.Current.MuteOnTx = _muteTx.IsChecked == true; AppSettings.Current.Save(); MuteOnTxChanged?.Invoke(); };
        radio.Children.Add(_muteTx);
        stack.Children.Add(Card(radio));

        // ---- signature
        var sigRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 10) };
        sigRow.Children.Add(Label("HamRxBridge", 12, "DimBrush"));
        sigRow.Children.Add(Signature(14));
        stack.Children.Add(sigRow);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };

        PickSyncUi(AppSettings.Current.Sync);
        SetStep(_step);
    }

    void SubmitEntry()
    {
        if (TryParseFrequency(_entry.Text, out var hz)) { FrequencyRequested?.Invoke(hz); _entry.Text = ""; }
        else _entry.BorderBrush = Res("BadBrush");
    }

    void SetStep(int idx)
    {
        _step = idx;
        for (int i = 0; i < _stepBtns.Count; i++) _stepBtns[i].IsChecked = i == idx;
    }

    void PickSync(SyncMode m)
    {
        AppSettings.Current.Sync = m;
        AppSettings.Current.Save();
        PickSyncUi(m);
        SyncChanged?.Invoke(m);
    }

    void PickSyncUi(SyncMode m)
    {
        _syncOff.IsChecked = m == SyncMode.Off;
        _syncOne.IsChecked = m == SyncMode.RadioToSdr;
        _syncTwo.IsChecked = m == SyncMode.TwoWay;
    }

    void UpdatePresetChecks()
    {
        foreach (var child in _presets.Children)
            if (child is ToggleButton t && t.DataContext is double w) t.IsChecked = Math.Abs(w - _bw) < 1;
    }

    // ------------------------------------------------------------- state from the page

    public void ShowState(double hz, RigMode mode, double bw)
    {
        _vfo.Hz = hz;
        string band = BandName(hz);
        _bandBadge.Text = band.Length > 0 ? band : "out of band";
        _bandPill.Background = band.Length > 0 ? Res("SelectBrush") : Res("Panel2Brush");
        _modeBadge.Text = ModeName(mode);
        _modeBadge.Foreground = Brushes.Black;
        _modePill.Background = ModeBrush(mode);
        foreach (var kv in _modes) kv.Value.IsChecked = kv.Key == mode;

        if (mode != _mode || _presets.Children.Count == 0)
        {
            _presets.Children.Clear();
            foreach (var w in Passbands.Presets(mode))
            {
                double width = w;
                var t = new ToggleButton { Content = width >= 1000 ? $"{width / 1000:0.#}k" : $"{width:0}", DataContext = width };
                t.Click += (_, _) => BandwidthRequested?.Invoke(width);
                _presets.Children.Add(t);
            }
        }
        _mode = mode;
        _bw = bw;
        _suppress = true;
        _bwSlider.Value = Math.Clamp(1000 * Math.Log(Math.Max(100, bw) / 100.0) / Math.Log(160), 0, 1000);
        _suppress = false;
        _bwText.Text = $"{bw:N0} Hz";
        UpdatePresetChecks();
    }

    DateTime _lastMeter;

    public void ShowSignal(double dbm)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastMeter).TotalMilliseconds < 50) return;
        _lastMeter = now;
        _meter.Set(dbm);
        _sText.Text = dbm <= -73 ? $"S{Math.Max(0, Math.Round(9 + (dbm + 73) / 6)):0}" : $"S9+{dbm + 73:0}";
        _dbm.Text = $"{dbm:0} dBm";
    }

    void DspToggled()
    {
        var s = AppSettings.Current;
        s.NoiseBlanker = _nb.IsChecked == true;
        s.NoiseReduction = _nr.IsChecked == true;
        s.AutoNotch = _an.IsChecked == true;
        s.Save();
        DspChanged?.Invoke(s.NoiseBlanker, s.NoiseReduction, s.AutoNotch);
    }

    public void ApplyCaps(ReceiverCaps caps)
    {
        _nb.IsEnabled = _nr.IsEnabled = _an.IsEnabled = caps.Dsp;
        _meter.Available = caps.SMeter;
        if (!caps.SMeter) { _sText.Text = ""; _dbm.Text = ""; }
        _bwCard.IsEnabled = caps.Bandwidth;
        _bwCard.Opacity = caps.Bandwidth ? 1 : 0.45;
        _vol.IsEnabled = caps.Volume;
        _agc.IsEnabled = caps.Agc;
    }

    public void ShowRig(IRigBackend rig, SyncMode sync)
    {
        _rigName.Text = rig.LinkText;
        _led.Fill = rig.Link switch
        {
            RigLink.Online => rig.Transmitting ? Res("BadBrush") : Res("GoodBrush"),
            RigLink.Off => Res("DimBrush"),
            RigLink.NotInstalled => Res("BadBrush"),
            _ => Res("WarnBrush")
        };
        _rigFreq.Text = rig.Link == RigLink.Online && rig.FreqHz > 0
            ? $"Dial {FormatHz(rig.FreqHz)} {(rig.Mode.HasValue ? ModeName(rig.Mode.Value) : "")}{(rig.Transmitting ? "   TX" : "")}"
            : rig.Link == RigLink.Off ? "Turn on radio control in Settings" : "Waiting for the radio";
        PickSyncUi(sync);
    }
}
