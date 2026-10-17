using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tfmm.Core;

/// <summary>What mod.io knows about a mod: the real name, author, summary and tags.</summary>
public sealed class ModIoInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Author { get; set; } = "";
    public string ProfileUrl { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public DateTime Fetched { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, ModIoInfo>))]
internal partial class ModIoJson : JsonSerializerContext;

/// <summary>mod.io integration. Mods subscribed through the game land in
/// <c>C:\Users\Public\mod.io\&lt;gameId&gt;\mods\&lt;modId&gt;\</c> (TF3's game id is 10640, seen on a real install).
/// Names come from, in order: the cache, mod.io's local state file (format unverified, parsed loosely),
/// the REST API with the user's read-only key, and finally a prettified mod id.</summary>
public static partial class ModIo
{
    public const long Tf3GameId = 10640;

    [GeneratedRegex(@"^\d+$")] private static partial Regex DigitsRe();
    [GeneratedRegex(@"[_\-]+v?\d+$")] private static partial Regex VersionSuffixRe();

    /// <summary>The mod.io id if <paramref name="modDir"/> is …\mod.io\&lt;digits&gt;\mods\&lt;digits&gt;.</summary>
    public static long? IdFromPath(string modDir)
    {
        var dir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(modDir));
        if (dir.Parent is not { Name: var mods } parent || !mods.Equals("mods", StringComparison.OrdinalIgnoreCase)) return null;
        if (parent.Parent is not { } game || !DigitsRe().IsMatch(game.Name)) return null;
        return DigitsRe().IsMatch(dir.Name) && long.TryParse(dir.Name, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>Game id from a mods folder path (…\mod.io\10640\mods), else TF3's.</summary>
    public static long GameIdFromModsDir(string modsDir)
    {
        var parent = new DirectoryInfo(Path.TrimEndingDirectorySeparator(modsDir)).Parent;
        return parent is not null && long.TryParse(parent.Name, CultureInfo.InvariantCulture, out var g) ? g : Tf3GameId;
    }

    /// <summary>Candidate mod.io roots: the user's override in %LOCALAPPDATA%\mod.io\globalsettings.json, then %PUBLIC%\mod.io.</summary>
    public static IEnumerable<string> Roots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settings = Path.Combine(local, "mod.io", "globalsettings.json");
        if (File.Exists(settings))
        {
            string? custom = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.TryGetProperty("RootLocalStoragePath", out var p) && p.ValueKind == JsonValueKind.String)
                    custom = p.GetString();
            }
            catch (Exception e) when (e is IOException or JsonException) { }
            if (!string.IsNullOrWhiteSpace(custom)) yield return custom!;
        }
        var pub = Environment.GetEnvironmentVariable("PUBLIC");
        if (!string.IsNullOrEmpty(pub)) yield return Path.Combine(pub, "mod.io");
    }

    /// <summary>TF3's mods folder inside mod.io's storage, if it exists on this machine.</summary>
    public static string? DetectModsDir(IEnumerable<string>? roots = null)
    {
        foreach (var root in roots ?? Roots())
        {
            var dir = Path.Combine(root, Tf3GameId.ToString(CultureInfo.InvariantCulture), "mods");
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    /// <summary>"auto_signals_1" → "Auto Signals": drop a trailing version number, split on _ and -, capitalise.</summary>
    public static string Prettify(string modId)
    {
        var s = VersionSuffixRe().Replace(modId, "");
        var words = s.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return modId;
        return string.Join(' ', words.Select(w => w.Length <= 2 && w.All(char.IsLetter) && words.Length > 1
            ? w.ToUpperInvariant()
            : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    // ---------- local state (format unverified) ----------

    /// <summary>Reads any JSON under &lt;gameDir&gt;\metadata and collects objects that look like a mod profile:
    /// an id (ModID / id / mod_id) next to a name (directly or inside a Profile object).</summary>
    public static Dictionary<long, ModIoInfo> ReadLocalState(string gameDir)
    {
        var found = new Dictionary<long, ModIoInfo>();
        var meta = Path.Combine(gameDir, "metadata");
        if (!Directory.Exists(meta)) return found;
        foreach (var f in Directory.EnumerateFiles(meta, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                Walk(doc.RootElement, found, 0);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return found;
    }

    static void Walk(JsonElement e, Dictionary<long, ModIoInfo> found, int depth)
    {
        if (depth > 12) return;
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) Walk(x, found, depth + 1);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        if (TryProfile(e) is { } info && !found.ContainsKey(info.Id)) found[info.Id] = info;
        foreach (var p in e.EnumerateObject()) Walk(p.Value, found, depth + 1);
    }

    static ModIoInfo? TryProfile(JsonElement e)
    {
        long? id = null;
        foreach (var k in new[] { "ModID", "modId", "mod_id", "id", "ID" })
            if (Prop(e, k) is { } v && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) { id = n; break; }
        if (id is null) return null;
        var profile = e;
        foreach (var k in new[] { "Profile", "profile", "ModProfile" })
            if (Prop(e, k) is { ValueKind: JsonValueKind.Object } p) { profile = p; break; }
        var name = Str(profile, "name") ?? Str(profile, "Name") ?? Str(profile, "ProfileName");
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new ModIoInfo
        {
            Id = id.Value, Name = name!,
            Summary = Str(profile, "summary") ?? Str(profile, "ProfileSummary") ?? "",
            Author = Author(profile),
            ProfileUrl = Str(profile, "profile_url") ?? Str(profile, "ProfileURL") ?? "",
        };
    }

    static string Author(JsonElement profile)
    {
        foreach (var k in new[] { "submitted_by", "SubmittedBy", "ProfileSubmittedBy" })
            if (Prop(profile, k) is { } v)
                return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : Str(v, "username") ?? Str(v, "Username") ?? "";
        return "";
    }

    // ---------- REST API ----------

    /// <summary>Fetches profiles for up to 100 ids per call with a read-only API key. Returns what it got; errors give an empty result.</summary>
    public static async Task<Dictionary<long, ModIoInfo>> FetchAsync(HttpClient http, long gameId, IEnumerable<long> ids, string apiKey,
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, ModIoInfo>();
        var key = apiKey.Trim();
        if (key.Length == 0) return result;
        foreach (var chunk in ids.Distinct().Chunk(100))
        {
            var url = $"https://api.mod.io/v1/games/{gameId}/mods?api_key={Uri.EscapeDataString(key)}" +
                      $"&id-in={string.Join(',', chunk)}&_limit=100";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/json");
                req.Headers.UserAgent.ParseAdd("Junction");
                using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) break;
                foreach (var info in ParseMods(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)))
                    result[info.Id] = info;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { break; }
        }
        return result;
    }

    /// <summary>Parses a mod.io "Get Mods" response ({ data: [ mod objects ] }).</summary>
    public static List<ModIoInfo> ParseMods(string json)
    {
        var list = new List<ModIoInfo>();
        using var doc = JsonDocument.Parse(json);
        if (Prop(doc.RootElement, "data") is not { ValueKind: JsonValueKind.Array } data) return list;
        foreach (var m in data.EnumerateArray())
        {
            if (Prop(m, "id") is not { } idv || !idv.TryGetInt64(out var id) || Str(m, "name") is not { } name) continue;
            var tags = new List<string>();
            if (Prop(m, "tags") is { ValueKind: JsonValueKind.Array } ta)
                foreach (var t in ta.EnumerateArray())
                    if (Str(t, "name") is { Length: > 0 } tn) tags.Add(tn);
            list.Add(new ModIoInfo
            {
                Id = id, Name = name, Summary = Str(m, "summary") ?? "",
                Author = Prop(m, "submitted_by") is { } sb ? Str(sb, "username") ?? "" : "",
                ProfileUrl = Str(m, "profile_url") ?? "", Tags = tags, Fetched = DateTime.UtcNow,
            });
        }
        return list;
    }

    // ---------- cache ----------

    public static Dictionary<long, ModIoInfo> LoadCache(string path)
    {
        try
        {
            var d = JsonSerializer.Deserialize(File.ReadAllText(path), ModIoJson.Default.DictionaryStringModIoInfo) ?? [];
            return d.Values.ToDictionary(v => v.Id);
        }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    public static void SaveCache(string path, Dictionary<long, ModIoInfo> cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(
            cache.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value), ModIoJson.Default.DictionaryStringModIoInfo));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Fills in names, authors, summaries and tags from mod.io, else makes the id readable.</summary>
    public static void Apply(IEnumerable<Mod> mods, IReadOnlyDictionary<long, ModIoInfo> known)
    {
        foreach (var m in mods)
        {
            if (m.ModIoId is { } id && known.TryGetValue(id, out var info))
            {
                m.Name = info.Name;
                m.NameGuessed = false;
                if (string.IsNullOrWhiteSpace(m.Summary)) m.Summary = info.Summary;
                if (m.Authors.Count == 0 && info.Author.Length > 0) m.Authors.Add(info.Author);
                foreach (var t in info.Tags.Where(t => !m.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) m.Tags.Add(t);
                m.ProfileUrl = info.ProfileUrl;
            }
            else if (string.IsNullOrWhiteSpace(m.Name))
            {
                m.Name = Prettify(m.ModId);
                m.NameGuessed = true;
            }
        }
    }

    static JsonElement? Prop(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v : null;

    static string? Str(JsonElement e, string k) =>
        Prop(e, k) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
}
