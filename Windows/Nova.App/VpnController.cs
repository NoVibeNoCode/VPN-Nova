using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Nova.Core;

namespace Nova.App;

public enum CoreStatus { NotInstalled, NeedsUpdate, Off, Connecting, On, Error }

/// App state: profile, core lifecycle, live stats and the kill switch loop. Lives on the UI thread.
public sealed class VpnController
{
    public static readonly string AppVersion = Meta("NovaVersion");
    /// Version of the privileged part (service + core). Changes only when they change, so a new app
    /// version doesn't ask for administrator rights unless the service really needs updating.
    public static readonly string ServiceVersion = Meta("NovaServiceVersion");
    public static readonly string CoreVersion = Meta("NovaSingBoxVersion");
    public static string AppDir => AppContext.BaseDirectory;

    static string Meta(string key) =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "dev";

    public Profile Profile { get; private set; }
    public CoreStatus Status { get; private set; } = CoreStatus.Off;
    public string? StatusMessage { get; private set; }
    public DateTime? ConnectedSince { get; private set; }
    public Dictionary<string, HealthMonitor.ServerHealth> Health { get; private set; } = [];
    public Dictionary<string, List<int>> DelayHistory { get; } = [];
    public bool Applying { get; private set; }
    public string? LastError { get; private set; }
    /// Other VPN clients that are connected right now (they compete with Nova's routes).
    public List<string> OtherVpns { get; private set; } = [];

    /// Per-second traffic and the live connection list change constantly; they have their own events
    /// so only the views showing them re-render (not the whole menu and window).
    public LiveStats Stats { get; } = new();
    public List<ClashApi.Connection> Connections { get; private set; } = [];

    /// Profile, status, health, errors: anything most views show.
    public event Action? Changed;
    public event Action? ConnectionsChanged;

    public bool IsDemo { get; }
    readonly ICoreService _core = new ServiceCore();
    readonly List<CancellationTokenSource> _loops = [];
    CancellationTokenSource? _applyDelay;
    HealthMonitor? _monitor;
    Task? _ruleSetRefresh;
    /// Bumped by every connect / disconnect / apply; a finishing operation that has been
    /// superseded must not touch the state.
    int _opId;
    /// Core start/stop/apply run one at a time, in the order they were requested.
    static readonly SemaphoreSlim CoreQueue = new(1, 1);

    public VpnController()
    {
        Profile = ProfileStore.Load();
        LastError = ProfileStore.LoadError;
        // Persist right away: the Clash API secret must survive app restarts.
        if (!File.Exists(ProfileStore.ProfilePath)) TrySave();
        RefreshInstallState();
        if (Status == CoreStatus.Off) _ = AttachIfRunning();
    }

    void Notify() => Changed?.Invoke();

    public ClashApi Api => new(Profile.Settings.ClashPort, Profile.ClashSecret);
    public bool IsActive => Status is CoreStatus.On or CoreStatus.Connecting;

    /// Some server that has traffic routed to it is down.
    public bool HasDownServer =>
        Status == CoreStatus.On && Profile.Servers.Any(s => Health.GetValueOrDefault(s.Id)?.IsDown == true && IsUsed(s.Id));

    public bool IsUsed(string id) =>
        Profile.Settings.DefaultTarget == id
        || Profile.DomainRules.Any(r => r.Enabled && r.Target == id)
        || Profile.AppRules.Any(r => r.Enabled && r.Target == id);

    // MARK: - Install

    public void RefreshInstallState()
    {
        if (IsDemo) return;
        if (!ServiceCore.IsInstalled) SetStatus(CoreStatus.NotInstalled);
        else if (ServiceCore.InstalledVersion != ServiceVersion) SetStatus(CoreStatus.NeedsUpdate);
        else if (Status is CoreStatus.NotInstalled or CoreStatus.NeedsUpdate) SetStatus(CoreStatus.Off);
    }

    public async void Install()
    {
        if (IsDemo) { SetStatus(CoreStatus.Off); return; }
        var wasOn = IsActive;
        try
        {
            await Task.Run(() => ServiceCore.Install(AppDir, ServiceVersion));
            LastError = null;
            SetStatus(CoreStatus.Off);
            RefreshInstallState();
            if (wasOn) Connect();
        }
        catch (CoreException e)
        {
            LastError = $"Установка не удалась: {e.Message}";
            Notify();
        }
    }

