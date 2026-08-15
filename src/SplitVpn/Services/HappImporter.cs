using System.IO;
using System.Text.Json;
using SplitVpn.Models;

namespace SplitVpn.Services;

public sealed class ImportReport
{
    public List<string> Messages { get; } = new();
    public int AppRulesAdded { get; set; }
    public int GeoRulesAdded { get; set; }
    public bool AnythingFound { get; set; }
}

/// <summary>
/// Импорт настроек из клиента Happ.
/// </summary>
/// <remarks>
/// Серверы Happ хранит в зашифрованном subs.db, а Xray получает конфиг через stdin - на диск
/// учётные данные не попадают. Поэтому переносятся только правила маршрутизации;
/// серверы пользователь добавляет ссылкой подписки вручную.
/// </remarks>
public static class HappImporter
{
    private static readonly string[] PrivateRanges =
    {
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16",
        "224.0.0.0/4", "255.255.255.255", "127.0.0.0/8", "::1/128", "fc00::/7", "fe80::/10"
    };

    public static string HappDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Happ");

    public static bool IsInstalled => Directory.Exists(HappDataDir);

    public static ImportReport Import(AppSettings settings)
    {
        var report = new ImportReport();

        if (!IsInstalled)
        {
            report.Messages.Add($"Папка Happ не найдена: {HappDataDir}");
            return report;
        }

        report.AnythingFound = true;

        ImportRouting(settings, report);
        ImportProcessRules(settings, report);
        ReportSubscriptionState(report);

        return report;
    }

    private static void ImportRouting(AppSettings settings, ImportReport report)
    {
        var path = Path.Combine(HappDataDir, "routing.json");
        if (!File.Exists(path))
        {
            report.Messages.Add("routing.json не найден, гео-правила пропущены.");
            return;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            report.Messages.Add($"routing.json не разобран: {ex.Message}");
            return;
        }

        if (!root.TryGetProperty("routings", out var routings) || routings.ValueKind != JsonValueKind.Array)
        {
            report.Messages.Add("В routing.json нет секции routings.");
            return;
        }

        var activeName = root.TryGetProperty("activeRoutingName", out var an) ? an.GetString() : null;

        var active = routings.EnumerateArray().FirstOrDefault(r =>
            r.TryGetProperty("name", out var n) && n.GetString() == activeName);

        if (active.ValueKind != JsonValueKind.Object)
            active = routings.EnumerateArray().FirstOrDefault();

        if (active.ValueKind != JsonValueKind.Object)
        {
            report.Messages.Add("Не нашёл активный профиль маршрутизации.");
            return;
        }

        report.Messages.Add($"Профиль маршрутизации Happ: '{activeName ?? "?"}'.");

        var proxyTarget = settings.DefaultChannelName ?? RouteTargets.Direct;

        AddSites(settings, report, active, "blockSites", RouteTargets.Block);
        AddIps(settings, report, active, "blockIp", RouteTargets.Block);
        AddSites(settings, report, active, "directSites", RouteTargets.Direct);
        AddIps(settings, report, active, "directIp", RouteTargets.Direct);
        AddSites(settings, report, active, "proxySites", proxyTarget);
        AddIps(settings, report, active, "proxyIp", proxyTarget);

        if (active.TryGetProperty("globalProxy", out var gp) && gp.ValueKind == JsonValueKind.True)
            report.Messages.Add("В Happ включён globalProxy: маршрут по умолчанию - через канал.");
    }

    private static void AddSites(AppSettings settings, ImportReport report, JsonElement node, string field, string target)
    {
        if (!node.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        foreach (var item in arr.EnumerateArray())
        {
            var raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            GeoRule rule;

            if (raw.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase))
            {
                var code = raw["geosite:".Length..].Trim();
                rule = new GeoRule { Kind = GeoRuleKind.GeoSite, Value = code, Target = target };

                if (!IsKnownSagerNetGeosite(code))
                {
                    // Включённое правило с несуществующим .srs роняет ядро на старте:
                    // remote rule-set качается при запуске, 404 - фатальная ошибка.
                    rule.Enabled = false;

                    report.Messages.Add(
                        $"[!] Набор 'geosite:{code}' - из списков runetfreedom, в sing-geosite его нет. " +
                        "Правило добавлено ВЫКЛЮЧЕННЫМ, иначе ядро не стартует. Впишите прямую ссылку " +
                        "на .srs в колонке «Свой rule-set URL» и включите правило, либо замените набор на 'ru'.");
                }
            }
            else if (raw.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase))
            {
                rule = new GeoRule { Kind = GeoRuleKind.DomainRegex, Value = raw["regexp:".Length..].Trim(), Target = target };
            }
            else if (raw.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
            {
                rule = new GeoRule { Kind = GeoRuleKind.DomainSuffix, Value = raw["domain:".Length..].Trim(), Target = target };
            }
            else if (raw.StartsWith("full:", StringComparison.OrdinalIgnoreCase))
            {
                rule = new GeoRule { Kind = GeoRuleKind.Domain, Value = raw["full:".Length..].Trim(), Target = target };
            }
            else
            {
                rule = new GeoRule { Kind = GeoRuleKind.DomainKeyword, Value = raw.Trim(), Target = target };
            }

            if (AddGeoRuleIfMissing(settings, rule)) report.GeoRulesAdded++;
        }
    }

