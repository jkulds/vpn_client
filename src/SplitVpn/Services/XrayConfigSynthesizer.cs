using System.Text.Json.Nodes;
using System.Web;
using SplitVpn.Models;

namespace SplitVpn.Services;

/// <summary>
/// Собирает конфиг Xray для профиля, пришедшего share-ссылкой.
/// </summary>
/// <remarks>
/// Нужен там, где транспорт умеет Xray, но не умеет sing-box (xhttp, splithttp, kcp).
/// Строится из исходной ссылки, а не из полей модели: параметры вроде xhttp mode и extra
/// модель не описывает, и сборка по полям молча теряла бы то, что серверу необходимо.
/// </remarks>
public static class XrayConfigSynthesizer
{
    public static string FromLink(string link)
    {
        var uri = new Uri(link);
        var q = HttpUtility.ParseQueryString(uri.Query);
        var scheme = link.Split("://", 2)[0].ToLowerInvariant();

        var outbound = new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = scheme == "ss" ? "shadowsocks" : scheme,
            ["settings"] = BuildSettings(scheme, uri, q),
            ["streamSettings"] = BuildStream(q)
        };

        return new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["outbounds"] = new JsonArray(
                outbound,
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" })
        }.ToJsonString();
    }

    private static JsonObject BuildSettings(string scheme, Uri uri, System.Collections.Specialized.NameValueCollection q)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
        var user = Uri.UnescapeDataString(uri.UserInfo);

        switch (scheme)
        {
            case "vless":
            case "vmess":
            {
                var account = new JsonObject { ["id"] = user, ["encryption"] = Value(q, "encryption") ?? "none" };
                if (Value(q, "flow") is { } flow) account["flow"] = flow;
                if (scheme == "vmess") { account["security"] = Value(q, "scy") ?? "auto"; account["alterId"] = 0; }

                return new JsonObject
                {
                    ["vnext"] = new JsonArray(new JsonObject
                    {
                        ["address"] = host,
                        ["port"] = uri.Port,
                        ["users"] = new JsonArray(account)
                    })
                };
            }

            default:
            {
                var server = new JsonObject { ["address"] = host, ["port"] = uri.Port, ["password"] = user };
                if (Value(q, "method") is { } m) server["method"] = m;

                return new JsonObject { ["servers"] = new JsonArray(server) };
            }
        }
    }

    private static JsonObject BuildStream(System.Collections.Specialized.NameValueCollection q)
    {
        var network = Value(q, "type") ?? "tcp";
        var security = (Value(q, "security") ?? "none").ToLowerInvariant();

        var stream = new JsonObject { ["network"] = network, ["security"] = security };

        if (security is "tls" or "xtls")
        {
            var tls = new JsonObject();
            if ((Value(q, "sni") ?? Value(q, "peer")) is { } sni) tls["serverName"] = sni;
            if (Value(q, "fp") is { } fp) tls["fingerprint"] = fp;
            if (Value(q, "alpn") is { } alpn)
                tls["alpn"] = new JsonArray(alpn.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                            StringSplitOptions.TrimEntries)
                    .Select(a => (JsonNode)a).ToArray());
            if (Value(q, "allowInsecure") is "1" or "true" || Value(q, "insecure") is "1" or "true")
                tls["allowInsecure"] = true;

            stream["tlsSettings"] = tls;
        }
        else if (security == "reality")
        {
            var reality = new JsonObject();
            if (Value(q, "sni") is { } sni) reality["serverName"] = sni;
            if (Value(q, "fp") is { } fp) reality["fingerprint"] = fp;
            if (Value(q, "pbk") is { } pbk) reality["publicKey"] = pbk;
            if (Value(q, "sid") is { } sid) reality["shortId"] = sid;
            if (Value(q, "spx") is { } spx) reality["spiderX"] = spx;

            stream["realitySettings"] = reality;
        }

        switch (network)
        {
            case "xhttp":
            case "splithttp":
            {
                var xhttp = new JsonObject { ["path"] = Value(q, "path") ?? "/" };
                if (Value(q, "host") is { } h) xhttp["host"] = h;
                if (Value(q, "mode") is { } mode) xhttp["mode"] = mode;

                // extra приходит JSON-объектом в параметре ссылки; кладём как есть.
                if (Value(q, "extra") is { } extra)
                {
                    try { xhttp["extra"] = JsonNode.Parse(extra); }
                    catch (System.Text.Json.JsonException) { /* не JSON - пропускаем */ }
                }

                stream[network == "splithttp" ? "splithttpSettings" : "xhttpSettings"] = xhttp;
                break;
            }

            case "ws":
            {
                var ws = new JsonObject { ["path"] = Value(q, "path") ?? "/" };
                if (Value(q, "host") is { } h) ws["host"] = h;
                stream["wsSettings"] = ws;
                break;
            }

            case "httpupgrade":
            {
                var hu = new JsonObject { ["path"] = Value(q, "path") ?? "/" };
                if (Value(q, "host") is { } h) hu["host"] = h;
                stream["httpupgradeSettings"] = hu;
                break;
            }

            case "grpc":
            {
                var grpc = new JsonObject { ["serviceName"] = Value(q, "serviceName") ?? "" };
                if (Value(q, "mode") == "multi") grpc["multiMode"] = true;
                stream["grpcSettings"] = grpc;
                break;
            }

            case "kcp":
            {
                var kcp = new JsonObject();
                if (Value(q, "seed") is { } seed) kcp["seed"] = seed;
                if (Value(q, "headerType") is { } ht)
                    kcp["header"] = new JsonObject { ["type"] = ht };
                stream["kcpSettings"] = kcp;
                break;
            }
        }

        return stream;
    }

    private static string? Value(System.Collections.Specialized.NameValueCollection q, string key)
    {
        var v = q[key];
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}
