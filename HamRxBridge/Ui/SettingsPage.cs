using System.IO;
using System.IO.Ports;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HamRxBridge.Rig;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge.Ui;

public sealed class SettingsPage : UserControl
{
    readonly RigHub _rig;
    readonly TextBox _call = new() { Width = 160 };
    readonly TextBox _grid = new() { Width = 160 };
    readonly Dictionary<CatSource, ToggleButton> _cat = new();
    readonly StackPanel _omniBox = new(), _directBox = new();
    readonly ToggleButton _rig1, _rig2;
    readonly WrapPanel _ports = new();
    readonly WrapPanel _bauds = new();
    readonly TextBox _civ = new() { Width = 70 };
    readonly CheckBox _rtscts = new() { Content = "Kenwood: use RTS/CTS flow control (TS-480 needs this)" };
    readonly CheckBox _comp = new() { Content = "KiwiSDR compressed audio (uses about 4x less internet data)" };
    readonly CheckBox _muteTx = new() { Content = "Mute the receiver while my radio transmits" };
    readonly TextBlock _rigStatus = Label("", 13);
    readonly TextBlock _detect = Label("", 12, "DimBrush");

    public SettingsPage(RigHub rig)
    {
        _rig = rig;
        var s = AppSettings.Current;
        _rig1 = Tog("Rig 1", (_, _) => { s.RigIndex = 1; SyncUi(); });
        _rig2 = Tog("Rig 2", (_, _) => { s.RigIndex = 2; SyncUi(); });

        var stack = new StackPanel { Margin = new Thickness(24, 16, 24, 24), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(GradientText("Settings", 28));
        var sub = Label("Changes are saved automatically. Press Connect after changing radio settings.", 12, "DimBrush");
        sub.Margin = new Thickness(2, 2, 0, 14);
        stack.Children.Add(sub);

        // ---- operator
        var op = new StackPanel();
        op.Children.Add(Section("Operator"));
        var opRow = new WrapPanel();
        opRow.Children.Add(Label("Callsign", 12, "DimBrush"));
        _call.Text = s.Callsign;
        _call.TextChanged += (_, _) => { s.Callsign = _call.Text.Trim().ToUpperInvariant(); s.Save(); };
        opRow.Children.Add(_call);
        opRow.Children.Add(Label("Grid or lat,lon", 12, "DimBrush"));
        _grid.Text = s.Lat.HasValue ? $"{s.Lat:0.###},{s.Lon:0.###}" : "";
        _grid.LostFocus += (_, _) =>
        {
            if (TryParseLocation(_grid.Text, out var la, out var lo)) { s.Lat = la; s.Lon = lo; s.Save(); }
        };
        opRow.Children.Add(_grid);
        op.Children.Add(opRow);
        op.Children.Add(Label("Your callsign is shown to KiwiSDR owners as your user name. Location sorts receivers by distance.", 11, "DimBrush"));
        stack.Children.Add(Card(op));

        // ---- radio control
        var rc = new StackPanel();
        rc.Children.Add(Section("Radio control (CAT)"));
        var catRow = new WrapPanel();
        foreach (var (src, text) in new[] { (CatSource.OmniRig, "OmniRig"), (CatSource.IcomCiv, "Icom CI-V (built in)"),
                     (CatSource.KenwoodYaesu, "Kenwood / Yaesu (built in)"), (CatSource.None, "No radio") })
        {
            var cs = src;
            var b = Tog(text, (_, _) => { s.Cat = cs; s.Save(); SyncUi(); });
            _cat[src] = b;
            catRow.Children.Add(b);
        }
        rc.Children.Add(catRow);

        // OmniRig options
        _omniBox.Margin = new Thickness(0, 8, 0, 0);
        var omRow = new WrapPanel();
        omRow.Children.Add(Label("Which OmniRig rig", 12, "DimBrush"));
        omRow.Children.Add(_rig1);
        omRow.Children.Add(_rig2);
        omRow.Children.Add(Btn("Open OmniRig settings", (_, _) => OmniRigService.ShowDialog()));
        _omniBox.Children.Add(omRow);
        if (!OmniRigService.IsInstalled())
        {
            var warn = new WrapPanel();
            warn.Children.Add(Label("OmniRig is not installed on this PC.", 12, "WarnBrush"));
            warn.Children.Add(Btn("Download OmniRig", (_, _) => OpenInBrowser("https://www.dxatlas.com/omnirig/")));
            warn.Children.Add(Label("or use the built in Icom / Kenwood option, no extra software needed.", 12, "DimBrush"));
            _omniBox.Children.Add(warn);
        }
        rc.Children.Add(_omniBox);

        // Direct CAT options
        _directBox.Margin = new Thickness(0, 8, 0, 0);
        var portHead = new WrapPanel();
        portHead.Children.Add(Label("COM port", 12, "DimBrush"));
        portHead.Children.Add(Btn("Refresh", (_, _) => FillPorts()));
        portHead.Children.Add(Btn("Auto-detect my radio", async (_, _) => await AutoDetect()));
        _directBox.Children.Add(portHead);
        _directBox.Children.Add(_ports);
        var baudHead = Label("Baud rate (must match the radio's CI-V or COM menu)", 12, "DimBrush");
        baudHead.Margin = new Thickness(4, 8, 0, 2);
        _directBox.Children.Add(baudHead);
        _directBox.Children.Add(_bauds);
        var civRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        civRow.Children.Add(Label("Icom CI-V address (hex). IC-7300 is 94, IC-705 is A4, IC-7610 is 98", 12, "DimBrush"));
        _civ.Text = s.CivAddress;
        _civ.TextChanged += (_, _) => { s.CivAddress = _civ.Text.Trim(); s.Save(); };
        civRow.Children.Add(_civ);
        _directBox.Children.Add(civRow);
        _rtscts.IsChecked = s.KenwoodRtsCts;
        _rtscts.Click += (_, _) => { s.KenwoodRtsCts = _rtscts.IsChecked == true; s.Save(); };
        _directBox.Children.Add(_rtscts);
        _directBox.Children.Add(_detect);
        var safety = Label("Safety: the built in CAT only reads the radio and sets frequency and mode. It never sends a transmit command.", 11, "DimBrush");
        safety.TextWrapping = TextWrapping.Wrap;
        _directBox.Children.Add(safety);
        rc.Children.Add(_directBox);

        var connectRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        connectRow.Children.Add(AccentBtn("Connect", (_, _) => { s.Save(); _rig.Restart(); }));
        connectRow.Children.Add(_rigStatus);
        rc.Children.Add(connectRow);
        rc.Children.Add(Label("Tip: OmniRig and the built in CAT cannot share one COM port. Close other CAT programs first.", 11, "DimBrush"));
        stack.Children.Add(Card(rc));

        // ---- receiver
        var rx = new StackPanel();
        rx.Children.Add(Section("Receiver"));
        _comp.IsChecked = s.KiwiCompression;
        _comp.Click += (_, _) => { s.KiwiCompression = _comp.IsChecked == true; s.Save(); };
        rx.Children.Add(_comp);
        _muteTx.IsChecked = s.MuteOnTx;
        _muteTx.Click += (_, _) => { s.MuteOnTx = _muteTx.IsChecked == true; s.Save(); };
        rx.Children.Add(_muteTx);
        stack.Children.Add(Card(rx));

        // ---- about
        var about = new StackPanel();
        about.Children.Add(Section("About"));
        var ab = new WrapPanel();
        ab.Children.Add(GradientText("HamRxBridge", 18));
        ab.Children.Add(Label("version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0"), 12, "DimBrush"));
        ab.Children.Add(Signature(16));
        about.Children.Add(ab);
        var logRow = new WrapPanel();
        logRow.Children.Add(Btn("Open log and data folder", (_, _) =>
        {
            Directory.CreateDirectory(AppSettings.Folder);
            OpenInBrowser(AppSettings.Folder);
        }));
        logRow.Children.Add(Label("If something fails, send AP2FLY the file log.txt from this folder.", 11, "DimBrush"));
        about.Children.Add(logRow);
        stack.Children.Add(Card(about));

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };

