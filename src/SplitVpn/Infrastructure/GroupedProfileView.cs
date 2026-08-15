using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SplitVpn.Models;

namespace SplitVpn.Infrastructure;

public static class GroupedProfileView
{
    /// <summary>
    /// Отдельное представление на каждого потребителя: <see cref="CollectionViewSource.GetDefaultView"/>
    /// вернул бы общий объект, и списки начали бы делить CurrentItem между собой.
    /// </summary>
    public static ICollectionView Create(ObservableCollection<ProxyProfile> source, bool grouped)
    {
        var view = new CollectionViewSource { Source = source }.View;
        Apply(view, grouped);
        return view;
    }

    public static void Apply(ICollectionView view, bool grouped)
    {
        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();

            if (grouped)
                view.GroupDescriptions.Add(
                    new PropertyGroupDescription(nameof(ProxyProfile.SubscriptionName)));
        }
    }
}
