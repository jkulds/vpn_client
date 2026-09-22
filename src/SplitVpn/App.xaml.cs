using System.Windows;
using System.Windows.Threading;
using SplitVpn.Views;

namespace SplitVpn;

public partial class App : Application
{
    private const string MutexName = @"Local\SplitVpn.SingleInstance";
    private const string ShowEventName = @"Local\SplitVpn.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;

    /// <summary>Второй экземпляр попросил показать окно первого. Приходит на UI-потоке.</summary>
    public static event Action? ShowRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Окно живёт в трее, и повторный запуск ярлыка раньше давал второй экземпляр: два ядра,
        // два набора сайдкаров, а у старого окна - чужой секрет clash_api и «не отвечает» в журнале.
        // Именованный mutex - один на сеанс; второй экземпляр только будит первый и выходит.
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (!first)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        var waiter = new Thread(WaitForShowRequests) { IsBackground = true, Name = "single-instance" };
        waiter.Start();

        // Окно создаётся здесь, а не через StartupUri: иначе второй экземпляр успевал бы
        // построить окно, ViewModel и значок в трее до того, как выйти.
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void WaitForShowRequests()
    {
        while (true)
        {
            try
            {
                _showEvent!.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Dispatcher.InvokeAsync(() => ShowRequested?.Invoke());
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); }
        catch (ApplicationException) { /* mutex не наш - второй экземпляр */ }

        _mutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Необработанная ошибка",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
