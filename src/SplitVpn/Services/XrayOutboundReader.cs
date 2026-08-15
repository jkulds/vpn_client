using System.Text.Json;
using SplitVpn.Models;

namespace SplitVpn.Services;

/// <summary>
/// Записи подписки из JSON-конфигов Xray.
/// </summary>
/// <remarks>
/// Один элемент подписки - целый конфиг Xray со своим <c>remarks</c>, набором хостов и,
/// как правило, балансировщиком (<c>routing.balancers</c> + <c>observatory</c>).
/// Раскладывать его на отдельные outbound'ы бессмысленно: теряется и балансировка, и XHTTP,
/// которого в sing-box нет. Поэтому конфиг сохраняется целиком и запускается сайдкаром Xray,
/// а sing-box ходит в него через локальный SOCKS.
/// </remarks>
public static class XrayOutboundReader
{
    /// <summary>Служебные outbound'ы Xray - не серверы.</summary>
    private static readonly HashSet<string> Utility =
        new(StringComparer.OrdinalIgnoreCase) { "freedom", "blackhole", "dns", "loopback" };

    public static bool LooksLikeXray(JsonElement root)
    {
        foreach (var (config, _) in Configs(root))
            if (config.TryGetProperty("outbounds", out var outbounds) &&
                outbounds.ValueKind == JsonValueKind.Array)
                foreach (var o in outbounds.EnumerateArray())
                    if (o.TryGetProperty("protocol", out _)) return true;

        return false;
    }

    public static IReadOnlyList<ProxyProfile> Parse(string json, out List<string> errors)
    {
        errors = new List<string>();
        var result = new List<ProxyProfile>();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            errors.Add($"JSON не разобран: {ex.Message}");
            return result;
        }

        var index = 0;

        foreach (var (config, remarks) in Configs(root))
        {
            index++;

            try
            {
                var profile = ReadConfig(config, remarks, index);
                if (profile is not null) result.Add(profile);
                else errors.Add($"запись '{remarks ?? index.ToString()}': прокси-outbound не найден");
            }
            catch (Exception ex)
            {
                errors.Add($"запись '{remarks ?? index.ToString()}': {ex.Message}");
            }
        }

        if (result.Count == 0 && errors.Count == 0)
            errors.Add("в конфиге нет пригодных записей");

        return result;
    }

    private static ProxyProfile? ReadConfig(JsonElement config, string? remarks, int index)
    {
        if (!config.TryGetProperty("outbounds", out var outbounds) ||
            outbounds.ValueKind != JsonValueKind.Array)
            return null;

        var proxies = outbounds.EnumerateArray()
            .Where(o => Str(o, "protocol") is { } p && !Utility.Contains(p))
            .ToList();

        if (proxies.Count == 0) return null;

        var primary = proxies[0];
        var protocol = Str(primary, "protocol") ?? "unknown";

        // "hysteria" в схеме Xray - вендорское расширение: стоковый Xray такого протокола
        // не знает, сайдкар на нём не поднимется. Зато hysteria2 умеет сам sing-box,
        // поэтому такие записи разбираем в обычный профиль и ведём мимо Xray.
        if (proxies.Count == 1 && protocol.StartsWith("hysteria", StringComparison.OrdinalIgnoreCase))
            return ReadHysteria(primary, remarks, index);

        var p2 = new ProxyProfile
        {
            Name = remarks ?? Str(primary, "tag") ?? $"Xray #{index}",
            Protocol = protocol.ToLowerInvariant(),
            XrayConfigJson = config.GetRawText(),
            XrayHostCount = proxies.Count,
            Network = NullIfEmpty(Str(Stream(primary), "network")) ?? "tcp"
        };

        var (server, port) = Endpoint(primary, protocol);
        p2.Server = server;
        p2.ServerPort = port;

        return p2;
    }

    /// <summary>
    /// Адрес и порт у этого расширения лежат в settings, авторизация - в
    /// streamSettings.hysteriaSettings.auth, версия дублируется в обоих местах.
    /// </summary>
    private static ProxyProfile ReadHysteria(JsonElement outbound, string? remarks, int index)
    {
        var settings = Obj(outbound, "settings");
        var stream = Obj(outbound, "streamSettings");
        var hysteria = Obj(stream, "hysteriaSettings");
        var tls = Obj(stream, "tlsSettings");

        var version = Int(hysteria, "version") is var v && v > 0 ? v : Int(settings, "version");

        var p = new ProxyProfile
        {
            Name = remarks ?? Str(outbound, "tag") ?? $"Hysteria #{index}",
            // Версии несовместимы между собой. Вторую ядро тянет, про первую честно говорим,
            // что не поддержана: у неё другая авторизация и обязательные up/down.
            Protocol = version == 2 ? "hysteria2" : "hysteria",
            Network = "hysteria2",
            Server = Str(settings, "address") ?? "",
            ServerPort = Int(settings, "port"),
            Password = Str(hysteria, "auth"),
            ObfsType = NullIfEmpty(Str(hysteria, "obfs")),
            ObfsPassword = NullIfEmpty(Str(hysteria, "obfsPassword")) ?? NullIfEmpty(Str(hysteria, "obfs-password")),
            TlsEnabled = true,
            Sni = NullIfEmpty(Str(tls, "serverName")),
            AllowInsecure = Bool(tls, "allowInsecure")
        };

        if (tls.ValueKind == JsonValueKind.Object &&
            tls.TryGetProperty("alpn", out var alpn) && alpn.ValueKind == JsonValueKind.Array)
        {
            p.Alpn = alpn.EnumerateArray().Select(a => a.GetString() ?? "")
                .Where(a => a.Length > 0).ToArray();
        }

        if (p.Alpn is null or { Length: 0 }) p.Alpn = new[] { "h3" };
        if (string.IsNullOrWhiteSpace(p.Sni)) p.Sni = p.Server;

        return p;
    }

    private static JsonElement Obj(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : default;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static (string server, int port) Endpoint(JsonElement outbound, string protocol)
    {
        if (!outbound.TryGetProperty("settings", out var settings)) return ("", 0);

        // vless/vmess держат адрес в vnext, trojan/ss - в servers, остальные протоколы
        // раскладку не унифицируют, но для показа хватает и пустого значения.
        foreach (var arrayName in new[] { "vnext", "servers" })
        {
            if (!settings.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;

            foreach (var node in arr.EnumerateArray())
                return (Str(node, "address") ?? "", Int(node, "port"));
        }

        return ("", 0);
    }

    private static JsonElement Stream(JsonElement outbound) =>
        outbound.TryGetProperty("streamSettings", out var s) ? s : default;

    /// <summary>
    /// Каждый элемент массива - отдельная запись подписки со своим человекочитаемым
    /// <c>remarks</c>. Теги внутри (<c>mb-balancer-N-host-XXXXX</c>) - внутренние имена хостов
    /// балансировщика, показывать их пользователю бессмысленно.
    /// </summary>
    private static IEnumerable<(JsonElement config, string? remarks)> Configs(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    yield return (item, NullIfEmpty(Str(item, "remarks")));
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            yield return (root, NullIfEmpty(Str(root, "remarks")));
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
