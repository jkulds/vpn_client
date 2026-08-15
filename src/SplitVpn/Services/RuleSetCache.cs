using System.IO;
using System.Net;
using System.Net.Http;

namespace SplitVpn.Services;

public sealed record RuleSetFile(string Tag, string Url, bool Available, long Size, DateTime? Updated, string? Error);

/// <summary>
/// Локальная папка с .srs-наборами.
/// </summary>
/// <remarks>
/// Раньше наборы объявлялись как remote и качались самим ядром при старте. Это делало запуск
/// зависимым от сети: если канал не поднялся, ядро падало целиком с невнятным
/// "initialize rule-set ... EOF". Теперь файлы качает приложение, а конфиг ссылается на них
/// как на local - старт офлайновый и не падает.
/// </remarks>
public sealed class RuleSetCache
{
    public const string RelativeDir = "rulesets";

    public static string Dir => Path.Combine(SettingsStore.CoreDir, RelativeDir);

    public static string PathFor(string tag) => Path.Combine(Dir, tag + ".srs");

    /// <summary>Путь внутри конфига - относительно рабочей папки ядра (ключ -D).</summary>
    public static string ConfigPathFor(string tag) => $"{RelativeDir}/{tag}.srs";

    public static bool Exists(string tag) => File.Exists(PathFor(tag));

    /// <summary>
    /// Докачивает недостающие и устаревшие наборы. Существующий файл при ошибке сети сохраняется:
    /// старый набор лучше, чем отсутствующее правило.
    /// </summary>
    public async Task<IReadOnlyList<RuleSetFile>> EnsureAsync(
        IEnumerable<(string tag, string url)> wanted,
        TimeSpan maxAge,
        Uri? proxy = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Dir);

        using var http = CreateClient(proxy);
        var result = new List<RuleSetFile>();

        foreach (var (tag, url) in wanted.DistinctBy(x => x.tag))
        {
            var path = PathFor(tag);
            var info = new FileInfo(path);
            var fresh = info.Exists && info.Length > 0 && DateTime.Now - info.LastWriteTime < maxAge;

            if (fresh)
            {
                result.Add(new RuleSetFile(tag, url, true, info.Length, info.LastWriteTime, null));
                continue;
            }

            try
            {
                var bytes = await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                if (bytes.Length == 0) throw new InvalidOperationException("пустой ответ");

                // Через временный файл: обрыв на середине не должен оставить битый .srs.
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);

                result.Add(new RuleSetFile(tag, url, true, bytes.Length, DateTime.Now, null));
            }
            catch (Exception ex)
            {
                info.Refresh();
                var keptOld = info.Exists && info.Length > 0;

                result.Add(new RuleSetFile(
                    tag, url, keptOld, keptOld ? info.Length : 0,
                    keptOld ? info.LastWriteTime : null, ex.Message));
            }
        }

        return result;
    }

    private static HttpClient CreateClient(Uri? proxy)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy(proxy)
        };

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SplitVpn/1.0");
        return http;
    }
}
