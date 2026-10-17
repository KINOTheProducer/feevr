using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tfmm.App.Controls;
using Tfmm.App.Theme;
using Tfmm.Core;

namespace Tfmm.App.ViewModels;

public sealed record SessionRow(string Id, string Date, string Time, string Playset, string Duration, string AvgCpu, string P95Cpu, string PeakRam, List<IBrush> Meter);

public sealed record SessionDetail(string Title, string Subtitle, IReadOnlyList<double> Cpu, IReadOnlyList<double> Ram, double RamMax,
    string AvgCpu, string P95Cpu, string AvgRam, string PeakRam, string Mods, string RamMaxText);

public sealed record CompareRow(string Name, string Meta, List<BarPart> CpuBar, string Cpu, List<BarPart> RamBar, string Ram, string Delta, IBrush DeltaFg);

public sealed record KindRow(string Name, string Size, IBrush Brush);

public sealed class ModCostRow
{
    public required string Name { get; init; }
    public required string ModId { get; init; }
    public string Rank { get; init; } = "";
    public int Score { get; init; }
    public List<IBrush> Meter => Pal.Meter(Score);
    public string Size { get; init; } = "";
    public string Measured { get; init; } = "–";
    public IBrush MeasuredFg { get; init; } = Pal.Faint2;
    public string FlagText { get; init; } = "";
    public IBrush FlagFg { get; init; } = Pal.Faint2;
    public List<string> Flags { get; init; } = [];
    public bool Clean => Flags.Count == 0;
    public string FilesText { get; init; } = "";
    public List<BarPart> KindBar { get; init; } = [];
    public List<KindRow> Kinds { get; init; } = [];
    public List<KindRow> Largest { get; init; } = [];
    public string Timing { get; init; } = "";
    public bool HasTiming => Timing.Length > 0;
}

public partial class PerformanceViewModel : ObservableObject
{
    readonly MainViewModel _main;
    public PerformanceViewModel(MainViewModel main) => _main = main;

    /// <summary>Live readings live on the main VM so recording runs on every page.</summary>
    public MainViewModel Main => _main;

    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public ObservableCollection<CompareRow> Compare { get; } = [];
    public ObservableCollection<ModCostRow> Rows { get; } = [];
    public ObservableCollection<BarPart> Budget { get; } = [];

    [ObservableProperty] private SessionRow? _selectedSession;
    [ObservableProperty] private SessionDetail? _sessionDetail;
    [ObservableProperty] private bool _noSessions = true;
    [ObservableProperty] private bool _canCompare;
    [ObservableProperty] private string _compareHint = "";

    [ObservableProperty] private string _scope = "active";
    [ObservableProperty] private ModCostRow? _selected;
    [ObservableProperty] private string _modCount = "00";
    [ObservableProperty] private string _disk = "0 B";
    [ObservableProperty] private string _ticks = "00";
    [ObservableProperty] private string _flagged = "00";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _probeStatus = "";
    [ObservableProperty] private bool _hasProbe;

    List<PerfSession> _sessions = [];

    public bool ScopeActive { get => Scope == "active"; set { if (value) Scope = "active"; } }
    public bool ScopeAll { get => Scope == "all"; set { if (value) Scope = "all"; } }

    partial void OnScopeChanged(string value)
    {
        OnPropertyChanged(nameof(ScopeActive));
        OnPropertyChanged(nameof(ScopeAll));
        Reload();
    }

    partial void OnSelectedSessionChanged(SessionRow? value) =>
        SessionDetail = value is null ? null : Detail(_sessions.First(s => s.Id == value.Id));

    public void Reload()
    {
        ReloadSessions();
        ReloadCost();
    }

    // ---------- sessions ----------