    public async void Uninstall()
    {
        StopLoops();
        try
        {
            await Task.Run(() => ServiceCore.Uninstall(AppDir));
            SetStatus(CoreStatus.NotInstalled);
        }
        catch (CoreException e)
        {
            LastError = $"Удаление не удалось: {e.Message}";
            Notify();
        }
    }

    // MARK: - Connect

    public void Toggle()
    {
        if (IsDemo)
        {
            if (IsActive)
            {
                ConnectedSince = null;
                SetStatus(CoreStatus.Off);
            }
            else
            {
                SetStatus(CoreStatus.Connecting);
                DispatcherTimer.RunOnce(() => { ConnectedSince = DateTime.Now; SetStatus(CoreStatus.On); }, TimeSpan.FromSeconds(1.6));
            }
            return;
        }
        switch (Status)
        {
            case CoreStatus.On or CoreStatus.Connecting: Disconnect(); break;
            case CoreStatus.Off or CoreStatus.Error: Connect(); break;
            default: Install(); break;
        }
    }

    public async void RefreshOtherVpns()
    {
        if (IsDemo) return;
        var names = await Task.Run(SystemNetwork.ConnectedVpns);
        if (!names.SequenceEqual(OtherVpns))
        {
            OtherVpns = names;
            Notify();
        }
    }

    public async void Connect()
    {
        if (Status is CoreStatus.NotInstalled or CoreStatus.NeedsUpdate || IsDemo) return;
        RefreshOtherVpns();
        _applyDelay?.Cancel();
        var op = ++_opId;
        LastError = null;
        SetStatus(CoreStatus.Connecting);
        try
        {
            var config = await MakeConfig();
            if (op != _opId) return;
            await OnCoreQueue(() =>
            {
                _core.Apply(config);
                _core.Start();
            });
            if (op != _opId) return;
            if (await WaitForApi() && op == _opId)
            {
                ConnectedSince = DateTime.Now;
                SetStatus(CoreStatus.On);
                StartLoops();
            }
            else if (op == _opId)
            {
                // Don't leave the service restarting a core that can't come up.
                try { await OnCoreQueue(_core.Stop); } catch (CoreException) { }
                SetStatus(CoreStatus.Error, "Ядро не запустилось. Загляни в логи.");
            }
        }
        catch (CoreException e)
        {
            if (op == _opId) SetStatus(CoreStatus.Error, Short(e.Message));
        }
    }

    public async void Disconnect()
    {
        if (IsDemo) { Toggle(); return; }
        StopLoops();
        _applyDelay?.Cancel();
        var op = ++_opId;
        try
        {
            await OnCoreQueue(_core.Stop);
            if (op == _opId) SetStatus(CoreStatus.Off);
        }
        catch (CoreException e)
        {
            if (op == _opId) SetStatus(CoreStatus.Error, Short(e.Message));
        }
        ConnectedSince = null;
        Stats.Reset();
        SetConnections([]);
        Health = [];
        Notify();
    }

