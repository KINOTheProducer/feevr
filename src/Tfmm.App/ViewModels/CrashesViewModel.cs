using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tfmm.App.Theme;
using Tfmm.Core;

namespace Tfmm.App.ViewModels;

public sealed class CrashItem(CrashReport r, string level, string title, string headline)
{
    public CrashReport Report { get; } = r;
    public string Level { get; } = level;
    public string Title { get; } = title;
    public string Headline { get; } = headline;
    public string FileName => Path.GetFileName(Report.Path);
    public string TimeText => Report.Time.ToString("MMM d · HH:mm", CultureInfo.InvariantCulture);
    public string Sub => $"{FileName} · {MainViewModel.Plural(Report.Errors.Sum(e => e.Count), "error")}";
    public IBrush Dot => Pal.Dot(Level);
    public IBrush Fg => Pal.Fg(Level);
}

public sealed record SuspectRow(string ModId, string Name, string Why, bool Installed, bool Enabled, bool InFatal)
{
    public string Mono => ModItem.Monogram(Name);
    public IBrush Ring => InFatal ? Pal.Err : Pal.Warn;
    public string StateText => !Installed ? "Not installed" : Enabled ? "Enabled" : "Off";
}

public sealed record ErrorRow(CrashError Error, string ModsText)
{
    public string Stamp => Error.Fatal ? "FATAL" : Error.Kind == "Lua error" ? "LUA" : "ERR";
    public IBrush StampBg => Pal.Soft(Error.Fatal ? "err" : "warn");
    public IBrush StampFg => Pal.Fg(Error.Fatal ? "err" : "warn");
    public string Message => Error.Message.Length == 0 ? Error.Kind : Error.Message;
    public string Meta => $"line {Error.Line}" + (Error.Count > 1 ? $" · ×{Error.Count}" : "") + (ModsText.Length > 0 ? $" · {ModsText}" : "");
    public string Trace => string.Join('\n', Error.Context);
    public bool HasTrace => Error.Context.Count > 0;
}

public sealed class CrashDetail
{
    public required CrashItem Item { get; init; }
    public CrashReport Report => Item.Report;
    public List<SuspectRow> Suspects { get; init; } = [];
    public List<ErrorRow> Errors { get; init; } = [];
    public bool HasSuspects => Suspects.Count > 0;
    public bool HasErrors => Errors.Count > 0;
    public string Tail => string.Join('\n', Report.Tail);
    public string Facts => $"{Report.Time:yyyy-MM-dd HH:mm:ss} · {MainViewModel.Plural(Report.Lines, "line")} · {AppStateFmt.Bytes(Report.Bytes)}" +
                           (Report.Truncated ? " · read the last 16 MB" : "");
}

static class AppStateFmt
{
    public static string Bytes(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.0} MB" : b >= 1024 ? $"{b / 1024.0:0} KB" : $"{b} B";
}

