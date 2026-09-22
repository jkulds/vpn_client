using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SplitVpn.Services;

/// <summary>
/// Тонкая обёртка над clash_api ядра. Нужна ровно для одного: переключить сервер внутри канала
/// без перезапуска sing-box (иначе рвутся все соединения и пересоздаётся TUN).
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
            var url = $"http://127.0.0.1:{Port}/proxies/{Uri.EscapeDataString(channelName)}";
            using var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent($"{{\"name\":\"{Escape(profileTag)}\"}}", Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrEmpty(Secret))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrEmpty(Secret))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret);

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

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
