using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SplitVpn.Models;

namespace SplitVpn.Services;

public enum SubscriptionFormat
{
    Unknown,
    ShareLinks,
    Base64ShareLinks,
    SingBoxJson,
    XrayJson,
    ClashYaml
}

public sealed class SubscriptionFetchResult
{
    public required Subscription Subscription { get; init; }
    public List<ProxyProfile> Profiles { get; } = new();
    public List<string> Errors { get; } = new();
    public string? FatalError { get; set; }
    public string Diagnostics { get; set; } = "";
    public SubscriptionFormat Format { get; set; }
    public bool Success => FatalError is null && Profiles.Count > 0;
}

public sealed class SubscriptionService
{
    private const int MaxReportedErrors = 8;

    /// <summary>
    /// Панели различают клиентов по User-Agent. Одни по нему выбирают формат ответа
    /// (слова "clash" и "sing-box" переключают на YAML/JSON вместо списка ссылок),
    /// другие пускают только узнаваемые клиенты и на незнакомый UA отвечают 400.
    /// Поэтому перебор, а не одно фиксированное значение.
    /// </summary>
    private static readonly string[] FallbackUserAgents =
    {
        "v2rayNG/1.9.5",
        "Happ/2.16.2",
        "clash-verge/v2.0.0",
        "SplitVpn/1.0"
    };

    private readonly HttpClient _http;

    public SubscriptionService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            // Без этого сервер отдаёт gzip, а тело читается как текст -> бинарный мусор
            // вместо списка ссылок. Флаг заодно проставляет Accept-Encoding.
            AutomaticDecompression = DecompressionMethods.All
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<SubscriptionFetchResult> FetchAsync(Subscription sub, CancellationToken ct = default)
    {
        var result = new SubscriptionFetchResult { Subscription = sub };

        foreach (var ua in AgentOrder(sub))
        {
            var (bytes, diagnostics, failure) = await TryFetchAsync(sub.Url, ua, ct).ConfigureAwait(false);

            result.Diagnostics = $"{diagnostics}, UA={ua}";

            if (failure is not null)
            {
                result.Errors.Add($"UA={ua}: {failure}");
                result.FatalError = failure;
                continue;
            }

            var attempt = new SubscriptionFetchResult { Subscription = sub };
            Parse(Encoding.UTF8.GetString(bytes!), sub, attempt);

            if (!attempt.Success)
            {
                result.Errors.AddRange(attempt.Errors);
                result.FatalError = attempt.FatalError;
                result.Format = attempt.Format;
                continue;
            }

            // Удачный UA запоминаем: следующее обновление пойдёт одним запросом.
            sub.UserAgent = ua;

            result.Profiles.AddRange(attempt.Profiles);
            result.Errors.AddRange(attempt.Errors);
            result.Format = attempt.Format;
            result.FatalError = null;
            return result;
        }

        return result;
    }

    private static IEnumerable<string> AgentOrder(Subscription sub)
    {
        if (!string.IsNullOrWhiteSpace(sub.UserAgent)) yield return sub.UserAgent.Trim();

        foreach (var ua in FallbackUserAgents)
            if (!string.Equals(ua, sub.UserAgent, StringComparison.OrdinalIgnoreCase))
                yield return ua;
    }

