using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SplitVpn.Models;

namespace SplitVpn.Services;

public sealed record ConfigBuildResult(
    string Json,
    string ClashSecret,
    IReadOnlyList<string> Warnings,
    /// <summary>Id профиля -> tag outbound'а. По этим именам сервер зовётся в clash_api.</summary>
    IReadOnlyDictionary<string, string> TagByProfileId,
    /// <summary>
    /// Имя канала -> tag, записанный ему как default. После запуска ядра выбор надо
    /// переприменить через clash_api: с включённым cache_file ядро восстанавливает
    /// прошлый выбор selector'а из cache.db поверх default.
    /// </summary>
    IReadOnlyDictionary<string, string> ChannelDefaults);

/// <summary>
/// Собирает config.json для sing-box из настроек приложения.
/// </summary>
/// <remarks>
/// В 1.12 сменились схема DNS-серверов (address-строка -> типизированный объект) и способ
/// отбрасывать трафик (outbound "block" -> route action "reject"), поэтому билдер держит обе схемы.
/// Ветку выбирает <see cref="CoreProcessService.DetectModernSyntaxAsync"/> по выводу `sing-box version`.
/// </remarks>
public sealed class SingBoxConfigBuilder
{
    private const string GeoIpUrlTemplate = "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-{0}.srs";
    private const string GeoSiteUrlTemplate = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-{0}.srs";

    private readonly bool _modern;
    private readonly IReadOnlySet<string>? _availableRuleSets;
    private readonly IReadOnlyDictionary<string, int> _xrayPorts;
    private readonly string? _xrayPath;
    private readonly IReadOnlyCollection<string> _xrayServers;

    /// <param name="availableRuleSets">
    /// Теги наборов, файлы которых лежат в кеше. null - считать доступными все.
    /// Правила с недоступным набором в конфиг не попадают: ядро упало бы на старте.
    /// </param>
    /// <param name="xrayPorts">
    /// Id профиля -> локальный SOCKS-порт запущенного сайдкара Xray. Профили, требующие Xray,
    /// но без порта, в конфиг не попадают.
    /// </param>
    /// <param name="xrayPath">
    /// Путь к xray.exe. Его собственные соединения выводятся из туннеля отдельным правилом,
    /// иначе сайдкар заворачивает сам себя.
    /// </param>
    public SingBoxConfigBuilder(
        bool modernSyntax,
        IReadOnlySet<string>? availableRuleSets = null,
        IReadOnlyDictionary<string, int>? xrayPorts = null,
        string? xrayPath = null,
        IReadOnlyCollection<string>? xrayServers = null)
    {
        _modern = modernSyntax;
        _availableRuleSets = availableRuleSets;
        _xrayPorts = xrayPorts ?? new Dictionary<string, int>();
        _xrayPath = xrayPath;
        _xrayServers = xrayServers ?? Array.Empty<string>();
    }

