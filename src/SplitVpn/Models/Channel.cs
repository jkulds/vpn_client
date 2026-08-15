using SplitVpn.Infrastructure;

namespace SplitVpn.Models;

/// <summary>
/// Слот маршрутизации. Правила ссылаются на канал по имени, а не на конкретный сервер,
/// поэтому обновление подписки не рвёт настроенную маршрутизацию.
/// В конфиге разворачивается в outbound типа selector либо urltest.
/// </summary>
public sealed class Channel : NotifyBase
{
    private string _name = "";
    private string? _selectedProfileKey;
    private bool _autoFastest;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Становится tag'ом outbound'а, поэтому обязан быть уникальным.</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    public string? SelectedProfileKey { get => _selectedProfileKey; set => Set(ref _selectedProfileKey, value); }

    /// <summary>urltest вместо selector: ядро само выбирает быстрейший сервер из членов канала.</summary>
    public bool AutoFastest { get => _autoFastest; set => Set(ref _autoFastest, value); }

    /// <summary>Ограничение членов канала одной подпиской. Пусто -> все профили.</summary>
    public string? RestrictToSubscriptionId { get; set; }
}
