using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HamRxBridge.Rig;
using HamRxBridge.Sdr;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using static HamRxBridge.Ui.UiKit;
using static HamRxBridge.Sdr.WebBridgeJs;

namespace HamRxBridge.Ui;

/// <summary>
/// Shows a receiver's own web page and drives it from our control panel.
///
/// Live control talks to the page's own JavaScript:
///   WebSDR (PA3FWM software): setfreqif(kHz), set_mode('USB'), setband(i); read back nominalfreq() and window.mode
///   KiwiSDR web UI:          ext_tune(kHz, mode, ext_zoom.CUR), ext_set_passband(lo, hi); read back ext_get_freq() and ext_get_mode()
/// When a page does not expose these, we fall back to reloading it with a tune parameter.
/// </summary>
public sealed class WebReceiverView : UserControl, IReceiverView
{
    readonly ReceiverEntry _rx;
    readonly WebView2 _web = new();
    readonly TextBlock _overlay = Label("Loading receiver page ...", 16, "DimBrush");
    readonly DispatcherTimer _probe = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _reloadDebounce = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    bool _ready, _live, _disposed, _probing;
    DateTime _navigatedAt = DateTime.MaxValue;
    DateTime _holdUntil = DateTime.MinValue;
    double _hz = 7_100_000, _seenHz;
    RigMode _mode = RigMode.LSB;
    int _lo = -2900, _hi = -200;
    string? _lastUrl;
    bool _compactWanted = true, _compactOn, _diagLogged;
    DateTime _lastCompact = DateTime.MinValue;
    double _volume = AppSettings.Current.Volume;
    readonly System.Windows.Controls.Primitives.ToggleButton _compactBtn;

    public FrameworkElement Element => this;
    public ReceiverCaps Caps { get; private set; } = new() { Volume = true };
    public string ControlMethod { get; private set; } = "Connecting to page";

    public event Action<double, RigMode?>? ReceiverTuned;
    public event Action<double>? Signal { add { } remove { } }
    public event Action<string, StatusLevel>? StatusChanged;
    public event Action? CapsChanged;

    bool IsKiwi => _rx.Kind == SdrKind.Kiwi;

    public WebReceiverView(ReceiverEntry rx, double hz, RigMode mode)
    {
        _rx = rx; _hz = hz; _mode = mode;
        _compactWanted = rx.Kind == SdrKind.WebSdr;
        _compactBtn = Tog("Compact view", (_, _) => { _compactWanted = _compactBtn!.IsChecked == true; _ = ApplyCompactAsync(); },
            Res("VioletBrush"));
        _compactBtn.IsChecked = _compactWanted;
        _compactBtn.ToolTip = "Show only the receiver's waterfall, controlled from your panel. Turn off to see the whole page.";

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_compactBtn);
        bar.Children.Add(IconBtn(Glyph.Refresh, "Reload page", (_, _) => NavigateTuned(true)));
        bar.Children.Add(IconBtn(Glyph.Globe, "Open in my browser", (_, _) => OpenInBrowser(TunedUrl())));
        root.Children.Add(bar);

        var grid = new Grid();
        grid.Children.Add(new Border
        {
            BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            ClipToBounds = true, Child = _web
        });
        _overlay.HorizontalAlignment = HorizontalAlignment.Center;
        grid.Children.Add(_overlay);
        Grid.SetRow(grid, 1);
        root.Children.Add(grid);
        Content = root;

