using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tfmm.App.Theme;
using Tfmm.Core;

namespace Tfmm.App.ViewModels;

/// <summary>One station on the route map. A spur is a required dependency that is not on the line.</summary>
public sealed record Stop(int Index, string Name, string ModId, string Level, string Chip, bool IsFirst, bool IsLast, string? Spur, double SpurHeight = 98)
{
    public string Number => Index.ToString("00");
    public IBrush Core => Pal.Dot(Level);
    public IBrush ChipBg => Pal.Soft(Level);
    public IBrush ChipFg => Pal.Fg(Level);
    public bool NotFirst => !IsFirst;
    public bool NotLast => !IsLast;
    public bool HasSpur => Spur is not null;
}

public partial class PlaysetsViewModel : ObservableObject
{
    readonly MainViewModel _main;
    public PlaysetsViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<PlaysetItem> Items => _main.Playsets;
    public ObservableCollection<IssueRow> Issues { get; } = [];
    public ObservableCollection<Stop> Stops { get; } = [];

    [ObservableProperty] private PlaysetItem? _selected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _subline = "";
    [ObservableProperty] private string _bannerTag = "";
    [ObservableProperty] private IBrush _bannerBg = Pal.BannerOk;
    [ObservableProperty] private IBrush _bannerLine = Pal.BannerOkLine;
    [ObservableProperty] private IBrush _bannerTagBg = Pal.Ok;
    [ObservableProperty] private string _lineText = "";
    [ObservableProperty] private int _lineLen = 1;
    [ObservableProperty] private bool _canFix;
    [ObservableProperty] private string _fixText = "";
    [ObservableProperty] private bool _canPrune;
    [ObservableProperty] private bool _noIssues;
    [ObservableProperty] private bool _noStops;

    string? _selectedId;
    bool _loading;

    partial void OnSelectedChanged(PlaysetItem? value)
    {
        if (_loading || _main.IsRefreshing) return;
        _selectedId = value?.Id;
        Load();
    }