    /// "Выйти": turn the VPN off and close the app.
    public void Quit()
    {
        StopLoops();
        if (IsActive && !IsDemo)
        {
            try { OnCoreQueue(_core.Stop).Wait(TimeSpan.FromSeconds(10)); }
            catch (AggregateException) { }
        }
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    async Task AttachIfRunning()
    {
        var running = await Task.Run(_core.IsRunning);
        if (!running)
        {
            if (Profile.Settings.ConnectOnLaunch) Connect();
            return;
        }
        if (await WaitForApi(10))
        {
            ConnectedSince = DateTime.Now;
            SetStatus(CoreStatus.On);
            StartLoops();
        }
    }

    async Task<bool> WaitForApi(int attempts = 60)
    {
        var api = Api;
        for (var i = 0; i < attempts; i++)
        {
            if (await api.IsAlive()) return true;
            await Task.Delay(200);
        }
        return false;
    }

    // MARK: - Profile edits

    /// Mutates the profile, saves it, and restarts the core (debounced) when needed.
    public void Update(Action<Profile> change, bool restart = true)
    {
        change(Profile);
        Notify();
        if (IsDemo) return;
        TrySave();
        if (restart && Status == CoreStatus.On) ScheduleApply();
    }

    void TrySave()
    {
        try { ProfileStore.Save(Profile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = $"Не удалось сохранить профиль: {e.Message}";
        }
    }

    void ScheduleApply()
    {
        _applyDelay?.Cancel();
        var cts = _applyDelay = new CancellationTokenSource();
        DispatcherTimer.RunOnce(() => { if (!cts.IsCancellationRequested) _ = ApplyNow(); }, TimeSpan.FromMilliseconds(700));
    }

    async Task ApplyNow()
    {
        if (Status != CoreStatus.On) return;
        var op = ++_opId;
        Applying = true;
        SetStatus(CoreStatus.Connecting);
        StopLoops();
        try
        {
            var config = await MakeConfig();
            if (op != _opId) return;
            await OnCoreQueue(() =>
            {
                _core.Apply(config);
                _core.Start();
            });
            if (op != _opId) return;
            if (await WaitForApi() && op == _opId)
            {
                SetStatus(CoreStatus.On);
                StartLoops();
            }
            else if (op == _opId)
            {
                SetStatus(CoreStatus.Error, "Ядро не перезапустилось. Загляни в логи.");
            }
        }
        catch (CoreException e)
        {
            if (op != _opId) return;
            // The new config was rejected; the core keeps running the previous one.
            LastError = Short(e.Message);
            if (await WaitForApi(5) && op == _opId)
            {
                SetStatus(CoreStatus.On);
                StartLoops();
            }
            else if (op == _opId)
            {
                SetStatus(CoreStatus.Error, Short(e.Message));
            }
        }
        finally
        {
            Applying = false;
            Notify();
        }
    }

    /// Builds the TUN config from local rule-set files (shipped ones are seeded first): no network wait.
    /// Missing or stale sets are fetched in the background afterwards.
    async Task<byte[]> MakeConfig()
    {
        var store = new RuleSetStore();
        var profile = Profile.Clone();
        var data = await Task.Run(() =>
        {
            store.Seed(Path.Combine(AppDir, "rule-sets"));
            return new ConfigBuilder(profile) { RuleSetDirectory = store.Directory }.Data();
        });
        RefreshRuleSetsInBackground();
        return data;
    }

    void RefreshRuleSetsInBackground()
    {
        if (_ruleSetRefresh != null) return;
        var store = new RuleSetStore();
        var tags = RuleSetStore.Tags(Profile);
        var missing = store.Missing(tags);
        var need = missing.Concat(Profile.Settings.AutoUpdateRuleSets ? store.Stale(tags) : []).ToList();
        if (need.Count == 0) return;
        _ruleSetRefresh = Run();

        async Task Run()
        {
            var failed = await store.Ensure(need, refresh: true);
            _ruleSetRefresh = null;
            var fetched = missing.Where(t => !failed.ContainsKey(t)).ToList();
            if (failed.Count > 0)
            {
                LastError = $"Не скачались наборы: {string.Join(", ", failed.Keys.Order())}. Их правила пропущены.";
                Notify();
            }
            // Newly available sets take effect with a quick core restart.
            if (fetched.Count > 0 && Status == CoreStatus.On) ScheduleApply();
        }
    }

    /// "Всё остальное" switches live through the default-route selector.
    public async void SetDefaultTarget(string target)
    {
        Update(p => p.Settings.DefaultTarget = target, restart: false);
        if (Status != CoreStatus.On || IsDemo) return;
        var tag = new ConfigBuilder(Profile).OutboundTag(target);
        try { await Api.Select(ConfigBuilder.DefaultRouteTag, tag); }
        catch (ClashApi.ApiException) { }
    }

    public async void SetKillSwitch(bool on)
    {
        Update(p => p.Settings.KillSwitch = on, restart: false);
        if (Status != CoreStatus.On || IsDemo || _monitor == null) return;
        Health = await _monitor.Tick(Profile.Servers.Select(s => s.Id).ToList(), on);
        Notify();
        await Api.CloseAllConnections();
    }

    public void SetLaunchAtLogin(bool on)
    {
        try
        {
            WinIntegration.SetLaunchAtLogin(on);
            Update(p => p.Settings.LaunchAtLogin = on, restart: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            LastError = $"Автозапуск: {e.Message}";
            Notify();
        }
    }

    public void ClearError()
    {
        LastError = null;
        Notify();
    }

    // MARK: - Live loops

    void StartLoops()
    {
        StopLoops();
        var api = Api;
        var monitor = _monitor = new HealthMonitor(api);

        var traffic = new CancellationTokenSource();
        _loops.Add(traffic);
        _ = Task.Run(async () =>
        {
            while (!traffic.IsCancellationRequested)
            {
                try
                {
                    await foreach (var t in api.TrafficStream(traffic.Token))
                        Dispatcher.UIThread.Post(() => { if (!traffic.IsCancellationRequested) Stats.Push(t.Down, t.Up); });
                }
                catch (OperationCanceledException) { return; }
                try { await Task.Delay(1000, traffic.Token); } catch (OperationCanceledException) { return; }
            }
        });

        var poll = new CancellationTokenSource();
        _loops.Add(poll);
        Dispatcher.UIThread.Post(async () =>
        {
            var tick = 0;
            while (!poll.IsCancellationRequested)
            {
                ClashApi.Connections? c = null;
                try { c = await api.GetConnections(); } catch (ClashApi.ApiException) { }
                if (poll.IsCancellationRequested) return;
                if (c != null)
                {
                    var list = c.List ?? [];
                    // Skip identical snapshots: no re-render when nothing changed.
                    if (!list.Select(x => x.Id).SequenceEqual(Connections.Select(x => x.Id))) SetConnections(list);
                }
                else if (!await api.IsAlive())
                {
                    if (!poll.IsCancellationRequested) CoreDied();
                    return;
                }
                if (tick % 5 == 0)
                {
                    RefreshOtherVpns();
                    var h = await monitor.Tick(Profile.Servers.Select(s => s.Id).ToList(), Profile.Settings.KillSwitch);
                    if (poll.IsCancellationRequested) return;
                    Health = h;
                    foreach (var (id, s) in h)
                    {
                        var hist = DelayHistory.GetValueOrDefault(id) ?? [];
                        hist.Add(s.DelayMs ?? 0);
                        DelayHistory[id] = hist.TakeLast(24).ToList();
                    }
                    Notify();
                }
                tick++;
                try { await Task.Delay(2000, poll.Token); } catch (OperationCanceledException) { return; }
            }
        });
    }

    void StopLoops()
    {
        foreach (var l in _loops) l.Cancel();
        _loops.Clear();
    }

    void CoreDied()
    {
        StopLoops();
        SetConnections([]);
        Stats.Reset();
        SetStatus(CoreStatus.Error, "Ядро остановилось. Загляни в логи.");
    }

    void SetConnections(List<ClashApi.Connection> list)
    {
        Connections = list;
        ConnectionsChanged?.Invoke();
    }

    void SetStatus(CoreStatus s, string? message = null)
    {
        Status = s;
        StatusMessage = message;
        Notify();
    }

    // MARK: - Diagnostics

    /// The core can't see which program owns a connection, so app rules can't match.
    public bool ProcessLookupBroken => ConnectionInfo.ProcessLookupBroken(Connections);

    /// Target a live connection actually went to.
    public string TargetOf(ClashApi.Connection c) => ConnectionInfo.Target(c.Chains, Profile.Servers.Select(s => s.Id));

    // MARK: - Helpers

    static async Task OnCoreQueue(Action work)
    {
        await CoreQueue.WaitAsync();
        try { await Task.Run(work); }
        finally { CoreQueue.Release(); }
    }

    static string Short(string text) => (text.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? text).Trim();

    // MARK: - Demo (`--demo`, `--snapshot`): fake live data, never touches disk or the service

    public VpnController(bool demo)
    {
        IsDemo = true;
        var p = new Profile();
        var de = new Server("Германия", "DE", "#8B7CFF", (JsonObject)JsonNode.Parse("""{"type":"vless","server":"203.0.113.10","server_port":443,"flow":"xtls-rprx-vision","tls":{"enabled":true,"reality":{"enabled":true}}}""")!, "de");
        var ru = new Server("Россия", "RU", "#FF7A59", (JsonObject)JsonNode.Parse("""{"type":"shadowsocks","server":"198.51.100.20","server_port":8388}""")!, "ru");
        p.Servers = [de, ru];
        p.DomainRules =
        [
            new(DomainRuleKind.Domain, "youtube.com", "de"),
            new(DomainRuleKind.Domain, ".ru", "ru"),
            new(DomainRuleKind.Ruleset, "geosite-cn,geoip-cn", Target.Direct),
            new(DomainRuleKind.Keyword, "openai", "de"),
        ];
        p.AppRules =
        [
            new("Telegram", @"C:\Users\alex\AppData\Roaming\Telegram Desktop\Telegram.exe", "de"),
            new("Claude", @"C:\Users\alex\AppData\Local\AnthropicClaude\app-1.4.0\claude.exe", "de"),
            new("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "ru"),
        ];
        p.Settings.DefaultTarget = "de";
        Profile = p;
        Status = CoreStatus.On;
        ConnectedSince = DateTime.Now.AddSeconds(-8057);
        Stats.Down = 4_200_000;
        Stats.Up = 310_000;
        Stats.DownHistory = Enumerable.Range(0, 24).Select(i => 1_500_000 + 2_500_000 * Math.Abs(Math.Sin(i * 0.7))).ToList();
        Stats.UpHistory = Enumerable.Range(0, 24).Select(i => 120_000 + 200_000 * Math.Abs(Math.Cos(i * 0.5))).ToList();
        Health = new() { ["de"] = new(42), ["ru"] = new(18) };
        DelayHistory["de"] = [48, 44, 51, 40, 46, 39, 42, 45, 41, 38, 44, 42];
        DelayHistory["ru"] = [20, 19, 22, 18, 17, 21, 18, 19, 18, 17, 19, 18];
        (string Host, string? Path, string Chain)[] sample =
        [
            ("www.youtube.com", null, "via-de"), ("i.ytimg.com", null, "via-de"), ("yandex.ru", null, "via-ru"),
            ("api.telegram.org", @"C:\Users\alex\AppData\Roaming\Telegram Desktop\Telegram.exe", "via-de"),
            ("149.154.167.51", @"C:\Users\alex\AppData\Roaming\Telegram Desktop\Telegram.exe", "via-de"),
            ("claude.ai", @"C:\Users\alex\AppData\Local\AnthropicClaude\app-1.4.0\claude.exe", "via-de"),
            ("ya.ru", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "via-ru"),
            ("baidu.com", null, "direct"), ("github.com", null, "via-de"),
        ];
        var items = sample.Select((s, i) =>
            $"{{\"id\":\"{i}\",\"upload\":0,\"download\":0,\"chains\":[\"{s.Chain}\"],\"metadata\":{{{(s.Path == null ? "" : $"\"processPath\":{JsonSerializer.Serialize(s.Path)},")}\"network\":\"tcp\",\"host\":\"{s.Host}\"}}}}");
        Connections = ClashApi.Connections.Parse($"{{\"downloadTotal\":0,\"uploadTotal\":0,\"connections\":[{string.Join(",", items)}]}}").List ?? [];
        if (Environment.GetEnvironmentVariable("NOVA_DEMO_STATIC") == null)
        {
            var rng = new Random(7);
            DispatcherTimer.Run(() =>
            {
                if (Status != CoreStatus.On) return true;
                Stats.Push(rng.Next(900_000, 5_800_000), rng.Next(90_000, 420_000));
                foreach (var id in new[] { "de", "ru" })
                {
                    var ms = (id == "de" ? 38 : 16) + rng.Next(0, 13);
                    Health[id] = (Health.GetValueOrDefault(id) ?? new()) with { DelayMs = ms };
                    DelayHistory[id] = DelayHistory[id].Append(ms).TakeLast(24).ToList();
                }
                Notify();
                return true;
            }, TimeSpan.FromSeconds(1));
        }
    }

    /// Puts a demo controller into an arbitrary state for snapshot checks.
    public void DemoSet(CoreStatus? status = null, string? message = null, Profile? profile = null,
                        Dictionary<string, HealthMonitor.ServerHealth>? health = null, List<string>? otherVpns = null,
                        string? lastError = null, List<ClashApi.Connection>? connections = null)
    {
        if (!IsDemo) return;
        if (status is { } s) { Status = s; StatusMessage = message; }
        if (profile != null) Profile = profile;
        if (health != null) Health = health;
        if (otherVpns != null) OtherVpns = otherVpns;
        if (lastError != null) LastError = lastError;
        if (connections != null) SetConnections(connections);
        Notify();
    }
}

public sealed class LiveStats
{
    public long Down { get; set; }
    public long Up { get; set; }
    public List<double> DownHistory { get; set; } = Enumerable.Repeat(0.0, 24).ToList();
    public List<double> UpHistory { get; set; } = Enumerable.Repeat(0.0, 24).ToList();
    public event Action? Changed;

    public void Push(long down, long up)
    {
        Down = down;
        Up = up;
        DownHistory = DownHistory.Append(down).TakeLast(24).ToList();
        UpHistory = UpHistory.Append(up).TakeLast(24).ToList();
        Changed?.Invoke();
    }

    public void Reset()
    {
        Down = 0;
        Up = 0;
        Changed?.Invoke();
    }
}
