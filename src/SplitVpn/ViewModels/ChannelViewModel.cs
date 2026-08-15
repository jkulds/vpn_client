using System.Collections.ObjectModel;
using System.ComponentModel;
using SplitVpn.Infrastructure;
using SplitVpn.Models;

namespace SplitVpn.ViewModels;

public sealed class ChannelViewModel : NotifyBase
{
    private readonly Action _onSelectionChanged;

    public ChannelViewModel(
        Channel model,
        ObservableCollection<ProxyProfile> allProfiles,
        bool grouped,
        Action onSelectionChanged)
    {
        Model = model;
        AvailableProfiles = allProfiles;
        _onSelectionChanged = onSelectionChanged;
        ProfilesView = GroupedProfileView.Create(allProfiles, grouped);
    }

    public Channel Model { get; }
    public ObservableCollection<ProxyProfile> AvailableProfiles { get; }

    /// <summary>Сгруппированное по подпискам представление для выпадающего списка канала.</summary>
    public ICollectionView ProfilesView { get; }

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; Raise(); } }
    }

    public bool AutoFastest
    {
        get => Model.AutoFastest;
        set
        {
            if (Model.AutoFastest == value) return;
            Model.AutoFastest = value;
            Raise();
            Raise(nameof(ManualSelectionEnabled));
        }
    }

    public bool ManualSelectionEnabled => !Model.AutoFastest;

    public ProxyProfile? Selected
    {
        get => AvailableProfiles.FirstOrDefault(p => p.StableKey == Model.SelectedProfileKey);
        set
        {
            var key = value?.StableKey;
            if (Model.SelectedProfileKey == key) return;
            Model.SelectedProfileKey = key;
            Raise();
            _onSelectionChanged();
        }
    }

    public void RefreshSelection() => Raise(nameof(Selected));

    public void SetGrouping(bool grouped) => GroupedProfileView.Apply(ProfilesView, grouped);
}
