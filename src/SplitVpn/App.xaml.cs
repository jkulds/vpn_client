using System.Windows;
using System.Windows.Threading;

namespace SplitVpn;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Необработанная ошибка",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
