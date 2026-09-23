using System.Collections.ObjectModel;
using SplitVpn.Models;

namespace SplitVpn.Services;

/// <summary>
/// Замена серверов подписки в общем списке с сохранением выбора каналов.
/// </summary>
/// <remarks>
/// Выбор каждого канала снимается до замены и возвращается после. Без этого канал терял сервер
/// при каждом обновлении подписки: ComboBox, чей выбранный объект пропал из ItemsSource, пишет
/// в двустороннюю привязку null ещё внутри RemoveAt, а свежий объект того же сервера появляется
/// в списке только потом. Ключ выбора при этом стабилен (<see cref="ProxyProfile.StableKey"/>),
/// так что вернуть его - значит вернуть тот же сервер уже новым объектом.
/// </remarks>
public static class SubscriptionProfileUpdater
{
    /// <param name="prepare">
    /// Вызывается для каждого свежего сервера до вставки в список. Заголовок группы обязан быть
    /// проставлен здесь: CollectionView без live shaping раскладывает элемент по группам один раз,
    /// при вставке, и смену свойства потом не замечает. Сервер, вставленный с заголовком
    /// по умолчанию, висел бы в чужой группе до перезапуска приложения.
    /// </param>
    public static void Replace(
        ObservableCollection<ProxyProfile> profiles,
        IReadOnlyList<Channel> channels,
        Subscription sub,
        IReadOnlyList<ProxyProfile> fresh,
        Action<ProxyProfile> prepare,
        Action<string> log)
    {
        var oldKeys = profiles
            .Where(p => p.SubscriptionId == sub.Id)
            .Select(p => p.StableKey)
            .ToHashSet();

        foreach (var p in fresh) prepare(p);

        var savedKeys = channels.ToDictionary(ch => ch, ch => ch.SelectedProfileKey);

        var insertAt = -1;

        for (var i = profiles.Count - 1; i >= 0; i--)
        {
            if (profiles[i].SubscriptionId != sub.Id) continue;
            profiles.RemoveAt(i);
            insertAt = i;
        }

        // Свежие серверы встают на место старых, а не в конец: иначе после каждого обновления
        // группы в списке перетасовывались, а с ними и порядок outbound'ов в конфиге.
        if (insertAt < 0) insertAt = profiles.Count;
        for (var j = 0; j < fresh.Count; j++) profiles.Insert(insertAt + j, fresh[j]);

        foreach (var ch in channels)
        {
            var key = savedKeys[ch];

            // Канал смотрел не на эту подписку либо сервер в ней остался: вернуть выбор как был,
            // даже если список успел его сбросить.
            if (key is null || !oldKeys.Contains(key) || fresh.Any(p => p.StableKey == key))
            {
                ch.SelectedProfileKey = key;
                continue;
            }

            // Сервер пропал: сначала по имени (провайдер сменил адрес), иначе первый в подписке -
            // это хотя бы тот же провайдер. И то и другое пишется в журнал.
            var oldName = key.Split('|').ElementAtOrDefault(1);
            var replacement = fresh.FirstOrDefault(p => p.Name == oldName) ?? fresh.FirstOrDefault();

            ch.SelectedProfileKey = replacement?.StableKey;
            log($"[подписки] канал '{ch.Name}': сервер '{oldName}' исчез из подписки, " +
                $"переназначен на '{replacement?.Name ?? "-"}'");
        }
    }
}