    public void ReloadSessions()
    {
        var keep = SelectedSession?.Id;
        _sessions = _main.State.Sessions.All();
        Sessions.Clear();
        foreach (var s in _sessions)
            Sessions.Add(new(s.Id, s.Start.ToString("ddd d MMM"), s.Start.ToString("HH:mm"), s.PlaysetName, Dur(s.Duration),
                $"{s.AvgCpu:0}%", $"{s.P95Cpu:0}%", Ram(s.PeakRam), Pal.Meter(s.P95Cpu, 100, 10)));
        NoSessions = Sessions.Count == 0;
        SelectedSession = Sessions.FirstOrDefault(r => r.Id == keep) ?? Sessions.FirstOrDefault();

        Compare.Clear();
        var groups = PerfStats.ByPlayset(_sessions);
        CanCompare = groups.Count >= 2;
        CompareHint = groups.Count switch
        {
            0 => "Play a session to start building a baseline.",
            1 => $"Only {groups[0].Name} has sessions. Play another playset to compare.",
            _ => "Weighted by play time. Worst-case CPU is the 95th percentile: the slow moments you notice.",
        };
        if (groups.Count == 0) return;
        var maxCpu = Math.Max(1, groups.Max(g => g.P95Cpu));
        var maxRam = Math.Max(1, groups.Max(g => g.PeakRam));
        var best = groups.MinBy(g => g.P95Cpu)!;
        foreach (var g in groups)
        {
            var delta = g.P95Cpu - best.P95Cpu;
            Compare.Add(new(g.Name, $"{MainViewModel.Plural(g.Sessions, "session")} · {Dur(g.PlayTime)}",
                Bar(g.P95Cpu, maxCpu, g == best ? Pal.Ok : delta > 10 ? Pal.Err : Pal.Warn), $"{g.P95Cpu:0}%",
                Bar(g.PeakRam, maxRam, Pal.Info), Ram(g.PeakRam),
                groups.Count < 2 ? "" : g == best ? "Lightest" : $"+{delta:0} pts CPU vs {best.Name}",
                g == best ? Pal.OkText : Pal.Warn));
        }
    }

    static List<BarPart> Bar(double v, double max, IBrush b) => [new(v, b), new(Math.Max(0, max - v), Pal.Empty)];

    SessionDetail Detail(PerfSession s)
    {
        var cpu = Downsample(s.Samples.Select(x => x.Cpu).ToList(), 240);
        var ram = Downsample(s.Samples.Select(x => x.RamMb).ToList(), 240);
        var ramMax = Math.Max(1024, Math.Ceiling(s.PeakRam * 1.15 / 1024) * 1024);
        return new($"{s.PlaysetName}", $"{s.Start:dddd d MMMM, HH:mm} · {Dur(s.Duration)} · {s.Process}",
            cpu, ram, ramMax, $"{s.AvgCpu:0.0}%", $"{s.P95Cpu:0.0}%", Ram(s.AvgRam), Ram(s.PeakRam),
            s.Mods.Count == 0 ? "No mods" : string.Join(", ", s.Mods.Select(id => _main.State.Catalog.TryGetValue(id, out var m) ? m.DisplayName : id)),
            Ram(ramMax));
    }

    /// <summary>Bucket average down to at most n points so long sessions chart quickly.</summary>
    public static List<double> Downsample(List<double> v, int n)
    {
        if (v.Count <= n) return v;
        var res = new List<double>(n);
        for (int i = 0; i < n; i++)
        {
            int a = i * v.Count / n, b = Math.Max(a + 1, (i + 1) * v.Count / n);
            double sum = 0;
            for (int j = a; j < b; j++) sum += v[j];
            res.Add(sum / (b - a));
        }
        return res;
    }

