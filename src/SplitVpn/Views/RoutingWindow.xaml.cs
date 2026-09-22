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

    /// <summary>Списки под РФ из репозитория legiz-ru/sb-rule-sets, в формате .srs для sing-box.</summary>
    private const string RuSetsBase = "https://raw.githubusercontent.com/legiz-ru/sb-rule-sets/main";

    /// <summary>
    /// Набор правил под РФ. Порядок значим: первым совпавшим выигрывает, поэтому список
    /// заблокированного идёт раньше правил «российское напрямую» - иначе домен, попавший
    /// в оба списка, ушёл бы мимо туннеля и остался недоступен.
    /// </summary>
    private void OnPresetRu(object sender, RoutedEventArgs e)
    {
        var vpn = _vm.AvailableTargets.FirstOrDefault(
                      t => t != RouteTargets.Direct && t != RouteTargets.Block)
                  ?? RouteTargets.Direct;

        AddIfMissing(GeoRuleKind.GeoSite, "category-ads-all", RouteTargets.Block);

        // Заблокированное в РФ - через туннель. Своя ссылка: в sing-geosite такого набора нет.
        AddIfMissing(GeoRuleKind.GeoSite, "ru-bundle", vpn, $"{RuSetsBase}/ru-bundle.srs");

        AddIfMissing(GeoRuleKind.GeoSite, "github", vpn);

        // Российское - напрямую.
        AddIfMissing(GeoRuleKind.GeoSite, "category-ru", RouteTargets.Direct);
        AddIfMissing(GeoRuleKind.GeoSite, "category-gov-ru", RouteTargets.Direct);
        AddIfMissing(GeoRuleKind.GeoIp, "ru", RouteTargets.Direct);

        _vm.NotifyChanged();
    }

    private void AddIfMissing(GeoRuleKind kind, string value, string target, string? customUrl = null)
    {
        if (_vm.GeoRules.Any(r => r.Kind == kind && string.Equals(r.Value, value, StringComparison.OrdinalIgnoreCase)))
            return;

        _vm.GeoRules.Add(new GeoRule
        {
            Kind = kind,
            Value = value,
            Target = target,
            CustomRuleSetUrl = customUrl
        });
    }
}
