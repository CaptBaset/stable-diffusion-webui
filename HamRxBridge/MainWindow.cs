using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HamRxBridge.Rig;
using HamRxBridge.Sdr;
using HamRxBridge.Ui;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge;

public sealed class MainWindow : Window
{
    readonly OmniRigService _rig = new();
    readonly SyncController _sync;
    readonly ContentControl _host = new();
    readonly Dictionary<SdrKind, DirectoryPage> _dirs = new();
    UIElement? _current;
    SdrKind _lastKind = SdrKind.Kiwi;

    readonly ToggleButton _rig1, _rig2, _syncOff, _syncOne, _syncTwo;
    readonly System.Windows.Shapes.Ellipse _led = new() { Width = 12, Height = 12, Margin = new Thickness(8, 0, 4, 0) };
    readonly TextBlock _rigText = Label("", 12, "DimBrush");
    readonly TextBlock _rigFreq = Label("", 16, "TextBrush", true);
    readonly Button _back;

    public MainWindow()
    {
        Title = "HamRxBridge";
        Width = 1400; Height = 880; MinWidth = 1050; MinHeight = 650;
        Background = Res("BgBrush");
        Foreground = Res("TextBrush");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _sync = new SyncController(_rig);
        _rig1 = Tog("Rig 1", (_, _) => PickRig(1));
        _rig2 = Tog("Rig 2", (_, _) => PickRig(2));
        _syncOff = Tog("Off", (_, _) => PickSync(SyncMode.Off));
        _syncOne = Tog("Radio to SDR", (_, _) => PickSync(SyncMode.RadioToSdr));
        _syncTwo = Tog("Two way", (_, _) => PickSync(SyncMode.TwoWay));
        _back = Btn("< Receivers", (_, _) => ShowDirectory(_lastKind));
        _back.Visibility = Visibility.Collapsed;

        var root = new DockPanel();
        var top = BuildTopBar();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_host);
        Content = root;

        _rig.StateChanged += UpdateRigBar;
        Closing += (_, _) => { AppSettings.Current.Save(); DisposeCurrent(); _rig.Dispose(); };

        int idx = AppSettings.Current.RigIndex == 2 ? 2 : 1;
        PickSyncUi(_sync.Mode);
        _rig1.IsChecked = idx == 1; _rig2.IsChecked = idx == 2;
        _rig.Start(idx);
        UpdateRigBar();
        ShowStart();
    }

    UIElement BuildTopBar()
    {
        var bar = new DockPanel { Background = Res("PanelBrush"), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 8) };
        left.Children.Add(Label("HamRxBridge", 18, "AccentBrush", true));
        left.Children.Add(Btn("Home", (_, _) => ShowStart()));
        left.Children.Add(_back);
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 8) };
        DockPanel.SetDock(right, Dock.Right);
        right.Children.Add(_rigFreq);
        right.Children.Add(_led);
        right.Children.Add(_rigText);
        right.Children.Add(_rig1);
        right.Children.Add(_rig2);
        right.Children.Add(Btn("OmniRig setup", (_, _) => _rig.ShowOmniRigDialog()));
        right.Children.Add(Label("Sync", 12, "DimBrush"));
        right.Children.Add(_syncOff);
        right.Children.Add(_syncOne);
        right.Children.Add(_syncTwo);
        bar.Children.Add(right);
        return bar;
    }

    // ---------------------------------------------------------- rig bar

    void PickRig(int i)
    {
        _rig1.IsChecked = i == 1; _rig2.IsChecked = i == 2;
        AppSettings.Current.RigIndex = i;
        _rig.SelectRig(i);
        UpdateRigBar();
        _sync.PushRigToSdr();
    }

    void PickSync(SyncMode m)
    {
        _sync.Mode = m;
        AppSettings.Current.Sync = m;
        PickSyncUi(m);
        _sync.PushRigToSdr();
    }

    void PickSyncUi(SyncMode m)
    {
        _syncOff.IsChecked = m == SyncMode.Off;
        _syncOne.IsChecked = m == SyncMode.RadioToSdr;
        _syncTwo.IsChecked = m == SyncMode.TwoWay;
    }

    void UpdateRigBar()
    {
        _rigText.Text = _rig.LinkText;
        _led.Fill = Res(_rig.Link == RigLink.Online ? (_rig.Transmitting ? "BadBrush" : "GoodBrush")
            : _rig.Link == RigLink.NotInstalled ? "BadBrush" : "WarnBrush");
        _rigFreq.Text = _rig.Link == RigLink.Online && _rig.FreqHz > 0
            ? $"{FormatHz(_rig.FreqHz)}  {_rig.Mode}" : "";
    }

    // ------------------------------------------------------- navigation

    void Show(UIElement page, bool keepAlive = false)
    {
        if (!keepAlive || _current != page) DisposeCurrentIfNotCached();
        _current = page;
        _host.Content = page;
    }

    void DisposeCurrentIfNotCached()
    {
        _sync.Detach();
        if (_current is IDisposable d) d.Dispose();
        _current = null;
    }

    void DisposeCurrent() => DisposeCurrentIfNotCached();

    void ShowStart()
    {
        _back.Visibility = Visibility.Collapsed;
        var p = new StartPage();
        p.Chosen += kind => ShowDirectory(kind);
        Show(p);
    }

    void ShowDirectory(SdrKind kind)
    {
        _lastKind = kind;
        _back.Visibility = Visibility.Collapsed;
        if (!_dirs.TryGetValue(kind, out var page))
        {
            page = _dirs[kind] = new DirectoryPage(kind);
            page.OpenRequested += (e, native) => OpenReceiver(e, native);
        }
        Show(page, true);
    }

    void OpenReceiver(ReceiverEntry e, bool native)
    {
        _back.Visibility = Visibility.Visible;
        if (native && e.Kind == SdrKind.Kiwi)
        {
            var k = new KiwiPage(e);
            k.OpenWebViewRequested += () => OpenReceiver(e, false);
            Show(k);
            _sync.Attach(k);
        }
        else
        {
            var w = new WebReceiverPage(e);
            Show(w);
            _sync.Attach(w);
        }
    }
}
