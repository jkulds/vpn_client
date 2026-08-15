using System.Collections.ObjectModel;
using SplitVpn.Infrastructure;
using SplitVpn.Models;

namespace SplitVpn.ViewModels;

public sealed class RoutingViewModel : NotifyBase
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private AppRule? _selectedAppRule;
    private GeoRule? _selectedGeoRule;

    public RoutingViewModel(AppSettings settings, IReadOnlyList<string> targets, Action onChanged)
    {
        _settings = settings;
        _onChanged = onChanged;
        AvailableTargets = targets;

        AddAppRuleCommand = new RelayCommand(() => AddAppRule("app.exe", AppMatchMode.ProcessName));
        RemoveAppRuleCommand = new RelayCommand(RemoveAppRule, () => SelectedAppRule is not null);
        MoveAppRuleUpCommand = new RelayCommand(() => Move(AppRules, SelectedAppRule, -1), () => SelectedAppRule is not null);
        MoveAppRuleDownCommand = new RelayCommand(() => Move(AppRules, SelectedAppRule, +1), () => SelectedAppRule is not null);

        AddGeoRuleCommand = new RelayCommand(() => AddGeoRule(GeoRuleKind.DomainSuffix, "example.com"));
        RemoveGeoRuleCommand = new RelayCommand(RemoveGeoRule, () => SelectedGeoRule is not null);
        MoveGeoRuleUpCommand = new RelayCommand(() => Move(GeoRules, SelectedGeoRule, -1), () => SelectedGeoRule is not null);
        MoveGeoRuleDownCommand = new RelayCommand(() => Move(GeoRules, SelectedGeoRule, +1), () => SelectedGeoRule is not null);
    }

    public ObservableCollection<AppRule> AppRules => _settings.AppRules;
    public ObservableCollection<GeoRule> GeoRules => _settings.GeoRules;
    public IReadOnlyList<string> AvailableTargets { get; }

    public AppMatchMode[] MatchModes { get; } = Enum.GetValues<AppMatchMode>();
    public GeoRuleKind[] GeoKinds { get; } = Enum.GetValues<GeoRuleKind>();

    public AppRule? SelectedAppRule { get => _selectedAppRule; set => Set(ref _selectedAppRule, value); }
    public GeoRule? SelectedGeoRule { get => _selectedGeoRule; set => Set(ref _selectedGeoRule, value); }

    public RelayCommand AddAppRuleCommand { get; }
    public RelayCommand RemoveAppRuleCommand { get; }
    public RelayCommand MoveAppRuleUpCommand { get; }
    public RelayCommand MoveAppRuleDownCommand { get; }
    public RelayCommand AddGeoRuleCommand { get; }
    public RelayCommand RemoveGeoRuleCommand { get; }
    public RelayCommand MoveGeoRuleUpCommand { get; }
    public RelayCommand MoveGeoRuleDownCommand { get; }

    public AppRule AddAppRule(string value, AppMatchMode mode, string? note = null)
    {
        var defaultTarget = AvailableTargets.FirstOrDefault(t => t != RouteTargets.Direct && t != RouteTargets.Block)
                            ?? RouteTargets.Direct;

        var rule = new AppRule { Value = value, Mode = mode, Target = defaultTarget, Note = note };
        AppRules.Add(rule);
        SelectedAppRule = rule;
        _onChanged();
        return rule;
    }

    public void AddGeoRule(GeoRuleKind kind, string value)
    {
        var rule = new GeoRule { Kind = kind, Value = value, Target = RouteTargets.Direct };
        GeoRules.Add(rule);
        SelectedGeoRule = rule;
        _onChanged();
    }

    public void NotifyChanged() => _onChanged();

    private void RemoveAppRule()
    {
        if (SelectedAppRule is null) return;
        AppRules.Remove(SelectedAppRule);
        SelectedAppRule = null;
        _onChanged();
    }

    private void RemoveGeoRule()
    {
        if (SelectedGeoRule is null) return;
        GeoRules.Remove(SelectedGeoRule);
        SelectedGeoRule = null;
        _onChanged();
    }

    /// <summary>Порядок значим: в sing-box выигрывает первое совпавшее правило.</summary>
    private void Move<T>(ObservableCollection<T> list, T? item, int delta)
    {
        if (item is null) return;
        var index = list.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count) return;

        list.Move(index, target);
        _onChanged();
    }
}