    partial void OnNameChanged(string value)
    {
        if (_loading || _main.State.GetPlayset(_selectedId) is not { } ps || ps.Name == value.Trim() || value.Trim().Length == 0) return;
        ps.Name = value.Trim();
        _main.State.SavePlayset(ps);
        _main.Refresh();
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            _selectedId ??= _main.State.Config.Active ?? Items.FirstOrDefault()?.Id;
            Selected = Items.FirstOrDefault(p => p.Id == _selectedId) ?? Items.FirstOrDefault();
            _selectedId = Selected?.Id;
        }
        finally { _loading = false; }
        Load();
    }

    void Load()
    {
        var ps = _main.State.GetPlayset(_selectedId);
        _loading = true;
        Name = ps?.Name ?? "";
        _loading = false;
        HasSelection = ps != null;
        IsActive = ps != null && ps.Id == _main.State.Config.Active;
        Issues.Clear();
        Stops.Clear();
        if (ps is null) return;

        var cat = _main.State.Catalog;
        var rep = _main.State.Check(ps);
        int errors = rep.Count(IssueLevel.Error), warnings = rep.Count(IssueLevel.Warning);
        NoIssues = rep.Issues.Count == 0;
        foreach (var i in rep.Issues.OrderByDescending(i => i.Level)) Issues.Add(_main.Library.ToRow(i));

        var byMod = rep.Issues.GroupBy(i => i.ModId).ToDictionary(g => g.Key, g => g.ToList());
        var on = rep.Order.ToHashSet();
        for (int n = 0; n < rep.Order.Count; n++)
        {
            var id = rep.Order[n];
            var m = cat[id];
            byMod.TryGetValue(id, out var iss);
            var worst = iss?.Where(i => i.Level != IssueLevel.Info).Select(i => i.Level).DefaultIfEmpty(IssueLevel.Info).Max() ?? IssueLevel.Info;
            var level = worst == IssueLevel.Error ? "err" : worst == IssueLevel.Warning ? "warn" : "ok";
            var chip = iss?.FirstOrDefault(i => i.Level == worst && worst != IssueLevel.Info) switch
            {
                null => "Ready",
                { Fix: IssueFix.Enable } => "Missing deps",
                { Message: var msg } when msg.StartsWith("Incompatible") => "Conflict",
                { Message: var msg } when msg.StartsWith("Needs") => "Wrong revision",
                { Message: var msg } when msg.Contains("save", StringComparison.OrdinalIgnoreCase) => "Save impact",
                _ => worst == IssueLevel.Error ? "Error" : "Warning",
            };
            var spur = m.Dependencies.FirstOrDefault(d => !d.Optional && !on.Contains(d.Ref.ModId));
            var spurName = spur is null ? null : cat.TryGetValue(spur.Ref.ModId, out var sm) ? sm.DisplayName : spur.Label;
            Stops.Add(new(n + 1, m.DisplayName, id, level, chip, n == 0, n == rep.Order.Count - 1, spurName));
        }
        // only reserve room above the line when some stop has a branch
        if (!Stops.Any(st => st.HasSpur))
        {
            var flat = Stops.Select(st => st with { SpurHeight = 18 }).ToList();
            Stops.Clear();
            foreach (var st in flat) Stops.Add(st);
        }
        LineLen = Math.Max(1, Stops.Count);
        NoStops = Stops.Count == 0;
        LineText = Stops.Count == 0 ? "Empty route. Enable mods in the Library." : $"{MainViewModel.Plural(Stops.Count, "stop")} · loads left to right";

        var fix = _main.State.Check(ps, addDeps: true);
        var adds = fix.Order.Count - rep.Order.Count;
        CanFix = adds > 0;
        FixText = adds == 1 ? "Enable 1 dependency" : $"Enable {adds} dependencies";
        CanPrune = rep.Missing.Any(ps.Mods.Contains);

        if (errors > 0)
        {
            (BannerBg, BannerLine, BannerTagBg, BannerTag) = (Pal.BannerErr, Pal.BannerErrLine, Pal.Err, $"{errors} TO FIX");
            Headline = "This playset will not load cleanly";
        }
        else if (warnings > 0)
        {
            (BannerBg, BannerLine, BannerTagBg, BannerTag) = (Pal.BannerWarn, Pal.BannerWarnLine, Pal.Warn, MainViewModel.Plural(warnings, "WARNING").ToUpperInvariant());
            Headline = "Ready to play, with warnings";
        }
        else
        {
            (BannerBg, BannerLine, BannerTagBg, BannerTag) = (Pal.BannerOk, Pal.BannerOkLine, Pal.Ok, "READY");
            Headline = "Ready to play";
        }
        Subline = (rep.Missing.Count > 0 ? $"{MainViewModel.Plural(rep.Missing.Count, "mod")} not installed. " : "")
            + (errors > 0 ? "Fix the red rows below, or use the one-click fixes." : "Everything this playset needs is on the line.");
    }

    [RelayCommand]
    private void ApplyFix(IssueRow row)
    {
        if (_main.State.GetPlayset(_selectedId) is not { } ps) return;
        if (row.Fix == IssueFix.Enable && row.Target is { } t)
        {
            _main.State.Toggle(ps, t, true);
            _main.Notify($"Enabled {(_main.State.Catalog.TryGetValue(t, out var m) ? m.DisplayName : t)}");
        }
        else if (row.Fix == IssueFix.Disable)
        {
            _main.State.Toggle(ps, row.ModId, false);
            _main.Notify($"Turned off {row.ModName}");
        }
        _main.Refresh();
    }

    [RelayCommand]
    private void New()
    {
        var ps = _main.State.CreatePlayset("New Playset");
        _selectedId = ps.Id;
        _main.Refresh();
        _main.Notify("Playset created");
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (_main.State.GetPlayset(_selectedId) is not { } ps) return;
        var copy = _main.State.CreatePlayset(ps.Name + " copy", ps.Mods);
        _selectedId = copy.Id;
        _main.Refresh();
        _main.Notify($"Duplicated \"{ps.Name}\"");
    }

    [RelayCommand]
    private void Delete()
    {
        if (_main.State.GetPlayset(_selectedId) is not { } ps) return;
        _main.State.DeletePlayset(ps.Id);
        _selectedId = null;
        _main.Refresh();
        _main.Notify($"Deleted \"{ps.Name}\"");
    }

    [RelayCommand]
    private void MakeActive()
    {
        if (_selectedId is null) return;
        _main.State.SetActive(_selectedId);
        _main.Refresh();
        _main.Notify($"\"{Name}\" is now the active playset");
    }

    [RelayCommand]
    private void FixDeps()
    {
        if (_main.State.GetPlayset(_selectedId) is not { } ps) return;
        var n = _main.State.ApplyDeps(ps);
        _main.Refresh();
        _main.Notify(n == 0 ? "Nothing to add" : n == 1 ? "Enabled 1 dependency" : $"Enabled {n} dependencies");
    }

    [RelayCommand]
    private void Prune()
    {
        if (_main.State.GetPlayset(_selectedId) is not { } ps) return;
        var before = ps.Mods.Count;
        ps.Mods = ps.Mods.Where(_main.State.Catalog.ContainsKey).ToList();
        _main.State.SavePlayset(ps);
        _main.Refresh();
        _main.Notify($"Removed {MainViewModel.Plural(before - ps.Mods.Count, "missing mod")}");
    }
}
