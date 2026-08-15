using System.Diagnostics;
using System.IO;

namespace SplitVpn.Services;

public sealed record RunningApp(string Name, string? Path)
{
    public string Display => Path is null ? Name : $"{Name}   -   {Path}";
}

public static class ProcessScanner
{
    /// <summary>
    /// Список запущенных приложений для выбора в правилах. Путь доступен не всегда:
    /// у защищённых процессов MainModule закрыт даже под администратором.
    /// </summary>
    public static IReadOnlyList<RunningApp> Scan()
    {
        var found = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* доступ закрыт */ }

                var name = path is not null
                    ? Path.GetFileName(path)
                    : p.ProcessName + ".exe";

                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!found.TryGetValue(name, out var existing) || (existing.Path is null && path is not null))
                    found[name] = new RunningApp(name, path);
            }
            catch
            {
                // процесс успел умереть между перечислением и чтением - пропускаем
            }
            finally
            {
                p.Dispose();
            }
        }

        return found.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
