using System.Windows;

namespace HamRxBridge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
}
