using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tfmm.App.Theme;
using Tfmm.Core;

namespace Tfmm.App.ViewModels;

public partial class ModItem : ObservableObject
{
    readonly MainViewModel _main;
    public Mod Mod { get; }
    public ModItem(MainViewModel main, Mod mod, bool enabled, string level, string statusText)
    {
        _main = main;
        Mod = mod;
        _isEnabled = enabled;
        Level = level;
        StatusText = statusText;
    }

    public string Name => Mod.DisplayName;
    public string ModId => Mod.ModId;
    public string IdLine => $"{Mod.ModId} · r{Mod.Revision}";
    public string Source => Mod.Source.ToString();
    public string Mono => Monogram(Mod.DisplayName);
    public IBrush MonoBrush => IsEnabled ? Pal.AccentHi : Pal.Muted2;
    public IReadOnlyList<string> Tags => Mod.Tags.Take(3).ToList();
    public string Level { get; }
    public string StatusText { get; }
    public IBrush StatusDot => Pal.Dot(Level);
    public IBrush StatusFg => Pal.Fg(Level);
    public bool IsIssue => Level is "err" or "warn";

    [ObservableProperty] private bool _isEnabled;
    // Posted so the list isn't rebuilt from inside the toggle's own binding update.
    partial void OnIsEnabledChanged(bool value) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _main.ToggleMod(ModId, value));

    /// <summary>Two-letter tile text: initials of the first two words, else the first two letters.</summary>
    public static string Monogram(string name)
    {
        var words = name.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        var s = words.Length >= 2 ? $"{words[0][0]}{words[1][0]}" : name.Length >= 2 ? name[..2] : name;
        return s.ToUpperInvariant();
    }
}

public sealed record DepRow(string Label, string State, string Code, IBrush Ring);
public sealed record IssueRow(string Level, string ModName, string Message, IssueFix Fix = IssueFix.None, string? Target = null, string ModId = "")
{
    public string Stamp => Level switch { "err" => "ERR", "warn" => "WARN", _ => "NOTE" };
    public IBrush StampBg => Pal.Soft(Level);
    public IBrush StampFg => Pal.Fg(Level);
    public bool HasFix => Fix != IssueFix.None;
    public string FixText => Fix == IssueFix.Enable ? "Enable" : "Turn off";
}
public sealed record Badge(string Text, IBrush Bg, IBrush Fg);

public sealed class ModDetail
{
    public required ModItem Item { get; init; }
    public Mod Mod => Item.Mod;
    public string Byline => $"by {(Mod.Authors.Count > 0 ? string.Join(", ", Mod.Authors) : "unknown")} · {Mod.Source} · r{Mod.Revision}";
    public string Summary => string.IsNullOrWhiteSpace(Mod.Summary) ? "No description." : Mod.Summary;
    public bool HasProfile => Mod.ProfileUrl.Length > 0;
    public bool NameGuessed => Mod.NameGuessed && Mod.ModIoId is not null;
    public string ToggleText => Item.IsEnabled ? "Disable" : "Enable";
    public List<Badge> Badges { get; init; } = [];
    public List<DepRow> Deps { get; init; } = [];
    public List<DepRow> Incompat { get; init; } = [];
    public List<IssueRow> Issues { get; init; } = [];
    public bool HasDeps => Deps.Count > 0;
    public bool HasIncompat => Incompat.Count > 0;
    public bool HasIssues => Issues.Count > 0;
    public int Score { get; init; }
    public List<IBrush> Meter => Pal.Meter(Score);
    public string SizeText { get; init; } = "";
    public string HooksText { get; init; } = "";
    public string Measured { get; init; } = "";
    public bool HasMeasured => Measured.Length > 0;
    public string Shadowed { get; init; } = "";
    public bool HasShadowed => Shadowed.Length > 0;
}

public partial class LibraryViewModel : ObservableObject
{
    readonly MainViewModel _main;
    List<ModItem> _all = [];

