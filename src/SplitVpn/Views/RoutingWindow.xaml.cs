using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SplitVpn.Models;
using SplitVpn.ViewModels;

namespace SplitVpn.Views;

public partial class RoutingWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly RoutingViewModel _vm;

    public RoutingWindow(RoutingViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnCellEdited(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(_vm.NotifyChanged));

    private void OnPickRunning(object sender, RoutedEventArgs e)
    {
        var picker = new AppPickerWindow { Owner = this };
        if (picker.ShowDialog() != true) return;

        foreach (var app in picker.SelectedApps)
            _vm.AddAppRule(app.Name, AppMatchMode.ProcessName);
    }

    private void OnBrowseExe(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите исполняемый файл",
            Filter = "Исполняемые файлы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        foreach (var path in dialog.FileNames)
            _vm.AddAppRule(path, AppMatchMode.ProcessPath, Path.GetFileName(path));
    }

    private void OnPresetRu(object sender, RoutedEventArgs e)
    {
        AddIfMissing(GeoRuleKind.GeoSite, "category-ads-all", RouteTargets.Block);
        AddIfMissing(GeoRuleKind.GeoSite, "category-ru", RouteTargets.Direct);
        AddIfMissing(GeoRuleKind.GeoSite, "category-gov-ru", RouteTargets.Direct);
        AddIfMissing(GeoRuleKind.GeoIp, "ru", RouteTargets.Direct);
        _vm.NotifyChanged();
    }

    private void AddIfMissing(GeoRuleKind kind, string value, string target)
    {
        if (_vm.GeoRules.Any(r => r.Kind == kind && string.Equals(r.Value, value, StringComparison.OrdinalIgnoreCase)))
            return;

        _vm.GeoRules.Add(new GeoRule { Kind = kind, Value = value, Target = target });
    }
}
