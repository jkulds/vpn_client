using System.Text.Json.Serialization;
using SplitVpn.Infrastructure;

namespace SplitVpn.Models;

/// <summary>
/// Один сервер. Заполняется из share-ссылки (vless/vmess/trojan/ss) либо вручную.
/// </summary>
public sealed class ProxyProfile : NotifyBase
{
    private string _name = "";
    private string _server = "";
    private int _serverPort;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Пусто у профилей, добавленных ссылкой вручную.</summary>
    public string? SubscriptionId { get; set; }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Protocol { get; set; } = "vless";
    public string Server { get => _server; set => Set(ref _server, value); }
    public int ServerPort { get => _serverPort; set => Set(ref _serverPort, value); }

    public string? Uuid { get; set; }
    public string? Password { get; set; }
    public string? Method { get; set; }

    /// <summary>
    /// Исходная share-ссылка, если профиль пришёл из неё. Хранится целиком намеренно:
    /// параметры транспорта (xhttp mode/extra и подобные) модель не описывает, а для
    /// сборки конфига сайдкару они нужны дословно.
    /// </summary>
    public string? SourceLink { get; set; }

    /// <summary>Обфускация Hysteria2. Единственное поддерживаемое значение - salamander.</summary>
    public string? ObfsType { get; set; }

    public string? ObfsPassword { get; set; }
    public string? Flow { get; set; }
    public string? VmessSecurity { get; set; }
    public int AlterId { get; set; }

    public string Network { get; set; } = "tcp";
    public string? WsPath { get; set; }
    public string? WsHost { get; set; }
    public int WsMaxEarlyData { get; set; }
    public string? GrpcServiceName { get; set; }

    public bool TlsEnabled { get; set; }
    public string? Sni { get; set; }
    public string[]? Alpn { get; set; }
    public string? Fingerprint { get; set; }
    public bool AllowInsecure { get; set; }

    public bool RealityEnabled { get; set; }
    public string? RealityPublicKey { get; set; }
    public string? RealityShortId { get; set; }

    public string Display
    {
        get
        {
            var where = XrayHostCount > 1
                ? $"xray, {XrayHostCount} хостов, балансировка"
                : RequiresXray
                    ? $"xray, {Server}:{ServerPort}"
                    : $"{Protocol}, {Server}:{ServerPort}";

            return string.IsNullOrWhiteSpace(Name)
                ? $"{Protocol}://{Server}:{ServerPort}"
                : $"{Name}  ({where})";
        }
    }

    /// <summary>
    /// Заголовок группы в списках: имя подписки, а у серверов без подписки - <see cref="GroupName"/>
    /// либо <see cref="ManualGroup"/>. В settings.json не пишется, проставляется при загрузке.
    /// </summary>
    [JsonIgnore]
    public string SubscriptionName
    {
        get => _subscriptionName;
        set => Set(ref _subscriptionName, value);
    }

    private string _subscriptionName = ManualGroup;

    /// <summary>
    /// Группа для серверов, добавленных ссылкой или конфигом, а не подпиской. Задаётся при импорте:
    /// без неё все такие серверы сваливались в одну кучу «добавлено вручную», и провайдера
    /// было не отличить от провайдера.
    /// </summary>
    public string? GroupName { get; set; }

    public const string ManualGroup = "Импортированные ссылки";

    /// <summary>
    /// Готовый конфиг Xray из подписки. Если заполнен, сервер обслуживается сайдкаром Xray,
    /// а не собственным outbound'ом sing-box: так работают XHTTP и балансировщики провайдера,
    /// которых в sing-box нет.
    /// </summary>
    public string? XrayConfigJson { get; set; }

    [JsonIgnore]
    public bool RequiresXray => !string.IsNullOrWhiteSpace(XrayConfigJson);

    /// <summary>
    /// Транспорт умеет Xray, но не умеет sing-box: конфиг сайдкару синтезируется из
    /// <see cref="SourceLink"/>. Проставляется снаружи - знание о возможностях ядер
    /// живёт в Services, тянуть его в модель незачем.
    /// </summary>
    [JsonIgnore]
    public bool NeedsSynthesizedXray { get; set; }

    /// <summary>Любой профиль, для которого нужен процесс Xray.</summary>
    [JsonIgnore]
    public bool UsesXray => RequiresXray || NeedsSynthesizedXray;

    /// <summary>Сколько прокси-outbound'ов внутри конфига Xray. Больше одного - балансировщик.</summary>
    public int XrayHostCount { get; set; }

    private int? _latencyMs;
    private bool _latencyChecked;

    /// <summary>Задержка последнего замера. null при непройденной проверке. В файл не пишется.</summary>
    [JsonIgnore]
    public int? LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (!Set(ref _latencyMs, value)) return;
            Raise(nameof(LatencyText));
            Raise(nameof(LatencyState));
        }
    }

    /// <summary>Замер вообще проводился - без этого не отличить «не проверяли» от «не ответил».</summary>
    [JsonIgnore]
    public bool LatencyChecked
    {
        get => _latencyChecked;
        set
        {
            if (!Set(ref _latencyChecked, value)) return;
            Raise(nameof(LatencyText));
            Raise(nameof(LatencyState));
        }
    }

    [JsonIgnore]
    public string LatencyText => !LatencyChecked ? "" : LatencyMs is { } ms ? $"{ms} мс" : "нет ответа";

    /// <summary>ok / slow / fail / unknown - для раскраски в списке.</summary>
    [JsonIgnore]
    public string LatencyState =>
        !LatencyChecked ? "unknown"
        : LatencyMs is null ? "fail"
        : LatencyMs < 300 ? "ok"
        : LatencyMs < 800 ? "slow"
        : "bad";

    /// <summary>Заполнено, если ядро такой сервер не потянет. В settings.json не пишется.</summary>
    [JsonIgnore]
    public string? UnsupportedReason
    {
        get => _unsupportedReason;
        set
        {
            if (Set(ref _unsupportedReason, value)) Raise(nameof(IsSupported));
        }
    }

    private string? _unsupportedReason;

    [JsonIgnore]
    public bool IsSupported => _unsupportedReason is null;

    /// <summary>
    /// Ключ для переноса выбора канала между обновлениями подписки: Id пересоздаётся,
    /// имя провайдер обычно держит стабильным.
    /// </summary>
    public string StableKey => $"{SubscriptionId ?? "manual"}|{Name}|{Server}:{ServerPort}";
}