    /// <summary>Наборы, нужные текущим правилам - для предварительной загрузки в кеш.</summary>
    public static IReadOnlyList<(string tag, string url)> RequiredRuleSets(AppSettings s)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in s.GeoRules)
        {
            if (!r.Enabled || string.IsNullOrWhiteSpace(r.Value)) continue;
            if (r.Kind is not (GeoRuleKind.GeoIp or GeoRuleKind.GeoSite)) continue;

            var isIp = r.Kind == GeoRuleKind.GeoIp;
            var tag = (isIp ? "geoip-" : "geosite-") + Slug(r.Value);
            if (!seen.Add(tag)) continue;

            var url = string.IsNullOrWhiteSpace(r.CustomRuleSetUrl)
                ? string.Format(isIp ? GeoIpUrlTemplate : GeoSiteUrlTemplate, Slug(r.Value))
                : r.CustomRuleSetUrl.Trim();

            result.Add((tag, url));
        }

        return result;
    }

    public ConfigBuildResult Build(AppSettings s)
    {
        var warnings = new List<string>();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var channelNames = s.Channels
            .Select(c => c.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tagByProfileId = AssignTags(s.Profiles, channelNames);

        var defaultOutbound = ResolveTarget(s.DefaultChannelName, channelNames, warnings, "канал по умолчанию");
        if (defaultOutbound == RouteTargets.Block) defaultOutbound = RouteTargets.Direct;

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = string.IsNullOrWhiteSpace(s.CoreLogLevel) ? "warn" : s.CoreLogLevel,
                ["timestamp"] = true
            },
            ["dns"] = BuildDns(s, channelNames, defaultOutbound),
            ["inbounds"] = BuildInbounds(s),
            ["outbounds"] = BuildOutbounds(s, tagByProfileId, warnings, out var usableProfileIds, out var channelDefaults),
            ["route"] = BuildRoute(s, channelNames, defaultOutbound, warnings),
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{s.ClashApiPort}",
                    ["secret"] = secret
                },
                ["cache_file"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["path"] = "cache.db"
                }
            }
        };

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        // В карту тегов попадают только те, что реально есть в конфиге: иначе замер задержки
        // спрашивает ядро про несуществующий outbound и показывает «нет ответа» вместо
        // честного «сервер не собран».
        var actualTags = tagByProfileId
            .Where(kv => usableProfileIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new ConfigBuildResult(json, secret, warnings, actualTags, channelDefaults);
    }

    // ---------- inbounds ----------

    private JsonArray BuildInbounds(AppSettings s)
    {
        var tun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = "tun-in",
            // Собственное имя адаптера: по нему отличается наш туннель от чужого.
            ["interface_name"] = TunnelConflict.OwnInterfaceName,
            ["address"] = new JsonArray("172.19.0.1/30"),
            ["mtu"] = 9000,
            ["auto_route"] = true,
            // Без strict_route часть трафика уходит мимо туннеля по исходному маршруту.
            ["strict_route"] = true,
            ["stack"] = "gvisor"
        };

        if (!_modern)
        {
            tun["sniff"] = true;
            tun["sniff_override_destination"] = false;
        }

        var mixed = new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "mixed-in",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = s.MixedPort
        };

        if (!_modern) mixed["sniff"] = true;

        return new JsonArray(tun, mixed);
    }

    // ---------- outbounds ----------

    private JsonArray BuildOutbounds(
        AppSettings s,
        Dictionary<string, string> tagByProfileId,
        List<string> warnings,
        out HashSet<string> usableProfileIds,
        out Dictionary<string, string> channelDefaults)
    {
        var arr = new JsonArray();

        var usable = new HashSet<string>();
        usableProfileIds = usable;
        channelDefaults = new Dictionary<string, string>();

        var selectedKeys = s.Channels
            .Where(c => !c.AutoFastest)
            .Select(c => c.SelectedProfileKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.Ordinal)!;

        foreach (var p in s.Profiles)
        {
            // Пропускать молча нельзя: без транспорта outbound собрался бы как голый TCP,
            // конфиг прошёл бы проверку, а соединение рвалось бы уже в работе.
            if (!CoreCapabilities.IsSupported(p, out var reason))
            {
                warnings.Add($"Сервер '{p.Name}' пропущен: {reason}.");
                continue;
            }

            if (p.UsesXray)
            {
                if (!_xrayPorts.TryGetValue(p.Id, out var port))
                {
                    // Сайдкар поднимается только под выбранный сервер канала: держать
                    // по процессу Xray на каждый сервер подписки было бы расточительно.
                    // Предупреждаем лишь о тех, что где-то выбраны, - иначе на каждое
                    // подключение сыпалось бы по строке на каждый сервер подписки.
                    if (selectedKeys.Contains(p.StableKey))
                        warnings.Add($"Сервер '{p.Name}' требует Xray, но сайдкар не запущен - пропущен.");

                    continue;
                }

                usable.Add(p.Id);
                arr.Add(new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = tagByProfileId[p.Id],
                    ["server"] = "127.0.0.1",
                    ["server_port"] = port,
                    ["version"] = "5"
                });

                continue;
            }

            var node = BuildProfileOutbound(p, tagByProfileId[p.Id]);
            if (node is null)
            {
                warnings.Add($"Сервер '{p.Name}': протокол '{p.Protocol}' не поддерживается, пропущен.");
                continue;
            }

            usable.Add(p.Id);
            arr.Add(node);
        }

        foreach (var ch in s.Channels)
        {
            if (string.IsNullOrWhiteSpace(ch.Name)) continue;

            var memberProfiles = s.Profiles
                .Where(p => ch.RestrictToSubscriptionId is null || p.SubscriptionId == ch.RestrictToSubscriptionId)
                .Where(p => usable.Contains(p.Id))
                .ToList();

            var members = memberProfiles.Select(p => tagByProfileId[p.Id]).ToList();

            if (members.Count == 0)
            {
                warnings.Add($"Канал '{ch.Name}' пуст - трафик по нему пойдёт напрямую.");
                members.Add(RouteTargets.Direct);
            }

            var group = new JsonObject
            {
                ["type"] = ch.AutoFastest ? "urltest" : "selector",
                ["tag"] = ch.Name
            };

            if (ch.AutoFastest)
            {
                group["url"] = "https://www.gstatic.com/generate_204";
                group["interval"] = "3m";
                group["tolerance"] = 50;
            }
            else
            {
                var chosen = ResolveChannelDefault(ch, s, memberProfiles, tagByProfileId, warnings);

                if (chosen == RouteTargets.Direct && !members.Contains(RouteTargets.Direct))
                    members.Add(RouteTargets.Direct);

                group["default"] = chosen;
                channelDefaults[ch.Name] = chosen;
            }

            group["outbounds"] = new JsonArray(members.Select(m => (JsonNode)m!).ToArray());
            arr.Add(group);
        }

        arr.Add(new JsonObject { ["type"] = "direct", ["tag"] = RouteTargets.Direct });

        if (!_modern)
        {
            arr.Add(new JsonObject { ["type"] = "block", ["tag"] = RouteTargets.Block });
            arr.Add(new JsonObject { ["type"] = "dns", ["tag"] = "dns-out" });
        }

        return arr;
    }

    /// <summary>
    /// Сервер, который канал получит как default: выбранный, если он попал в конфиг; иначе сервер
    /// той же подписки - это хотя бы тот же провайдер; иначе прямое соединение.
    /// </summary>
    /// <remarks>
    /// Раньше при пропаже выбранного сервера default не писался, и ядро брало первый outbound
    /// списка. Список у всех каналов общий, поэтому канал по умолчанию мог уехать на сервер,
    /// заведённый ради одного сайта, и весь трафик шёл через него незаметно. Прямое соединение
    /// заметно сразу, чужой сервер - нет.
    /// </remarks>
    private static string ResolveChannelDefault(
        Channel ch,
        AppSettings s,
        List<ProxyProfile> memberProfiles,
        Dictionary<string, string> tagByProfileId,
        List<string> warnings)
    {
        var selected = s.Profiles.FirstOrDefault(p => p.StableKey == ch.SelectedProfileKey);

        if (selected is not null && memberProfiles.Contains(selected))
            return tagByProfileId[selected.Id];

        string problem;

        if (selected is not null)
        {
            CoreCapabilities.IsSupported(selected, out var reason);

            // Причин две и они разные: сервер вообще не поддерживается либо
            // поддерживается, но через сайдкар, который не поднялся.
            var why = reason
                      ?? (selected.UsesXray
                          ? "нужен сайдкар Xray, а он не запущен"
                          : "причина не определена");

            problem = $"сервер '{selected.Name}' не подошёл ({why})";
        }
        else if (ch.SelectedProfileKey is { Length: > 0 })
        {
            problem = "выбранный сервер исчез из подписки";
        }
        else
        {
            problem = "сервер не выбран";
        }

        var sameProvider = selected?.SubscriptionId is null
            ? null
            : memberProfiles.FirstOrDefault(p => p.SubscriptionId == selected.SubscriptionId);

        if (sameProvider is not null)
        {
            warnings.Add($"Канал '{ch.Name}': {problem}, временно взят '{sameProvider.Name}' из той же подписки.");
            return tagByProfileId[sameProvider.Id];
        }

        warnings.Add($"Канал '{ch.Name}': {problem}. Канал пущен напрямую, пока не выбран другой сервер.");
        return RouteTargets.Direct;
    }

    private static JsonObject? BuildProfileOutbound(ProxyProfile p, string tag)
    {
        JsonObject o;

        switch (p.Protocol.ToLowerInvariant())
        {
            case "vless":
                o = new JsonObject
                {
                    ["type"] = "vless",
                    ["tag"] = tag,
                    ["server"] = p.Server,
                    ["server_port"] = p.ServerPort,
                    ["uuid"] = p.Uuid ?? "",
                    ["packet_encoding"] = "xudp"
                };
                if (!string.IsNullOrWhiteSpace(p.Flow)) o["flow"] = p.Flow;
                break;

            case "vmess":
                o = new JsonObject
                {
                    ["type"] = "vmess",
                    ["tag"] = tag,
                    ["server"] = p.Server,
                    ["server_port"] = p.ServerPort,
                    ["uuid"] = p.Uuid ?? "",
                    ["security"] = string.IsNullOrWhiteSpace(p.VmessSecurity) ? "auto" : p.VmessSecurity,
                    ["alter_id"] = p.AlterId
                };
                break;

            case "trojan":
                o = new JsonObject
                {
                    ["type"] = "trojan",
                    ["tag"] = tag,
                    ["server"] = p.Server,
                    ["server_port"] = p.ServerPort,
                    ["password"] = p.Password ?? ""
                };
                break;

            case "shadowsocks":
                o = new JsonObject
                {
                    ["type"] = "shadowsocks",
                    ["tag"] = tag,
                    ["server"] = p.Server,
                    ["server_port"] = p.ServerPort,
                    ["method"] = p.Method ?? "aes-128-gcm",
                    ["password"] = p.Password ?? ""
                };
                break;

            case "hysteria2":
            {
                o = new JsonObject
                {
                    ["type"] = "hysteria2",
                    ["tag"] = tag,
                    ["server"] = p.Server,
                    ["server_port"] = p.ServerPort,
                    ["password"] = p.Password ?? ""
                };

                if (!string.IsNullOrWhiteSpace(p.ObfsType))
                {
                    o["obfs"] = new JsonObject
                    {
                        ["type"] = p.ObfsType,
                        ["password"] = p.ObfsPassword ?? ""
                    };
                }

                // Hysteria2 идёт поверх QUIC: TLS обязателен, а транспорты v2ray и uTLS
                // к нему неприменимы - ядро отвергнет и utls, и transport.
                var hyTls = new JsonObject { ["enabled"] = true };

                if (!string.IsNullOrWhiteSpace(p.Sni)) hyTls["server_name"] = p.Sni;
                if (p.AllowInsecure) hyTls["insecure"] = true;

                hyTls["alpn"] = p.Alpn is { Length: > 0 }
                    ? new JsonArray(p.Alpn.Select(a => (JsonNode)a!).ToArray())
                    : new JsonArray("h3");

                o["tls"] = hyTls;
                return o;
            }

            default:
                return null;
        }

        var tls = BuildTls(p);
        if (tls is not null) o["tls"] = tls;

        var transport = BuildTransport(p);
        if (transport is not null) o["transport"] = transport;

        return o;
    }

    private static JsonObject? BuildTls(ProxyProfile p)
    {
        if (!p.TlsEnabled) return null;

        var tls = new JsonObject { ["enabled"] = true };

        if (!string.IsNullOrWhiteSpace(p.Sni)) tls["server_name"] = p.Sni;
        if (p.AllowInsecure) tls["insecure"] = true;
        if (p.Alpn is { Length: > 0 })
            tls["alpn"] = new JsonArray(p.Alpn.Select(a => (JsonNode)a!).ToArray());

        if (!string.IsNullOrWhiteSpace(p.Fingerprint) || p.RealityEnabled)
        {
            // Reality без uTLS не работает: сервер проверяет отпечаток ClientHello.
            tls["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = string.IsNullOrWhiteSpace(p.Fingerprint) ? "chrome" : p.Fingerprint
            };
        }

        if (p.RealityEnabled)
        {
            var reality = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = p.RealityPublicKey ?? ""
            };
            if (!string.IsNullOrWhiteSpace(p.RealityShortId)) reality["short_id"] = p.RealityShortId;
            tls["reality"] = reality;
        }

        return tls;
    }

    private static JsonObject? BuildTransport(ProxyProfile p)
    {
        switch (p.Network.ToLowerInvariant())
        {
            case "ws":
            {
                var t = new JsonObject
                {
                    ["type"] = "ws",
                    ["path"] = string.IsNullOrWhiteSpace(p.WsPath) ? "/" : p.WsPath
                };
                if (!string.IsNullOrWhiteSpace(p.WsHost))
                    t["headers"] = new JsonObject { ["Host"] = p.WsHost };
                if (p.WsMaxEarlyData > 0)
                {
                    t["max_early_data"] = p.WsMaxEarlyData;
                    t["early_data_header_name"] = "Sec-WebSocket-Protocol";
                }
                return t;
            }

            case "httpupgrade":
            {
                var t = new JsonObject
                {
                    ["type"] = "httpupgrade",
                    ["path"] = string.IsNullOrWhiteSpace(p.WsPath) ? "/" : p.WsPath
                };
                if (!string.IsNullOrWhiteSpace(p.WsHost)) t["host"] = p.WsHost;
                return t;
            }

            case "grpc":
                return new JsonObject
                {
                    ["type"] = "grpc",
                    ["service_name"] = p.GrpcServiceName ?? ""
                };

            // HTTP/2-транспорт: в ссылках type=http, у Xray httpSettings. Без этой ветки
            // outbound собирался бы голым TCP, хотя транспорт значится поддерживаемым.
            case "http":
            {
                var t = new JsonObject
                {
                    ["type"] = "http",
                    ["path"] = string.IsNullOrWhiteSpace(p.WsPath) ? "/" : p.WsPath
                };
                if (!string.IsNullOrWhiteSpace(p.WsHost)) t["host"] = new JsonArray(p.WsHost);
                return t;
            }

            case "quic":
                return new JsonObject { ["type"] = "quic" };

            default:
                return null;
        }
    }

    // ---------- dns ----------

    private JsonObject BuildDns(AppSettings s, HashSet<string> channelNames, string defaultOutbound)
    {
        var servers = new JsonArray();

        servers.Add(DnsServer("dns-local", "local", detour: null));

        foreach (var ch in s.Channels.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
            servers.Add(DnsServer(DnsTagFor(ch.Name), "https://1.1.1.1/dns-query", ch.Name));

        var rules = new JsonArray();

        // Первым делом - домены серверов сайдкара, локальным резолвером.
        // Xray резолвит адрес своего сервера сам, запрос перехватывается TUN и по общим
        // правилам ушёл бы в DNS канала, то есть обратно в Xray: круг замыкается,
        // и соединения рвутся без внятной причины.
        var xrayDomains = new JsonArray();
        foreach (var address in _xrayServers)
            if (!System.Net.IPAddress.TryParse(address, out _))
                xrayDomains.Add(address);

        if (xrayDomains.Count > 0)
            rules.Add(new JsonObject { ["domain"] = xrayDomains, ["server"] = "dns-local" });

        // Резолв должен идти через тот же канал, что и трафик приложения,
        // иначе провайдер канала по умолчанию видит все запрашиваемые домены.
        foreach (var r in s.AppRules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Value)))
        {
            var server = DnsServerForTarget(r.Target, channelNames);
            var rule = new JsonObject { ["server"] = server };
            AddAppMatch(rule, r);
            rules.Add(rule);
        }

        foreach (var r in s.GeoRules.Where(r => r.Enabled && IsDomainKind(r.Kind) && !string.IsNullOrWhiteSpace(r.Value)))
        {
            var rule = new JsonObject { ["server"] = DnsServerForTarget(r.Target, channelNames) };
            if (r.Kind == GeoRuleKind.GeoSite)
            {
                var tag = $"geosite-{Slug(r.Value)}";
                if (_availableRuleSets is not null && !_availableRuleSets.Contains(tag)) continue;
                rule["rule_set"] = new JsonArray(tag);
            }
            else
            {
                rule[DomainFieldName(r.Kind)] = new JsonArray(r.Value);
            }

            rules.Add(rule);
        }

        var dns = new JsonObject
        {
            ["servers"] = servers,
            ["rules"] = rules,
            ["final"] = DnsServerForTarget(defaultOutbound, channelNames),
            ["strategy"] = s.Ipv4Only ? "ipv4_only" : "prefer_ipv4",
            // Ответы кешируются отдельно на каждый detour - иначе каналы делят чужие CDN-адреса.
            ["independent_cache"] = true
        };

        return dns;
    }

    private JsonObject DnsServer(string tag, string address, string? detour)
    {
        var o = new JsonObject { ["tag"] = tag };

        if (_modern)
        {
            if (address == "local")
            {
                o["type"] = "local";
            }
            else
            {
                var uri = new Uri(address);
                o["type"] = "https";
                o["server"] = uri.Host;
            }
        }
        else
        {
            o["address"] = address;
        }

        if (detour is not null) o["detour"] = detour;
        return o;
    }

    private static string DnsTagFor(string channelName) => $"dns-{Slug(channelName)}";

    private static string DnsServerForTarget(string target, HashSet<string> channelNames) =>
        channelNames.Contains(target) ? DnsTagFor(target) : "dns-local";

    // ---------- route ----------

    private JsonObject BuildRoute(
        AppSettings s,
        HashSet<string> channelNames,
        string defaultOutbound,
        List<string> warnings)
    {
        var rules = new JsonArray();

        if (_modern)
        {
            rules.Add(new JsonObject { ["action"] = "sniff" });
            rules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });
        }
        else
        {
            rules.Add(new JsonObject { ["protocol"] = "dns", ["outbound"] = "dns-out" });
        }

        rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = RouteTargets.Direct });

        // Соединения самого сайдкара должны идти мимо туннеля, иначе Xray заворачивает себя же.
        if (_xrayPorts.Count > 0 && !string.IsNullOrWhiteSpace(_xrayPath))
        {
            rules.Add(new JsonObject
            {
                ["process_path"] = new JsonArray(_xrayPath),
                ["outbound"] = RouteTargets.Direct
            });
        }

        // Правила по процессу мало: сопоставление владельца соединения на Windows срабатывает
        // не всегда ("failed to search process" в журнале ядра). Адреса серверов сайдкара
        // выводим из туннеля ещё и по назначению - это уже не зависит от поиска процесса.
        AddXrayServerBypass(rules);

        if (s.BlockQuic)
        {
            var quic = new JsonObject { ["network"] = "udp", ["port"] = new JsonArray(443) };
            ApplyAction(quic, RouteTargets.Block);
            rules.Add(quic);
        }

        // Правила приложений идут строго раньше гео: "этот процесс всегда через X"
        // должно перевешивать "домены такой-то страны напрямую".
        foreach (var r in s.AppRules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Value)))
        {
            var target = ResolveTarget(r.Target, channelNames, warnings, $"правило для '{r.Value}'");
            var rule = new JsonObject();
            AddAppMatch(rule, r);
            ApplyAction(rule, target);
            rules.Add(rule);
        }

        var ruleSets = new JsonArray();
        var declaredSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in s.GeoRules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Value)))
        {
            var target = ResolveTarget(r.Target, channelNames, warnings, $"гео-правило '{r.Value}'");
            var rule = new JsonObject();

            switch (r.Kind)
            {
                case GeoRuleKind.GeoIp:
                case GeoRuleKind.GeoSite:
                {
                    var isIp = r.Kind == GeoRuleKind.GeoIp;
                    var tag = (isIp ? "geoip-" : "geosite-") + Slug(r.Value);

                    if (_availableRuleSets is not null && !_availableRuleSets.Contains(tag))
                    {
                        warnings.Add($"Набор '{tag}' не скачан, правило '{r.Value}' пропущено.");
                        continue;
                    }

                    if (declaredSets.Add(tag))
                    {
                        // local, а не remote: иначе ядро качает набор при старте и падает,
                        // если канал не поднялся. Файлы держит приложение в RuleSetCache.
                        ruleSets.Add(new JsonObject
                        {
                            ["type"] = "local",
                            ["tag"] = tag,
                            ["format"] = "binary",
                            ["path"] = RuleSetCache.ConfigPathFor(tag)
                        });
                    }

                    rule["rule_set"] = new JsonArray(tag);
                    break;
                }

                case GeoRuleKind.IpCidr:
                    rule["ip_cidr"] = new JsonArray(r.Value);
                    break;

                case GeoRuleKind.Port:
                    if (!int.TryParse(r.Value, out var port))
                    {
                        warnings.Add($"Гео-правило: '{r.Value}' не число, порт пропущен.");
                        continue;
                    }
                    rule["port"] = new JsonArray(port);
                    break;

                default:
                    rule[DomainFieldName(r.Kind)] = new JsonArray(r.Value);
                    break;
            }

            ApplyAction(rule, target);
            rules.Add(rule);
        }

        var route = new JsonObject
        {
            ["rules"] = rules,
            ["final"] = defaultOutbound,
            ["auto_detect_interface"] = true
        };

        if (ruleSets.Count > 0) route["rule_set"] = ruleSets;
        if (_modern) route["default_domain_resolver"] = new JsonObject { ["server"] = "dns-local" };

        return route;
    }

    /// <summary>
    /// Выводит адреса серверов сайдкара из туннеля по назначению: домены отдельно, IP отдельно.
    /// </summary>
    private void AddXrayServerBypass(JsonArray rules)
    {
        if (_xrayServers.Count == 0) return;

        var domains = new JsonArray();
        var ips = new JsonArray();

        foreach (var address in _xrayServers)
        {
            if (System.Net.IPAddress.TryParse(address, out var ip))
                ips.Add($"{ip}/{(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32)}");
            else
                domains.Add(address);
        }

        if (domains.Count > 0)
            rules.Add(new JsonObject { ["domain"] = domains, ["outbound"] = RouteTargets.Direct });

        if (ips.Count > 0)
            rules.Add(new JsonObject { ["ip_cidr"] = ips, ["outbound"] = RouteTargets.Direct });
    }

    private void ApplyAction(JsonObject rule, string target)
    {
        if (target == RouteTargets.Block)
        {
            if (_modern) rule["action"] = "reject";
            else rule["outbound"] = RouteTargets.Block;
        }
        else
        {
            rule["outbound"] = target;
        }
    }

    private static void AddAppMatch(JsonObject rule, AppRule r)
    {
        var field = r.Mode == AppMatchMode.ProcessPath ? "process_path" : "process_name";
        rule[field] = new JsonArray(r.Value);
    }

    private static string ResolveTarget(string? target, HashSet<string> channelNames, List<string> warnings, string context)
    {
        if (string.IsNullOrWhiteSpace(target)) return RouteTargets.Direct;
        if (target == RouteTargets.Direct || target == RouteTargets.Block) return target;
        if (channelNames.Contains(target)) return target;

        warnings.Add($"{context}: канал '{target}' не найден, использовано прямое соединение.");
        return RouteTargets.Direct;
    }

    private static bool IsDomainKind(GeoRuleKind k) =>
        k is GeoRuleKind.GeoSite or GeoRuleKind.Domain or GeoRuleKind.DomainSuffix
            or GeoRuleKind.DomainKeyword or GeoRuleKind.DomainRegex;

    private static string DomainFieldName(GeoRuleKind k) => k switch
    {
        GeoRuleKind.Domain => "domain",
        GeoRuleKind.DomainSuffix => "domain_suffix",
        GeoRuleKind.DomainKeyword => "domain_keyword",
        GeoRuleKind.DomainRegex => "domain_regex",
        _ => "domain"
    };

    /// <summary>Теги в конфиге обязаны быть уникальными и не совпадать с именами каналов.</summary>
    private static Dictionary<string, string> AssignTags(IEnumerable<ProxyProfile> profiles, HashSet<string> reserved)
    {
        var used = new HashSet<string>(reserved, StringComparer.OrdinalIgnoreCase)
        {
            RouteTargets.Direct, RouteTargets.Block, "dns-out", "tun-in", "mixed-in"
        };

        var map = new Dictionary<string, string>();

        foreach (var p in profiles)
        {
            var baseTag = string.IsNullOrWhiteSpace(p.Name) ? $"{p.Server}-{p.ServerPort}" : p.Name.Trim();
            var tag = baseTag;
            var i = 2;
            while (!used.Add(tag)) tag = $"{baseTag} #{i++}";
            map[p.Id] = tag;
        }

        return map;
    }

    private static string Slug(string s) =>
        new string(s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
}