    private async Task<(byte[]? bytes, string diagnostics, string? failure)> TryFetchAsync(
        string url, string userAgent, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(userAgent);
            request.Headers.Accept.ParseAdd("*/*");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            var contentEncoding = string.Join(",", response.Content.Headers.ContentEncoding);
            if (contentEncoding.Length == 0) contentEncoding = "-";

            var diagnostics =
                $"HTTP {(int)response.StatusCode} {response.StatusCode}, " +
                $"type={response.Content.Headers.ContentType?.ToString() ?? "-"}, " +
                $"encoding={contentEncoding}, {bytes.Length} байт";

            if (response.IsSuccessStatusCode) return (bytes, diagnostics, null);

            // Причина отказа лежит в теле ("Device not supported", "Device already connected"),
            // а не в коде статуса - без неё диагностировать нечего.
            var reason = ServerMessage(bytes);
            var failure = $"сервер ответил {(int)response.StatusCode} {response.StatusCode}" +
                          (reason is null ? "" : $": {reason}");

            return (null, diagnostics, failure);
        }
        catch (Exception ex)
        {
            return (null, "запрос не выполнен", ex.Message);
        }
    }

    /// <summary>Вытаскивает человекочитаемое сообщение об ошибке из тела ответа.</summary>
    private static string? ServerMessage(byte[] bytes)
    {
        if (bytes.Length == 0) return null;

        var text = Encoding.UTF8.GetString(bytes).Trim();
        if (text.Length == 0) return null;

        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);

                foreach (var key in new[] { "message", "error", "detail", "msg" })
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
            }
            catch (JsonException)
            {
                // не JSON - отдадим как есть ниже
            }
        }

        return Preview(text);
    }

    private static void Parse(string body, Subscription sub, SubscriptionFetchResult result)
    {
        var (format, payload) = Detect(body);
        result.Format = format;

        switch (format)
        {
            case SubscriptionFormat.ShareLinks:
            case SubscriptionFormat.Base64ShareLinks:
            {
                var parsed = ShareLinkParser.ParseMany(payload, out var errors);
                Report(result, errors);

                foreach (var p in parsed)
                {
                    p.SubscriptionId = sub.Id;
                    result.Profiles.Add(p);
                }
                break;
            }

            case SubscriptionFormat.SingBoxJson:
            case SubscriptionFormat.XrayJson:
            {
                var parsed = format == SubscriptionFormat.XrayJson
                    ? XrayOutboundReader.Parse(payload, out var errors)
                    : SingBoxOutboundReader.Parse(payload, out errors);

                Report(result, errors);

                foreach (var p in parsed)
                {
                    p.SubscriptionId = sub.Id;
                    result.Profiles.Add(p);
                }
                break;
            }

            case SubscriptionFormat.ClashYaml:
                result.FatalError =
                    "подписка отдана в формате Clash YAML, он не поддерживается. " +
                    "Запросите у провайдера ссылку в формате v2ray (список ссылок) или sing-box.";
                return;

            default:
                result.FatalError = $"формат ответа не распознан. Начало тела: {Preview(body)}";
                return;
        }

        if (result.Profiles.Count == 0)
            result.FatalError = "формат распознан, но ни одного сервера извлечь не удалось";
    }

    /// <summary>
    /// JSON проверяется раньше ссылок: в конфиге Xray встречаются поля вида "email":"https...",
    /// и проверка на "://" принимала такой конфиг за список ссылок.
    /// </summary>
    private static (SubscriptionFormat, string) Detect(string body)
    {
        var trimmed = body.TrimStart('﻿', ' ', '\t', '\r', '\n');

        var json = DetectJson(trimmed);
        if (json != SubscriptionFormat.Unknown) return (json, trimmed);

        if (body.Contains("://", StringComparison.Ordinal))
            return (SubscriptionFormat.ShareLinks, body);

        if (LooksLikeClashYaml(trimmed))
            return (SubscriptionFormat.ClashYaml, trimmed);

        try
        {
            var decoded = Encoding.UTF8.GetString(ShareLinkParser.DecodeBase64(body));
            var decodedTrimmed = decoded.TrimStart('﻿', ' ', '\t', '\r', '\n');

            var decodedJson = DetectJson(decodedTrimmed);
            if (decodedJson != SubscriptionFormat.Unknown) return (decodedJson, decodedTrimmed);

            if (decoded.Contains("://", StringComparison.Ordinal))
                return (SubscriptionFormat.Base64ShareLinks, decoded);

            if (LooksLikeClashYaml(decodedTrimmed))
                return (SubscriptionFormat.ClashYaml, decodedTrimmed);
        }
        catch (FormatException)
        {
            // не base64 - падать сюда нормально, ниже вернём Unknown
        }

        return (SubscriptionFormat.Unknown, body);
    }

    /// <summary>
    /// Различает конфиги sing-box и Xray по содержимому outbounds: у первого элементы
    /// помечены полем "type", у второго - "protocol".
    /// </summary>
    private static SubscriptionFormat DetectJson(string trimmed)
    {
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return SubscriptionFormat.Unknown;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);

            if (XrayOutboundReader.LooksLikeXray(doc.RootElement)) return SubscriptionFormat.XrayJson;

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("outbounds", out _))
                return SubscriptionFormat.SingBoxJson;
        }
        catch (JsonException)
        {
            // не JSON - пусть решают следующие проверки
        }

        return SubscriptionFormat.Unknown;
    }

    private static bool LooksLikeClashYaml(string s) =>
        s.StartsWith("proxies:", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("\nproxies:", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("proxy-groups:", StringComparison.OrdinalIgnoreCase);

    private static void Report(SubscriptionFetchResult result, List<string> errors)
    {
        foreach (var e in errors.Take(MaxReportedErrors)) result.Errors.Add(e);

        if (errors.Count > MaxReportedErrors)
            result.Errors.Add($"...и ещё {errors.Count - MaxReportedErrors} строк с ошибками");
    }

    /// <summary>Безопасный для журнала кусок тела: непечатаемое вырезается, чтобы бинарь не залил лог.</summary>
    private static string Preview(string body)
    {
        var sb = new StringBuilder();

        foreach (var c in body)
        {
            if (sb.Length >= 60) break;
            sb.Append(char.IsControl(c) || c == '�' ? '.' : c);
        }

        return sb.Length == 0 ? "(пусто)" : sb.ToString();
    }
}

/// <summary>Обратное преобразование: outbound'ы из конфига sing-box в профили.</summary>
public static class SingBoxOutboundReader
{
    private static readonly HashSet<string> Supported =
        new(StringComparer.OrdinalIgnoreCase) { "vless", "vmess", "trojan", "shadowsocks", "hysteria2" };

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

        if (!root.TryGetProperty("outbounds", out var outbounds) || outbounds.ValueKind != JsonValueKind.Array)
        {
            errors.Add("в JSON нет массива outbounds");
            return result;
        }

        foreach (var o in outbounds.EnumerateArray())
        {
            var type = Str(o, "type");
            if (type is null || !Supported.Contains(type)) continue;

            try
            {
                result.Add(ReadOutbound(o, type));
            }
            catch (Exception ex)
            {
                errors.Add($"outbound '{Str(o, "tag") ?? type}': {ex.Message}");
            }
        }

        if (result.Count == 0 && errors.Count == 0)
            errors.Add("в outbounds нет поддерживаемых протоколов");

        return result;
    }

    private static ProxyProfile ReadOutbound(JsonElement o, string type)
    {
        var p = new ProxyProfile
        {
            Protocol = type.ToLowerInvariant(),
            Name = Str(o, "tag") ?? "",
            Server = Str(o, "server") ?? "",
            ServerPort = Int(o, "server_port"),
            Uuid = Str(o, "uuid"),
            Password = Str(o, "password"),
            Method = Str(o, "method"),
            Flow = Str(o, "flow"),
            VmessSecurity = Str(o, "security"),
            AlterId = Int(o, "alter_id")
        };

        if (p.Protocol == "hysteria2")
        {
            p.Network = "hysteria2";

            if (o.TryGetProperty("obfs", out var obfs) && obfs.ValueKind == JsonValueKind.Object)
            {
                p.ObfsType = Str(obfs, "type");
                p.ObfsPassword = Str(obfs, "password");
            }
        }

        if (o.TryGetProperty("tls", out var tls) && tls.ValueKind == JsonValueKind.Object)
        {
            p.TlsEnabled = Bool(tls, "enabled");
            p.Sni = Str(tls, "server_name");
            p.AllowInsecure = Bool(tls, "insecure");

            if (tls.TryGetProperty("alpn", out var alpn) && alpn.ValueKind == JsonValueKind.Array)
                p.Alpn = alpn.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0).ToArray();

            if (tls.TryGetProperty("utls", out var utls) && utls.ValueKind == JsonValueKind.Object)
                p.Fingerprint = Str(utls, "fingerprint");

            if (tls.TryGetProperty("reality", out var reality) && reality.ValueKind == JsonValueKind.Object)
            {
                p.RealityEnabled = Bool(reality, "enabled");
                p.RealityPublicKey = Str(reality, "public_key");
                p.RealityShortId = Str(reality, "short_id");
            }
        }

        if (o.TryGetProperty("transport", out var tr) && tr.ValueKind == JsonValueKind.Object)
        {
            p.Network = Str(tr, "type") ?? "tcp";

            switch (p.Network)
            {
                case "ws":
                case "httpupgrade":
                    p.WsPath = Str(tr, "path");
                    p.WsMaxEarlyData = Int(tr, "max_early_data");
                    p.WsHost = Str(tr, "host")
                               ?? (tr.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object
                                   ? Str(h, "Host")
                                   : null);
                    break;

                case "grpc":
                    p.GrpcServiceName = Str(tr, "service_name");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(p.Server))
            throw new InvalidOperationException("не указан server");

        if (string.IsNullOrWhiteSpace(p.Name))
            p.Name = $"{p.Server}:{p.ServerPort}";

        return p;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
