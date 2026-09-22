using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace SplitVpn.Services;

/// <summary>
/// Замер задержки для серверов, которых нет в конфиге ядра.
/// </summary>
/// <remarks>
/// Постоянный сайдкар поднимается только под сервер, выбранный в канале, - держать процесс
/// Xray на каждый сервер подписки нельзя. Поэтому для проверки поднимаем сайдкар на время
/// одного замера и сразу гасим: иначе XHTTP-серверы измерить было бы нечем.
/// </remarks>
public sealed class XrayLatencyProbe : IDisposable
{
    private const string ProbeUrl = "http://www.gstatic.com/generate_204";

    private readonly JobObject _job = new();

    /// <summary>Задержка в миллисекундах либо null, если сервер не ответил.</summary>
    public async Task<int?> MeasureAsync(
        string xrayPath, string configJson, TimeSpan timeout, CancellationToken ct = default)
    {
        var port = FreePort();
        string instanceConfig;

        try
        {
            instanceConfig = XrayInstanceBuilder.Build(configJson, port).Json;
        }
        catch (Exception)
        {
            return null;
        }

        var dir = Path.Combine(XrayProcessService.ConfigDir, "probe");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"probe-{port}.json");

        Process? process = null;

        try
        {
            await File.WriteAllTextAsync(path, instanceConfig, ct).ConfigureAwait(false);

            process = Process.Start(new ProcessStartInfo
            {
                FileName = xrayPath,
                Arguments = $"run -c \"{path}\"",
                WorkingDirectory = Path.GetDirectoryName(xrayPath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            });

            if (process is null) return null;

            _job.Assign(process);

            // Вывод читаем и выбрасываем: без этого буфер конвейера переполняется
            // и процесс подвисает на записи в stdout.
            _ = process.StandardOutput.ReadToEndAsync(ct);
            _ = process.StandardError.ReadToEndAsync(ct);

            if (!await WaitPortAsync(port, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false)) return null;

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                UseProxy = true
            };

            using var http = new HttpClient(handler) { Timeout = timeout };

            var sw = Stopwatch.StartNew();
            using var response = await http.GetAsync(ProbeUrl, ct).ConfigureAwait(false);
            sw.Stop();

            return response.IsSuccessStatusCode || (int)response.StatusCode == 204
                ? (int)sw.ElapsedMilliseconds
                : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
                catch (Exception)
                {
                    // процесс уже мёртв
                }

                process.Dispose();
            }

            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static async Task<bool> WaitPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(120, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose() => _job.Dispose();
}
