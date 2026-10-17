using System.Text.RegularExpressions;

namespace Tfmm.Core;

public sealed class Audit
{
    public required string ModId { get; init; }
    public long TotalBytes { get; set; }
    public int Files { get; set; }
    public Dictionary<string, long> BytesByKind { get; } = [];
    public List<(long Bytes, string Path)> Largest { get; set; } = [];
    public Dictionary<string, List<string>> Hooks { get; } = [];
    public Dictionary<string, int> EntityLoops { get; } = [];
    public List<string> Flags { get; } = [];
    public int Score { get; set; }

    public List<string> TickHookFiles =>
        Hooks.Where(kv => kv.Value.Any(Auditor.TickHooks.Contains)).Select(kv => kv.Key).Order().ToList();
}

/// <summary>Static cost heuristics, no game needed, so no real timings. Hook names from the
/// GameScript docs; regex matching is a heuristic (unverified against real mods).</summary>
public static partial class Auditor
{
    public static readonly string[] TickHooks = ["update", "postUpdate", "guiUpdate"];
    const string AllHooks = "update|postUpdate|guiUpdate|handleEvent|guiHandleEvent|preRunScript|runScript|postRunScript";

    [GeneratedRegex(@"\b(" + AllHooks + @")\s*[=:]\s*function\b|\bfunction\s+[\w.:]*\b(" + AllHooks + @")\s*\(")]
    private static partial Regex HookRe();
    [GeneratedRegex(@"\b(forEachEntity\w*|forEachModelWithMetadata|getEntitiesWithComponent)\b")]
    private static partial Regex LoopRe();

    public const long BigTexture = 8L << 20;
    public const long BigMod = 500L << 20;

    static string Kind(string ext) => ext switch
    {
        ".dds" or ".tga" or ".png" or ".jpg" => "Textures",
        ".msh" or ".mdl" or ".mtl" => "Models",
        ".lua" or ".tl" => "Scripts",
        ".con" or ".json" => "Data",
        _ => "Other",
    };

    public static Audit Run(Mod mod, int top = 5)
    {
        var a = new Audit { ModId = mod.ModId };
        var sizes = new List<(long, string)>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(mod.Path, "*", SearchOption.AllDirectories).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files = []; }
        foreach (var p in files)
        {
            long size;
            try { size = new FileInfo(p).Length; } catch (IOException) { continue; }
            var rel = Path.GetRelativePath(mod.Path, p).Replace('\\', '/');
            var ext = Path.GetExtension(p).ToLowerInvariant();
            var kind = Kind(ext);
            a.Files++;
            a.TotalBytes += size;
            a.BytesByKind[kind] = a.BytesByKind.GetValueOrDefault(kind) + size;
            sizes.Add((size, rel));
            if (kind == "Textures" && size >= BigTexture)
                a.Flags.Add($"Large texture {rel} ({size / 1048576.0:0.0} MB)");
            if (kind == "Scripts") ScanScript(a, p, rel);
        }
        foreach (var ls in mod.LoadScripts)
        {
            if (ModScanner.ScriptFile(mod.Path, ls.File) is not { } f) continue;
            var rel = Path.GetRelativePath(mod.Path, f).Replace('\\', '/');
            var hooks = a.Hooks.TryGetValue(rel, out var h) ? h : a.Hooks[rel] = [];
            if (!hooks.Contains(ls.Phase)) { hooks.Add(ls.Phase); hooks.Sort(StringComparer.Ordinal); }
        }
        a.Largest = sizes.OrderByDescending(s => s.Item1).Take(top).ToList();
        if (a.TotalBytes >= BigMod) a.Flags.Add($"Mod is {a.TotalBytes / 1048576.0:0} MB");
        foreach (var rel in a.TickHookFiles)
        {
            var loops = a.EntityLoops.GetValueOrDefault(rel);
            a.Flags.Add(loops > 0
                ? $"Per-tick hook in {rel} with {loops} entity/model loop(s), check it is throttled"
                : $"Per-tick hook in {rel}");
        }
        a.Score = Score(a);
        return a;
    }

    static void ScanScript(Audit a, string path, string rel)
    {
        string src;
        try { src = File.ReadAllText(path); } catch (IOException) { return; }
        var names = HookRe().Matches(src)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Distinct().Order().ToList();
        if (names.Count > 0) a.Hooks[rel] = names;
        var loops = LoopRe().Matches(src).Count;
        if (loops > 0) a.EntityLoops[rel] = loops;
    }

    /// <summary>Rough 0-100 cost score for sorting only. Weights are guesses.</summary>
    public static int Score(Audit a)
    {
        var tick = a.TickHookFiles;
        double s = Math.Min(a.TotalBytes / (double)BigMod, 1.0) * 40;
        s += Math.Min(tick.Count, 3) * 15;
        s += Math.Min(tick.Sum(p => a.EntityLoops.GetValueOrDefault(p)), 5) * 3;
        return (int)Math.Round(Math.Min(s, 100));
    }
}
