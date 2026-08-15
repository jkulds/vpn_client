using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace SplitVpn.Services;

/// <summary>
/// Тонкая обёртка над clash_api ядра. Нужна ровно для одного: переключить сервер внутри канала
/// без перезапуска sing-box (иначе рвутся все соединения и пересоздаётся TUN).
/// </summary>
public sealed class ClashApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

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

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
