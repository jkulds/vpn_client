using SplitVpn.Infrastructure;

namespace SplitVpn.Models;

public sealed class Subscription : NotifyBase
{
    private string _name = "";
    private string _url = "";
    private DateTimeOffset? _lastUpdated;
    private string? _lastError;
    private int _profileCount;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// User-Agent, на который этот провайдер ответил успешно. Запоминается после первой удачи,
    /// чтобы не перебирать варианты заново: часть панелей считает каждый запрос устройством
    /// и упирается в лимит подключений.
    /// </summary>
    public string? UserAgent { get; set; }

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        set { if (Set(ref _lastUpdated, value)) Raise(nameof(Status)); }
    }

    public string? LastError
    {
        get => _lastError;
        set { if (Set(ref _lastError, value)) Raise(nameof(Status)); }
    }

    public int ProfileCount
    {
        get => _profileCount;
        set { if (Set(ref _profileCount, value)) Raise(nameof(Status)); }
    }

    public string Status => LastError is { Length: > 0 }
        ? $"ошибка: {LastError}"
        : LastUpdated is null
            ? "ещё не обновлялась"
            : $"{ProfileCount} серв. · {LastUpdated:dd.MM HH:mm}";
}
