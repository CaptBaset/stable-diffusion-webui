using System.Windows;

namespace HamRxBridge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "HamRxBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        new MainWindow().Show();
    }
}
