using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using HamRxBridge.Sdr;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge.Ui;

/// <summary>Searchable receiver list for either KiwiSDR or WebSDR.org.</summary>
public sealed class DirectoryPage : UserControl
{
    readonly SdrKind _kind;
    List<ReceiverEntry> _all = new();
    readonly DataGrid _grid = new();
    readonly TextBox _search = new() { Width = 220 };
    readonly TextBox _where = new() { Width = 130 };
    readonly TextBox _custom = new() { Width = 260 };
    readonly CheckBox _hideFull = new() { Content = "Hide full or offline", IsChecked = true, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    readonly TextBlock _status = Label("", 12, "DimBrush");

    public event Action<ReceiverEntry, bool>? OpenRequested;   // entry, native view

    public DirectoryPage(SdrKind kind)
    {
        _kind = kind;
        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = Label(kind == SdrKind.Kiwi ? "KiwiSDR public receivers" : "WebSDR.org receivers", 20, "TextBrush", true);
        title.Margin = new Thickness(2, 0, 2, 8);
        root.Children.Add(title);

        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(Label("Search", 12, "DimBrush"));
        _search.TextChanged += (_, _) => Refilter();
        tools.Children.Add(_search);
        if (kind == SdrKind.Kiwi)
        {
            tools.Children.Add(Label("My grid or lat,lon", 12, "DimBrush"));
            _where.Text = AppSettings.Current.Lat.HasValue ? $"{AppSettings.Current.Lat:0.###},{AppSettings.Current.Lon:0.###}" : "";
            _where.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyLocation(); };
            _where.LostFocus += (_, _) => ApplyLocation();
            tools.Children.Add(_where);
            _hideFull.Click += (_, _) => Refilter();
            tools.Children.Add(_hideFull);
        }
        tools.Children.Add(Btn("Reload list", async (_, _) => await LoadAsync()));
        Grid.SetRow(tools, 1);
        root.Children.Add(tools);

        BuildGrid();
        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        if (kind == SdrKind.Kiwi)
            actions.Children.Add(Btn("Open pro view", (_, _) => OpenSelected(true)));
        actions.Children.Add(Btn(kind == SdrKind.Kiwi ? "Open web view" : "Open receiver", (_, _) => OpenSelected(false)));
        bottom.Children.Add(actions);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Label(kind == SdrKind.Kiwi ? "Or type a Kiwi address" : "Or type a WebSDR address", 12, "DimBrush"));
        _custom.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenCustom(); };
        left.Children.Add(_custom);
        left.Children.Add(Btn("Go", (_, _) => OpenCustom()));
        left.Children.Add(_status);
        bottom.Children.Add(left);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        Content = root;
        Loaded += async (_, _) => { if (_all.Count == 0) await LoadAsync(); };
    }

    void BuildGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.CanUserAddRows = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
        _grid.Background = Res("PanelBrush");
        _grid.RowBackground = Res("PanelBrush");
        _grid.AlternatingRowBackground = Res("Panel2Brush");
        _grid.Foreground = Res("TextBrush");
        _grid.BorderBrush = Res("LineBrush");
        _grid.RowHeight = 28;
        _grid.FontSize = 13;

        var hs = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        hs.Setters.Add(new Setter(BackgroundProperty, Res("Panel2Brush")));
        hs.Setters.Add(new Setter(ForegroundProperty, Res("DimBrush")));
        hs.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 6, 8, 6)));
        hs.Setters.Add(new Setter(BorderBrushProperty, Res("LineBrush")));
        hs.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        _grid.ColumnHeaderStyle = hs;

        void Col(string header, string path, double width, string? sort = null) =>
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = width <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
                SortMemberPath = sort ?? path
            });

        Col("Name", nameof(ReceiverEntry.Name), 0);
        if (_kind == SdrKind.Kiwi)
        {
            Col("Location", nameof(ReceiverEntry.Location), 190);
            Col("Km", nameof(ReceiverEntry.DistanceText), 80, nameof(ReceiverEntry.DistanceKm));
            Col("Users", nameof(ReceiverEntry.UsersText), 70, nameof(ReceiverEntry.Users));
            Col("SNR", nameof(ReceiverEntry.SnrText), 60, nameof(ReceiverEntry.Snr));
            Col("Coverage", nameof(ReceiverEntry.Coverage), 110);
            Col("Antenna", nameof(ReceiverEntry.Antenna), 220);
        }
        else
        {
            Col("Address", nameof(ReceiverEntry.Url), 380);
        }
        _grid.MouseDoubleClick += (_, _) => OpenSelected(_kind == SdrKind.Kiwi);
    }

    void ApplyLocation()
    {
        if (string.IsNullOrWhiteSpace(_where.Text)) { AppSettings.Current.Lat = AppSettings.Current.Lon = null; }
        else if (TryParseLocation(_where.Text, out var la, out var lo)) { AppSettings.Current.Lat = la; AppSettings.Current.Lon = lo; }
        else { _status.Text = "   Use a grid like JN58 or lat,lon like 48.1,11.6"; return; }
        AppSettings.Current.Save();
        Refilter();
    }

    async Task LoadAsync()
    {
        _status.Text = "   Loading list ...";
        _status.Foreground = Res("DimBrush");
        try
        {
            _all = _kind == SdrKind.Kiwi
                ? await Directories.LoadKiwiAsync(CancellationToken.None)
                : await Directories.LoadWebSdrAsync(CancellationToken.None);
            _status.Text = $"   {_all.Count} receivers";
            if (_all.Count == 0)
                _status.Text = "   List came back empty. Type an address on the left instead.";
        }
        catch (Exception ex)
        {
            _status.Text = "   Could not load the list (" + ex.Message + "). You can still type an address.";
            _status.Foreground = Res("BadBrush");
            if (_kind == SdrKind.WebSdr)
                _all = new() { new ReceiverEntry { Kind = SdrKind.WebSdr, Name = "University of Twente, NL", Url = "http://websdr.ewi.utwente.nl:8901/" } };
        }
        Refilter();
    }

    void Refilter()
    {
        var lat = AppSettings.Current.Lat; var lon = AppSettings.Current.Lon;
        foreach (var e in _all)
            e.DistanceKm = lat.HasValue && lon.HasValue && !double.IsNaN(e.Lat)
                ? ReceiverEntry.Haversine(lat.Value, lon.Value, e.Lat, e.Lon) : double.NaN;

        string q = _search.Text.Trim();
        IEnumerable<ReceiverEntry> list = _all;
        if (q.Length > 0)
            list = list.Where(e => (e.Name + " " + e.Location + " " + e.Antenna + " " + e.Url).Contains(q, StringComparison.OrdinalIgnoreCase));
        if (_kind == SdrKind.Kiwi && _hideFull.IsChecked == true)
            list = list.Where(e => !e.Offline && !e.Full);
        list = lat.HasValue
            ? list.OrderBy(e => double.IsNaN(e.DistanceKm) ? double.MaxValue : e.DistanceKm)
            : list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        _grid.ItemsSource = list.ToList();
    }

    void OpenSelected(bool native)
    {
        if (_grid.SelectedItem is ReceiverEntry e) OpenRequested?.Invoke(e, native && e.Kind == SdrKind.Kiwi);
        else _status.Text = "   Select a receiver first";
    }

    void OpenCustom()
    {
        string t = _custom.Text.Trim();
        if (t.Length == 0) return;
        try
        {
            ReceiverEntry e;
            if (_kind == SdrKind.Kiwi) e = ReceiverEntry.FromKiwiAddress(t);
            else
            {
                if (!t.Contains("://")) t = "http://" + t;
                var u = new Uri(t);
                e = new ReceiverEntry { Kind = SdrKind.WebSdr, Name = u.Host, Location = u.Host, Url = t };
            }
            OpenRequested?.Invoke(e, _kind == SdrKind.Kiwi);
        }
        catch { _status.Text = "   That address does not look right"; }
    }
}
