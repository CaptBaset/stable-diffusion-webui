using System.Windows;

namespace HamRxBridge;

public partial class App : Application
{
    /// <summary>Windows 11 is build 22000 or later. Mica needs 22000, the official backdrop API 22621.</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (IsWindows11)
        {
            // Let the Mica backdrop glow through cards and bars. Set before any window is built.
            Resources["PanelBrush"] = Frozen(System.Windows.Media.Color.FromArgb(0xC8, 0x0E, 0x15, 0x22));
            Resources["Panel3Brush"] = Frozen(System.Windows.Media.Color.FromArgb(0xE0, 0x1C, 0x2A, 0x42));
        }
        Log.Write("---- HamRxBridge started ----");
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("UI error: " + ex.Exception);
            MessageBox.Show(ex.Exception.Message, "HamRxBridge by AP2FLY", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("Fatal: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write("Task: " + ex.Exception); ex.SetObserved(); };
        new MainWindow().Show();
    }

    static System.Windows.Media.SolidColorBrush Frozen(System.Windows.Media.Color c)
    {
        var b = new System.Windows.Media.SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
