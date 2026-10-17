using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tfmm.Core;

public sealed class Config
{
    public Dictionary<ModSource, List<string>> Roots { get; set; } = [];
    public string? Active { get; set; }
    public const string DefaultLaunch = "steam://rungameid/" + CrashLogs.SteamAppId;
    /// <summary>Steam URL or exe path. Empty = <see cref="DefaultLaunch"/>.</summary>
    public string Launch { get; set; } = "";
    public string LaunchCommand => Launch.Trim() is { Length: > 0 } l ? l : DefaultLaunch;
    /// <summary>Process name(s) to watch, comma separated. TF3's exe name is unverified.</summary>
    public string GameProcess { get; set; } = "TransportFever3";
    /// <summary>Lua file written by the perf-probe mod (in the game's userdata folder, path unverified).</summary>
    public string ProbeFile { get; set; } = "";
    /// <summary>Check GitHub Releases for a newer Feevr at startup and every few hours.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>The user's read-only mod.io API key (mod.io → My account → API access). Used only to look up mod names.</summary>
    public string ModIoApiKey { get; set; } = "";
    /// <summary>Folder with the game's stdout/crash logs. Empty = look in the usual places.</summary>
    public string CrashDir { get; set; } = "";
}

/// <summary>Source-generated so the app can be trimmed.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(Playset))]
internal partial class StoreJson : JsonSerializerContext;

/// <summary>Config, catalog, playsets, cached audits. Stored under Home: config.json and playsets/&lt;id&gt;.json.</summary>
public sealed partial class AppState
{
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex SlugRe();

    public string Home { get; }
    public Config Config { get; private set; } = new();
    public Dictionary<string, Mod> Catalog { get; private set; } = [];
    public List<Mod> Shadowed { get; private set; } = [];
    readonly Dictionary<string, Audit> _audits = [];
    public SessionStore Sessions { get; }
    /// <summary>Everything known about mod.io mods: cached API results plus mod.io's local state.</summary>
    public Dictionary<long, ModIoInfo> ModIoKnown { get; private set; } = [];
    string ModIoCachePath => Path.Combine(Home, "modio.json");

    public AppState(string home)
    {
        Home = home;
        Directory.CreateDirectory(PlaysetDir);
        Sessions = new SessionStore(Path.Combine(home, "sessions"));
        ModIoKnown = ModIo.LoadCache(ModIoCachePath);
        LoadConfig();
        Rescan();
    }