    private static void AddIps(AppSettings settings, ImportReport report, JsonElement node, string field, string target)
    {
        if (!node.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        foreach (var item in arr.EnumerateArray())
        {
            var raw = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // Приватные диапазоны уже покрыты правилом ip_is_private, которое строится всегда.
            if (PrivateRanges.Contains(raw, StringComparer.OrdinalIgnoreCase)) continue;

            var rule = raw.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase)
                ? new GeoRule { Kind = GeoRuleKind.GeoIp, Value = raw["geoip:".Length..].Trim(), Target = target }
                : new GeoRule { Kind = GeoRuleKind.IpCidr, Value = raw, Target = target };

            if (AddGeoRuleIfMissing(settings, rule)) report.GeoRulesAdded++;
        }
    }

    private static void ImportProcessRules(AppSettings settings, ImportReport report)
    {
        var path = Path.Combine(HappDataDir, "config.json");
        if (!File.Exists(path)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            if (!doc.RootElement.TryGetProperty("route", out var route) ||
                !route.TryGetProperty("rules", out var rules) ||
                rules.ValueKind != JsonValueKind.Array)
                return;

            foreach (var r in rules.EnumerateArray())
            {
                if (!r.TryGetProperty("process_name", out var names) || names.ValueKind != JsonValueKind.Array)
                    continue;

                var target = r.TryGetProperty("outbound", out var ob) && ob.GetString() == "direct"
                    ? RouteTargets.Direct
                    : settings.DefaultChannelName ?? RouteTargets.Direct;

                foreach (var n in names.EnumerateArray())
                {
                    var name = n.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Happ перечисляет ядра и без .exe - для process_name это мёртвая запись на Windows.
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                    if (settings.AppRules.Any(x =>
                            x.Mode == AppMatchMode.ProcessName &&
                            string.Equals(x.Value, name, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    settings.AppRules.Add(new AppRule
                    {
                        Mode = AppMatchMode.ProcessName,
                        Value = name,
                        Target = target,
                        Note = "из Happ"
                    });
                    report.AppRulesAdded++;
                }
            }
        }
        catch (Exception ex)
        {
            report.Messages.Add($"config.json Happ не разобран: {ex.Message}");
        }
    }

    private static void ReportSubscriptionState(ImportReport report)
    {
        var db = Path.Combine(HappDataDir, "subs.db");
        if (!File.Exists(db)) return;

        report.Messages.Add(
            "Серверы из Happ перенести автоматически нельзя: subs.db зашифрован, а Xray получает конфиг " +
            "через stdin. Скопируйте ссылку подписки в Happ (Подписки -> поделиться) и вставьте её " +
            "кнопкой «Добавить» в разделе «Подписки».");
    }

    private static bool AddGeoRuleIfMissing(AppSettings settings, GeoRule rule)
    {
        if (settings.GeoRules.Any(x =>
                x.Kind == rule.Kind &&
                string.Equals(x.Value, rule.Value, StringComparison.OrdinalIgnoreCase)))
            return false;

        settings.GeoRules.Add(rule);
        return true;
    }

    /// <summary>Наборы, которые точно есть в sing-geosite. Остальное требует своей ссылки на .srs.</summary>
    private static bool IsKnownSagerNetGeosite(string code) =>
        !code.Contains("available-only-inside", StringComparison.OrdinalIgnoreCase) &&
        !code.Contains("refilter", StringComparison.OrdinalIgnoreCase) &&
        !code.Contains("runet", StringComparison.OrdinalIgnoreCase);
}
