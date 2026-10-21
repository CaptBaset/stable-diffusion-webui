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

    public FrameworkElement Element => this;
    public ReceiverCaps Caps { get; private set; } = new();
    public string ControlMethod { get; private set; } = "Connecting to page";

    public event Action<double, RigMode?>? ReceiverTuned;
    public event Action<double>? Signal { add { } remove { } }
    public event Action<string, StatusLevel>? StatusChanged;
    public event Action? CapsChanged;

    bool IsKiwi => _rx.Kind == SdrKind.Kiwi;

    public WebReceiverView(ReceiverEntry rx, double hz, RigMode mode)
    {
        _rx = rx; _hz = hz; _mode = mode;
        var grid = new Grid();
        grid.Children.Add(new Border
        {
            BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            ClipToBounds = true, Child = _web
        });
        _overlay.HorizontalAlignment = HorizontalAlignment.Center;
        grid.Children.Add(_overlay);
        Content = grid;

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
            _web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                _overlay.Visibility = Visibility.Collapsed;
                _navigatedAt = DateTime.UtcNow;
                if (!e.IsSuccess) Status($"Page failed to load ({e.WebErrorStatus})", StatusLevel.Bad);
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

    const string ProbeWebSdr = @"(function(){try{
var ok=(typeof setfreqif==='function'&&typeof set_mode==='function');
var k=null;try{k=(typeof nominalfreq==='function')?Number(nominalfreq()):Number(window.freq);}catch(e){}
var m='';try{m=String(window.mode||'');}catch(e){}
return {ok:ok,kHz:(k!==null&&isFinite(k))?k:null,mode:m};}catch(e){return {ok:false};}})()";

    const string ProbeKiwi = @"(function(){try{
var ok=(typeof ext_tune==='function'&&typeof ext_get_freq==='function');
var hz=null,m='';if(ok){try{hz=Number(ext_get_freq());m=String(ext_get_mode()||'');}catch(e){ok=false;}}
return {ok:ok&&isFinite(hz)&&hz>0,kHz:hz?hz/1000:null,mode:m};}catch(e){return {ok:false};}})()";

    static string TuneWebSdrJs(double kHz, string mode) => string.Create(CultureInfo.InvariantCulture, $@"(function(k,m){{try{{
var t=(typeof bi!=='undefined'&&bi&&bi.length)?bi:((typeof bandinfo!=='undefined')?bandinfo:null);
if(t&&typeof setband==='function'&&typeof band!=='undefined'){{
 var n=(typeof nvbands==='number'&&nvbands>0)?Math.min(nvbands,t.length):t.length;
 for(var i=0;i<n;i++){{var b=t[i];if(!b||typeof b.centerfreq!=='number')continue;var h=b.samplerate/2+4;
  if(k>b.centerfreq-h&&k<b.centerfreq+h){{if(i!==band){{try{{b.vfo=k;}}catch(e){{}}setband(i);}}break;}}}}}}
if(m)set_mode(m);setfreqif(k.toFixed(3));if(m)set_mode(m);return 'ok';}}catch(e){{return 'err '+e;}}}})({kHz:F3},'{mode}')");

    static string TuneKiwiJs(double kHz, string mode, int lo, int hi) => string.Create(CultureInfo.InvariantCulture, $@"(function(k,m,lo,hi){{try{{
var z=(typeof ext_zoom!=='undefined')?ext_zoom.CUR:undefined;
ext_tune(k,m,z);
if(typeof ext_set_passband==='function'&&lo!==hi)ext_set_passband(lo,hi);
return 'ok';}}catch(e){{return 'err '+e;}}}})({kHz:F3},'{mode}',{lo},{hi})");

    async Task ProbeAsync()
    {
        if (!_ready || _disposed || _probing) return;
        _probing = true;
        try
        {
            string json = await _web.CoreWebView2.ExecuteScriptAsync(IsKiwi ? ProbeKiwi : ProbeWebSdr);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            bool ok = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;

            if (ok && !_live)
            {
                _live = true;
                ControlMethod = IsKiwi ? "Live control of the KiwiSDR page" : "Live control of the WebSDR page";
                Caps = new ReceiverCaps { Bandwidth = IsKiwi };
                CapsChanged?.Invoke();
                Status("Connected. Your panel now controls this page directly.", StatusLevel.Good);
                _seenHz = 0;
                await SendTuneAsync();          // make the page match our panel
                return;
            }
            if (!ok && !_live && DateTime.UtcNow - _navigatedAt > TimeSpan.FromSeconds(12))
            {
                _navigatedAt = DateTime.MaxValue;
                ControlMethod = "Reload control (page has no live API)";
                Caps = new ReceiverCaps();
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
    public void SetVolume(double v) { }
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
