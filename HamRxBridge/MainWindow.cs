using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HamRxBridge.Rig;
using HamRxBridge.Sdr;
using HamRxBridge.Ui;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge;

public sealed class MainWindow : Window
{
    readonly RigHub _rig = new();
    readonly ContentControl _host = new();
    readonly Dictionary<SdrKind, DirectoryPage> _dirs = new();
    UIElement? _current;
    SdrKind _lastKind = SdrKind.Kiwi;

    readonly Button _receiversBtn;
    readonly Ellipse _led = new() { Width = 10, Height = 10, Margin = new Thickness(10, 0, 6, 0) };
    readonly TextBlock _rigText = Label("", 12, "DimBrush");
    readonly TextBlock _status = Label("Ready", 12, "DimBrush");
    readonly DockPanel _root = new();

    public MainWindow()
    {
        Title = "HamRxBridge  By AP2FLY";
        Width = 1480; Height = 920; MinWidth = 1100; MinHeight = 700;
        Background = Res("BgBrush");
        Foreground = Res("TextBrush");
        FontFamily = TextFont;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.png")); } catch { }
        SourceInitialized += (_, _) => ApplyWindowsLook();

        _receiversBtn = IconBtn(Glyph.List, "Receivers", (_, _) => ShowDirectory(_lastKind));

        var root = _root;
        var top = BuildTopBar();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var bottom = BuildStatusBar();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_host);
        Content = root;

        _rig.Changed += UpdateRig;
        Closing += (_, _) => { DisposeCurrent(); _rig.Dispose(); AppSettings.Current.Save(); };

        _rig.Restart();
        ShowStart();
    }

    UIElement BuildTopBar()
    {
        var bar = new Border
        {
            Background = Res("PanelBrush"),
            BorderBrush = Res("LineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8, 12, 8)
        };
        var dock = new DockPanel { LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        try
        {
            left.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/app.png")), Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 0) });
        }
        catch { }
        left.Children.Add(GradientText("HamRxBridge", 20));
        left.Children.Add(Signature(13));
        var gap = new Border { Width = 18 };
        left.Children.Add(gap);
        left.Children.Add(IconBtn(Glyph.Home, "Home", (_, _) => ShowStart()));
        left.Children.Add(_receiversBtn);
        left.Children.Add(IconBtn(Glyph.Settings, "Settings", (_, _) => ShowSettings()));
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);

        var pill = new Border
        {
            Background = Res("Panel2Brush"), BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(4, 4, 12, 4), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Radio control. Click for settings."
        };
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
        pillRow.Children.Add(_led);
        pillRow.Children.Add(_rigText);
        pill.Child = pillRow;
        pill.MouseLeftButtonUp += (_, _) => ShowSettings();
        DockPanel.SetDock(pill, Dock.Right);
        dock.Children.Add(pill);

        bar.Child = dock;
        return bar;
    }

    UIElement BuildStatusBar()
    {
        var bar = new Border
        {
            Background = Res("PanelBrush"), BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 4, 12, 4)
        };
        var dock = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Label("HamRxBridge 1.0", 11, "DimBrush"));
        right.Children.Add(Signature(12));
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(_status);
        bar.Child = dock;
        return bar;
    }

    void SetStatus(string text, StatusLevel level)
    {
        _status.Text = text;
        _status.Foreground = Res(level switch
        {
            StatusLevel.Good => "GoodBrush",
            StatusLevel.Warn => "WarnBrush",
            StatusLevel.Bad => "BadBrush",
            _ => "DimBrush"
        });
    }

    void UpdateRig()
    {
        var b = _rig.Backend;
        _led.Fill = b.Link switch
        {
            RigLink.Online => b.Transmitting ? Res("BadBrush") : Res("GoodBrush"),
            RigLink.Off => Res("DimBrush"),
            RigLink.NotInstalled => Res("BadBrush"),
            _ => Res("WarnBrush")
        };
        _rigText.Text = b.Link == RigLink.Online && b.FreqHz > 0
            ? $"{b.LinkText}    {FormatHz(b.FreqHz)} {(b.Mode.HasValue ? ModeName(b.Mode.Value) : "")}{(b.Transmitting ? "   TX" : "")}"
            : b.LinkText;
    }

    // ------------------------------------------------------------- navigation

    void Show(UIElement page)
    {
        if (!ReferenceEquals(_current, page)) DisposeCurrent();
        _current = page;
        _host.Content = page;
    }

    void DisposeCurrent()
    {
        if (_current is ReceiverPage rp) rp.Dispose();
        _current = null;
    }

    void ShowStart()
    {
        var p = new StartPage();
        p.Chosen += ShowDirectory;
        p.ResumeRequested += OpenReceiver;
        p.SettingsRequested += ShowSettings;
        Show(p);
        SetStatus("Pick a receiver network", StatusLevel.Info);
    }

    void ShowSettings()
    {
        Show(new SettingsPage(_rig));
        SetStatus("Settings", StatusLevel.Info);
    }

    void ShowDirectory(SdrKind kind)
    {
        _lastKind = kind;
        if (!_dirs.TryGetValue(kind, out var page))
        {
            page = _dirs[kind] = new DirectoryPage(kind);
            page.OpenRequested += OpenReceiver;
        }
        Show(page);
        SetStatus(kind == SdrKind.Kiwi ? "KiwiSDR receivers" : "WebSDR receivers", StatusLevel.Info);
    }

    void OpenReceiver(ReceiverEntry e, bool native)
    {
        _lastKind = e.Kind;
        var s = AppSettings.Current;
        s.LastReceiver = e;
        s.LastWasNative = native;
        s.Save();
        Log.Write($"Open receiver {e.Name} ({e.BaseUrl}) native={native}");

        var page = new ReceiverPage(e, native, _rig);
        page.StatusChanged += SetStatus;
        page.SwitchToWebPage += r => OpenReceiver(r, false);
        Show(page);
        SetStatus("Opening " + e.Name, StatusLevel.Info);
    }

    // ------------------------------------------------------------- Windows 11 look

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_MAINWINDOW = 2;   // Mica, Windows 11 22H2+
    const int DWMWA_MICA_EFFECT = 1029;                                // Mica, Windows 11 21H2

    /// <summary>
    /// Dark title bar everywhere. On Windows 11 also rounded corners and the Mica backdrop,
    /// shown through a transparent client area. Falls back to the solid theme if Mica is refused.
    /// </summary>
    void ApplyWindowsLook()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            if (!App.IsWindows11) return;

            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            int backdrop = DWMSBT_MAINWINDOW;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            if (hr != 0)
            {
                int mica = 1;
                hr = DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref mica, sizeof(int));
            }
            if (hr != 0) return;

            var src = HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget == null) return;
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
            var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            Background = Brushes.Transparent;
            _root.Background = new SolidColorBrush(Color.FromArgb(0x9C, 0x07, 0x0B, 0x12));   // keep text contrast
            Log.Write("Windows 11 Mica backdrop on");
        }
        catch (Exception ex) { Log.Write("window look: " + ex.Message); }
    }
}
