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

        // Простой конфиг - один outbound на транспорте, который умеет sing-box, - разбирается
        // в обычный профиль: без сайдкара сервер участвует в режиме «авто» и в замерах ядра,
        // а xray.exe для него не нужен вовсе. Всё, чего sing-box не тянет (XHTTP, mKCP,
        // HTTP-маскировка TCP, балансировщики), по-прежнему уходит сайдкару целиком.
        if (proxies.Count == 1 && TryReadNative(primary, remarks, index, out var native))
            return native;

        var p2 = new ProxyProfile
        {
            Name = remarks ?? Str(primary, "tag") ?? $"Xray #{index}",
            Protocol = protocol.ToLowerInvariant(),
            XrayConfigJson = config.GetRawText(),
            XrayHostCount = proxies.Count,
            Network = ShareLinkParser.NormalizeNetwork(Str(Stream(primary), "network"))
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

    /// <summary>Ключи streamSettings, смысл которых известен. Что-то сверх - лучше отдать сайдкару.</summary>
    private static readonly HashSet<string> KnownStreamKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "network", "security", "tlsSettings", "realitySettings", "wsSettings", "grpcSettings",
        "httpupgradeSettings", "httpSettings", "tcpSettings", "rawSettings", "sockopt"
    };

    /// <summary>
    /// Пытается прочитать одиночный outbound Xray как нативный профиль sing-box.
    /// false - конфиг требует сайдкара либо не разобран; тогда вызывающий код ничего не теряет.
    /// </summary>
    private static bool TryReadNative(JsonElement outbound, string? remarks, int index, out ProxyProfile? profile)
    {
        profile = null;

        var protocol = (Str(outbound, "protocol") ?? "").ToLowerInvariant();
        var settings = Obj(outbound, "settings");
        var stream = Obj(outbound, "streamSettings");

        if (stream.ValueKind == JsonValueKind.Object)
            foreach (var prop in stream.EnumerateObject())
                if (!KnownStreamKeys.Contains(prop.Name)) return false;

        var network = ShareLinkParser.NormalizeNetwork(Str(stream, "network"));
        if (network is not ("tcp" or "ws" or "grpc" or "httpupgrade" or "http")) return false;

        // HTTP-маскировка поверх TCP (header.type=http) - вещь Xray, в sing-box её нет.
        var tcp = Obj(stream, "tcpSettings");
        if (tcp.ValueKind != JsonValueKind.Object) tcp = Obj(stream, "rawSettings");
        var headerType = Str(Obj(tcp, "header"), "type");
        if (headerType is not null && !headerType.Equals("none", StringComparison.OrdinalIgnoreCase)) return false;

        var p = new ProxyProfile
        {
            Name = remarks ?? Str(outbound, "tag") ?? $"Xray #{index}",
            Protocol = protocol,
            Network = network
        };

        switch (protocol)
        {
            case "vless":
            case "vmess":
            {
                var node = FirstObject(settings, "vnext");
                var user = FirstObject(node, "users");
                if (node.ValueKind != JsonValueKind.Object || user.ValueKind != JsonValueKind.Object) return false;

                p.Server = Str(node, "address") ?? "";
                p.ServerPort = Int(node, "port");
                p.Uuid = Str(user, "id");

                if (protocol == "vless")
                {
                    p.Flow = NullIfEmpty(Str(user, "flow"));
                }
                else
                {
                    p.VmessSecurity = NullIfEmpty(Str(user, "security")) ?? "auto";
                    p.AlterId = Int(user, "alterId");
                }
                break;
            }

            case "trojan":
            case "shadowsocks":
            {
                var node = FirstObject(settings, "servers");
                if (node.ValueKind != JsonValueKind.Object) return false;

                p.Server = Str(node, "address") ?? "";
                p.ServerPort = Int(node, "port");
                p.Password = Str(node, "password");
                if (protocol == "shadowsocks") p.Method = Str(node, "method");
                break;
            }

            default:
                return false;
        }

        if (string.IsNullOrWhiteSpace(p.Server) || p.ServerPort <= 0 ||
            (protocol is "vless" or "vmess" && string.IsNullOrWhiteSpace(p.Uuid)))
            return false;

        switch (network)
        {
            case "ws":
            {
                var ws = Obj(stream, "wsSettings");
                p.WsPath = NullIfEmpty(Str(ws, "path")) ?? "/";
                p.WsHost = NullIfEmpty(Str(ws, "host")) ?? NullIfEmpty(Str(Obj(ws, "headers"), "Host"));
                break;
            }

            case "httpupgrade":
            {
                var hu = Obj(stream, "httpupgradeSettings");
                p.WsPath = NullIfEmpty(Str(hu, "path")) ?? "/";
                p.WsHost = NullIfEmpty(Str(hu, "host"));
                break;
            }

            case "http":
            {
                var h = Obj(stream, "httpSettings");
                p.WsPath = NullIfEmpty(Str(h, "path")) ?? "/";
                p.WsHost = FirstString(h, "host");
                break;
            }

            case "grpc":
                p.GrpcServiceName = NullIfEmpty(Str(Obj(stream, "grpcSettings"), "serviceName"));
                break;
        }

        var security = (Str(stream, "security") ?? "none").ToLowerInvariant();
        p.TlsEnabled = security is "tls" or "reality";
        p.RealityEnabled = security == "reality";

        if (p.RealityEnabled)
        {
            var reality = Obj(stream, "realitySettings");
            p.Sni = NullIfEmpty(Str(reality, "serverName")) ?? p.Server;
            p.Fingerprint = NullIfEmpty(Str(reality, "fingerprint"));
            // Xray 25.x переименовал publicKey в password - встречаются оба варианта.
            p.RealityPublicKey = NullIfEmpty(Str(reality, "publicKey")) ?? NullIfEmpty(Str(reality, "password"));
            p.RealityShortId = NullIfEmpty(Str(reality, "shortId"));
            if (string.IsNullOrWhiteSpace(p.RealityPublicKey)) return false;
        }
        else if (p.TlsEnabled)
        {
            var tls = Obj(stream, "tlsSettings");
            p.Sni = NullIfEmpty(Str(tls, "serverName")) ?? p.WsHost ?? p.Server;
            p.Fingerprint = NullIfEmpty(Str(tls, "fingerprint"));
            p.AllowInsecure = Bool(tls, "allowInsecure");
            p.Alpn = StringArray(tls, "alpn");
        }
        else if (protocol == "trojan")
        {
            // У trojan TLS включён всегда, даже если security не указан.
            p.TlsEnabled = true;
            p.Sni = p.Server;
        }

        profile = p;
        return true;
    }

    private static JsonElement FirstObject(JsonElement parent, string arrayName)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return default;

        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object) return item;

        return default;
    }

    /// <summary>Поле, которое у Xray бывает и строкой, и массивом строк (host у httpSettings).</summary>
    private static string? FirstString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) return null;

        if (v.ValueKind == JsonValueKind.String) return NullIfEmpty(v.GetString());

        if (v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && NullIfEmpty(item.GetString()) is { } s) return s;

        return null;
    }

    private static string[]? StringArray(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;

        var list = v.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.String)
            .Select(a => a.GetString() ?? "")
            .Where(a => a.Length > 0)
            .ToArray();

        return list.Length > 0 ? list : null;
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
