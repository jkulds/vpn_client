using System.ComponentModel;
using System.Windows;
using SplitVpn.ViewModels;

namespace SplitVpn.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.ImportLinksRequested += OnImportLinks;
        _vm.AddSubscriptionRequested += OnAddSubscription;
        _vm.RoutingRequested += OnOpenRouting;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LogText))
            LogBox.ScrollToEnd();
    }

    private void OnImportLinks()
    {
        var dialog = new TextInputWindow(
            "Импорт серверов",
            "Вставьте ссылки vless:// / vmess:// / trojan:// / ss://, по одной в строке:",
            multiline: true) { Owner = this };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
            _vm.ImportLinks(dialog.Value);
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

        if (_vm.IsRunning)
        {
            var answer = MessageBox.Show(
                "Ядро запущено. При выходе туннель будет закрыт и сеть вернётся к обычной маршрутизации.\nВыйти?",
                "Выход", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _vm.Dispose();
    }
}
