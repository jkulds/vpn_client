using SplitVpn.Models;

namespace SplitVpn.Services;

/// <summary>
/// Что из разобранных ссылок ядро вообще умеет.
/// </summary>
/// <remarks>
/// Проверка нужна до запуска: неподдерживаемый транспорт нельзя просто опустить - outbound
/// соберётся как голый TCP, конфиг пройдёт `sing-box check`, а сервер оборвёт соединение
/// с невнятным EOF. Xray-специфичные транспорты (xhttp/splithttp, kcp) в sing-box отсутствуют.
/// </remarks>
public static class CoreCapabilities
{
    private static readonly HashSet<string> Protocols =
        new(StringComparer.OrdinalIgnoreCase) { "vless", "vmess", "trojan", "shadowsocks", "hysteria2" };

    /// <summary>Hysteria2 работает поверх QUIC, транспорты v2ray к нему неприменимы.</summary>
    private static readonly HashSet<string> TransportFree =
        new(StringComparer.OrdinalIgnoreCase) { "hysteria2" };

    private static readonly HashSet<string> Transports =
        new(StringComparer.OrdinalIgnoreCase) { "tcp", "none", "raw", "ws", "grpc", "http", "httpupgrade", "quic" };

    private static readonly Dictionary<string, string> KnownGaps =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["xhttp"] = "XHTTP - транспорт Xray, в sing-box его нет",
            ["splithttp"] = "SplitHTTP - транспорт Xray, в sing-box его нет",
            ["kcp"] = "mKCP - транспорт Xray, в sing-box его нет",
            ["mkcp"] = "mKCP - транспорт Xray, в sing-box его нет",
        };

    /// <summary>Что умеет сам Xray. Для сайдкара транспорт не проверяем - XHTTP там родной.</summary>
    private static readonly HashSet<string> XrayProtocols =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "vless", "vmess", "trojan", "shadowsocks", "socks", "http", "wireguard"
        };

    /// <summary>Транспорт, который есть в Xray и отсутствует в sing-box.</summary>
    public static bool IsXrayOnlyTransport(string? network) =>
        network is not null && KnownGaps.ContainsKey(network);

    public static bool IsSupported(ProxyProfile profile, out string? reason)
    {
        if (profile.UsesXray)
        {
            if (!XrayProtocols.Contains(profile.Protocol))
            {
                reason = $"протокол '{profile.Protocol}' не поддерживается и самим Xray";
                return false;
            }

            reason = null;
            return true;
        }

        if (!Protocols.Contains(profile.Protocol))
        {
            reason = $"протокол '{profile.Protocol}' не поддерживается";
            return false;
        }

        if (TransportFree.Contains(profile.Protocol))
        {
            reason = null;
            return true;
        }

        var network = string.IsNullOrWhiteSpace(profile.Network) ? "tcp" : profile.Network;

        if (KnownGaps.TryGetValue(network, out var gap))
        {
            reason = gap;
            return false;
        }

        if (!Transports.Contains(network))
        {
            reason = $"транспорт '{network}' не поддерживается ядром";
            return false;
        }

        reason = null;
        return true;
    }
}
