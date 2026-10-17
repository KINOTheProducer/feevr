using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tfmm.Core;

/// <summary>Parses TF3 mod folders: mod.json + _metadata/modinfo.json. Schema per wiki "Mod Definition".</summary>
public static partial class ModScanner
{
    [GeneratedRegex("^[A-Za-z0-9]+_[A-Za-z0-9_]+$")] private static partial Regex FolderRe();
    [GeneratedRegex("^[A-Za-z0-9_]+$")] private static partial Regex IdRe();
    public static readonly string[] Severities = ["None", "Warning", "Critical"];

    static readonly JsonDocumentOptions Opts = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Returns null when mod.json is missing or unreadable.</summary>
    public static Mod? Parse(string path, ModSource source = ModSource.Manual)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(path, "mod.json")), Opts);
            root = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
        if (root.ValueKind != JsonValueKind.Object) return null;

        var w = new List<string>();
        var folder = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        var modId = Str(root, "modId");
        if (modId is null || !IdRe().IsMatch(modId))
        {
            w.Add("missing or invalid modId");
            if (string.IsNullOrEmpty(modId)) modId = folder;
        }
        if (!FolderRe().IsMatch(folder)) w.Add("folder name not <author>_<modname>");

        if (!root.TryGetProperty("revision", out _)) w.Add("missing revision");
        var sevAdd = Str(root, "severityAdd") ?? "None";
        var sevRem = Str(root, "severityRemove") ?? "Warning";
        if (!Severities.Contains(sevAdd)) w.Add($"severityAdd has unknown value '{sevAdd}'");
        if (!Severities.Contains(sevRem)) w.Add($"severityRemove has unknown value '{sevRem}'");

        var mod = new Mod
        {
            ModId = modId, Revision = Int(root, "revision", 0), Path = path, Source = source,
            SeverityAdd = sevAdd, SeverityRemove = sevRem,
            Visible = Bool(root, "visible", true), Cosmetic = Bool(root, "cosmetic", false),
        };

        int i = 0;
        foreach (var d in Arr(root, "dependencies"))
        {
            var r = Ref(d, w, $"dependencies[{i++}]");
            if (r is null) continue;
            var info = d.TryGetProperty("modInfo", out var mi) && mi.ValueKind == JsonValueKind.Object ? mi : default;
            mod.Dependencies.Add(new Dependency(r,
                info.ValueKind == JsonValueKind.Object ? Str(info, "displayName") ?? "" : "",
                info.ValueKind == JsonValueKind.Object ? Str(info, "url") ?? "" : "",
                Bool(d, "loadBefore", true), Bool(d, "optional", false)));
        }
        i = 0;
        foreach (var d in Arr(root, "incompatibilities"))
            if (Ref(d, w, $"incompatibilities[{i++}]") is { } r) mod.Incompatibilities.Add(r);

        var infoPath = System.IO.Path.Combine(path, "_metadata", "modinfo.json");
        if (!File.Exists(infoPath)) w.Add("missing _metadata/modinfo.json");
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(infoPath), Opts);
                var info = doc.RootElement;
                if (info.ValueKind == JsonValueKind.Object)
                {
                    mod.Name = Str(info, "name") ?? "";
                    mod.Summary = Str(info, "summary") ?? "";
                    foreach (var a in Arr(info, "authors"))
                        mod.Authors.Add(a.ValueKind == JsonValueKind.Object ? Str(a, "name") ?? "" : a.ToString());
                    foreach (var t in Arr(info, "tags"))
                        if (t.ValueKind == JsonValueKind.String) mod.Tags.Add(t.GetString()!);
                    if (mod.Name.Length > 32) w.Add("modinfo name over 32 chars");
                    if (mod.Summary.Length > 100) w.Add("modinfo summary over 100 chars");
                }
            }
            catch (Exception e) when (e is IOException or JsonException) { w.Add("unreadable _metadata/modinfo.json"); }
        }

        foreach (var phase in LoadPhases)
        {
            if (!root.TryGetProperty(phase, out var ps)) continue;
            if (ps.ValueKind != JsonValueKind.Object || Str(ps, "fileName") is not { Length: > 0 } fn)
            {
                w.Add($"{phase} without fileName");
                continue;
            }
            var ls = ParseScriptRef(phase, fn);
            mod.LoadScripts.Add(ls);
            if (ScriptFile(path, ls.File) is null) w.Add($"{phase} file '{ls.File}' not found");
        }
        mod.HasRootScript = mod.LoadScripts.Count > 0 ||
                            File.Exists(System.IO.Path.Combine(path, "mod.script.tl")) ||
                            File.Exists(System.IO.Path.Combine(path, "mod.script.lua"));
        // Packaging checks only matter for mods you are building; downloaded ones (mod.io stores them
        // in numbered folders without _metadata) would all fail them for no useful reason.
        if (source != ModSource.Staging) w.RemoveAll(x => !x.Contains("entry without") && !x.Contains("not found"));
        mod.Warnings.AddRange(w);
        mod.ModIoId = ModIo.IdFromPath(path);
        return mod;
    }

    public static readonly string[] LoadPhases = ["preRunScript", "runScript", "postRunScript"];

    /// <summary>"curvy_cities::/mod.script@preRunFn" → File "mod.script", Function "preRunFn".
    /// Format seen in a real mod.io mod; the part before "::" is the mod's own id.</summary>
    public static LoadScript ParseScriptRef(string phase, string fileName)
    {
        var s = fileName;
        var at = s.LastIndexOf('@');
        var fn = at >= 0 ? s[(at + 1)..] : "";
        if (at >= 0) s = s[..at];
        var colons = s.IndexOf("::", StringComparison.Ordinal);
        if (colons >= 0) s = s[(colons + 2)..];
        return new LoadScript(phase, s.TrimStart('/', '\\'), fn);
    }

    /// <summary>Script file on disk for a reference like "mod.script": tries .lua, .tl, then the name as-is.</summary>
    public static string? ScriptFile(string modDir, string file)
    {
        foreach (var ext in new[] { ".lua", ".tl", "" })
        {
            var p = System.IO.Path.Combine(modDir, file + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Same modId in several sources: staging beats manual beats subscribed (wiki). Losers go to shadowed.</summary>
    public static (Dictionary<string, Mod> Catalog, List<Mod> Shadowed) Scan(IEnumerable<(ModSource Source, string Dir)> roots)
    {
        var found = new List<Mod>();
        foreach (var (source, dir) in roots)
        {
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            Array.Sort(subs, StringComparer.Ordinal);
            foreach (var p in subs)
                if (Parse(p, source) is { } m) found.Add(m);
        }
        var catalog = new Dictionary<string, Mod>();
        var shadowed = new List<Mod>();
        foreach (var m in found.OrderBy(m => (int)m.Source))
            if (!catalog.TryAdd(m.ModId, m)) shadowed.Add(m);
        return (catalog, shadowed);
    }

    static ModRef? Ref(JsonElement obj, List<string> w, string where)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("mod", out var m) &&
            m.ValueKind == JsonValueKind.Object && Str(m, "modId") is { } id)
            return new ModRef(id, Int(m, "revisionMin", -1), Int(m, "revisionMax", -1));
        w.Add($"{where}: entry without mod.modId, skipped");
        return null;
    }

    static string? Str(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int Int(JsonElement e, string k, int def) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : def;

    static bool Bool(JsonElement e, string k, bool def) =>
        e.TryGetProperty(k, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => def } : def;

    static IEnumerable<JsonElement> Arr(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
}