    static string Dur(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}m" : $"{Math.Max(0, d.Minutes)}m {d.Seconds:00}s";
    static string Ram(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";

    [RelayCommand]
    private void DeleteSession(SessionRow row)
    {
        _main.State.Sessions.Delete(row.Id);
        ReloadSessions();
        _main.Notify("Session deleted");
    }

    // ---------- per-mod cost ----------

    void ReloadCost()
    {
        var keep = Selected?.ModId;
        var s = _main.State;
        var (timings, probeError) = s.ProbeTimings();
        HasProbe = timings.Count > 0;
        ProbeStatus = probeError ?? (HasProbe
            ? $"Measured column comes from the probe mod ({timings.Values.Max(t => t.Ticks):N0} ticks)."
            : "Static estimate from mod files. Add the probe mod for measured per-mod timings.");

        var ids = Scope == "active" ? (_main.Active?.Mods ?? []).Where(s.Catalog.ContainsKey).ToList() : s.Catalog.Keys.ToList();
        var maxTick = timings.Count == 0 ? 0 : timings.Values.Max(t => t.TickMs + t.GuiMs);
        var rows = ids.Select(id =>
        {
            var a = s.AuditOf(id)!;
            var total = Math.Max(a.TotalBytes, 1);
            timings.TryGetValue(id, out var t);
            var ms = t is null ? 0 : t.TickMs + t.GuiMs;
            var kinds = a.BytesByKind.OrderByDescending(k => k.Value).ToList();
            IBrush KindBrush(string k) => k switch { "Textures" => Pal.KindTextures, "Models" => Pal.KindModels, "Scripts" => Pal.KindScripts, _ => Pal.KindData };
            return new ModCostRow
            {
                Name = s.Catalog[id].DisplayName, ModId = id, Score = a.Score,
                Size = LibraryViewModel.Size(a.TotalBytes),
                Measured = t is null ? "–" : $"{ms:0.00} ms",
                MeasuredFg = t is null ? Pal.Faint2 : ms >= Math.Max(0.5, maxTick * 0.6) ? Pal.ErrText : ms > 0.1 ? Pal.Warn : Pal.OkText,
                FlagText = a.Flags.Count == 0 ? "Clean" : MainViewModel.Plural(a.Flags.Count, "flag"),
                FlagFg = a.Flags.Count == 0 ? Pal.Faint2 : Pal.Warn,
                Flags = a.Flags,
                FilesText = $"{LibraryViewModel.Size(a.TotalBytes)} · {MainViewModel.Plural(a.Files, "file")}",
                KindBar = kinds.Select(k => new BarPart(k.Value / (double)total, KindBrush(k.Key))).ToList(),
                Kinds = kinds.Select(k => new KindRow(k.Key, LibraryViewModel.Size(k.Value), KindBrush(k.Key))).ToList(),
                Largest = a.Largest.Select(l => new KindRow(l.Path, LibraryViewModel.Size(l.Bytes), Pal.Amber)).ToList(),
                Timing = t is null ? "" : $"{t.TickMs:0.00} ms per tick · {t.GuiMs:0.00} ms GUI · {t.LoadMs:0.0} ms at load",
            };
        })
        // measured cost first when we have it, else the static score
        .OrderByDescending(r => timings.TryGetValue(r.ModId, out var t) ? t.TickMs + t.GuiMs : -1)
        .ThenByDescending(r => r.Score).ThenBy(r => r.Name).ToList();

        Rows.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            Rows.Add(new ModCostRow
            {
                Name = r.Name, ModId = r.ModId, Rank = (i + 1).ToString("00"), Score = r.Score, Size = r.Size,
                Measured = r.Measured, MeasuredFg = r.MeasuredFg, FlagText = r.FlagText, FlagFg = r.FlagFg, Flags = r.Flags,
                FilesText = r.FilesText, KindBar = r.KindBar, Kinds = r.Kinds, Largest = r.Largest, Timing = r.Timing,
            });
        }

        // share bar: measured ms per tick when the probe has data (unmeasured mods get no share), else static score
        Budget.Clear();
        var weights = Rows.Select(r => HasProbe
            ? timings.TryGetValue(r.ModId, out var t) ? t.TickMs + t.GuiMs : 0
            : r.Score + 2.0).ToList();
        var sum = weights.Sum();
        for (int i = 0; i < Rows.Count; i++)
        {
            var pct = sum <= 0 ? 0 : weights[i] / sum * 100;
            Budget.Add(new BarPart(weights[i], Pal.Shares[Math.Min(i, Pal.Shares.Length - 1)],
                pct >= 12 ? $"{Rows[i].Name} {pct:0}%" : "", i == 0 ? Pal.Ink : Pal.Amber));
        }

        var audits = ids.Select(id => s.AuditOf(id)!).ToList();
        ModCount = ids.Count.ToString("00");
        Disk = LibraryViewModel.Size(audits.Sum(a => a.TotalBytes));
        Ticks = audits.Sum(a => a.TickHookFiles.Count).ToString("00");
        Flagged = audits.Count(a => a.Flags.Count > 0).ToString("00");
        IsEmpty = Rows.Count == 0;
        Selected = Rows.FirstOrDefault(r => r.ModId == keep) ?? Rows.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenSettings() => _main.Page = "settings";
}
