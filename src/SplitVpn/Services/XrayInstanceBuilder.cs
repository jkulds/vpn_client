using System.Text.Json;
using System.Text.Json.Nodes;

namespace SplitVpn.Services;

public sealed record XrayInstanceConfig(string Json, string PrimaryTarget, bool Balanced);

/// <summary>
/// Готовит конфиг провайдера к запуску сайдкаром.
/// </summary>
/// <remarks>
/// Конфиг берётся как есть - вместе с outbound'ами, балансировщиком и observatory: именно
/// в них живут XHTTP и leastPing, ради которых сайдкар и нужен. Меняются две вещи:
/// inbound переставляется на локальный порт, а собственные правила маршрутизации провайдера
/// вырезаются. Второе принципиально: они уводят часть доменов в direct мимо туннеля, и тогда
/// правила приложения врали бы - маршрут решает sing-box, а не подписка.
/// </remarks>
public static class XrayInstanceBuilder
{
    public static XrayInstanceConfig Build(string providerConfigJson, int socksPort)
    {
        var root = JsonNode.Parse(providerConfigJson)?.AsObject()
                   ?? throw new InvalidOperationException("конфиг Xray не является объектом");

        root.Remove("remarks");

        root["log"] = new JsonObject { ["loglevel"] = "warning" };

        root["inbounds"] = new JsonArray(new JsonObject
        {
            ["tag"] = "socks-in",
            ["listen"] = "127.0.0.1",
            ["port"] = socksPort,
            ["protocol"] = "socks",
            ["settings"] = new JsonObject
            {
                ["auth"] = "noauth",
                ["udp"] = true
            },
            ["sniffing"] = new JsonObject
            {
                ["enabled"] = true,
                ["destOverride"] = new JsonArray("http", "tls")
            }
        });

        var (target, balanced) = ResolveTarget(root);

        var routing = root["routing"]?.AsObject() ?? new JsonObject();
        routing["domainStrategy"] = "AsIs";

        var finalRule = new JsonObject
        {
            ["type"] = "field",
            ["network"] = "tcp,udp"
        };

        if (balanced) finalRule["balancerTag"] = target;
        else finalRule["outboundTag"] = target;

        routing["rules"] = new JsonArray(finalRule);
        root["routing"] = routing;

        return new XrayInstanceConfig(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            target, balanced);
    }

    /// <summary>
    /// Балансировщик, если он есть в конфиге, иначе первый неслужебный outbound.
    /// </summary>
    private static (string target, bool balanced) ResolveTarget(JsonObject root)
    {
        var balancers = root["routing"]?["balancers"]?.AsArray();

        if (balancers is { Count: > 0 })
        {
            var tag = balancers[0]?["tag"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(tag)) return (tag, true);
        }

        var outbounds = root["outbounds"]?.AsArray()
                        ?? throw new InvalidOperationException("в конфиге нет outbounds");

        foreach (var node in outbounds)
        {
            var protocol = node?["protocol"]?.GetValue<string>();
            var tag = node?["tag"]?.GetValue<string>();

            if (protocol is null || tag is null) continue;
            if (protocol is "freedom" or "blackhole" or "dns" or "loopback") continue;

            return (tag, false);
        }

        throw new InvalidOperationException("в конфиге нет прокси-outbound'ов");
    }
}
