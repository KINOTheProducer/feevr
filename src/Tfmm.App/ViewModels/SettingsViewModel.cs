using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tfmm.Core;

namespace Tfmm.App.ViewModels;

public sealed partial class SourceGroup(SettingsViewModel owner, ModSource source, string rank, string title, string blurb) : ObservableObject
{
    public ModSource Source { get; } = source;
    public string Rank { get; } = rank;
    public string Title { get; } = title;
    public string Blurb { get; } = blurb;
    public ObservableCollection<string> Folders { get; } = [];
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _chip = "Not set";

    [RelayCommand]
    private void Remove(string folder) => owner.RemoveFolder(Source, folder);
}

public partial class SettingsViewModel : ObservableObject
{
    readonly MainViewModel _main;

    public List<SourceGroup> Groups { get; }
    [ObservableProperty] private string _launch = "";
    [ObservableProperty] private string _gameProcess = "";
    [ObservableProperty] private string _probeFile = "";
    [ObservableProperty] private string _probeState = "";
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private string _modIoKey = "";
    [ObservableProperty] private string _crashDir = "";
    [ObservableProperty] private string _crashDirHint = "";
    [ObservableProperty] private string _gameSettings = "";
    [ObservableProperty] private string _gameSettingsHint = "";
    [ObservableProperty] private string _modIoState = "";
    public MainViewModel Main => _main;
    public string Home => _main.State.Home;
    bool _loading;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Groups =
        [
            new(this, ModSource.Staging, "1ST", "Staging area", "Mods you are building. Beats every other copy."),
            new(this, ModSource.Manual, "2ND", "Manual installs", "Mods you unpacked yourself."),
            new(this, ModSource.Subscribed, "3RD", "Subscribed", "Downloaded through the in-game Mod Hub (mod.io)."),
        ];
    }

    public void Reload()
    {
        foreach (var g in Groups)
        {
            g.Folders.Clear();
            foreach (var f in _main.State.Config.Roots[g.Source]) g.Folders.Add(f);
            g.IsEmpty = g.Folders.Count == 0;
            var n = _main.State.Catalog.Values.Count(m => m.Source == g.Source);
            g.Chip = g.IsEmpty ? "Not set" : MainViewModel.Plural(n, "mod");
        }
        _loading = true;
        Launch = _main.State.Config.Launch;
        GameProcess = _main.State.Config.GameProcess;
        ProbeFile = _main.State.Config.ProbeFile;
        AutoUpdate = _main.State.Config.AutoUpdate;
        ModIoKey = _main.State.Config.ModIoApiKey;
        CrashDir = _main.State.Config.CrashDir;
        CrashDirHint = CrashLogs.DefaultDirs().FirstOrDefault() ?? @"[STEAM]\userdata\[ID]\3493540\local\crash_dump";
        GameSettings = _main.State.Config.GameSettings;
        GameSettingsHint = Tfmm.Core.GameSettings.DefaultFiles().FirstOrDefault() ?? @"[STEAM]\userdata\[ID]\3493540\local\settings.lua";
        _loading = false;
        var mods = _main.State.ModIoMods.ToList();
        var named = mods.Count(m => !m.NameGuessed);
        ModIoState = mods.Count == 0 ? "No mod.io mods in your folders yet"
            : $"{named} of {MainViewModel.Plural(mods.Count, "mod.io mod")} have real names";
        var (t, err) = _main.State.ProbeTimings();
        ProbeState = ProbeFile.Length == 0 ? "Not set" : err ?? $"Reading {MainViewModel.Plural(t.Count, "mod")}";
    }

    partial void OnLaunchChanged(string value) => Save(c => c.Launch = value.Trim());
    partial void OnGameProcessChanged(string value) => Save(c => c.GameProcess = value.Trim());
    partial void OnAutoUpdateChanged(bool value) => Save(c => c.AutoUpdate = value);
    partial void OnModIoKeyChanged(string value) => Save(c => c.ModIoApiKey = value.Trim());
    partial void OnCrashDirChanged(string value) => Save(c => c.CrashDir = value.Trim());
    partial void OnGameSettingsChanged(string value) => Save(c => c.GameSettings = value.Trim());
    partial void OnProbeFileChanged(string value)
    {
        Save(c => c.ProbeFile = value.Trim());
        if (!_loading) _main.Refresh();
    }

    void Save(Action<Config> set)
    {
        if (_loading) return;
        set(_main.State.Config);
        _main.State.SaveConfig();
    }

    public void AddFolder(ModSource s, string path)
    {
        _main.State.AddRoot(s, path);
        _main.Refresh();
        _main.Notify($"Added folder · {MainViewModel.Plural(_main.State.Catalog.Count, "mod")} found");
    }

    public void RemoveFolder(ModSource s, string path)
    {
        _main.State.RemoveRoot(s, path);
        _main.Refresh();
        _main.Notify("Folder removed");
    }

    [RelayCommand]
    private async Task FetchModIo()
    {
        if (ModIoKey.Trim().Length == 0) { _main.Notify("Paste a mod.io API key first", true); return; }
        await _main.FetchModIoAsync(onlyMissing: false);
        Reload();
    }

    [RelayCommand]
    private void FindModIo()
    {
        if (ModIo.DetectModsDir() is not { } dir) { _main.Notify("No mod.io folder for Transport Fever 3 on this PC", true); return; }
        if (_main.State.Config.Roots[ModSource.Subscribed].Contains(dir, StringComparer.OrdinalIgnoreCase))
        {
            _main.Notify("mod.io folder is already added");
            return;
        }
        AddFolder(ModSource.Subscribed, dir);
    }

    [RelayCommand]
    private void OpenModIoKeys()
    {
        try { AppState.OpenFolder("https://mod.io/me/access"); }
        catch (Exception e) { _main.Notify(e.Message, true); }
    }

    [RelayCommand]
    private void OpenHome()
    {
        try { AppState.OpenFolder(Home); }
        catch (Exception e) { _main.Notify(e.Message, true); }
    }
}
