using System.Collections.ObjectModel;
using SplitVpn.Infrastructure;

namespace SplitVpn.Models;

public sealed class AppSettings : NotifyBase
{
    private string _corePath = "sing-box.exe";
    private string? _defaultChannelName;
    private int _subscriptionUpdateHours = 12;
    private bool _blockQuic = true;
    private bool _ipv4Only = true;
    private bool _restartAfterSubscriptionUpdate = true;
    private int _mixedPort = 2080;
    private int _clashApiPort = 9095;

    /// <summary>Путь к sing-box.exe. Относительный резолвится от папки приложения.</summary>
    public string CorePath { get => _corePath; set => Set(ref _corePath, value); }

    /// <summary>
    /// Путь к xray.exe. Нужен только для серверов из подписок в формате конфига Xray:
    /// XHTTP и балансировщики sing-box не умеет, поэтому такие конфиги запускаются сайдкаром.
    /// </summary>
    public string XrayPath { get => _xrayPath; set => Set(ref _xrayPath, value); }

    private string _xrayPath = "xray.exe";

    /// <summary>Канал для всего, что не попало ни в одно правило. Пусто -> напрямую.</summary>
    public string? DefaultChannelName { get => _defaultChannelName; set => Set(ref _defaultChannelName, value); }

    public int SubscriptionUpdateHours
    {
        get => _subscriptionUpdateHours;
        set => Set(ref _subscriptionUpdateHours, Math.Clamp(value, 1, 24 * 7));
    }

    public bool RestartAfterSubscriptionUpdate
    {
        get => _restartAfterSubscriptionUpdate;
        set => Set(ref _restartAfterSubscriptionUpdate, value);
    }

    /// <summary>
    /// QUIC (UDP/443) идёт мимо сниффера, из-за чего домен-правила не срабатывают для Chrome.
    /// Блокировка заставляет браузер откатиться на TCP.
    /// </summary>
    public bool BlockQuic { get => _blockQuic; set => Set(ref _blockQuic, value); }

    public bool Ipv4Only { get => _ipv4Only; set => Set(ref _ipv4Only, value); }

    public bool GroupProfilesBySubscription
    {
        get => _groupProfiles;
        set => Set(ref _groupProfiles, value);
    }

    private bool _groupProfiles = true;

    /// <summary>
    /// Уровень журнала ядра. На info sing-box пишет каждый DNS-ответ и каждое соединение -
    /// десятки строк в секунду, что заметно грузит и журнал, и интерфейс.
    /// </summary>
    public string CoreLogLevel
    {
        get => _coreLogLevel;
        set => Set(ref _coreLogLevel, value);
    }

    private string _coreLogLevel = "warn";

    public int MixedPort { get => _mixedPort; set => Set(ref _mixedPort, value); }
    public int ClashApiPort { get => _clashApiPort; set => Set(ref _clashApiPort, value); }

    /// <summary>Имена свёрнутых групп в списке серверов. Всё, чего здесь нет, развёрнуто.</summary>
    public ObservableCollection<string> CollapsedGroups { get; set; } = new();

    public ObservableCollection<Subscription> Subscriptions { get; set; } = new();
    public ObservableCollection<ProxyProfile> Profiles { get; set; } = new();
    public ObservableCollection<Channel> Channels { get; set; } = new();
    public ObservableCollection<AppRule> AppRules { get; set; } = new();
    public ObservableCollection<GeoRule> GeoRules { get; set; } = new();

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings();
        s.Channels.Add(new Channel { Name = "VPN-A" });
        s.Channels.Add(new Channel { Name = "VPN-B" });
        s.DefaultChannelName = "VPN-A";
        // Имена наборов сверены с sing-geosite/sing-geoip: 'ru' у geosite не существует,
        // российские домены лежат в 'category-ru'. Приватные подсети покрыты правилом
        // ip_is_private, отдельного geoip-private в репозитории нет.
        s.GeoRules.Add(new GeoRule { Kind = GeoRuleKind.GeoSite, Value = "category-ads-all", Target = RouteTargets.Block });
        s.GeoRules.Add(new GeoRule { Kind = GeoRuleKind.GeoSite, Value = "category-ru", Target = RouteTargets.Direct });
        s.GeoRules.Add(new GeoRule { Kind = GeoRuleKind.GeoIp, Value = "ru", Target = RouteTargets.Direct });
        return s;
    }
}
