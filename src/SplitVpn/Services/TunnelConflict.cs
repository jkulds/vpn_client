using System.Net.NetworkInformation;

namespace SplitVpn.Services;

/// <summary>
/// Ищет чужие активные туннели.
/// </summary>
/// <remarks>
/// Два TUN-адаптера с auto_route одновременно делят таблицу маршрутов: чужой туннель
/// перехватывает в том числе наши прямые запросы, и ядро не может даже зарезолвить домен
/// собственного сервера. В логе это выглядит как "lookup ...: context deadline exceeded",
/// по которому причину не угадать.
/// </remarks>
public static class TunnelConflict
{
    /// <summary>Имя нашего адаптера - задаётся в конфиге, чтобы отличать своё от чужого.</summary>
    public const string OwnInterfaceName = "splitvpn-tun";

    private static readonly string[] Markers =
        { "sing-tun", "wintun", "wireguard", "tap-windows", "tap adapter", "openvpn" };

    public static IReadOnlyList<string> FindForeignTunnels()
    {
        var found = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.Name.Equals(OwnInterfaceName, StringComparison.OrdinalIgnoreCase)) continue;

            var haystack = $"{nic.Name} {nic.Description}".ToLowerInvariant();
            if (!Markers.Any(m => haystack.Contains(m, StringComparison.Ordinal))) continue;

            // Npcap показывает те же адаптеры второй раз - это не отдельный туннель.
            if (haystack.Contains("npcap", StringComparison.Ordinal)) continue;

            found.Add(nic.Description.Equals(nic.Name, StringComparison.OrdinalIgnoreCase)
                ? nic.Name
                : $"{nic.Name} ({nic.Description})");
        }

        return found;
    }
}
