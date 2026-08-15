using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SplitVpn.Services;

public sealed partial class CoreProcessService : IDisposable
{
    private readonly JobObject _job = new();
    private Process? _process;

    public event Action<string>? Log;
    public event Action<bool>? RunningChanged;

    public bool IsRunning => _process is { HasExited: false };

    public static string ResolveCorePath(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;

        var baseDir = AppContext.BaseDirectory;
        var local = Path.Combine(baseDir, configured);
        if (File.Exists(local)) return local;

        return configured;
    }

    public async Task<string?> GetVersionAsync(string corePath)
    {
        var (code, output) = await RunOnceAsync(corePath, "version", workDir: null).ConfigureAwait(false);
        return code == 0 ? output.Trim() : null;
    }

    /// <summary>
    /// true -> синтаксис 1.12+ (типизированные DNS-серверы, route action).
    /// При нераспознанной версии возвращает true: новых сборок в обращении больше.
    /// </summary>
    public async Task<bool> DetectModernSyntaxAsync(string corePath)
    {
        var version = await GetVersionAsync(corePath).ConfigureAwait(false);
        if (version is null) return true;

        var m = VersionRegex().Match(version);
        if (!m.Success) return true;

        var major = int.Parse(m.Groups[1].Value);
        var minor = int.Parse(m.Groups[2].Value);
        return major > 1 || (major == 1 && minor >= 12);
    }

    public async Task<(bool ok, string output)> CheckConfigAsync(string corePath, string configPath, string workDir)
    {
        var (code, output) = await RunOnceAsync(
            corePath, $"check -c \"{configPath}\" -D \"{workDir}\"", workDir).ConfigureAwait(false);
        return (code == 0, output.Trim());
    }

    public void Start(string corePath, string configPath, string workDir)
    {
        if (IsRunning) throw new InvalidOperationException("Ядро уже запущено.");

        var psi = new ProcessStartInfo
        {
            FileName = corePath,
            Arguments = $"run -c \"{configPath}\" -D \"{workDir}\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log?.Invoke(e.Data); };
        process.Exited += (_, _) =>
        {
            Log?.Invoke($"[ядро] процесс завершился, код {SafeExitCode(process)}");
            RunningChanged?.Invoke(false);
        };

        process.Start();
        _job.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _process = process;
        RunningChanged?.Invoke(true);
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[ядро] ошибка при остановке: {ex.Message}");
        }
        finally
        {
            process.Dispose();
            RunningChanged?.Invoke(false);
        }
    }

    private static async Task<(int code, string output)> RunOnceAsync(string exe, string args, string? workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (workDir is not null) psi.WorkingDirectory = workDir;

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (-1, "не удалось запустить процесс");

            var stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);

            return (p.ExitCode, string.Concat(stdout, stderr));
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); }
        catch { return "?"; }
    }

    public void Dispose()
    {
        Stop();
        _job.Dispose();
    }

    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRegex();
}
