using System.Text.Json;

namespace SplitVpn.Services;

/// <summary>
/// Адреса серверов внутри конфига Xray.
/// </summary>
/// <remarks>
/// Нужны, чтобы вывести собственные соединения сайдкара из туннеля. Одного правила по
/// process_path мало: Xray ещё и резолвит домен своего сервера сам, а этот запрос TUN
/// перехватывает и уводит в DNS канала - то есть обратно в Xray. Получается замкнутый круг,
/// в котором соединения молча рвутся.
/// </remarks>
public static class XrayEndpoints
{
    public static IReadOnlyList<string> Extract(string xrayConfigJson)
    {
        var result = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(xrayConfigJson);
            Walk(doc.RootElement, result);
        }
        catch (JsonException)
        {
            // конфиг мы сами и собирали - сюда попасть неоткуда
        }

        return result
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Walk(JsonElement element, List<string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    // Адрес сервера в схеме Xray лежит под "address" - и в vnext, и в servers.
                    if (p.NameEquals("address") && p.Value.ValueKind == JsonValueKind.String)
                        found.Add(p.Value.GetString()!);
                    else
                        Walk(p.Value, found);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Walk(item, found);
                break;
        }
    }
}