    public ObservableCollection<ModItem> Items { get; } = [];
    public ObservableCollection<string> TagOptions { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private string _tag = AllTags;
    [ObservableProperty] private ModItem? _selected;
    [ObservableProperty] private ModDetail? _detail;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _enabledText = "00";
    [ObservableProperty] private string _installedText = "00";
    [ObservableProperty] private string _problemText = "00";
    [ObservableProperty] private string _diskText = "0";
    [ObservableProperty] private string _diskUnit = "MB";
    [ObservableProperty] private string _tickText = "00";
    [ObservableProperty] private string _allLabel = "All";
    [ObservableProperty] private string _enabledLabel = "Enabled";
    [ObservableProperty] private string _issuesLabel = "Needs attention";

    public const string AllTags = "All tags";

    public LibraryViewModel(MainViewModel main) => _main = main;

    public bool FilterAll { get => Filter == "all"; set { if (value) Filter = "all"; } }
    public bool FilterEnabled { get => Filter == "enabled"; set { if (value) Filter = "enabled"; } }
    public bool FilterIssues { get => Filter == "issues"; set { if (value) Filter = "issues"; } }

    partial void OnSearchChanged(string value) => Apply();
    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(FilterAll));
        OnPropertyChanged(nameof(FilterEnabled));
        OnPropertyChanged(nameof(FilterIssues));
        Apply();
    }
    partial void OnTagChanged(string value) => Apply();
    partial void OnSelectedChanged(ModItem? value) => Detail = value is null ? null : BuildDetail(value);

    static string Two(int n) => n.ToString("00");

    public void Reload()
    {
        var keep = Selected?.ModId;
        var active = _main.Active?.Mods.ToHashSet() ?? [];
        var byMod = _main.Report.Issues.GroupBy(i => i.ModId).ToDictionary(g => g.Key, g => g.ToList());
        _all = _main.State.ModsSorted().Select(m =>
        {
            var on = active.Contains(m.ModId);
            byMod.TryGetValue(m.ModId, out var iss);
            int err = iss?.Count(i => i.Level == IssueLevel.Error) ?? 0;
            int warn = (iss?.Count(i => i.Level == IssueLevel.Warning) ?? 0) + (on ? 0 : m.Warnings.Count);
            var level = err > 0 ? "err" : warn > 0 ? "warn" : on ? "ok" : "idle";
            var text = err > 0 ? MainViewModel.Plural(err, "error") : warn > 0 ? MainViewModel.Plural(warn, "warning") : on ? "Ready" : "Off";
            return new ModItem(_main, m, on, level, text);
        }).ToList();

        var enabled = _all.Count(i => i.IsEnabled);
        var issues = _all.Count(i => i.IsIssue);
        var audits = _main.Report.Order.Select(_main.State.AuditOf).OfType<Audit>().ToList();
        var bytes = audits.Sum(a => a.TotalBytes);
        EnabledText = Two(enabled);
        InstalledText = $" / {_all.Count}";
        ProblemText = Two(_main.Report.Count(IssueLevel.Error));
        (DiskText, DiskUnit) = bytes >= 1L << 30 ? ($"{bytes / (double)(1L << 30):0.0}", " GB") : ($"{bytes / 1048576.0:0.0}", " MB");
        TickText = Two(audits.Sum(a => a.TickHookFiles.Count));
        AllLabel = $"All  {_all.Count}";
        EnabledLabel = $"Enabled  {enabled}";
        IssuesLabel = $"Needs attention  {issues}";

        var tag = Tag;
        TagOptions.Clear();
        TagOptions.Add(AllTags);
        foreach (var t in _all.SelectMany(i => i.Mod.Tags).Distinct().Order()) TagOptions.Add(t);
        Tag = TagOptions.Contains(tag) ? tag : AllTags;
        Apply(keep);
    }

    void Apply() => Apply(Selected?.ModId);

    void Apply(string? keep)
    {
        var q = Search.Trim();
        var list = _all.Where(i =>
            (q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.ModId.Contains(q, StringComparison.OrdinalIgnoreCase)
             || i.Mod.Authors.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))) &&
            (Filter != "enabled" || i.IsEnabled) &&
            (Filter != "issues" || i.IsIssue) &&
            (Tag == AllTags || i.Mod.Tags.Contains(Tag))).ToList();
        Items.Clear();
        foreach (var i in list) Items.Add(i);
        IsEmpty = Items.Count == 0;
        Selected = Items.FirstOrDefault(i => i.ModId == keep) ?? Items.FirstOrDefault();
    }

    ModDetail BuildDetail(ModItem item)
    {
        var m = item.Mod;
        var s = _main.State;
        var active = _main.Active?.Mods.ToHashSet() ?? [];
        var badges = new List<Badge>();
        void Add(string t, string level) => badges.Add(new(t, Pal.Soft(level), level == "idle" ? Pal.Muted : Pal.Fg(level)));
        if (m.Cosmetic) Add("Cosmetic", "ok");
        if (m.SeverityAdd == "Critical") Add("Critical to add", "err");
        else if (m.SeverityAdd == "Warning") Add("Risky to add", "warn");
        if (m.SeverityRemove == "Critical") Add("Save-breaking to remove", "err");
        foreach (var ls in m.LoadScripts)
            Add(ls.Phase switch { "preRunScript" => "Pre-run script", "postRunScript" => "Post-run script", _ => "Run script" }, "info");
        if (m.HasRootScript && m.LoadScripts.Count == 0) Add("Script", "info");
        foreach (var t in m.Tags) Add(t, "idle");

        var deps = m.Dependencies.Select(d =>
        {
            s.Catalog.TryGetValue(d.Ref.ModId, out var dm);
            var (state, code, lvl) = dm is null ? (d.Optional ? "Optional, not installed" : "Not installed", d.Optional ? "OPT" : "MISS", d.Optional ? "idle" : "err")
                : !d.Ref.Accepts(dm.Revision) ? ($"Wrong revision (r{dm.Revision}){d.Ref.RangeText}", "REV", "err")
                : active.Contains(dm.ModId) ? ("Enabled", "ON", "ok")
                : d.Optional ? ("Optional", "OPT", "idle") : ("Installed, not enabled", "OFF", "warn");
            return new DepRow(dm?.DisplayName ?? d.Label, state, code, lvl == "idle" ? Pal.Dim : Pal.Dot(lvl));
        }).ToList();

        var inc = m.Incompatibilities.Select(r =>
        {
            s.Catalog.TryGetValue(r.ModId, out var om);
            var conflict = om != null && active.Contains(r.ModId) && r.Accepts(om.Revision) && active.Contains(m.ModId);
            return new DepRow(om?.DisplayName ?? r.ModId, conflict ? "Conflict in this playset" : om is null ? "Not installed" : "Installed",
                conflict ? "CLASH" : "", conflict ? Pal.Err : Pal.Dim);
        }).ToList();

        var issues = _main.Report.Issues.Where(i => i.ModId == m.ModId).Select(ToRow).ToList();
        if (!active.Contains(m.ModId)) issues.AddRange(m.Warnings.Select(w => new IssueRow("warn", m.DisplayName, w)));

        var a = s.AuditOf(m.ModId);
        var hooks = a?.TickHookFiles.Count ?? 0;
        var (timings, _) = s.ProbeTimings();
        var measured = timings.TryGetValue(m.ModId, out var t2)
            ? $"Measured {t2.TickMs:0.00} ms per tick · {t2.GuiMs:0.00} ms GUI · {t2.LoadMs:0} ms load" : "";
        return new ModDetail
        {
            Item = item, Badges = badges, Deps = deps, Incompat = inc, Issues = issues,
            Score = a?.Score ?? 0,
            SizeText = a is null ? "" : $"{Size(a.TotalBytes)} · {MainViewModel.Plural(a.Files, "file")}",
            HooksText = hooks == 0 ? "No per-tick scripts" : MainViewModel.Plural(hooks, "per-tick script"),
            Measured = measured,
            Shadowed = string.Join("\n", s.Shadowed.Where(x => x.ModId == m.ModId).Select(x => $"{x.Source}: {x.Path}")),
        };
    }

    public IssueRow ToRow(Issue i) =>
        new(i.Level switch { IssueLevel.Error => "err", IssueLevel.Warning => "warn", _ => "info" },
            _main.State.Catalog.TryGetValue(i.ModId, out var m) ? m.DisplayName : i.ModId, i.Message, i.Fix, i.Target, i.ModId);

    public static string Size(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0.0} KB",
        _ => $"{b} B",
    };

    [RelayCommand]
    private void OpenFolder()
    {
        if (Detail is null) return;
        try { AppState.OpenFolder(Detail.Mod.Path); }
        catch (Exception e) { _main.Notify(e.Message, true); }
    }

    [RelayCommand]
    private void OpenProfile()
    {
        if (Detail is not { HasProfile: true } d) return;
        try { AppState.OpenFolder(d.Mod.ProfileUrl); }
        catch (Exception e) { _main.Notify(e.Message, true); }
    }

    [RelayCommand]
    private void ToggleSelected()
    {
        if (Selected is { } s) s.IsEnabled = !s.IsEnabled;
    }
}
