using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SplitVpn.Models;

namespace SplitVpn.Infrastructure;

public sealed class EnumLabelConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        [nameof(AppMatchMode.ProcessName)] = "Имя процесса",
        [nameof(AppMatchMode.ProcessPath)] = "Путь к .exe",
        [nameof(GeoRuleKind.GeoIp)] = "GeoIP (страна)",
        [nameof(GeoRuleKind.GeoSite)] = "GeoSite (набор доменов)",
        [nameof(GeoRuleKind.Domain)] = "Домен точно",
        [nameof(GeoRuleKind.DomainSuffix)] = "Домен и поддомены",
        [nameof(GeoRuleKind.DomainKeyword)] = "Домен содержит",
        [nameof(GeoRuleKind.DomainRegex)] = "Домен по regex",
        [nameof(GeoRuleKind.IpCidr)] = "IP / подсеть",
        [nameof(GeoRuleKind.Port)] = "Порт"
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() ?? "";
        return Labels.TryGetValue(key, out var label) ? label : key;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RouteTargetLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        RouteTargets.Label(value?.ToString() ?? "");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}
