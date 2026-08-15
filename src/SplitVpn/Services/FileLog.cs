using System.IO;
using System.Text;

namespace SplitVpn.Services;

/// <summary>
/// Журнал на диск. Нужен потому, что интересное (диагностика подписок, отказ ядра принять конфиг)
/// случается до того, как пользователь успевает посмотреть в окно.
/// </summary>
public static class FileLog
{
    private const int RetentionDays = 7;

    private static readonly object Gate = new();
    private static bool _cleaned;

    public static string Dir => Path.Combine(SettingsStore.RootDir, "logs");

    public static string CurrentPath => Path.Combine(Dir, $"splitvpn-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Write(string line) => WriteBatch(new[] { line });

    /// <summary>
    /// Пачкой, а не построчно: ядро выдаёт десятки строк в секунду, и открытие файла
    /// на каждую из них само по себе становится заметной нагрузкой.
    /// </summary>
    public static void WriteBatch(IReadOnlyCollection<string> lines)
    {
        if (lines.Count == 0) return;

        try
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss.fff");

            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                if (!_cleaned) { CleanOld(); _cleaned = true; }

                File.AppendAllLines(CurrentPath, lines.Select(l => $"{stamp} {l}"), Encoding.UTF8);
            }
        }
        catch
        {
            // журнал не должен ронять приложение
        }
    }

    private static void CleanOld()
    {
        var cutoff = DateTime.Now.AddDays(-RetentionDays);

        foreach (var f in Directory.EnumerateFiles(Dir, "splitvpn-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
            catch
            {
                // занят или уже удалён
            }
        }
    }
}
