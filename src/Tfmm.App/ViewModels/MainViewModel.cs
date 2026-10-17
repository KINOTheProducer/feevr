using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tfmm.App.Theme;
using Tfmm.Core;

namespace Tfmm.App.ViewModels;

/// <summary>A playset as a "ticket" in lists: name, subtitle and one status bar per mod.</summary>
public sealed record PlaysetItem(string Id, string Name, int Count, string Subtitle, bool IsActive, List<IBrush> Bars)
{
    public string CountText => Count == 1 ? "1 mod" : $"{Count} mods";
}

public partial class MainViewModel : ObservableObject, IDisposable
{
    public AppState State { get; }
    public LibraryViewModel Library { get; }
    public PlaysetsViewModel PlaysetsPage { get; }
    public PerformanceViewModel Performance { get; }
    public SettingsViewModel Settings { get; }
    public CrashesViewModel Crashes { get; }

    public ObservableCollection<PlaysetItem> Playsets { get; } = [];
    public CheckReport Report { get; private set; } = new();

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private string _page = "library";
    [ObservableProperty] private PlaysetItem? _activePlayset;
    [ObservableProperty] private string _activeStatus = "";
    [ObservableProperty] private IBrush _activeDot = Pal.Off;
    [ObservableProperty] private string _ticketStats = "";
    [ObservableProperty] private int _installedCount;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _toastIsError;

    // live tracking
    public const int LiveWindow = 120;
    readonly ProcessSampler _sampler;
    readonly SessionRecorder _recorder;
    readonly List<double> _cpu = [];
    readonly List<double> _ram = [];
    readonly DispatcherTimer _perfTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    [ObservableProperty] private bool _isGameRunning;
    [ObservableProperty] private string _liveTitle = "";
    [ObservableProperty] private string _liveDetail = "";
    [ObservableProperty] private string _liveCpu = "--";
    [ObservableProperty] private string _liveRam = "--";
    [ObservableProperty] private string _liveThreads = "--";
    [ObservableProperty] private string _liveElapsed = "00:00";
    [ObservableProperty] private IReadOnlyList<double> _cpuSeries = [];
    [ObservableProperty] private IReadOnlyList<double> _ramSeries = [];
    [ObservableProperty] private double _ramMax = 1024;

