using System.Windows;
using System.Windows.Controls;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge.Ui;

public sealed class StartPage : UserControl
{
    public event Action<Sdr.SdrKind>? Chosen;

    public StartPage()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var h = Label("Choose your receiver network", 28, "TextBrush", true);
        h.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(h);
        var sub = Label("A clean antenna somewhere quiet, tuned by your own radio.", 14, "DimBrush");
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        sub.Margin = new Thickness(0, 4, 0, 28);
        stack.Children.Add(sub);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(Tile("KiwiSDR", "kiwisdr.com/public", "Pro view with spectrum and waterfall, plus the classic web page.", Sdr.SdrKind.Kiwi));
        row.Children.Add(Tile("WebSDR", "websdr.org", "The receiver's own page inside the app. Follows your radio.", Sdr.SdrKind.WebSdr));
        stack.Children.Add(row);
        var by = Label("By AP2FLY", 14, "DimBrush");
        by.HorizontalAlignment = HorizontalAlignment.Center;
        by.Margin = new Thickness(0, 30, 0, 0);
        stack.Children.Add(by);
        Content = stack;
    }

    Button Tile(string title, string site, string blurb, Sdr.SdrKind kind)
    {
        var s = new StackPanel { Width = 300, Margin = new Thickness(18) };
        s.Children.Add(Label(title, 30, "AccentBrush", true));
        s.Children.Add(Label(site, 13, "DimBrush"));
        var t = Label(blurb, 14);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(4, 14, 4, 0);
        s.Children.Add(t);
        var b = new Button { Content = s, Margin = new Thickness(12), Padding = new Thickness(4), Background = Res("PanelBrush") };
        b.Click += (_, _) => Chosen?.Invoke(kind);
        return b;
    }
}
