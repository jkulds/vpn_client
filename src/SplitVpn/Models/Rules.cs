using SplitVpn.Infrastructure;

namespace SplitVpn.Models;

public static class RouteTargets
{
    public const string Direct = "direct";
    public const string Block = "block";

    public static string Label(string target) => target switch
    {
        Direct => "Напрямую",
        Block => "Блокировать",
        _ => target
    };
}

public enum AppMatchMode
{
    ProcessName,
    ProcessPath
}

public sealed class AppRule : NotifyBase
{
    private bool _enabled = true;
    private AppMatchMode _mode = AppMatchMode.ProcessName;
    private string _value = "";
    private string _target = RouteTargets.Direct;
    private string? _note;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public AppMatchMode Mode { get => _mode; set => Set(ref _mode, value); }

    /// <summary>Имя процесса с расширением (Telegram.exe) либо полный путь к .exe.</summary>
    public string Value { get => _value; set => Set(ref _value, value); }

    /// <summary>Имя канала, либо <see cref="RouteTargets.Direct"/> / <see cref="RouteTargets.Block"/>.</summary>
    public string Target { get => _target; set => Set(ref _target, value); }

    public string? Note { get => _note; set => Set(ref _note, value); }
}

public enum GeoRuleKind
{
    GeoIp,
    GeoSite,
    Domain,
    DomainSuffix,
    DomainKeyword,
    DomainRegex,
    IpCidr,
    Port
}

public sealed class GeoRule : NotifyBase
{
    private bool _enabled = true;
    private GeoRuleKind _kind = GeoRuleKind.GeoSite;
    private string _value = "";
    private string _target = RouteTargets.Direct;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public GeoRuleKind Kind { get => _kind; set => Set(ref _kind, value); }

    /// <summary>Для GeoIp/GeoSite - код набора (ru, cn, category-ads-all). Иначе - домен/CIDR/порт.</summary>
    public string Value { get => _value; set => Set(ref _value, value); }

    public string Target { get => _target; set => Set(ref _target, value); }

    /// <summary>
    /// Прямая ссылка на .srs вместо набора SagerNet. Нужна для сторонних списков
    /// (например runetfreedom), которых в sing-geosite нет.
    /// </summary>
    public string? CustomRuleSetUrl { get => _customRuleSetUrl; set => Set(ref _customRuleSetUrl, value); }

    private string? _customRuleSetUrl;
}