    // self-update
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    public Version AppVersion { get; } = Updater.Current(typeof(MainViewModel).Assembly);
    public string AppVersionText => $"v{AppVersion.ToString(3)}";
    [ObservableProperty] private UpdateInfo? _update;
    [ObservableProperty] private bool _updateBusy;
    [ObservableProperty] private string _updateStatus = "";
    public bool HasUpdate => Update is not null;
    public string UpdateTitle => Update is null ? "" : $"Feevr {Update.Version.ToString(3)} is out";
    public string UpdateNotes => Update is null ? "" :
        Update.Notes.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith('#'))
            .Select(l => l.TrimStart('-', '*', ' ')).FirstOrDefault(l => l.Length > 0) ?? "A new version is ready.";
    partial void OnUpdateChanged(UpdateInfo? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateTitle));
        OnPropertyChanged(nameof(UpdateNotes));
    }

    /// <summary>Path of the running Feevr.exe, or null under `dotnet run` (nothing to replace then).</summary>
    static string? InstalledExe => Environment.ProcessPath is { } p &&
        Path.GetFileNameWithoutExtension(p).Equals("Feevr", StringComparison.OrdinalIgnoreCase) ? p : null;

    bool _refreshing;
    public bool IsRefreshing => _refreshing;
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };

    public MainViewModel(AppState state, bool startTimers = true)
    {
        State = state;
        _sampler = new ProcessSampler(State.GameProcessNames);
        _recorder = new SessionRecorder(State.Sessions, () =>
        {
            var a = State.Active;
            return (a?.Id, a?.Name ?? "No playset", a?.Mods.ToList() ?? []);
        });
        Library = new(this);
        PlaysetsPage = new(this);
        Performance = new(this);
        Settings = new(this);
        Crashes = new(this);
        _currentPage = Library;
        _toastTimer.Tick += (_, _) => { Toast = null; _toastTimer.Stop(); };
        _perfTimer.Tick += (_, _) => TickPerf(DateTime.Now);
        Refresh();
        UpdateLive(null);
        if (startTimers)
        {
            _perfTimer.Start();
            if (State.Config.ModIoApiKey.Length > 0) _ = FetchModIoAsync(onlyMissing: true);
            if (InstalledExe is { } exe) Updater.CleanupOld(exe);
            if (State.Config.AutoUpdate)
            {
                DispatcherTimer.RunOnce(() => _ = CheckForUpdatesAsync(false), TimeSpan.FromSeconds(5));
                _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(false);
                _updateTimer.Start();
            }
        }
    }

    [ObservableProperty] private bool _modIoBusy;

    /// <summary>Gets real names for mod.io mods. Quiet on startup (onlyMissing), toasts when asked for.</summary>
    public async Task<int> FetchModIoAsync(bool onlyMissing)
    {
        if (ModIoBusy) return 0;
        ModIoBusy = true;
        try
        {
            var got = await State.RefreshModIoAsync(Http, onlyMissing);
            if (got > 0) Refresh();
            if (!onlyMissing)
                Notify(got > 0 ? $"Got names for {Plural(got, "mod")} from mod.io"
                    : State.ModIoMods.Any() ? "mod.io sent nothing back. Check the API key." : "No mod.io mods found in your folders",
                    got == 0 && State.ModIoMods.Any());
            return got;
        }
        catch (IOException e) { Notify("Couldn't save mod.io names: " + e.Message, true); return 0; }
        finally { ModIoBusy = false; }
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (UpdateBusy) return;
        var found = await Updater.CheckAsync(Http, AppVersion);
        if (found is not null) Update = found;
        else if (manual) Notify($"You're on the latest version ({AppVersionText})");
    }

    [RelayCommand]
    private Task CheckUpdates() => CheckForUpdatesAsync(true);

    [RelayCommand]
    private void DismissUpdate() => Update = null;

    /// <summary>Download, verify, swap the exe and restart. Outside an installed exe (or off Windows) opens the release page.</summary>
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (Update is not { } u || UpdateBusy) return;
        var exe = InstalledExe;
        if (exe is null || !OperatingSystem.IsWindows() || u.ExeUrl is null)
        {
            try { AppState.OpenFolder(u.PageUrl); } catch (Exception e) { Notify(e.Message, true); }
            return;
        }
        UpdateBusy = true;
        UpdateStatus = "Downloading…";
        try
        {
            var fresh = await Updater.DownloadAsync(Http, u, Path.GetDirectoryName(exe)!);
            UpdateStatus = "Restarting…";
            Updater.Swap(exe, fresh);
            Dispose();  // saves a session that is still recording
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (UnauthorizedAccessException)
        {
            Notify("Feevr can't write to its own folder. Move Feevr.exe somewhere you own, like Documents, and try again.", true);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Notify("Update failed: " + e.Message, true);
        }
        finally
        {
            UpdateBusy = false;
            UpdateStatus = "";
        }
    }

    partial void OnPageChanged(string value)
    {
        CurrentPage = value switch
        {
            "playsets" => PlaysetsPage,
            "performance" => Performance,
            "crashes" => Crashes,
            "settings" => Settings,
            _ => Library,
        };
        foreach (var n in new[] { nameof(IsLibrary), nameof(IsPlaysets), nameof(IsPerformance), nameof(IsSettings), nameof(IsCrashes) })
            OnPropertyChanged(n);
        if (value == "crashes") _ = Crashes.ReloadAsync();
    }

    public bool IsLibrary { get => Page == "library"; set { if (value) Page = "library"; } }
    public bool IsPlaysets { get => Page == "playsets"; set { if (value) Page = "playsets"; } }
    public bool IsPerformance { get => Page == "performance"; set { if (value) Page = "performance"; } }
    public bool IsSettings { get => Page == "settings"; set { if (value) Page = "settings"; } }
    public bool IsCrashes { get => Page == "crashes"; set { if (value) Page = "crashes"; } }

    partial void OnActivePlaysetChanged(PlaysetItem? value)
    {
        if (_refreshing) return;
        State.SetActive(value?.Id);
        Refresh();
    }

    public Playset? Active => State.Active;

    /// <summary>Reload everything from State. Cheap at mod-list sizes.</summary>
    public void Refresh()
    {
        var active = State.Active;
        Report = State.Check(active);
        _refreshing = true;
        try
        {
            Playsets.Clear();
            foreach (var p in State.Playsets())
            {
                var rep = State.Check(p);
                var worst = rep.Issues.GroupBy(i => i.ModId).ToDictionary(g => g.Key, g => g.Max(i => i.Level));
                var bars = rep.Order.Select(id => !worst.TryGetValue(id, out var w) || w == IssueLevel.Info ? Pal.Ok
                    : w == IssueLevel.Error ? Pal.Err : Pal.Warn).ToList();
                int e = rep.Count(IssueLevel.Error), w = rep.Count(IssueLevel.Warning);
                var sub = $"{Plural(p.Mods.Count, "mod")} · " + (e > 0 ? Plural(e, "problem") : w > 0 ? Plural(w, "warning") : "ready");
                Playsets.Add(new(p.Id, p.Name, p.Mods.Count, sub, p.Id == State.Config.Active, bars));
            }
            ActivePlayset = Playsets.FirstOrDefault(p => p.Id == State.Config.Active);
        }
        finally { _refreshing = false; }

        int err = Report.Count(IssueLevel.Error), warn = Report.Count(IssueLevel.Warning);
        (ActiveStatus, ActiveDot) = active is null ? ("No playset selected", Pal.Off)
            : err > 0 ? ($"{Plural(err, "problem")} to fix", Pal.Err)
            : warn > 0 ? ($"Ready · {Plural(warn, "warning")}", Pal.Warn)
            : ("Ready to play", Pal.Ok);
        var audits = Report.Order.Select(State.AuditOf).OfType<Audit>().ToList();
        TicketStats = $"{Report.Order.Count} MODS   {LibraryViewModel.Size(audits.Sum(a => a.TotalBytes))}   {audits.Sum(a => a.TickHookFiles.Count)} TICK";
        InstalledCount = State.Catalog.Count;

        Library.Reload();
        PlaysetsPage.Reload();
        Performance.Reload();
        Settings.Reload();
    }

    public static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    public void Notify(string text, bool error = false)
    {
        Toast = text;
        ToastIsError = error;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Toggles a mod in the active playset, creating one if none exists yet.</summary>
    public void ToggleMod(string modId, bool on)
    {
        var ps = State.Active;
        if (ps is null)
        {
            ps = State.CreatePlayset("My Playset");
            State.SetActive(ps.Id);
            Notify("Created \"My Playset\" and made it active");
        }
        State.Toggle(ps, modId, on);
        Refresh();
    }

    /// <summary>One sampling step. The timer calls it every second; tests call it directly.</summary>
    public void TickPerf(DateTime now)
    {
        var s = _sampler.Sample(now);
        var started = _recorder.Current?.Start;
        if (_recorder.Push(s, _sampler.ProcessName, now))
        {
            Performance.ReloadSessions();
            if (started is { } t && Crashes.CrashSince(t) is { } crash)
            {
                Notify(Crashes.Summary(crash), true);
                _ = Crashes.ReloadAsync();
            }
            else Notify("Session saved to Performance");
        }
        UpdateLive(s);
    }

    void UpdateLive(PerfSample? s)
    {
        var rec = _recorder.Current;
        IsGameRunning = rec is not null;
        if (s is not null)
        {
            Push(_cpu, s.Cpu);
            Push(_ram, s.RamMb);
            LiveCpu = $"{s.Cpu:0.0}";
            LiveRam = s.RamMb >= 1024 ? $"{s.RamMb / 1024:0.00}" : $"{s.RamMb:0}";
            LiveThreads = s.Threads.ToString();
            CpuSeries = _cpu.ToArray();
            RamSeries = _ram.ToArray();
            RamMax = Math.Max(1024, Math.Ceiling(_ram.Max() * 1.25 / 512) * 512);
        }
        if (rec is null)
        {
            if (s is null && _cpu.Count > 0 && _sampler.ProcessName is null)
            {
                _cpu.Clear();
                _ram.Clear();
                CpuSeries = [];
                RamSeries = [];
                LiveCpu = LiveRam = LiveThreads = "--";
            }
            LiveTitle = "Waiting for the game";
            LiveDetail = $"Watching for {string.Join(" or ", State.GameProcessNames().DefaultIfEmpty("(no process set)"))}. Recording starts on its own when it launches.";
            LiveElapsed = "00:00";
        }
        else
        {
            LiveTitle = $"Recording · {rec.PlaysetName}";
            LiveDetail = $"{rec.Process} · {Plural(rec.Mods.Count, "mod")} · started {rec.Start:HH:mm}";
            var d = rec.Duration;
            LiveElapsed = d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes:00}:{d.Seconds:00}";
        }
        LiveRamUnit = _ram.Count > 0 && _ram[^1] >= 1024 ? "GB" : "MB";
    }

    [ObservableProperty] private string _liveRamUnit = "MB";

    static void Push(List<double> list, double v)
    {
        list.Add(v);
        if (list.Count > LiveWindow) list.RemoveAt(0);
    }

    [RelayCommand]
    private void Rescan()
    {
        State.Rescan();
        Refresh();
        Notify($"Found {Plural(State.Catalog.Count, "mod")}");
    }

    [RelayCommand]
    private void Launch()
    {
        try
        {
            var n = State.WriteActivePlayset();
            State.Launch();
            Notify(n is { } c
                ? $"Launching with {Plural(c, "mod")} ready on the New Game screen. Saves keep their own mods."
                : "Launching Transport Fever 3. Recording starts when it is up.");
        }
        catch (Exception e) { Notify(e.Message, true); }
    }

    [RelayCommand]
    private void DismissToast() => Toast = null;

    public void Dispose()
    {
        _perfTimer.Stop();
        _updateTimer.Stop();
        _recorder.Finish();
        _sampler.Dispose();
    }
}
