using System.Globalization;
using System.IO;
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
/// Hosts a receiver's own web page (WebSDR.org sites, or the classic KiwiSDR page).
/// The radio drives the page by loading it with a tune parameter. The page itself
/// cannot report manual tuning back, so this view is one way: radio to receiver.
/// </summary>
public sealed class WebReceiverPage : UserControl, ITunable, IDisposable
{
    readonly ReceiverEntry _rx;
    readonly WebView2 _web = new();
    readonly TextBlock _info = Label("", 12, "DimBrush");
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    double _hz;
    RigMode _mode = RigMode.USB;
    string? _lastUrl;
    bool _ready;

    public event Action<double, RigMode>? UserTuned { add { } remove { } }

    public WebReceiverPage(ReceiverEntry rx)
    {
        _rx = rx;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 6) };
        bar.Children.Add(Label(rx.Name, 14, "TextBrush", true));
        bar.Children.Add(_info);
        bar.Children.Add(Btn("Reload", (_, _) => Navigate(true)));
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        Grid.SetRow(_web, 1);
        root.Children.Add(_web);
        Content = root;

        _debounce.Tick += (_, _) => { _debounce.Stop(); Navigate(false); };
        Loaded += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HamRxBridge", "webview");
            var opts = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, data, opts);
            await _web.EnsureCoreWebView2Async(env);
            _ready = true;
            Navigate(true);
        }
        catch (Exception ex)
        {
            _info.Text = "Web view could not start: " + ex.Message +
                         " (the Microsoft WebView2 Runtime is required, it is free from Microsoft)";
            _info.Foreground = Res("BadBrush");
        }
    }

    string BuildUrl()
    {
        string b = _rx.BaseUrl.TrimEnd('/');
        if (_hz <= 0) return b + "/";
        double khz = _hz / 1000.0;
        return _rx.Kind == SdrKind.Kiwi
            ? string.Create(CultureInfo.InvariantCulture, $"{b}/?f={khz:F2}{RigModeInfo.ToWire(_mode)}")
            : string.Create(CultureInfo.InvariantCulture, $"{b}/?tune={khz:F1}{RigModeInfo.ToWebSdr(_mode)}");
    }

    void Navigate(bool force)
    {
        if (!_ready) return;
        string url = BuildUrl();
        if (!force && url == _lastUrl) return;
        _lastUrl = url;
        _web.CoreWebView2.Navigate(url);
        _info.Text = $"   {_hz / 1000:0.00} kHz {_mode}   (follows your radio, page reloads when you stop turning the dial)";
    }

    public void TuneFromRig(double hz, RigMode mode)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => TuneFromRig(hz, mode)); return; }
        _hz = hz; _mode = mode;
        _debounce.Stop();
        _debounce.Start();
    }

    public void SetTxMute(bool muted)
    {
        if (!_ready) return;
        Dispatcher.BeginInvoke(() => { try { _web.CoreWebView2.IsMuted = muted; } catch { } });
    }

    public void Dispose()
    {
        _debounce.Stop();
        try { _web.Dispose(); } catch { }
    }
}