        FillPorts();
        FillBauds();
        SyncUi();
        _rig.Changed += UpdateRigStatus;
        Unloaded += (_, _) => _rig.Changed -= UpdateRigStatus;
        UpdateRigStatus();
    }

    void SyncUi()
    {
        var s = AppSettings.Current;
        foreach (var kv in _cat) kv.Value.IsChecked = kv.Key == s.Cat;
        _rig1.IsChecked = s.RigIndex != 2;
        _rig2.IsChecked = s.RigIndex == 2;
        _omniBox.Visibility = s.Cat == CatSource.OmniRig ? Visibility.Visible : Visibility.Collapsed;
        _directBox.Visibility = s.Cat is CatSource.IcomCiv or CatSource.KenwoodYaesu ? Visibility.Visible : Visibility.Collapsed;
        s.Save();
    }

    void FillPorts()
    {
        _ports.Children.Clear();
        var s = AppSettings.Current;
        var names = SerialPort.GetPortNames().Distinct().OrderBy(n => n.Length).ThenBy(n => n).ToList();
        if (names.Count == 0) _ports.Children.Add(Label("No COM ports found. Plug in the radio's USB cable and press Refresh.", 12, "WarnBrush"));
        foreach (var n in names)
        {
            string name = n;
            var b = Tog(name, (_, _) => { s.CatPort = name; s.Save(); FillPorts(); });
            b.IsChecked = name == s.CatPort;
            _ports.Children.Add(b);
        }
    }

    void FillBauds()
    {
        _bauds.Children.Clear();
        var s = AppSettings.Current;
        foreach (int b in new[] { 4800, 9600, 19200, 38400, 57600, 115200 })
        {
            int baud = b;
            var t = Tog(b.ToString(), (_, _) => { s.CatBaud = baud; s.Save(); FillBauds(); });
            t.IsChecked = b == s.CatBaud;
            _bauds.Children.Add(t);
        }
    }

    async Task AutoDetect()
    {
        var r = MessageBox.Show(
            "Auto-detect opens each COM port briefly and sends read-only queries.\n\n" +
            "Close OmniRig and other CAT programs first. Continue?",
            "HamRxBridge by AP2FLY", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;

        var s = AppSettings.Current;
        var prevCat = s.Cat;
        s.Cat = CatSource.None;
        _rig.Restart();                     // release our own hold on the port first
        _detect.Text = "Searching ...";
        var found = await Task.Run(() => DirectCat.Detect(msg => Dispatcher.BeginInvoke(() => _detect.Text = msg)));
        if (found is { } f)
        {
            s.Cat = f.proto; s.CatPort = f.port; s.CatBaud = f.baud;
            if (f.proto == CatSource.IcomCiv) { s.CivAddress = f.civ; _civ.Text = f.civ; }
            s.Save();
            _detect.Text = f.text + ". Connected.";
            _detect.Foreground = Res("GoodBrush");
        }
        else
        {
            s.Cat = prevCat;
            _detect.Text = "No radio answered. Check the cable, the baud rate in the radio's menu, and that no other program has the port.";
            _detect.Foreground = Res("WarnBrush");
        }
        FillPorts(); FillBauds(); SyncUi();
        _rig.Restart();
    }

    void UpdateRigStatus()
    {
        var b = _rig.Backend;
        _rigStatus.Text = b.Link == RigLink.Online
            ? $"Connected: {b.LinkText}   {FormatHz(b.FreqHz)}"
            : b.LinkText;
        _rigStatus.Foreground = Res(b.Link == RigLink.Online ? "GoodBrush" : b.Link == RigLink.Off ? "DimBrush" : "WarnBrush");
    }
}