public partial class CrashesViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly Dictionary<string, (DateTime Stamp, long Len, CrashReport Report)> _cache = [];
    readonly Lock _lock = new();

    public ObservableCollection<CrashItem> Items { get; } = [];
    [ObservableProperty] private CrashItem? _selected;
    [ObservableProperty] private CrashDetail? _detail;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _crashCount = "00";
    [ObservableProperty] private string _lastCrash = "--";
    [ObservableProperty] private string _topSuspect = "--";
    [ObservableProperty] private string _whereText = "";

    public CrashesViewModel(MainViewModel main) => _main = main;

    /// <summary>Finds and parses logs. Safe off the UI thread; unchanged files come from the cache.</summary>
    public List<CrashReport> Scan()
    {
        var catalog = _main.State.Catalog;
        var files = CrashLogs.Find(_main.State.CrashDirs());
        var list = new List<CrashReport>();
        foreach (var f in files)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(f.FullName, out var c) && c.Stamp == f.LastWriteTimeUtc && c.Len == f.Length)
                {
                    list.Add(c.Report);
                    continue;
                }
            }
            var r = CrashLogs.Read(f.FullName, catalog);
            lock (_lock) _cache[f.FullName] = (f.LastWriteTimeUtc, f.Length, r);
            list.Add(r);
        }
        return list;
    }

    public async Task ReloadAsync()
    {
        if (Busy) return;
        Busy = true;
        try { Apply(await Task.Run(Scan)); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private Task Refresh() => ReloadAsync();

    public void Apply(List<CrashReport> reports)
    {
        var keep = Selected?.Report.Path;
        Items.Clear();
        foreach (var r in reports) Items.Add(ToItem(r));
        IsEmpty = Items.Count == 0;
        var crashes = Items.Where(i => i.Report.Crashed).ToList();
        CrashCount = crashes.Count.ToString("00", CultureInfo.InvariantCulture);
        LastCrash = crashes.Count == 0 ? "--" : crashes[0].Report.Time.ToString("MMM d", CultureInfo.InvariantCulture);
        TopSuspect = crashes.SelectMany(c => c.Report.Suspects.Take(1)).GroupBy(s => s.ModId)
            .OrderByDescending(g => g.Count()).Select(g => NameOf(g.Key)).FirstOrDefault() ?? "--";
        var dirs = _main.State.CrashDirs();
        WhereText = dirs.Count == 0 ? "No log folder found. Set it in Settings." : string.Join("\n", dirs);
        // open on the newest crash, not a clean live log
        Selected = Items.FirstOrDefault(i => i.Report.Path == keep) ?? crashes.FirstOrDefault() ?? Items.FirstOrDefault();
        Detail = Selected is null ? null : BuildDetail(Selected);
    }

    partial void OnSelectedChanged(CrashItem? value)
    {
        if (value is not null) Detail = BuildDetail(value);
    }

    string NameOf(string modId) => _main.State.Catalog.TryGetValue(modId, out var m) ? m.DisplayName : modId;

    CrashItem ToItem(CrashReport r)
    {
        var top = r.Suspects.FirstOrDefault();
        var (level, title) = r.Crashed ? ("err", "Crash") : r.Errors.Count > 0 ? ("warn", "Errors logged") : ("ok", "Clean run");
        var headline = top is not null ? $"Likely cause: {NameOf(top.ModId)}"
            : r.Crashed ? "No mod named in the errors" : r.Errors.Count > 0 ? "Errors, no mod named" : "Nothing wrong logged";
        return new CrashItem(r, level, title, headline);
    }

    CrashDetail BuildDetail(CrashItem item)
    {
        var active = _main.Active?.Mods.ToHashSet() ?? [];
        var r = item.Report;
        return new CrashDetail
        {
            Item = item,
            Suspects = r.Suspects.Select(s => new SuspectRow(s.ModId, NameOf(s.ModId),
                (s.InFatal ? "Named in the fatal error" : "Named in errors") + $" · {MainViewModel.Plural(s.Hits, "hit")}",
                _main.State.Catalog.ContainsKey(s.ModId), active.Contains(s.ModId), s.InFatal)).ToList(),
            Errors = r.Errors.OrderByDescending(e => e.Fatal).ThenBy(e => e.Line).Take(60)
                .Select(e => new ErrorRow(e, string.Join(", ", e.Mods.Select(NameOf)))).ToList(),
        };
    }

    /// <summary>After a play session ends: the newest log written since it started, if it shows a crash.</summary>
    public CrashReport? CrashSince(DateTime start)
    {
        var f = CrashLogs.Find(_main.State.CrashDirs(), 5).FirstOrDefault(f => f.LastWriteTime >= start.AddSeconds(-5));
        if (f is null) return null;
        var r = CrashLogs.Read(f.FullName, _main.State.Catalog);
        return r.Crashed ? r : null;
    }

    public string Summary(CrashReport r) =>
        r.Suspects.FirstOrDefault() is { } s ? $"The game crashed. Likely cause: {NameOf(s.ModId)}" : "The game crashed. Details in Crashes.";

    [RelayCommand]
    private void Disable(SuspectRow row)
    {
        _main.ToggleMod(row.ModId, false);
        Apply(Items.Select(i => i.Report).ToList());
        _main.Notify($"Turned off {row.Name} in the active playset");
    }

    [RelayCommand]
    private void ShowInLibrary(SuspectRow row)
    {
        _main.Page = "library";
        _main.Library.Search = "";
        _main.Library.Selected = _main.Library.Items.FirstOrDefault(i => i.ModId == row.ModId) ?? _main.Library.Selected;
    }

    [RelayCommand]
    private void OpenLog()
    {
        if (Detail is null) return;
        try { AppState.OpenFolder(Detail.Report.Path); }
        catch (Exception e) { _main.Notify(e.Message, true); }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var dir = Detail is { } d ? Path.GetDirectoryName(d.Report.Path) : _main.State.CrashDirs().FirstOrDefault();
        if (dir is null) { _main.Notify("No log folder found. Set it in Settings.", true); return; }
        try { AppState.OpenFolder(dir); }
        catch (Exception e) { _main.Notify(e.Message, true); }
    }

    [RelayCommand]
    private void CopyReport()
    {
        if (Detail is not { } d) return;
        var lines = new List<string> { $"{d.Item.Title}: {d.Item.Headline}", d.Report.Path, "" };
        foreach (var e in d.Errors.Take(10))
        {
            lines.Add($"[{e.Stamp}] {e.Message} ({e.Meta})");
            lines.AddRange(e.Error.Context.Take(6));
        }
        var text = string.Join(Environment.NewLine, lines);
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
            _ = w.Clipboard?.SetTextAsync(text);
        _main.Notify("Crash summary copied");
    }
}
