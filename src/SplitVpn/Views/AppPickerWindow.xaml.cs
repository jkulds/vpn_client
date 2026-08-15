using System.Windows;
using System.Windows.Controls;
using SplitVpn.Services;

namespace SplitVpn.Views;

public partial class AppPickerWindow : Wpf.Ui.Controls.FluentWindow
{
    private IReadOnlyList<RunningApp> _all = Array.Empty<RunningApp>();

    public AppPickerWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    public IReadOnlyList<RunningApp> SelectedApps { get; private set; } = Array.Empty<RunningApp>();

    private void Reload()
    {
        _all = ProcessScanner.Scan();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();

        AppList.ItemsSource = filter.Length == 0
            ? _all
            : _all.Where(a =>
                a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (a.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        SelectedApps = AppList.SelectedItems.Cast<RunningApp>().ToList();

        if (SelectedApps.Count == 0)
        {
            MessageBox.Show("Ничего не выбрано.", "Запущенные приложения",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }
}
