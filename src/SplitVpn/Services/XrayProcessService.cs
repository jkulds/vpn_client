using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SplitVpn.Services;

public sealed record XrayInstance(string ProfileId, string ProfileName, int SocksPort, string ConfigPath);

/// <summary>
/// Сайдкары Xray для серверов, которые sing-box не тянет (XHTTP, балансировщики провайдера).
/// Каждый экземпляр слушает свой локальный SOCKS-порт, куда ходит sing-box.
/// </summary>
public sealed class XrayProcessService : IDisposable
{
    private readonly JobObject _job = new();
    private readonly List<(Process process, XrayInstance instance)> _running = new();

    public event Action<string>? Log;

    public IReadOnlyList<XrayInstance> Running => _running.Select(x => x.instance).ToList();

    public static string ConfigDir => Path.Combine(SettingsStore.CoreDir, "xray");

    /// <summary>
    /// Ищет xray.exe: сначала по указанному пути, затем рядом с приложением. Если не нашли -
    /// пробуем комплект Happ и сообщаем об этом, чтобы подмена чужого бинарника не была молчаливой.
    /// </summary>
    public static string? Resolve(string configured, Action<string>? log = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;

            var local = Path.Combine(AppContext.BaseDirectory, configured);
            if (File.Exists(local)) return local;
        }

        var happ = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "FlyFrogLLC", "Happ", "core", "xray.exe");

        if (File.Exists(happ))
        {
            log?.Invoke($"[xray] не найден по пути '{configured}', беру из комплекта Happ: {happ}");
            return happ;
        }

        return null;
    }

    public async Task<XrayInstance?> StartAsync(
        string xrayPath, string profileId, string profileName, string providerConfigJson)
    {
        var port = FreeLoopbackPort();

        XrayInstanceConfig built;
        try
        {
            built = XrayInstanceBuilder.Build(providerConfigJson, port);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[xray] '{profileName}': конфиг не собран - {ex.Message}");
            return null;
        }

        Directory.CreateDirectory(ConfigDir);
        var configPath = Path.Combine(ConfigDir, Sanitize(profileId) + ".json");
        await File.WriteAllTextAsync(configPath, built.Json).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = xrayPath,
            Arguments = $"run -c \"{configPath}\"",
            WorkingDirectory = Path.GetDirectoryName(xrayPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke($"[xray:{profileName}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke($"[xray:{profileName}] {e.Data}"); };

        try
        {
            process.Start();
            _job.Assign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[xray] '{profileName}': не запустился - {ex.Message}");
            process.Dispose();
            return null;
        }

        var instance = new XrayInstance(profileId, profileName, port, configPath);
        _running.Add((process, instance));

        var mode = built.Balanced ? $"балансировщик {built.PrimaryTarget}" : $"outbound {built.PrimaryTarget}";
        Log?.Invoke($"[xray] '{profileName}' -> 127.0.0.1:{port} ({mode})");

        // Порт открывается не мгновенно: без ожидания sing-box стартует в пустоту.
        if (!await WaitForPortAsync(port, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            Log?.Invoke($"[!] xray '{profileName}': порт {port} не открылся за 5 с, соединения будут падать");

        return instance;
    }

    public void StopAll()
    {
        foreach (var (process, instance) in _running)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[xray] '{instance.ProfileName}': ошибка при остановке - {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        if (_running.Count > 0) Log?.Invoke($"[xray] остановлено экземпляров: {_running.Count}");
        _running.Clear();
    }

    private static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(150).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    public void Dispose()
    {
        StopAll();
        _job.Dispose();
    }
}
