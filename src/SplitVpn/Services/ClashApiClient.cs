using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SplitVpn.Services;

/// <summary>
/// Тонкая обёртка над clash_api ядра: переключить сервер внутри канала без перезапуска sing-box
/// (иначе рвутся все соединения и пересоздаётся TUN), проверить, что ядро выбрало на самом деле,
/// и замерить задержку сквозь туннель.
/// </summary>
public sealed class ClashApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public int Port { get; set; }
    public string? Secret { get; set; }

    public async Task<bool> SelectAsync(string channelName, string profileTag, CancellationToken ct = default)
    {
        try
        {
            using var request = Authorized(HttpMethod.Put, $"http://127.0.0.1:{Port}/proxies/{Uri.EscapeDataString(channelName)}");
            request.Content = new StringContent($"{{\"name\":\"{Escape(profileTag)}\"}}", Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Текущий сервер selector-канала по данным самого ядра (поле <c>now</c>).
    /// null - канал не найден либо API не ответило.
    /// </summary>
    /// <remarks>
    /// Нужно, потому что default в конфиге - не гарантия: с включённым cache_file ядро
    /// восстанавливает прошлый выбор selector'а из cache.db поверх default.
    /// </remarks>
    public async Task<string?> GetSelectedAsync(string channelName, CancellationToken ct = default)
    {
        try
        {
            using var request = Authorized(HttpMethod.Get, $"http://127.0.0.1:{Port}/proxies/{Uri.EscapeDataString(channelName)}");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.TryGetProperty("now", out var now) && now.ValueKind == JsonValueKind.String
                ? now.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ждёт, пока clash_api начнёт отвечать: сразу после старта процесса порт ещё закрыт,
    /// и первый же запрос ушёл бы в пустоту. false - не дождались либо отменено.
    /// </summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var request = Authorized(HttpMethod.Get, $"http://127.0.0.1:{Port}/version");
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                // порт ещё не слушает - обычное дело первые сотни миллисекунд
            }

            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        return false;
    }

    /// <summary>
    /// Замер задержки через конкретный outbound силами ядра: оно делает HTTP-запрос
    /// к probe-адресу сквозь туннель. В отличие от ICMP это проверяет всю цепочку -
    /// что сервер жив, ключ принят и трафик действительно ходит.
    /// </summary>
    /// <returns>Задержка в миллисекундах либо null, если outbound не ответил.</returns>
    public async Task<int?> DelayAsync(string tag, CancellationToken ct = default, int timeoutMs = 5000)
    {
        try
        {
            var url = $"http://127.0.0.1:{Port}/proxies/{Uri.EscapeDataString(tag)}/delay" +
                      $"?url={Uri.EscapeDataString("http://www.gstatic.com/generate_204")}&timeout={timeoutMs}";

            using var request = Authorized(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.TryGetProperty("delay", out var d) && d.TryGetInt32(out var ms) ? ms : null;
        }
        catch
        {
            return null;
        }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrEmpty(Secret))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret);

        return request;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
