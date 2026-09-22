using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using System.Web;
using SplitVpn.Models;

namespace SplitVpn.Services;

/// <summary>
/// Разбор share-ссылок vless/vmess/trojan/ss в <see cref="ProxyProfile"/>.
/// </summary>
public static class ShareLinkParser
{
    public static IReadOnlyList<ProxyProfile> ParseMany(string text, out List<string> errors)
    {
        errors = new List<string>();
        var result = new List<ProxyProfile>();

        foreach (var raw in SplitLines(text))
        {
            if (TryParse(raw, out var profile, out var error))
                result.Add(profile!);
            else if (error is not null)
                errors.Add($"{Truncate(raw)}: {error}");
        }

        return result;
    }

    public static bool TryParse(string link, out ProxyProfile? profile, out string? error)
    {
        profile = null;
        error = null;
        link = link.Trim();

        if (link.Length == 0 || link.StartsWith('#'))
        {
            error = null;
            return false;
        }

        try
        {
            var scheme = link.Split("://", 2)[0].ToLowerInvariant();
            profile = scheme switch
            {
                "vless" => ParseVless(link),
                "trojan" => ParseTrojan(link),
                "vmess" => ParseVmess(link),
                "ss" => ParseShadowsocks(link),
                "hysteria2" or "hy2" => ParseHysteria2(link),
                _ => null
            };

            if (profile is null)
            {
                // Схему обрезаем: если тело оказалось не списком ссылок, сюда попадает
                // вся строка целиком и без обрезки залила бы журнал.
                error = $"неподдерживаемая схема '{Truncate(scheme)}'";
                return false;
            }

            if (string.IsNullOrWhiteSpace(profile.Server) || profile.ServerPort <= 0)
            {
                error = "не удалось определить адрес или порт";
                profile = null;
                return false;
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
                profile.Name = $"{profile.Server}:{profile.ServerPort}";

            profile.SourceLink = link;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            profile = null;
            return false;
        }
    }

    private static ProxyProfile ParseVless(string link)
    {
        var uri = new Uri(link);
        var q = ParseQuery(uri);

        var p = new ProxyProfile
        {
            Protocol = "vless",
            Name = DecodeFragment(uri),
            Server = HostOf(uri),
            ServerPort = uri.Port,
            Uuid = Uri.UnescapeDataString(uri.UserInfo),
            Flow = NullIfEmpty(q["flow"])
        };

        ApplyTransport(p, q);
        ApplyTls(p, q, defaultSniHost: p.Server);
        return p;
    }

    private static ProxyProfile ParseTrojan(string link)
    {
        var uri = new Uri(link);
        var q = ParseQuery(uri);

        var p = new ProxyProfile
        {
            Protocol = "trojan",
            Name = DecodeFragment(uri),
            Server = HostOf(uri),
            ServerPort = uri.Port,
            Password = Uri.UnescapeDataString(uri.UserInfo)
        };

        ApplyTransport(p, q);
        ApplyTls(p, q, defaultSniHost: p.Server);

        // У trojan TLS включён всегда, даже если security в ссылке не указан.
        if (!p.RealityEnabled) p.TlsEnabled = true;
        return p;
    }

    /// <summary>
    /// hysteria2://auth@host:port?sni=...&amp;insecure=1&amp;obfs=salamander&amp;obfs-password=...#имя
    /// </summary>
    private static ProxyProfile ParseHysteria2(string link)
    {
        var uri = new Uri(link);
        var q = ParseQuery(uri);

        var p = new ProxyProfile
        {
            Protocol = "hysteria2",
            Name = DecodeFragment(uri),
            Server = HostOf(uri),
            // Порт по умолчанию у Hysteria2 - 443.
            ServerPort = uri.IsDefaultPort ? 443 : uri.Port,
            Password = Uri.UnescapeDataString(uri.UserInfo),
            Network = "hysteria2",
            TlsEnabled = true,
            Sni = NullIfEmpty(q["sni"]) ?? NullIfEmpty(q["peer"]) ?? HostOf(uri),
            Alpn = SplitAlpn(q["alpn"]) ?? new[] { "h3" },
            AllowInsecure = q["insecure"] is "1" or "true" || q["allowInsecure"] is "1" or "true",
            ObfsType = NullIfEmpty(q["obfs"]),
            ObfsPassword = NullIfEmpty(q["obfs-password"]) ?? NullIfEmpty(q["obfsPassword"])
        };

        return p;
    }

    private static ProxyProfile ParseVmess(string link)
    {
        var payload = link[("vmess://".Length)..];
        var json = Encoding.UTF8.GetString(DecodeBase64(payload));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? Str(string name) =>
            root.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString()
                : null;

        var p = new ProxyProfile
        {
            Protocol = "vmess",
            Name = Str("ps") ?? "",
            Server = Str("add") ?? "",
            ServerPort = int.TryParse(Str("port"), out var port) ? port : 0,
            Uuid = Str("id"),
            AlterId = int.TryParse(Str("aid"), out var aid) ? aid : 0,
            VmessSecurity = NullIfEmpty(Str("scy")) ?? "auto",
            Network = NormalizeNetwork(Str("net"))
        };

        var host = Str("host");
        var path = Str("path");

        switch (p.Network)
        {
            case "ws":
            case "httpupgrade":
            case "http":
                p.WsPath = NullIfEmpty(path) ?? "/";
                p.WsHost = NullIfEmpty(host);
                break;
            case "grpc":
                p.GrpcServiceName = NullIfEmpty(path);
                break;
        }

        var tls = Str("tls");
        p.TlsEnabled = tls is "tls" or "reality";
        if (p.TlsEnabled)
        {
            p.Sni = NullIfEmpty(Str("sni")) ?? NullIfEmpty(host) ?? p.Server;
            p.Alpn = SplitAlpn(Str("alpn"));
            p.Fingerprint = NullIfEmpty(Str("fp"));
        }

        return p;
    }

    private static ProxyProfile ParseShadowsocks(string link)
    {
        var hashIndex = link.IndexOf('#');
        var name = hashIndex >= 0 ? Uri.UnescapeDataString(link[(hashIndex + 1)..]) : "";
        var body = hashIndex >= 0 ? link[..hashIndex] : link;
        body = body["ss://".Length..];

        var queryIndex = body.IndexOf('?');
        if (queryIndex >= 0) body = body[..queryIndex];

        string method, password, server;
        int port;

        var at = body.LastIndexOf('@');
        if (at >= 0)
        {
            // SIP002: ss://base64(method:password)@host:port
            var creds = Encoding.UTF8.GetString(DecodeBase64(body[..at]));
            var colon = creds.IndexOf(':');
            method = creds[..colon];
            password = creds[(colon + 1)..];
            (server, port) = SplitHostPort(body[(at + 1)..]);
        }
        else
        {
            // Legacy: ss://base64(method:password@host:port)
            var all = Encoding.UTF8.GetString(DecodeBase64(body));
            var atPos = all.LastIndexOf('@');
            var creds = all[..atPos];
            var colon = creds.IndexOf(':');
            method = creds[..colon];
            password = creds[(colon + 1)..];
            (server, port) = SplitHostPort(all[(atPos + 1)..]);
        }

        return new ProxyProfile
        {
            Protocol = "shadowsocks",
            Name = name,
            Server = server,
            ServerPort = port,
            Method = method,
            Password = password
        };
    }

    private static void ApplyTransport(ProxyProfile p, NameValueCollection q)
    {
        p.Network = NormalizeNetwork(q["type"]);

        switch (p.Network)
        {
            case "ws":
            case "httpupgrade":
            case "http":
            {
                var path = NullIfEmpty(q["path"]) ?? "/";
                // ?ed=N внутри path - это max_early_data, отдельное поле в sing-box.
                var edIndex = path.IndexOf("?ed=", StringComparison.OrdinalIgnoreCase);
                if (edIndex >= 0)
                {
                    if (int.TryParse(path[(edIndex + 4)..], out var ed)) p.WsMaxEarlyData = ed;
                    path = path[..edIndex];
                }
                p.WsPath = path;
                p.WsHost = NullIfEmpty(q["host"]);
                break;
            }
            case "grpc":
                p.GrpcServiceName = NullIfEmpty(q["serviceName"]);
                break;
        }
    }

    private static void ApplyTls(ProxyProfile p, NameValueCollection q, string defaultSniHost)
    {
        var security = (NullIfEmpty(q["security"]) ?? "none").ToLowerInvariant();
        p.TlsEnabled = security is "tls" or "reality" or "xtls";
        p.RealityEnabled = security == "reality";

        if (!p.TlsEnabled) return;

        p.Sni = NullIfEmpty(q["sni"]) ?? NullIfEmpty(q["peer"]) ?? NullIfEmpty(q["host"]) ?? defaultSniHost;
        p.Fingerprint = NullIfEmpty(q["fp"]);
        p.Alpn = SplitAlpn(q["alpn"]);
        p.AllowInsecure = q["allowInsecure"] is "1" or "true" || q["insecure"] is "1" or "true";

        if (p.RealityEnabled)
        {
            p.RealityPublicKey = NullIfEmpty(q["pbk"]);
            p.RealityShortId = NullIfEmpty(q["sid"]);
            p.AllowInsecure = false;
        }
    }

    /// <summary>
    /// Xray переименовал транспорт tcp в raw (с 24.9.30), а в ссылках и в sing-box он по-прежнему tcp.
    /// Пустое значение и none тоже означают голый TCP.
    /// </summary>
    public static string NormalizeNetwork(string? network)
    {
        var n = (network ?? "").Trim().ToLowerInvariant();
        return n is "" or "raw" or "none" ? "tcp" : n;
    }

    private static (string host, int port) SplitHostPort(string s)
    {
        var colon = s.LastIndexOf(':');
        if (colon < 0) return (s, 0);
        var host = s[..colon].Trim('[', ']');
        return (host, int.TryParse(s[(colon + 1)..], out var p) ? p : 0);
    }

    private static string HostOf(Uri uri) => uri.HostNameType == UriHostNameType.IPv6
        ? uri.Host.Trim('[', ']')
        : uri.Host;

    private static NameValueCollection ParseQuery(Uri uri) =>
        HttpUtility.ParseQueryString(uri.Query);

    private static string DecodeFragment(Uri uri) =>
        uri.Fragment.Length > 1 ? Uri.UnescapeDataString(uri.Fragment[1..]) : "";

    private static string[]? SplitAlpn(string? alpn) =>
        string.IsNullOrWhiteSpace(alpn)
            ? null
            : alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Непечатаемое схлопывается в точки: иначе нераспознанный бинарь заливает журнал.</summary>
    private static string Truncate(string s)
    {
        var sb = new StringBuilder();

        foreach (var c in s)
        {
            if (sb.Length >= 48) { sb.Append("..."); break; }
            sb.Append(char.IsControl(c) || c == '�' ? '.' : c);
        }

        return sb.ToString();
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Base64 с поправкой на url-safe алфавит и отсутствующий padding - в ссылках встречается и то и другое.</summary>
    public static byte[] DecodeBase64(string s)
    {
        // Переносы строк удаляются до расчёта padding: тела подписок часто свёрнуты по 76 символов,
        // и если считать длину вместе с ними, дополнение выйдет неверным.
        var sb = new StringBuilder(s.Length);

        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }

        var pad = sb.Length % 4;
        if (pad != 0) sb.Append('=', 4 - pad);

        return Convert.FromBase64String(sb.ToString());
    }
}
