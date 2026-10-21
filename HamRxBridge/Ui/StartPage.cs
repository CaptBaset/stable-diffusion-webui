using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HamRxBridge.Sdr;
using static HamRxBridge.Ui.UiKit;

namespace HamRxBridge.Ui;

public sealed class StartPage : UserControl
{
    public event Action<SdrKind>? Chosen;
    public event Action<ReceiverEntry, bool>? ResumeRequested;
    public event Action? SettingsRequested;
    public event Action? MineRequested;

    public StartPage()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20) };

        var logoRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        try
        {
            logoRow.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/app.png")),
                Width = 76, Height = 76, Margin = new Thickness(0, 0, 16, 0)
            });
        }
        catch { }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(GradientText("HamRxBridge", 52));
        var sig = Signature(20);
        sig.Margin = new Thickness(4, -6, 0, 0);
        titles.Children.Add(sig);
        logoRow.Children.Add(titles);
        stack.Children.Add(logoRow);

        var sub = Label("Listen on a quiet antenna anywhere in the world, steered by your own radio.", 15, "DimBrush");
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        sub.Margin = new Thickness(0, 14, 0, 30);
        stack.Children.Add(sub);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(Tile("KiwiSDR", "Pro view", "Our own waterfall, spectrum, S-meter and audio. 800+ receivers worldwide.", "#29C5FF",
            () => Chosen?.Invoke(SdrKind.Kiwi)));
        row.Children.Add(Tile("WebSDR", "Live control", "The receiver's own page, tuned live by this app's panel and your radio.", "#A855F7",
            () => Chosen?.Invoke(SdrKind.WebSdr)));
        stack.Children.Add(row);

        var s = AppSettings.Current;
        if (s.LastReceiver != null)
        {
            var last = s.LastReceiver;
            var resume = AccentBtn($"Continue with  {last.Name}", (_, _) => ResumeRequested?.Invoke(last, s.LastWasNative));
            resume.Margin = new Thickness(0, 26, 0, 0);
            resume.Padding = new Thickness(22, 10, 22, 10);
            resume.FontSize = 15;
            resume.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(resume);
        }

        // ---- My receivers: one click each
        var mine = AppSettings.Current.MyReceivers;
        var mineHead = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 6) };
        mineHead.Children.Add(Label("MY RECEIVERS", 12, "DimBrush", true));
        mineHead.Children.Add(Btn(mine.Count == 0 ? "Add some" : "Manage", (_, _) => MineRequested?.Invoke()));
        stack.Children.Add(mineHead);
        if (mine.Count == 0)
        {
            var none = Label("Save receivers once, then open them here with one click.", 13, "DimBrush");
            none.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(none);
        }
        else
        {
            var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 900 };
            foreach (var r in mine.Take(12))
            {
                var rr = r;
                var b = Btn("", (_, _) => ResumeRequested?.Invoke(rr, rr.Kind == SdrKind.Kiwi), rr.BaseUrl);
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new Border
                {
                    Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0),
                    Background = Res(rr.Kind == SdrKind.Kiwi ? "AccentBrush" : "VioletBrush")
                });
                content.Children.Add(new TextBlock { Text = rr.Name.Length > 34 ? rr.Name[..34] + "..." : rr.Name, VerticalAlignment = VerticalAlignment.Center });
                b.Content = content;
                b.Padding = new Thickness(12, 7, 12, 7);
                wrap.Children.Add(b);
            }
            stack.Children.Add(wrap);
        }

        var settings = Btn("Settings: radio, callsign, location", (_, _) => SettingsRequested?.Invoke());
        settings.HorizontalAlignment = HorizontalAlignment.Center;
        settings.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(settings);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
    }

    Button Tile(string title, string tag, string blurb, string color, Action click)
    {
        var accent = Hex(color);
        var s = new StackPanel { Width = 300, Margin = new Thickness(20, 18, 20, 20) };
        s.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = accent, Margin = new Thickness(0, 0, 0, 14), Width = 60, HorizontalAlignment = HorizontalAlignment.Left });
        var t = Label(title, 32, "TextBrush", true);
        t.Foreground = accent;
        s.Children.Add(t);
        s.Children.Add(Label(tag.ToUpperInvariant(), 12, "DimBrush", true));
        var b = Label(blurb, 14);
        b.TextWrapping = TextWrapping.Wrap;
        b.TextTrimming = TextTrimming.None;
        b.Margin = new Thickness(4, 12, 4, 0);
        s.Children.Add(b);
        var btn = new Button { Content = s, Margin = new Thickness(12), Padding = new Thickness(0), Background = Res("PanelBrush"), HorizontalContentAlignment = HorizontalAlignment.Left };
        btn.Click += (_, _) => click();
        return btn;
    }
}
