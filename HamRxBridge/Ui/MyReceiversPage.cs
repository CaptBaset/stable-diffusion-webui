using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using HamRxBridge.Sdr;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge.Ui;

/// <summary>
/// The operator's own receiver memory: paste many addresses at once, rename, remove, open.
/// Saved receivers open directly, without loading any list from the internet.
/// </summary>
public sealed class MyReceiversPage : UserControl
{
    readonly TextBox _paste = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 110,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
        ToolTip = "One address per line, for example:\nsdr.example.com:8073\nhttp://websdr.ewi.utwente.nl:8901/"
    };
    readonly ToggleButton _auto, _kiwi, _web;
    readonly Button _addBtn;
    readonly TextBlock _addStatus = Label("", 12, "DimBrush");
    readonly DataGrid _grid = new();
    readonly TextBox _rename = new() { Width = 260 };
    readonly TextBlock _count = Label("", 13, "DimBrush");

    public event Action<ReceiverEntry, bool>? OpenRequested;

    public MyReceiversPage()
    {
        _auto = Tog("Detect automatically", (_, _) => PickType(null), Res("GoodBrush"));
        _kiwi = Tog("All are KiwiSDR", (_, _) => PickType(SdrKind.Kiwi));
        _web = Tog("All are WebSDR", (_, _) => PickType(SdrKind.WebSdr), Res("VioletBrush"));
        _auto.IsChecked = true;
        _addBtn = AccentBtn("Add to My receivers", async (_, _) => await AddAllAsync());

        var root = new Grid { Margin = new Thickness(18, 14, 18, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(GradientText("My receivers", 26));
        head.Children.Add(_count);
        root.Children.Add(head);

        // ---- bulk add
        var add = new StackPanel();
        add.Children.Add(Section("Add receivers (paste as many as you like, one per line)"));
        add.Children.Add(_paste);
        var addRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        addRow.Children.Add(_auto);
        addRow.Children.Add(_kiwi);
        addRow.Children.Add(_web);
        addRow.Children.Add(_addBtn);
        addRow.Children.Add(_addStatus);
        add.Children.Add(addRow);
        var tip = Label("Tip: you can also press the star on any receiver in the KiwiSDR or WebSDR lists, or the Save button while listening.", 11, "DimBrush");
        tip.TextWrapping = TextWrapping.Wrap;
        add.Children.Add(tip);
        var addCard = Card(add);
        Grid.SetRow(addCard, 1);
        root.Children.Add(addCard);

        // ---- list
        BuildGrid();
        var frame = new Border { BorderBrush = Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _grid };
        Grid.SetRow(frame, 2);
        root.Children.Add(frame);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(IconBtn(Glyph.Globe, "Open as web page", (_, _) => OpenSelected(false)));
        right.Children.Add(AccentBtn("Open", (_, _) => OpenSelected(true)));
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(right);
        var left = new WrapPanel();
        left.Children.Add(_rename);
        left.Children.Add(Btn("Rename", (_, _) => RenameSelected()));
        left.Children.Add(Btn("Remove", (_, _) => RemoveSelected()));
        left.Children.Add(Btn("Move up", (_, _) => MoveSelected(-1)));
        left.Children.Add(Btn("Move down", (_, _) => MoveSelected(+1)));
        left.Children.Add(Btn("Export", (_, _) => Export(), "Save your list to a text file (backup or share)"));
        left.Children.Add(Btn("Import", (_, _) => Import(), "Load addresses from a text file"));
        bottom.Children.Add(left);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        Content = root;
        Loaded += (_, _) => Refresh();
    }

    void PickType(SdrKind? k)
    {
        _auto.IsChecked = k == null;
        _kiwi.IsChecked = k == SdrKind.Kiwi;
        _web.IsChecked = k == SdrKind.WebSdr;
    }

    SdrKind? ChosenType => _kiwi.IsChecked == true ? SdrKind.Kiwi : _web.IsChecked == true ? SdrKind.WebSdr : null;

    void BuildGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.CanUserAddRows = false;
        _grid.CanUserSortColumns = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
        _grid.Background = Res("PanelBrush");
        _grid.RowBackground = Res("PanelBrush");
        _grid.AlternatingRowBackground = Res("Panel2Brush");
        _grid.Foreground = Res("TextBrush");
        _grid.BorderThickness = new Thickness(0);
        _grid.RowHeight = 32;
        _grid.FontSize = 13;
        var hs = new Style(typeof(DataGridColumnHeader));
        hs.Setters.Add(new Setter(BackgroundProperty, Res("Panel3Brush")));
        hs.Setters.Add(new Setter(ForegroundProperty, Res("AccentBrush")));
        hs.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        hs.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 7, 8, 7)));
        hs.Setters.Add(new Setter(BorderBrushProperty, Res("LineBrush")));
        hs.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        _grid.ColumnHeaderStyle = hs;

        void Col(string header, string path, double width) => _grid.Columns.Add(new DataGridTextColumn
        {
            Header = header, Binding = new Binding(path),
            Width = width <= 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width)
        });
        Col("Name", nameof(ReceiverEntry.Name), 0);
        Col("Type", nameof(ReceiverEntry.KindText), 90);
        Col("Location", nameof(ReceiverEntry.Location), 220);
        Col("Address", nameof(ReceiverEntry.BaseUrl), 320);

        _grid.SelectionChanged += (_, _) => { if (_grid.SelectedItem is ReceiverEntry r) _rename.Text = r.Name; };
        _grid.MouseDoubleClick += (_, _) => OpenSelected(true);
        _grid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelected(true); e.Handled = true; }
            if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; }
        };
    }

    void Refresh()
    {
        var list = AppSettings.Current.MyReceivers;
        _grid.ItemsSource = null;
        _grid.ItemsSource = list;
        _count.Text = list.Count == 0 ? "   empty so far" : $"   {list.Count} saved";
    }

    async Task AddAllAsync()
    {
        var lines = Directories.ExtractAddresses(_paste.Text);
        if (lines.Count == 0) { _addStatus.Text = "No addresses found. Paste lines like sdr.example.com:8073"; return; }

        _addBtn.IsEnabled = false;
        int added = 0, failed = 0, done = 0;
        var forced = ChosenType;
        var bad = new List<string>();
        // check up to 6 receivers at a time
        using var gate = new SemaphoreSlim(6);
        var tasks = lines.Select(async line =>
        {
            await gate.WaitAsync();
            try
            {
                var e = await Directories.ResolveAddressAsync(line, forced, CancellationToken.None);
                await Dispatcher.InvokeAsync(() =>
                {
                    done++;
                    if (e == null) { failed++; bad.Add(line); }
                    else { AppSettings.Current.SaveReceiver(e); added++; }
                    _addStatus.Text = $"Checking {done} of {lines.Count} ...";
                });
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        _addBtn.IsEnabled = true;
        _paste.Text = string.Join(Environment.NewLine, bad);
        _addStatus.Text = failed == 0 ? $"Added {added}." : $"Added {added}. {failed} did not look like addresses (left in the box).";
        _addStatus.Foreground = Res(failed == 0 ? "GoodBrush" : "WarnBrush");
        Refresh();
    }

    ReceiverEntry? Selected => _grid.SelectedItem as ReceiverEntry;

    void OpenSelected(bool native)
    {
        if (Selected is { } e) OpenRequested?.Invoke(e, native && e.Kind == SdrKind.Kiwi);
        else _addStatus.Text = "Select a receiver in the list first.";
    }

    void RenameSelected()
    {
        if (Selected is not { } e || string.IsNullOrWhiteSpace(_rename.Text)) return;
        e.Name = _rename.Text.Trim();
        AppSettings.Current.Save();
        Refresh();
    }

    void RemoveSelected()
    {
        if (Selected is not { } e) return;
        AppSettings.Current.RemoveReceiver(e);
        Refresh();
    }

    void MoveSelected(int dir)
    {
        if (Selected is not { } e) return;
        var list = AppSettings.Current.MyReceivers;
        int i = list.IndexOf(e), j = i + dir;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        AppSettings.Current.Save();
        Refresh();
        _grid.SelectedItem = e;
    }

    void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "my_receivers.txt", Filter = "Text file|*.txt" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllLines(dlg.FileName, AppSettings.Current.MyReceivers.Select(r => r.BaseUrl));
        _addStatus.Text = "Exported " + AppSettings.Current.MyReceivers.Count + " receivers.";
    }

    void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Text file|*.txt|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        _paste.Text = File.ReadAllText(dlg.FileName);
        _addStatus.Text = "Loaded. Press 'Add to My receivers'.";
    }
}