    public static string DefaultHome() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "feevr");

    public string PlaysetDir => Path.Combine(Home, "playsets");
    string ConfigPath => Path.Combine(Home, "config.json");

    public static string Slug(string? name)
    {
        var s = SlugRe().Replace((name ?? "").ToLowerInvariant(), "-").Trim('-');
        if (s.Length > 60) s = s[..60].TrimEnd('-');
        return s.Length == 0 ? "playset" : s;
    }

    void LoadConfig()
    {
        try { Config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), StoreJson.Default.Config) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { Config = new(); }
        foreach (var s in Enum.GetValues<ModSource>())
            Config.Roots.TryAdd(s, []);
    }

    public void SaveConfig() => AtomicWrite(ConfigPath, JsonSerializer.Serialize(Config, StoreJson.Default.Config));

    public string[] GameProcessNames() => Config.GameProcess.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Per-mod timings from the probe file, or empty when it is unset, missing or unreadable.</summary>
    public (Dictionary<string, ModTiming> Timings, string? Error) ProbeTimings()
    {
        var f = Config.ProbeFile.Trim();
        if (f.Length == 0) return ([], null);
        if (!File.Exists(f)) return ([], "Probe file not found yet. Play a session with the probe mod on.");
        try { return (ProbeReader.Read(f), null); }
        catch (Exception e) when (e is IOException or FormatException) { return ([], "Probe file unreadable: " + e.Message); }
    }

    public IEnumerable<(ModSource, string)> Roots() =>
        Config.Roots.OrderBy(kv => (int)kv.Key).SelectMany(kv => kv.Value.Select(d => (kv.Key, d)));

    public void AddRoot(ModSource s, string dir)
    {
        if (!Config.Roots[s].Contains(dir)) Config.Roots[s].Add(dir);
        SaveConfig();
        Rescan();
    }

    public void RemoveRoot(ModSource s, string dir)
    {
        Config.Roots[s].Remove(dir);
        SaveConfig();
        Rescan();
    }

    public void Rescan()
    {
        (Catalog, Shadowed) = ModScanner.Scan(Roots());
        // mod.io's own local data fills gaps the API cache doesn't cover
        foreach (var gameDir in Roots().Select(r => Directory.GetParent(Path.TrimEndingDirectorySeparator(r.Item2))?.FullName)
                     .OfType<string>().Distinct())
            foreach (var (id, info) in ModIo.ReadLocalState(gameDir))
                ModIoKnown.TryAdd(id, info);
        ModIo.Apply(Catalog.Values.Concat(Shadowed), ModIoKnown);
        _audits.Clear();
    }

    /// <summary>On a fresh install, adds mod.io's TF3 mods folder as Subscribed. Returns the folder if it added one.</summary>
    public string? AutoAddModIo(IEnumerable<string>? roots = null)
    {
        if (Roots().Any()) return null;
        if (ModIo.DetectModsDir(roots) is not { } dir) return null;
        AddRoot(ModSource.Subscribed, dir);
        return dir;
    }

    public List<string> CrashDirs() =>
        Config.CrashDir.Trim() is { Length: > 0 } d ? [d] : CrashLogs.DefaultDirs();

    public IEnumerable<Mod> ModIoMods => Catalog.Values.Where(m => m.ModIoId is not null);

    /// <summary>Looks up names for mod.io mods with the user's API key and caches them. Returns how many it got.</summary>
    public async Task<int> RefreshModIoAsync(HttpClient http, bool onlyMissing, CancellationToken ct = default)
    {
        var key = Config.ModIoApiKey;
        if (string.IsNullOrWhiteSpace(key)) return 0;
        var got = 0;
        foreach (var group in ModIoMods.GroupBy(m => ModIo.GameIdFromModsDir(Path.GetDirectoryName(m.Path)!)))
        {
            var ids = group.Select(m => m.ModIoId!.Value)
                .Where(id => !onlyMissing || !ModIoKnown.TryGetValue(id, out var k) || k.Fetched == default || k.Fetched < DateTime.UtcNow.AddDays(-7));
            // no ConfigureAwait(false): Apply below mutates mods the UI is showing
            var fetched = await ModIo.FetchAsync(http, group.Key, ids, key, ct);
            foreach (var (id, info) in fetched) ModIoKnown[id] = info;
            got += fetched.Count;
        }
        if (got > 0)
        {
            ModIo.SaveCache(ModIoCachePath, ModIoKnown);
            ModIo.Apply(Catalog.Values.Concat(Shadowed), ModIoKnown);
        }
        return got;
    }

    public IEnumerable<Mod> ModsSorted() =>
        Catalog.Values.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase);

    public Audit? AuditOf(string id)
    {
        if (!_audits.TryGetValue(id, out var a) && Catalog.TryGetValue(id, out var m))
            _audits[id] = a = Auditor.Run(m);
        return a;
    }

    // playsets
    public List<Playset> Playsets()
    {
        var list = new List<Playset>();
        foreach (var f in Directory.EnumerateFiles(PlaysetDir, "*.json"))
        {
            try
            {
                var ps = JsonSerializer.Deserialize(File.ReadAllText(f), StoreJson.Default.Playset);
                if (ps is null) continue;
                ps.Id = Path.GetFileNameWithoutExtension(f);
                ps.Mods = ps.Mods.Where(m => !string.IsNullOrEmpty(m)).Distinct().ToList();
                list.Add(ps);
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Playset? GetPlayset(string? id) => id is null ? null : Playsets().FirstOrDefault(p => p.Id == id);

    public void SavePlayset(Playset ps)
    {
        ps.Id = Slug(ps.Id);  // keeps writes inside PlaysetDir
        ps.Name = string.IsNullOrWhiteSpace(ps.Name) ? "Untitled" : ps.Name.Trim();
        ps.Mods = ps.Mods.Distinct().ToList();
        AtomicWrite(Path.Combine(PlaysetDir, ps.Id + ".json"), JsonSerializer.Serialize(ps, StoreJson.Default.Playset));
    }

    public Playset CreatePlayset(string name, IEnumerable<string>? mods = null)
    {
        var existing = Playsets().Select(p => p.Id).ToHashSet();
        var baseId = Slug(name);
        var id = baseId;
        for (int n = 2; existing.Contains(id); n++) id = $"{baseId}-{n}";
        var ps = new Playset { Id = id, Name = name, Mods = mods?.ToList() ?? [] };
        SavePlayset(ps);
        return ps;
    }

    public void DeletePlayset(string id)
    {
        var p = Path.Combine(PlaysetDir, Slug(id) + ".json");
        if (File.Exists(p)) File.Delete(p);
        if (Config.Active == id) SetActive(null);
    }

    public Playset? Active => GetPlayset(Config.Active);

    public void SetActive(string? id)
    {
        Config.Active = id;
        SaveConfig();
    }

    public void Toggle(Playset ps, string modId, bool on)
    {
        ps.Mods.Remove(modId);
        if (on) ps.Mods.Add(modId);
        SavePlayset(ps);
    }

    public CheckReport Check(Playset? ps, bool addDeps = false) =>
        Resolver.Check(ps?.Mods ?? [], Catalog, addDeps);

    /// <summary>Writes auto-added deps into the playset. Returns how many were added.</summary>
    public int ApplyDeps(Playset ps)
    {
        var rep = Resolver.Check(ps.Mods, Catalog, autoAddDeps: true);
        var added = rep.Order.Where(m => !ps.Mods.Contains(m)).ToList();
        ps.Mods.AddRange(added);
        SavePlayset(ps);
        return added.Count;
    }

    public void Launch()
    {
        Process.Start(new ProcessStartInfo(Config.LaunchCommand) { UseShellExecute = true });
    }

    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text + "\n");
        File.Move(tmp, path, overwrite: true);
    }
}
