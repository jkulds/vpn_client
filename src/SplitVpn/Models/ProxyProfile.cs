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

    /// <summary>Заголовок группы в списках. Проставляется из имени подписки, в settings.json не пишется.</summary>
    [JsonIgnore]
    public string SubscriptionName
    {
        get => _subscriptionName;
        set => Set(ref _subscriptionName, value);
    }

    private string _subscriptionName = ManualGroup;

    public const string ManualGroup = "Добавлено вручную";

    /// <summary>
    /// Готовый конфиг Xray из подписки. Если заполнен, сервер обслуживается сайдкаром Xray,
    /// а не собственным outbound'ом sing-box: так работают XHTTP и балансировщики провайдера,
    /// которых в sing-box нет.
    /// </summary>
    public string? XrayConfigJson { get; set; }

    [JsonIgnore]
    public bool RequiresXray => !string.IsNullOrWhiteSpace(XrayConfigJson);

    /// <summary>Сколько прокси-outbound'ов внутри конфига Xray. Больше одного - балансировщик.</summary>
    public int XrayHostCount { get; set; }

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
