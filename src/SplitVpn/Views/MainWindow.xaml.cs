using System.ComponentModel;
using System.Windows;
using SplitVpn.Models;
using SplitVpn.Services;
using SplitVpn.ViewModels;

namespace SplitVpn.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly TrayIcon _tray;

    /// <summary>Отличает настоящий выход от закрытия окна в трей.</summary>
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.ImportLinksRequested += OnImportLinks;
        _vm.AddSubscriptionRequested += OnAddSubscription;
        _vm.RoutingRequested += OnOpenRouting;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        _tray = new TrayIcon();
        _tray.ShowRequested += RestoreFromTray;
        _tray.ConnectRequested += () => Dispatcher.Invoke(() => Execute(_vm.ConnectCommand));
        _tray.DisconnectRequested += () => Dispatcher.Invoke(() => Execute(_vm.DisconnectCommand));
        _tray.ExitRequested += () => Dispatcher.Invoke(ExitApplication);

        _tray.Update(_vm.IsRunning, _vm.Status);

        // Повторный запуск ярлыка при живом экземпляре теперь не плодит второе окно,
        // а поднимает это из трея.
        App.ShowRequested += RestoreFromTray;

        StateChanged += OnStateChanged;
    }

    private static void Execute(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.LogText):
                LogBox.ScrollToEnd();
                break;

            case nameof(MainViewModel.IsRunning):
            case nameof(MainViewModel.Status):
                _tray.Update(_vm.IsRunning, _vm.Status);
                break;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _vm.Settings.MinimizeToTray) HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private void OnImportLinks()
    {
        var dialog = new TextInputWindow(
            "Импорт серверов",
            "Вставьте ссылки vless:// / vmess:// / trojan:// / ss:// / hysteria2:// по одной в строке " +
            "либо JSON-конфиг sing-box или Xray целиком (можно в base64):",
            multiline: true,
            secondaryLabel: "Группа в списке серверов, например имя провайдера (необязательно)",
            secondaryPlaceholder: ProxyProfile.ManualGroup) { Owner = this };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
            _vm.ImportLinks(dialog.Value, dialog.SecondaryValue);
    }

    private void OnAddSubscription()
    {
        var dialog = new SubscriptionWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _vm.AddSubscription(dialog.SubscriptionName, dialog.SubscriptionUrl);
    }

    private void OnOpenRouting()
    {
        var vm = new RoutingViewModel(_vm.Settings, _vm.RouteTargetOptions, _vm.Save);
        var window = new RoutingWindow(vm) { Owner = this };
        window.ShowDialog();
        _vm.Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // Закрытие окна при включённой настройке прячет приложение, а не завершает:
        // ядро продолжает работать, и туннель не рвётся посреди сессии.
        if (!_exiting && _vm.Settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();

            if (_vm.IsRunning)
                _tray.Notify("SplitVpn работает", "Приложение свёрнуто в трей, туннель активен.");

            return;
        }

        if (_vm.IsRunning)
        {
            var answer = MessageBox.Show(
                "Ядро запущено. При выходе туннель будет закрыт и сеть вернётся к обычной маршрутизации.\nВыйти?",
                "Выход", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                _exiting = false;
                return;
            }
        }

        App.ShowRequested -= RestoreFromTray;
        _tray.Dispose();
        _vm.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