        _probe.Tick += async (_, _) => await ProbeAsync();
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); NavigateTuned(false); };
        Loaded += async (_, _) => { if (!_ready) await InitAsync(); };
    }

    async Task InitAsync()
    {
        try
        {
            string data = Path.Combine(AppSettings.Folder, "webview");
            var opts = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, data, opts);
            await _web.EnsureCoreWebView2Async(env);
            if (_disposed) return;
            _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 7, 11, 18);
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(AudioHookJs);
            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                _overlay.Visibility = Visibility.Collapsed;
                _navigatedAt = DateTime.UtcNow;
                _compactOn = false;
                _diagLogged = false;
                if (!e.IsSuccess) Status($"Page failed to load ({e.WebErrorStatus})", StatusLevel.Bad);
                await SetPageVolumeAsync();
            };
            _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; OpenInBrowser(e.Uri); };
            _ready = true;
            ApplyMute();
            NavigateTuned(true);
            _probe.Start();
        }
        catch (Exception ex)
        {
            Log.Write("WebView2: " + ex);
            _overlay.Text = "The page viewer could not start. Install the free Microsoft WebView2 Runtime and try again.";
            _overlay.Foreground = Res("BadBrush");
            _overlay.TextWrapping = TextWrapping.Wrap;
            Status("Microsoft WebView2 Runtime missing", StatusLevel.Bad);
        }
    }

    string TunedUrl()
    {
        string b = _rx.BaseUrl.TrimEnd('/');
        double khz = _hz / 1000.0;
        return IsKiwi
            ? string.Create(CultureInfo.InvariantCulture, $"{b}/?f={khz:F2}{RigModeInfo.ToWire(_mode)}")
            : string.Create(CultureInfo.InvariantCulture, $"{b}/?tune={khz:F2}{RigModeInfo.ToWebSdr(_mode)}");
    }

    void NavigateTuned(bool force)
    {
        if (!_ready || _disposed) return;
        string url = TunedUrl();
        if (!force && url == _lastUrl) return;
        _lastUrl = url;
        _live = false;
        _navigatedAt = DateTime.MaxValue;
        _web.CoreWebView2.Navigate(url);
        Log.Write("Web receiver: " + url);
    }

    // ------------------------------------------------------------- JavaScript bridge

    async Task ProbeAsync()
    {
        if (!_ready || _disposed || _probing) return;
        _probing = true;
        try
        {
            string json = await _web.CoreWebView2.ExecuteScriptAsync(IsKiwi ? ProbeKiwi : ProbeWebSdr);
            // Re-apply compact view every few seconds: the page adds its waterfall canvases after loading.
            bool due = _compactWanted ? DateTime.UtcNow - _lastCompact > TimeSpan.FromSeconds(3) : _compactOn;
            if (due && DateTime.UtcNow - _navigatedAt > TimeSpan.FromSeconds(1.5)) await ApplyCompactAsync();
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            bool ok = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;

            if (ok && !_live)
            {
                _live = true;
                Log.Write($"Live control on for {_rx.BaseUrl}");
                ControlMethod = IsKiwi ? "Live control of the KiwiSDR page" : "Live control of the WebSDR page";
                Caps = new ReceiverCaps { Bandwidth = IsKiwi, Volume = true };
                CapsChanged?.Invoke();
                Status("Connected. Your panel now controls this page directly.", StatusLevel.Good);
                _seenHz = 0;
                await SendTuneAsync();          // make the page match our panel
                return;
            }
            if (!ok && !_live && DateTime.UtcNow - _navigatedAt > TimeSpan.FromSeconds(12))
            {
                _navigatedAt = DateTime.MaxValue;
                await LogDiagnosticsAsync();
                ControlMethod = "Reload control (page has no live API)";
                Caps = new ReceiverCaps { Volume = true };
                CapsChanged?.Invoke();
                Status("This page does not offer live control, so it is retuned by reloading.", StatusLevel.Warn);
                return;
            }
            if (!ok || !_live) return;

            if (r.TryGetProperty("kHz", out var kEl) && kEl.ValueKind == JsonValueKind.Number)
            {
                double hz = Math.Round(kEl.GetDouble() * 1000.0);
                RigMode? mode = r.TryGetProperty("mode", out var mEl) ? ParseMode(mEl.GetString()) : null;
                bool moved = Math.Abs(hz - _seenHz) > 5;
                _seenHz = hz;
                // The page is reporting something we did not ask for: the operator tuned on the page.
                if (moved && DateTime.UtcNow > _holdUntil && Math.Abs(hz - _hz) > 5)
                {
                    _hz = hz;
                    if (mode.HasValue) _mode = mode.Value;
                    ReceiverTuned?.Invoke(hz, mode);
                }
            }
        }
        catch (Exception ex)
        {
            if (_live) Log.Write("probe: " + ex.Message);
        }
        finally { _probing = false; }
    }

    static RigMode? ParseMode(string? m)
    {
        m = (m ?? "").Trim().ToLowerInvariant();
        if (m.StartsWith("us")) return RigMode.USB;
        if (m.StartsWith("ls")) return RigMode.LSB;
        if (m.StartsWith("cw")) return RigMode.CW;
        if (m.StartsWith("am") || m.StartsWith("sa") || m == "qam") return RigMode.AM;
        if (m.Contains("fm")) return RigMode.NFM;
        return null;
    }

    async Task SendTuneAsync()
    {
        if (!_ready || !_live) return;
        _holdUntil = DateTime.UtcNow.AddMilliseconds(1500);
        string mode = IsKiwi ? RigModeInfo.ToWire(_mode) : RigModeInfo.ToWebSdr(_mode).ToUpperInvariant();
        string js = IsKiwi ? TuneKiwiJs(_hz / 1000.0, mode, _lo, _hi) : TuneWebSdrJs(_hz / 1000.0, mode);
        try
        {
            string res = await _web.CoreWebView2.ExecuteScriptAsync(js);
            if (res.Contains("err")) Log.Write("tune script: " + res);
        }
        catch (Exception ex) { Log.Write("tune: " + ex.Message); }
    }

    async Task LogDiagnosticsAsync()
    {
        if (_diagLogged || !_ready) return;
        _diagLogged = true;
        try { Log.Write($"Page diagnostics {_rx.BaseUrl}: {await _web.CoreWebView2.ExecuteScriptAsync(DiagJs)}"); }
        catch (Exception ex) { Log.Write("diag: " + ex.Message); }
    }

    async Task ApplyCompactAsync()
    {
        if (!_ready) return;
        _lastCompact = DateTime.UtcNow;
        try
        {
            string r = await _web.CoreWebView2.ExecuteScriptAsync(CompactJs(_compactWanted));
            _compactOn = _compactWanted && r.Contains("on");
            if (r.Contains("nocanvas") && DateTime.UtcNow - _navigatedAt > TimeSpan.FromSeconds(15) && _compactWanted)
            {
                _compactWanted = false;           // this page has no waterfall canvas: show it whole
                _compactBtn.IsChecked = false;
                Log.Write("compact view: no waterfall canvas found, showing full page");
            }
        }
        catch (Exception ex) { Log.Write("compact: " + ex.Message); }
    }

    async Task SetPageVolumeAsync()
    {
        if (!_ready) return;
        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(string.Create(CultureInfo.InvariantCulture,
                $"window.__hrbSetVolume&&window.__hrbSetVolume({_volume:F3})"));
        }
        catch { }
    }

    // ------------------------------------------------------------- IReceiverView

    public void Tune(double hz, RigMode mode, int lowCut, int highCut)
    {
        _hz = hz; _mode = mode; _lo = lowCut; _hi = highCut;
        if (_live) _ = SendTuneAsync();
        else if (_ready)
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        }
    }

    bool _muted, _txMuted;
    void ApplyMute() { if (_ready) _web.CoreWebView2.IsMuted = _muted || _txMuted; }
    public void SetMuted(bool muted) { _muted = muted; ApplyMute(); }
    public void SetTxMuted(bool muted) { _txMuted = muted; ApplyMute(); }
    public void SetVolume(double v) { _volume = v; _ = SetPageVolumeAsync(); }
    public void SetAgc(bool on) { }

    void Status(string s, StatusLevel l) => StatusChanged?.Invoke(s, l);

    public void Dispose()
    {
        _disposed = true;
        _probe.Stop();
        _reloadDebounce.Stop();
        try { _web.Dispose(); } catch { }
    }
}
