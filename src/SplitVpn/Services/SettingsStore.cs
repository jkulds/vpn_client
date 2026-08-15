using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SplitVpn.Models;

namespace SplitVpn.Services;

public static class SettingsStore
{
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SplitVpn");

    public static string SettingsPath => Path.Combine(RootDir, "settings.json");

    /// <summary>Рабочая папка ядра: сюда пишутся config.json, cache.db и скачанные rule-set'ы.</summary>
    public static string CoreDir => Path.Combine(RootDir, "core");

    public static string ConfigPath => Path.Combine(CoreDir, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        Directory.CreateDirectory(CoreDir);

        if (!File.Exists(SettingsPath))
            return AppSettings.CreateDefault();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? AppSettings.CreateDefault();
        }
        catch (Exception)
        {
            var backup = SettingsPath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(SettingsPath, backup); } catch { /* уже недоступен - переживём */ }
            return AppSettings.CreateDefault();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(CoreDir);
        var json = JsonSerializer.Serialize(settings, Options);

        // Пишем через временный файл: обрыв на середине не должен оставить битый settings.json.
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}
