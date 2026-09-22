using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using SplitVpn.Infrastructure;
using SplitVpn.Models;
using SplitVpn.Services;

namespace SplitVpn.ViewModels;

public sealed partial class MainViewModel : NotifyBase, IGroupExpansionStore, IDisposable
{
    private const int MaxLogLines = 600;

    private readonly SubscriptionService _subscriptions = new();
    private readonly CoreProcessService _core = new();
    private readonly ClashApiClient _clash = new();
    private readonly RuleSetCache _ruleSets = new();
    private readonly XrayProcessService _xray = new();
    private IReadOnlyDictionary<string, string> _tagByProfileId = new Dictionary<string, string>();
    private readonly Queue<string> _logLines = new();
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private readonly DispatcherTimer _logTimer;
    private readonly CancellationTokenSource _shutdown = new();

    private bool _isRunning;
    private string _status = "Отключено";
    private string _logText = "";
    private Subscription? _selectedSubscription;
    private ProxyProfile? _selectedProfile;
    private string _coreVersion = "ядро не проверено";

    public MainViewModel()
    {
        Settings = SettingsStore.Load();
        RefreshProfileGroupNames();

        ProfilesView = GroupedProfileView.Create(Settings.Profiles, Settings.GroupProfilesBySubscription);

        Settings.PropertyChanged += OnSettingsPropertyChanged;

        foreach (var ch in Settings.Channels)
            Channels.Add(new ChannelViewModel(
                ch, Settings.Profiles, Settings.GroupProfilesBySubscription, OnChannelSelectionChanged));

        _core.Log += OnCoreLog;
        _xray.Log += AppendLog;
        _core.RunningChanged += running => Dispatch(() =>
        {
            IsRunning = running;
            Status = running ? "Подключено" : "Отключено";
        });

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsRunning);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsRunning);
        CheckConfigCommand = new AsyncRelayCommand(CheckConfigAsync);
        ImportLinksCommand = new RelayCommand(_ => ImportLinksRequested?.Invoke());
        AddSubscriptionCommand = new RelayCommand(_ => AddSubscriptionRequested?.Invoke());
        UpdateAllSubscriptionsCommand = new AsyncRelayCommand(() => UpdateSubscriptionsAsync(force: true));
        UpdateSelectedSubscriptionCommand = new AsyncRelayCommand(
            UpdateSelectedAsync, () => SelectedSubscription is not null);
        RemoveSubscriptionCommand = new RelayCommand(RemoveSubscription, () => SelectedSubscription is not null);
        RemoveProfileCommand = new RelayCommand(RemoveProfile, () => SelectedProfile is not null);
        AddChannelCommand = new RelayCommand(AddChannel);
        RemoveChannelCommand = new RelayCommand(RemoveChannel);
        OpenRoutingCommand = new RelayCommand(_ => RoutingRequested?.Invoke());
        SaveCommand = new RelayCommand(Save);
        OpenConfigFolderCommand = new RelayCommand(() => OpenFolder(SettingsStore.RootDir));
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(FileLog.Dir));
        CheckAllServersCommand = new AsyncRelayCommand(CheckAllServersAsync, () => IsRunning);
        ImportFromHappCommand = new RelayCommand(ImportFromHapp, () => HappImporter.IsInstalled);

        _logTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();

        _ = InitializeAsync();
    }

    public AppSettings Settings { get; }
    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    /// <summary>Список серверов с группировкой по подпискам.</summary>
    public ICollectionView ProfilesView { get; }

    /// <summary>Хранилище свёрнутости групп для привязки в шаблоне GroupItem.</summary>
    public IGroupExpansionStore GroupExpansion => this;

    bool IGroupExpansionStore.IsExpanded(string key) => !Settings.CollapsedGroups.Contains(key);

    void IGroupExpansionStore.SetExpanded(string key, bool expanded)
    {
        if (expanded)
        {
            if (!Settings.CollapsedGroups.Remove(key)) return;
        }
        else
        {
            if (Settings.CollapsedGroups.Contains(key)) return;
            Settings.CollapsedGroups.Add(key);
        }

        Save();
    }

    public event Action? ImportLinksRequested;
    public event Action? AddSubscriptionRequested;
    public event Action? RoutingRequested;

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand CheckConfigCommand { get; }
    public RelayCommand ImportLinksCommand { get; }
    public RelayCommand AddSubscriptionCommand { get; }
    public AsyncRelayCommand UpdateAllSubscriptionsCommand { get; }
    public AsyncRelayCommand UpdateSelectedSubscriptionCommand { get; }
    public RelayCommand RemoveSubscriptionCommand { get; }
    public RelayCommand RemoveProfileCommand { get; }
    public RelayCommand AddChannelCommand { get; }
    public RelayCommand RemoveChannelCommand { get; }
    public RelayCommand OpenRoutingCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public AsyncRelayCommand CheckAllServersCommand { get; }
    public RelayCommand ImportFromHappCommand { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;

            Raise(nameof(ConnectButtonText));

            // Состояние приходит из фонового события ядра, а CommandManager переспрашивает
            // CanExecute только по вводу пользователя - без этого кнопки остаются серыми,
            // пока не шевельнёшь мышью.
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ConnectButtonText => IsRunning ? "Отключить" : "Подключить";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string CoreVersion { get => _coreVersion; private set => Set(ref _coreVersion, value); }
    public string LogText { get => _logText; private set => Set(ref _logText, value); }

    public Subscription? SelectedSubscription
    {
        get => _selectedSubscription;
        set => Set(ref _selectedSubscription, value);
    }

    public ProxyProfile? SelectedProfile
    {
        get => _selectedProfile;
        set => Set(ref _selectedProfile, value);
    }

    /// <summary>Уровни журнала sing-box, от самого тихого к самому подробному.</summary>
    public string[] CoreLogLevels { get; } = { "error", "warn", "info", "debug", "trace" };

    /// <summary>Каналы + «Напрямую» + «Блокировать» - варианты для колонки Target в правилах.</summary>
    public IReadOnlyList<string> RouteTargetOptions =>
        Channels.Select(c => c.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Concat(new[] { RouteTargets.Direct, RouteTargets.Block })
            .ToList();

    // ---------- инициализация ----------

    private async Task InitializeAsync()
    {
        var corePath = CoreProcessService.ResolveCorePath(Settings.CorePath);
        var version = await _core.GetVersionAsync(corePath);

        CoreVersion = version is null
            ? $"sing-box не найден по пути '{Settings.CorePath}'"
            : version.Split('\n').FirstOrDefault()?.Trim() ?? version;

        if (version is null)
            AppendLog($"[!] Не найден sing-box.exe. Укажите путь в поле «Ядро» или положите файл рядом с {AppContext.BaseDirectory}");

        _ = RunAutoUpdateLoopAsync(_shutdown.Token);
        _ = RunChannelCheckLoopAsync(_shutdown.Token);
    }

    private async Task RunAutoUpdateLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);

                var due = Settings.Subscriptions.Where(s =>
                    s.AutoUpdate &&
                    (s.LastUpdated is null ||
                     DateTimeOffset.Now - s.LastUpdated > TimeSpan.FromHours(Settings.SubscriptionUpdateHours)))
                    .ToList();

                if (due.Count == 0) continue;

                await UpdateSubscriptionsAsync(force: false, only: due).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // штатное завершение
        }
    }

    // ---------- подписки и профили ----------

    public void AddSubscription(string name, string url)
    {
        var sub = new Subscription { Name = name, Url = url };
        Settings.Subscriptions.Add(sub);
        SelectedSubscription = sub;
        Save();
        _ = UpdateSubscriptionsAsync(force: true, only: new[] { sub });
    }

    public void ImportLinks(string text)
    {
        if (LooksLikeSubscriptionUrl(text, out var url))
        {
            var answer = MessageBox.Show(
                "Это похоже на ссылку подписки, а не на ссылку сервера.\nДобавить её как подписку?",
                "Импорт", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
            {
                AddSubscription(new Uri(url).Host, url);
                return;
            }
        }

        var parsed = ShareLinkParser.ParseMany(text, out var errors);

        var added = 0;
        foreach (var p in parsed)
        {
            if (Settings.Profiles.Any(x => x.StableKey == p.StableKey)) continue;
            Settings.Profiles.Add(p);
            added++;
        }

        foreach (var e in errors) AppendLog($"[импорт] {e}");
        AppendLog($"[импорт] добавлено серверов: {added} из {parsed.Count} распознанных");

        RefreshChannels();

        foreach (var p in parsed.Where(x => !CoreCapabilities.IsSupported(x, out _)))
        {
            CoreCapabilities.IsSupported(p, out var reason);
            AppendLog($"[!] Сервер '{p.Name}' ядро не потянет: {reason}. Выбирать его в канале нельзя.");
        }

        Save();
    }

    private static bool LooksLikeSubscriptionUrl(string text, out string url)
    {
        url = text.Trim();

        var singleLine = !url.Contains('\n') && !url.Contains('\r');

        return singleLine
               && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
               && Uri.TryCreate(url, UriKind.Absolute, out _);
    }

    private async Task UpdateSelectedAsync()
    {
        if (SelectedSubscription is null) return;
        await UpdateSubscriptionsAsync(force: true, only: new[] { SelectedSubscription });
    }

    private async Task UpdateSubscriptionsAsync(bool force, IReadOnlyCollection<Subscription>? only = null)
    {
        var targets = (only ?? Settings.Subscriptions).ToList();
        if (targets.Count == 0)
        {
            if (force) AppendLog("[подписки] нет ни одной подписки");
            return;
        }

        var changed = false;

        foreach (var sub in targets)
        {
            AppendLog($"[подписки] обновляю '{sub.Name}'...");
            var result = await _subscriptions.FetchAsync(sub, _shutdown.Token).ConfigureAwait(false);

            Dispatch(() =>
            {
                if (result.Diagnostics.Length > 0)
                    AppendLog($"[подписки] {sub.Name}: {result.Diagnostics}; формат={result.Format}");

                foreach (var e in result.Errors) AppendLog($"[подписки] {sub.Name}: {e}");

                if (!result.Success)
                {
                    sub.LastError = result.FatalError ?? "неизвестная ошибка";
                    AppendLog($"[подписки] '{sub.Name}' -> {sub.LastError}");
                    return;
                }

                ReplaceProfiles(sub, result.Profiles);
                sub.LastError = null;
                sub.LastUpdated = DateTimeOffset.Now;
                sub.ProfileCount = result.Profiles.Count;
                changed = true;
                AppendLog($"[подписки] '{sub.Name}' -> {result.Profiles.Count} серверов");
            });
        }

        Dispatch(() =>
        {
            RefreshChannels();
            Save();
        });

        if (changed && IsRunning && Settings.RestartAfterSubscriptionUpdate)
        {
            AppendLog("[подписки] состав серверов изменился, перезапускаю ядро");
            await Task.Run(() => { _core.Stop(); _xray.StopAll(); }).ConfigureAwait(false);
            await ConnectAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Заменяет серверы подписки, сохраняя привязки каналов: выбор переносится по
    /// <see cref="ProxyProfile.StableKey"/>, а если сервер исчез - по имени.
    /// </summary>
    private void ReplaceProfiles(Subscription sub, List<ProxyProfile> fresh)
    {
        var oldKeys = Settings.Profiles
            .Where(p => p.SubscriptionId == sub.Id)
            .Select(p => p.StableKey)
            .ToHashSet();

        for (var i = Settings.Profiles.Count - 1; i >= 0; i--)
            if (Settings.Profiles[i].SubscriptionId == sub.Id)
                Settings.Profiles.RemoveAt(i);

        foreach (var p in fresh) Settings.Profiles.Add(p);

        foreach (var ch in Settings.Channels)
        {
            if (ch.SelectedProfileKey is null || !oldKeys.Contains(ch.SelectedProfileKey)) continue;
            if (fresh.Any(p => p.StableKey == ch.SelectedProfileKey)) continue;

            var oldName = ch.SelectedProfileKey.Split('|').ElementAtOrDefault(1);
            var replacement = fresh.FirstOrDefault(p => p.Name == oldName) ?? fresh.FirstOrDefault();

            ch.SelectedProfileKey = replacement?.StableKey;
            AppendLog($"[подписки] канал '{ch.Name}': сервер переназначен на '{replacement?.Name ?? "-"}'");
        }
    }

    private void RemoveSubscription()
    {
        var sub = SelectedSubscription;
        if (sub is null) return;

        for (var i = Settings.Profiles.Count - 1; i >= 0; i--)
            if (Settings.Profiles[i].SubscriptionId == sub.Id)
                Settings.Profiles.RemoveAt(i);

        Settings.Subscriptions.Remove(sub);
        SelectedSubscription = null;
        RefreshChannels();
        Save();
    }

    private void RemoveProfile()
    {
        if (SelectedProfile is null) return;
        Settings.Profiles.Remove(SelectedProfile);
        SelectedProfile = null;
        RefreshChannels();
        Save();
    }

    // ---------- каналы ----------

    private void AddChannel()
    {
        var name = NextChannelName();
        var model = new Channel { Name = name };
        Settings.Channels.Add(model);
        Channels.Add(new ChannelViewModel(
            model, Settings.Profiles, Settings.GroupProfilesBySubscription, OnChannelSelectionChanged));
        Raise(nameof(RouteTargetOptions));
        Save();
    }

    private void RemoveChannel(object? parameter)
    {
        if (parameter is not ChannelViewModel vm) return;

        var used = Settings.AppRules.Any(r => r.Target == vm.Name) ||
                   Settings.GeoRules.Any(r => r.Target == vm.Name);

        if (used)
        {
            var answer = MessageBox.Show(
                $"На канал '{vm.Name}' ссылаются правила маршрутизации.\nОни переключатся на прямое соединение. Удалить?",
                "Удаление канала", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            foreach (var r in Settings.AppRules.Where(r => r.Target == vm.Name)) r.Target = RouteTargets.Direct;
            foreach (var r in Settings.GeoRules.Where(r => r.Target == vm.Name)) r.Target = RouteTargets.Direct;
        }

        Settings.Channels.Remove(vm.Model);
        Channels.Remove(vm);

        if (Settings.DefaultChannelName == vm.Name)
            Settings.DefaultChannelName = Channels.FirstOrDefault()?.Name;

        Raise(nameof(RouteTargetOptions));
        Save();
    }

    private string NextChannelName()
    {
        for (var c = 'A'; c <= 'Z'; c++)
        {
            var candidate = $"VPN-{c}";
            if (Settings.Channels.All(x => !string.Equals(x.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return $"VPN-{Guid.NewGuid().ToString("N")[..4]}";
    }

    private void RefreshChannels()
    {
        RefreshProfileGroupNames();
        foreach (var ch in Channels) ch.RefreshSelection();
        Raise(nameof(RouteTargetOptions));
    }

    /// <summary>Заголовки групп и пометки о поддержке живут только в памяти.</summary>
    private void RefreshProfileGroupNames()
    {
        foreach (var p in Settings.Profiles)
        {
            var sub = p.SubscriptionId is null
                ? null
                : Settings.Subscriptions.FirstOrDefault(s => s.Id == p.SubscriptionId);

            p.SubscriptionName = sub?.Name ?? ProxyProfile.ManualGroup;

            // Порядок важен: признак сайдкара влияет на вывод о поддержке.
            p.NeedsSynthesizedXray =
                !p.RequiresXray &&
                !string.IsNullOrWhiteSpace(p.SourceLink) &&
                CoreCapabilities.IsXrayOnlyTransport(p.Network);

            CoreCapabilities.IsSupported(p, out var reason);
            p.UnsupportedReason = reason;
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.GroupProfilesBySubscription)) return;

        var grouped = Settings.GroupProfilesBySubscription;
        GroupedProfileView.Apply(ProfilesView, grouped);
        foreach (var ch in Channels) ch.SetGrouping(grouped);
        Save();
    }

    private void OnChannelSelectionChanged()
    {
        Save();
        if (IsRunning) _ = ApplySelectionLiveAsync();
    }

    /// <summary>
    /// Переключение сервера на живом ядре - без разрыва TUN. Возможно не всегда: если
    /// выбранного сервера нет в текущем конфиге (например он на Xray, а сайдкар поднимается
    /// только при запуске), ядро перезапускается само - иначе выбор молча ни на что не влияет.
    /// </summary>
    private async Task ApplySelectionLiveAsync()
    {
        var restartNeeded = new List<string>();

        foreach (var ch in Channels)
        {
            if (ch.AutoFastest || ch.Selected is null) continue;

            // Обращаться нужно тегом outbound'а: одинаковые имена серверов в конфиге
            // разводятся суффиксом, и имя профиля тегу уже не равно.
            if (!_tagByProfileId.TryGetValue(ch.Selected.Id, out var tag))
            {
                restartNeeded.Add($"'{ch.Name}' -> '{ch.Selected.Name}'");
                continue;
            }

            var ok = await _clash.SelectAsync(ch.Name, tag, _shutdown.Token).ConfigureAwait(false);

            if (ok) AppendLog($"[канал] '{ch.Name}' -> '{ch.Selected.Name}'");
            else restartNeeded.Add($"'{ch.Name}' -> '{ch.Selected.Name}'");
        }

        if (restartNeeded.Count == 0) return;

        AppendLog($"[канал] на лету не применить ({string.Join(", ", restartNeeded)}) - перезапускаю ядро");

        await Task.Run(() => { _core.Stop(); _xray.StopAll(); }).ConfigureAwait(false);
        await ConnectAsync().ConfigureAwait(false);
    }

    // ---------- запуск ядра ----------

    private async Task ConnectAsync()
    {
        var corePath = CoreProcessService.ResolveCorePath(Settings.CorePath);
        if (!File.Exists(corePath))
        {
            AppendLog($"[!] sing-box.exe не найден: {corePath}");
            Status = "Ядро не найдено";
            return;
        }

        if (!ConfirmNoForeignTunnel())
        {
            Dispatch(() => Status = "Отменено");
            return;
        }

        Save();
        Status = "Сборка конфига...";

        var available = await EnsureRuleSetsAsync().ConfigureAwait(false);
        var (xrayPorts, xrayPath) = await StartXraySidecarsAsync().ConfigureAwait(false);
        var xrayServers = AllXrayServerAddresses();

        var modern = await _core.DetectModernSyntaxAsync(corePath).ConfigureAwait(false);
        var build = new SingBoxConfigBuilder(modern, available, xrayPorts, xrayPath, xrayServers).Build(Settings);

        foreach (var w in build.Warnings) AppendLog($"[конфиг] {w}");

        Directory.CreateDirectory(SettingsStore.CoreDir);
        await File.WriteAllTextAsync(SettingsStore.ConfigPath, build.Json, _shutdown.Token).ConfigureAwait(false);

        var (ok, output) = await _core.CheckConfigAsync(corePath, SettingsStore.ConfigPath, SettingsStore.CoreDir)
            .ConfigureAwait(false);

        if (!ok)
        {
            AppendLog("[!] Ядро отвергло конфиг:");
            AppendLog(output);
            AppendLog($"[i] Синтаксис собран как {(modern ? "1.12+" : "1.11 и старше")}. " +
                      "Если ошибка про неизвестное поле - версия ядра определена неверно.");
            _xray.StopAll();
            Dispatch(() => Status = "Ошибка конфига");
            return;
        }

        _clash.Port = Settings.ClashApiPort;
        _clash.Secret = build.ClashSecret;
        _tagByProfileId = build.TagByProfileId;

        Dispatch(() =>
        {
            try
            {
                _core.Start(corePath, SettingsStore.ConfigPath, SettingsStore.CoreDir);
                AppendLog("[ядро] запущено");
            }
            catch (Exception ex)
            {
                AppendLog($"[!] Не удалось запустить ядро: {ex.Message}");
                Status = "Ошибка запуска";
            }
        });
    }

    /// <summary>
    /// Чужой активный туннель ломает нашу маршрутизацию целиком, вплоть до невозможности
    /// зарезолвить адрес собственного сервера. Молча стартовать в такой ситуации бессмысленно.
    /// </summary>
    private bool ConfirmNoForeignTunnel()
    {
        var foreign = TunnelConflict.FindForeignTunnels();
        if (foreign.Count == 0) return true;

        foreach (var f in foreign) AppendLog($"[!] Активен чужой туннель: {f}");

        var answer = MessageBoxResult.No;

        Dispatch(() => answer = MessageBox.Show(
            "Обнаружен активный туннель другого VPN-клиента:\n\n  " + string.Join("\n  ", foreign) +
            "\n\nДва TUN-адаптера одновременно делят таблицу маршрутов, и подключение почти наверняка " +
            "не заработает: ядро не сможет даже зарезолвить адрес своего сервера.\n\n" +
            "Отключите второй клиент. Продолжить всё равно?",
            "Конфликт туннелей", MessageBoxButton.YesNo, MessageBoxImage.Warning));

        return answer == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Догружает .srs в локальный кеш и возвращает теги, которые реально доступны.
    /// Наборы объявлены в конфиге как local, поэтому запуск ядра от сети не зависит:
    /// нет файла - правило просто не попадёт в конфиг.
    /// </summary>
    private async Task<IReadOnlySet<string>> EnsureRuleSetsAsync()
    {
        var wanted = SingBoxConfigBuilder.RequiredRuleSets(Settings);
        if (wanted.Count == 0) return new HashSet<string>();

        // Ядро уже работает - качаем через его локальный прокси: снаружи GitHub может быть закрыт.
        Uri? proxy = IsRunning ? new Uri($"http://127.0.0.1:{Settings.MixedPort}") : null;

        var files = await _ruleSets
            .EnsureAsync(wanted, TimeSpan.FromDays(7), proxy, _shutdown.Token)
            .ConfigureAwait(false);

        foreach (var f in files)
        {
            if (f.Error is null)
                AppendLog($"[rule-set] {f.Tag}: {f.Size} байт, обновлён {f.Updated:dd.MM HH:mm}");
            else if (f.Available)
                AppendLog($"[rule-set] {f.Tag}: обновить не удалось ({f.Error}), взят старый файл от {f.Updated:dd.MM HH:mm}");
            else
                AppendLog($"[!] rule-set {f.Tag}: не скачан ({f.Error}). Правило будет пропущено.");
        }

        return files.Where(f => f.Available).Select(f => f.Tag).ToHashSet();
    }

    // ---------- проверка доступности ----------

    /// <summary>
    /// Меряет задержку по всем серверам. Пути два: то, что есть в конфиге, меряет само ядро
    /// через clash_api; серверы на Xray, для которых постоянный сайдкар не поднят, измеряются
    /// временным сайдкаром - иначе после перехода провайдера на XHTTP проверять было бы почти
    /// нечего, ведь в конфиг попадает лишь выбранное в каналах.
    /// </summary>
    private async Task CheckAllServersAsync()
    {
        if (!IsRunning) { AppendLog("[пинг] ядро не запущено"); return; }

        var viaCore = Settings.Profiles.Where(p => _tagByProfileId.ContainsKey(p.Id)).ToList();

        var viaProbe = Settings.Profiles
            .Where(p => p.UsesXray && !_tagByProfileId.ContainsKey(p.Id))
            .ToList();

        var xrayPath = viaProbe.Count > 0 ? XrayProcessService.Resolve(Settings.XrayPath) : null;

        if (viaProbe.Count > 0 && xrayPath is null)
        {
            AppendLog($"[пинг] {viaProbe.Count} серверов на Xray пропущены: xray.exe не найден");
            viaProbe.Clear();
        }

        var total = viaCore.Count + viaProbe.Count;
        var unreachable = Settings.Profiles.Count - total;

        if (total == 0)
        {
            AppendLog("[пинг] нечего проверять: ни один сервер не попал в конфиг. " +
                      "Выберите сервер в канале и переподключитесь.");
            return;
        }

        AppendLog($"[пинг] проверяю {total}: через ядро {viaCore.Count}, временным сайдкаром {viaProbe.Count}" +
                  (unreachable > 0 ? $"; не проверить: {unreachable}" : ""));

        Dispatch(() => Status = $"Проверка серверов (0/{total})");

        // Каждый замер - реальный трафик, а замер через сайдкар ещё и процесс Xray.
        // Десять одновременно: меньше - полный проход по полусотне серверов растягивается
        // на минуту, больше - заметный всплеск памяти на ровном месте.
        using var limit = new SemaphoreSlim(10);
        using var probe = new XrayLatencyProbe();
        var done = 0;

        async Task RunAsync(ProxyProfile p, Func<Task<int?>> measure)
        {
            await limit.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                var ms = await measure().ConfigureAwait(false);

                Dispatch(() =>
                {
                    p.LatencyMs = ms;
                    p.LatencyChecked = true;

                    var n = Interlocked.Increment(ref done);
                    if (n % 5 == 0 || n == total) Status = $"Проверка серверов ({n}/{total})";
                });
            }
            finally
            {
                limit.Release();
            }
        }

        var tasks = viaCore
            .Select(p => RunAsync(p, () => _clash.DelayAsync(_tagByProfileId[p.Id], _shutdown.Token)))
            .Concat(viaProbe.Select(p => RunAsync(p, async () =>
            {
                string config;
                try
                {
                    config = p.RequiresXray
                        ? p.XrayConfigJson!
                        : XrayConfigSynthesizer.FromLink(p.SourceLink!);
                }
                catch (Exception)
                {
                    return null;
                }

                // Живые серверы отвечают за 0.5-5 с, так что восьми секунд хватает,
                // а мёртвый не тормозит очередь дольше необходимого.
                return await probe
                    .MeasureAsync(xrayPath!, config, TimeSpan.FromSeconds(8), _shutdown.Token)
                    .ConfigureAwait(false);
            })));

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var measured = viaCore.Concat(viaProbe).ToList();
        var alive = measured.Count(p => p.LatencyMs is not null);
        var best = measured.Where(p => p.LatencyMs is not null).OrderBy(p => p.LatencyMs).FirstOrDefault();

        AppendLog($"[пинг] доступно {alive} из {total}" +
                  (best is null ? "" : $", быстрейший: '{best.Name}' {best.LatencyMs} мс"));

        Dispatch(() => Status = IsRunning ? "Подключено" : "Отключено");
    }

    /// <summary>Периодический замер только по выбранным серверам каналов - их единицы.</summary>
    private async Task CheckChannelsAsync()
    {
        if (!IsRunning) return;

        foreach (var ch in Channels)
        {
            var profile = ch.Selected;
            if (profile is null || !_tagByProfileId.TryGetValue(profile.Id, out var tag)) continue;

            var ms = await _clash.DelayAsync(tag, _shutdown.Token).ConfigureAwait(false);

            Dispatch(() =>
            {
                profile.LatencyMs = ms;
                profile.LatencyChecked = true;
            });

            if (ms is null)
                AppendLog($"[пинг] канал '{ch.Name}': сервер '{profile.Name}' не отвечает");
        }
    }

    private async Task RunChannelCheckLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, Settings.ChannelCheckSeconds)), ct)
                    .ConfigureAwait(false);

                if (Settings.ChannelCheckSeconds <= 0) continue;

                await CheckChannelsAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // штатное завершение
        }
    }

    /// <summary>Остановка ядра ждёт выхода процесса - на UI-потоке это подвешивало окно.</summary>
    private async Task DisconnectAsync()
    {
        await Task.Run(() =>
        {
            _core.Stop();
            _xray.StopAll();
        }).ConfigureAwait(false);

        AppendLog("[ядро] остановлено");
    }

    /// <summary>
    /// Поднимает сайдкар Xray под каждый выбранный сервер, который требует конфига Xray
    /// (XHTTP, балансировщик провайдера). Возвращает соответствие профиль -> локальный порт.
    /// </summary>
    private async Task<(Dictionary<string, int> ports, string? xrayPath)> StartXraySidecarsAsync()
    {
        _xray.StopAll();

        var ports = new Dictionary<string, int>();

        var wanted = Settings.Channels
            .Where(c => !c.AutoFastest && !string.IsNullOrWhiteSpace(c.SelectedProfileKey))
            .Select(c => Settings.Profiles.FirstOrDefault(p => p.StableKey == c.SelectedProfileKey))
            .Where(p => p is { UsesXray: true })
            .DistinctBy(p => p!.Id)
            .ToList();

        var autoWithXray = Settings.Channels
            .Where(c => c.AutoFastest)
            .Where(_ => Settings.Profiles.Any(p => p.UsesXray))
            .ToList();

        foreach (var ch in autoWithXray)
            AppendLog($"[xray] канал '{ch.Name}' в режиме «авто»: серверы на Xray в подбор не войдут - " +
                      "для каждого пришлось бы держать отдельный процесс.");

        if (wanted.Count == 0) return (ports, null);

        var xrayPath = XrayProcessService.Resolve(Settings.XrayPath, AppendLog);

        if (xrayPath is null)
        {
            AppendLog($"[!] Нужен xray.exe для {wanted.Count} выбранных серверов, но он не найден. " +
                      "Укажите путь в поле «Путь к xray.exe» - иначе эти серверы будут пропущены.");
            return (ports, null);
        }

        foreach (var p in wanted)
        {
            string config;
            try
            {
                // Профиль из подписки несёт готовый конфиг, профиль из ссылки - нет,
                // для него конфиг синтезируется, иначе XHTTP по ссылке не запустить.
                config = p!.RequiresXray
                    ? p.XrayConfigJson!
                    : XrayConfigSynthesizer.FromLink(p.SourceLink!);
            }
            catch (Exception ex)
            {
                AppendLog($"[!] xray '{p!.Name}': конфиг не собран из ссылки - {ex.Message}");
                continue;
            }

            var instance = await _xray
                .StartAsync(xrayPath, p.Id, p.Name, config)
                .ConfigureAwait(false);

            if (instance is not null) ports[p.Id] = instance.SocksPort;
        }

        return (ports, xrayPath);
    }

    /// <summary>
    /// Адреса всех серверов, которые обслуживаются через Xray. Выводятся из туннеля целиком,
    /// а не только запущенные: проверка доступности поднимает сайдкары на время замера,
    /// и их соединения TUN перехватил бы точно так же - Xray завернул бы сам себя.
    /// </summary>
    private List<string> AllXrayServerAddresses()
    {
        var result = new List<string>();

        foreach (var p in Settings.Profiles.Where(p => p.UsesXray))
        {
            if (p.RequiresXray)
                result.AddRange(XrayEndpoints.Extract(p.XrayConfigJson!));
            else if (!string.IsNullOrWhiteSpace(p.Server))
                result.Add(p.Server);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task CheckConfigAsync()
    {
        var corePath = CoreProcessService.ResolveCorePath(Settings.CorePath);
        Save();

        var available = await EnsureRuleSetsAsync().ConfigureAwait(false);

        var modern = await _core.DetectModernSyntaxAsync(corePath).ConfigureAwait(false);
        var build = new SingBoxConfigBuilder(modern, available).Build(Settings);

        foreach (var w in build.Warnings) AppendLog($"[конфиг] {w}");

        Directory.CreateDirectory(SettingsStore.CoreDir);
        await File.WriteAllTextAsync(SettingsStore.ConfigPath, build.Json, _shutdown.Token).ConfigureAwait(false);
        AppendLog($"[конфиг] записан: {SettingsStore.ConfigPath}");

        if (!File.Exists(corePath))
        {
            AppendLog("[!] sing-box.exe не найден, проверить конфиг нечем");
            return;
        }

        var (ok, output) = await _core.CheckConfigAsync(corePath, SettingsStore.ConfigPath, SettingsStore.CoreDir)
            .ConfigureAwait(false);

        AppendLog(ok ? "[конфиг] проверка пройдена" : "[!] проверка не пройдена:\n" + output);
    }

    // ---------- прочее ----------

    private void ImportFromHapp()
    {
        var report = HappImporter.Import(Settings);

        foreach (var m in report.Messages) AppendLog($"[Happ] {m}");
        AppendLog($"[Happ] импортировано правил: приложений {report.AppRulesAdded}, гео/доменных {report.GeoRulesAdded}");

        if (report.AnythingFound)
        {
            RefreshChannels();
            Save();
        }

        MessageBox.Show(
            $"Импортировано из Happ:\n" +
            $"  правил приложений: {report.AppRulesAdded}\n" +
            $"  гео- и доменных правил: {report.GeoRulesAdded}\n\n" +
            "Серверы не переносятся: Happ хранит их зашифрованными. Скопируйте ссылку подписки " +
            "из Happ и добавьте её в разделе «Подписки».\n\nПодробности - в журнале.",
            "Импорт из Happ", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void Save()
    {
        try { SettingsStore.Save(Settings); }
        catch (Exception ex) { AppendLog($"[!] Не удалось сохранить настройки: {ex.Message}"); }
    }

    private void OnCoreLog(string line)
    {
        AppendLog(line);

        // Самая частая причина падения на старте: наборы качаются через канал,
        // а туннель не поднялся. Сообщение ядра об этом ничего не говорит.
        if (line.Contains("initialize rule-set", StringComparison.Ordinal) && !_ruleSetHintShown)
        {
            _ruleSetHintShown = true;
            AppendLog("[i] Ядро не смогло скачать geo-наборы. Они качаются через канал по умолчанию - " +
                      "значит туннель до выбранного сервера не поднялся. Проверьте сервер в канале " +
                      $"'{Settings.DefaultChannelName}' либо переключите «Качать geo-базы» на прямое соединение.");
        }
    }

    private bool _ruleSetHintShown;

    /// <summary>
    /// Складывает строку в очередь. Отдача в интерфейс - пачками по таймеру: ядро выдаёт
    /// десятки строк в секунду, и маршрутизация каждой через Dispatcher.Invoke с полной
    /// пересборкой текста подвешивала UI-поток намертво.
    /// </summary>
    public void AppendLog(string line)
    {
        foreach (var l in line.Split('\n'))
            _pendingLog.Enqueue(AnsiCodes().Replace(l.TrimEnd('\r'), string.Empty));
    }

    private void FlushLog()
    {
        if (_pendingLog.IsEmpty) return;

        var batch = new List<string>();
        while (_pendingLog.TryDequeue(out var l)) batch.Add(l);

        FileLog.WriteBatch(batch);

        // При всплеске нет смысла тащить в буфер больше, чем он вмещает.
        var tail = batch.Count > MaxLogLines ? batch.Skip(batch.Count - MaxLogLines) : batch;

        foreach (var l in tail)
        {
            _logLines.Enqueue(l);
            if (_logLines.Count > MaxLogLines) _logLines.Dequeue();
        }

        var sb = new StringBuilder();
        foreach (var l in _logLines) sb.AppendLine(l);
        LogText = sb.ToString();
    }

    [GeneratedRegex(@"\u001B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiCodes();

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        _logTimer.Stop();
        FlushLog();
        _shutdown.Cancel();
        Save();
        _core.Dispose();
        _xray.Dispose();
        _shutdown.Dispose();
    }
}
