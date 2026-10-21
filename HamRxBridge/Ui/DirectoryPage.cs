using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using HamRxBridge.Sdr;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge.Ui;

/// <summary>Searchable receiver list for KiwiSDR or WebSDR.org.</summary>
public sealed class DirectoryPage : UserControl
{
    readonly SdrKind _kind;
    List<ReceiverEntry> _all = new();
    readonly DataGrid _grid = new();
    readonly TextBox _search = new() { Width = 220, ToolTip = "Name, place, antenna or country" };
    readonly TextBox _where = new() { Width = 130, ToolTip = "Your grid square (e.g. MM74) or lat,lon. Sorts receivers by distance." };
    readonly TextBox _custom = new() { Width = 280 };
    readonly ToggleButton _hideFull, _favOnly;
    readonly TextBlock _count = Label("", 13, "DimBrush");
    readonly TextBlock _status = Label("", 12, "DimBrush");
    bool _loading;

    public event Action<ReceiverEntry, bool>? OpenRequested;   // entry, native view

    public DirectoryPage(SdrKind kind)
    {
        _kind = kind;
        _hideFull = Tog("Hide full and offline", (_, _) => Refilter(), Res("GoodBrush"));
        _hideFull.IsChecked = true;
        _favOnly = Tog("★ Favourites", (_, _) => Refilter(), Res("WarnBrush"));

        var root = new Grid { Margin = new Thickness(18, 14, 18, 12) };
        for (int i = 0; i < 3; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(GradientText(kind == SdrKind.Kiwi ? "KiwiSDR receivers" : "WebSDR receivers", 26));
        head.Children.Add(_count);
        root.Children.Add(head);

        var hint = Label(kind == SdrKind.Kiwi
            ? "Double click a receiver to open it in the pro view: our own waterfall, audio and S-meter."
            : "Double click a receiver to open its page. Your control panel and your radio will drive it.", 12, "DimBrush");
        hint.Margin = new Thickness(2, 0, 0, 10);
        Grid.SetRow(hint, 1);
        root.Children.Add(hint);

        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(Label("Search", 12, "DimBrush"));
        _search.TextChanged += (_, _) => Refilter();
        tools.Children.Add(_search);
        if (kind == SdrKind.Kiwi)
        {
            tools.Children.Add(Label("My location", 12, "DimBrush"));
            var s = AppSettings.Current;
            _where.Text = s.Lat.HasValue ? $"{s.Lat:0.###},{s.Lon:0.###}" : "";
            _where.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyLocation(); };
            _where.LostFocus += (_, _) => ApplyLocation();
            tools.Children.Add(_where);
            tools.Children.Add(_hideFull);
        }
        tools.Children.Add(_favOnly);
        tools.Children.Add(Btn("Reload list", async (_, _) => await LoadAsync()));
        Grid.SetRow(tools, 2);
        root.Children.Add(tools);

        BuildGrid();
        var gridFrame = new Border { BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _grid };
        Grid.SetRow(gridFrame, 3);
        root.Children.Add(gridFrame);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        if (kind == SdrKind.Kiwi)
            actions.Children.Add(Btn("Open its Kiwi web page", (_, _) => OpenSelected(false)));
        actions.Children.Add(AccentBtn(kind == SdrKind.Kiwi ? "Open pro view" : "Open receiver", (_, _) => OpenSelected(true)));
        bottom.Children.Add(actions);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Label("Or type an address", 12, "DimBrush"));
        _custom.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenCustom(); };
        left.Children.Add(_custom);
        left.Children.Add(Btn("Go", (_, _) => OpenCustom()));
        left.Children.Add(_status);
        bottom.Children.Add(left);
        Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);

        Content = root;
        Loaded += async (_, _) => { if (_all.Count == 0 && !_loading) await LoadAsync(); };
    }

    void BuildGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.CanUserAddRows = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
        _grid.Background = Res("PanelBrush");
        _grid.RowBackground = Res("PanelBrush");
        _grid.AlternatingRowBackground = Res("Panel2Brush");
        _grid.Foreground = Res("TextBrush");
        _grid.BorderThickness = new Thickness(0);
        _grid.RowHeight = 30;
        _grid.FontSize = 13;
        _grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var hs = new Style(typeof(DataGridColumnHeader));
        hs.Setters.Add(new Setter(BackgroundProperty, Res("Panel3Brush")));
        hs.Setters.Add(new Setter(ForegroundProperty, Res("AccentBrush")));
        hs.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        hs.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 7, 8, 7)));
        hs.Setters.Add(new Setter(BorderBrushProperty, Res("LineBrush")));
        hs.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        _grid.ColumnHeaderStyle = hs;

        var star = new DataGridTextColumn
        {
            Header = "★", Binding = new Binding(nameof(ReceiverEntry.Star)), Width = new DataGridLength(40),
            SortMemberPath = nameof(ReceiverEntry.Favorite)
        };
        var starStyle = new Style(typeof(TextBlock));
        starStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Res("WarnBrush")));
        starStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 16.0));
        starStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        starStyle.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        star.ElementStyle = starStyle;
        _grid.Columns.Add(star);

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
            Col("Location", nameof(ReceiverEntry.Location), 220);
            Col("km", nameof(ReceiverEntry.DistanceText), 80, nameof(ReceiverEntry.DistanceKm));
            Col("Users", nameof(ReceiverEntry.UsersText), 70, nameof(ReceiverEntry.Users));
            Col("SNR", nameof(ReceiverEntry.SnrText), 60, nameof(ReceiverEntry.Snr));
            Col("Coverage", nameof(ReceiverEntry.Coverage), 110);
            Col("Antenna", nameof(ReceiverEntry.Antenna), 240);
        }
        else Col("Address", nameof(ReceiverEntry.Url), 380);

        _grid.MouseDoubleClick += (_, e) =>
        {
            if (FindParent<DataGridColumnHeader>(e.OriginalSource as DependencyObject) != null) return;
            OpenSelected(true);
        };
        _grid.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell?.Column == star && cell.DataContext is ReceiverEntry r)
            {
                AppSettings.Current.ToggleFavorite(r);
                r.Favorite = AppSettings.Current.IsFavorite(r);
                _grid.Items.Refresh();
            }
        };
        _grid.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenSelected(true); e.Handled = true; } };
    }

    static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    void ApplyLocation()
    {
        var s = AppSettings.Current;
        if (string.IsNullOrWhiteSpace(_where.Text)) { s.Lat = s.Lon = null; }
        else if (TryParseLocation(_where.Text, out var la, out var lo)) { s.Lat = la; s.Lon = lo; }
        else { Status("Use a grid like MM74 or lat,lon like 31.5,74.3", StatusLevel.Warn); return; }
        s.Save();
        Refilter();
    }

    async Task LoadAsync()
    {
        _loading = true;
        Status("Loading the receiver list ...", StatusLevel.Info);
        try
        {
            var res = await Directories.LoadAsync(_kind, msg => Dispatcher.BeginInvoke(() => Status(msg, StatusLevel.Info)), CancellationToken.None);
            _all = res.Items;
            foreach (var e in _all) e.Favorite = AppSettings.Current.IsFavorite(e);
            Status(res.FromCache ? $"Showing a saved list ({res.Source}). Reload when online." : $"Live list from {res.Source}",
                res.FromCache ? StatusLevel.Warn : StatusLevel.Good);
        }
        catch (Exception ex)
        {
            Status("Could not load any list: " + ex.Message + "  You can still type an address.", StatusLevel.Bad);
        }
        _loading = false;
        Refilter();
    }

    void Refilter()
    {
        var s = AppSettings.Current;
        foreach (var e in _all)
            e.DistanceKm = s.Lat.HasValue && s.Lon.HasValue && !double.IsNaN(e.Lat)
                ? ReceiverEntry.Haversine(s.Lat.Value, s.Lon.Value, e.Lat, e.Lon) : double.NaN;

        string q = _search.Text.Trim();
        IEnumerable<ReceiverEntry> list = _all;
        if (q.Length > 0)
            list = list.Where(e => (e.Name + " " + e.Location + " " + e.Antenna + " " + e.Url).Contains(q, StringComparison.OrdinalIgnoreCase));
        if (_kind == SdrKind.Kiwi && _hideFull.IsChecked == true)
            list = list.Where(e => !e.Offline && !e.Full);
        if (_favOnly.IsChecked == true)
            list = list.Where(e => e.Favorite);
        list = list.OrderByDescending(e => e.Favorite)
            .ThenBy(e => double.IsNaN(e.DistanceKm) ? double.MaxValue : e.DistanceKm)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var shown = list.ToList();
        _grid.ItemsSource = shown;
        _count.Text = _all.Count == 0 ? "" : $"   {shown.Count} of {_all.Count}";
    }

    void OpenSelected(bool native)
    {
        if (_grid.SelectedItem is ReceiverEntry e) OpenRequested?.Invoke(e, native && e.Kind == SdrKind.Kiwi);
        else Status("Select a receiver first", StatusLevel.Warn);
    }

    void OpenCustom()
    {
        string t = _custom.Text.Trim();
        if (t.Length == 0) return;
        try
        {
            var e = _kind == SdrKind.Kiwi ? ReceiverEntry.FromKiwiAddress(t) : ReceiverEntry.FromWebSdrAddress(t);
            OpenRequested?.Invoke(e, _kind == SdrKind.Kiwi);
        }
        catch { Status("That address does not look right. Example: myreceiver.com:8073", StatusLevel.Warn); }
    }

    void Status(string text, StatusLevel level)
    {
        _status.Text = "   " + text;
        _status.Foreground = Res(level switch
        {
            StatusLevel.Good => "GoodBrush",
            StatusLevel.Warn => "WarnBrush",
            StatusLevel.Bad => "BadBrush",
            _ => "DimBrush"
        });
    }
}
