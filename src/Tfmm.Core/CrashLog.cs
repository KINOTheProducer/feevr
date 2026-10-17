using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tfmm.Core;

/// <summary>One distinct error in a log. Repeats of the same message are folded into <see cref="Count"/>.</summary>
public sealed class CrashError
{
    public required string Kind { get; init; }
    public required string Message { get; init; }
    public int Line { get; init; }
    public bool Fatal { get; init; }
    public int Count { get; set; } = 1;
    public List<string> Context { get; } = [];
    /// <summary>Mod ids named in the message or its stack trace.</summary>
    public List<string> Mods { get; } = [];
}

public sealed record Suspect(string ModId, int Hits, bool InFatal);

public sealed class CrashReport
{
    public required string Path { get; init; }
    public DateTime Time { get; init; }
    public long Bytes { get; init; }
    public int Lines { get; set; }
    public bool Truncated { get; set; }
    public List<CrashError> Errors { get; } = [];
    public List<Suspect> Suspects { get; set; } = [];
    /// <summary>A fatal error was logged, or the file is the timestamped copy the game writes when it crashes.</summary>
    public bool Crashed { get; set; }
    public List<string> Tail { get; set; } = [];
}

/// <summary>Reads Transport Fever 3's stdout logs and blames mods. The location is from fan guides
/// (Steam: userdata\&lt;user&gt;\3493540\local\crash_dump\stdout.txt, overwritten each launch, plus a
/// timestamped copy on a crash; GOG/Epic: %APPDATA%\Transport Fever 3) and the line formats are
/// TF2's, so all of it is unverified for TF3. Matching is loose on purpose.</summary>
public static partial class CrashLogs
{
    public const string SteamAppId = "3493540";
    const long MaxRead = 16L << 20;
    const int ContextLines = 12;

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?<kind>Lua error|Error message|Assertion|Unhandled exception|terminate called|Fatal error|FATAL|Exception|Error|ERROR)\b[:\s]*(?<msg>.*)$")]
    private static partial Regex ErrorRe();
    [GeneratedRegex(@"\S+\.(?:lua|tl|mdl|con|module|script):\d+:\s*.+")]
    private static partial Regex LuaLocRe();
    [GeneratedRegex(@"mod\.io[\\/]\d+[\\/]mods[\\/](\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModIoPathRe();
    [GeneratedRegex(@"(?:mods|staging_area|content[\\/]\d+)[\\/]([A-Za-z0-9_\-]+)[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex ModDirRe();
    [GeneratedRegex(@"\b([A-Za-z0-9_]+)::")]
    private static partial Regex ModRefRe();
    [GeneratedRegex(@"0x[0-9a-fA-F]+|\b\d+(\.\d+)?\b")]
    private static partial Regex NumberRe();

    /// <summary>TF3's folders in Steam's userdata (userdata\&lt;user&gt;\3493540), one per Steam account.</summary>
    public static List<string> SteamGameDirs()
    {
        var dirs = new List<string>();
        var steamRoots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } pf86 ? Path.Combine(pf86, "Steam") : null,
            Environment.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } pf ? Path.Combine(pf, "Steam") : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Steam"),
        };
        foreach (var steam in steamRoots.OfType<string>())
        {
            var userdata = Path.Combine(steam, "userdata");
            if (!Directory.Exists(userdata)) continue;
            try
            {
                foreach (var user in Directory.GetDirectories(userdata))
                    dirs.Add(Path.Combine(user, SteamAppId));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Where TF3 likely writes its logs on this machine. Only existing folders are returned.</summary>
    public static List<string> DefaultDirs()
    {
        var dirs = SteamGameDirs().Select(d => Path.Combine(d, "local", "crash_dump")).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Transport Fever 3"));
        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Log files in <paramref name="dirs"/> (and their crash_dump subfolder), newest first.</summary>
    public static List<FileInfo> Find(IEnumerable<string> dirs, int max = 40)
    {
        var files = new List<FileInfo>();
        foreach (var dir in dirs)
        {
            foreach (var d in new[] { dir, Path.Combine(dir, "crash_dump") }.Where(Directory.Exists))
            {
                try
                {
                    files.AddRange(new DirectoryInfo(d).EnumerateFiles()
                        .Where(f => f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                                    f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
                                    (f.Name.Contains("stdout", StringComparison.OrdinalIgnoreCase) ||
                                     f.Name.Contains("crash", StringComparison.OrdinalIgnoreCase))));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return files.DistinctBy(f => f.FullName).OrderByDescending(f => f.LastWriteTimeUtc).Take(max).ToList();
    }

    /// <summary>The live log the game overwrites each launch (vs. a saved crash copy).</summary>
    public static bool IsLiveLog(string path) =>
        Path.GetFileName(path).Equals("stdout.txt", StringComparison.OrdinalIgnoreCase);

    public static CrashReport Read(string path, IReadOnlyDictionary<string, Mod> catalog)
    {
        var fi = new FileInfo(path);
        var report = new CrashReport { Path = path, Time = fi.Exists ? fi.LastWriteTime : default, Bytes = fi.Exists ? fi.Length : 0 };
        string text;
        try
        {
            // the game keeps stdout.txt open while running
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxRead)
            {
                fs.Seek(-MaxRead, SeekOrigin.End);
                report.Truncated = true;
            }
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = sr.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return report; }
        Parse(text, report, catalog);
        report.Crashed |= !IsLiveLog(path) && Path.GetFileName(path).Contains("stdout", StringComparison.OrdinalIgnoreCase);
        return report;
    }

    public static void Parse(string text, CrashReport report, IReadOnlyDictionary<string, Mod> catalog)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        report.Lines = lines.Length;
        report.Tail = lines.Reverse().SkipWhile(string.IsNullOrWhiteSpace).Take(25).Reverse().ToList();
        var resolver = new ModMatcher(catalog);
        var byKey = new Dictionary<string, CrashError>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            string kind, msg;
            if (ErrorRe().Match(line) is { Success: true } m)
            {
                kind = Normalize(m.Groups["kind"].Value);
                msg = m.Groups["msg"].Value.Trim();
            }
            else if (LuaLocRe().IsMatch(line) && !IsTraceLine(line))
            {
                kind = "Lua error";
                msg = line.Trim();
            }
            else continue;
            if (msg.Length == 0 && i + 1 < lines.Length) msg = lines[i + 1].Trim();

            var err = new CrashError { Kind = kind, Message = msg, Line = i + 1, Fatal = kind is not ("Lua error" or "Error") };
            var mods = new HashSet<string>(resolver.Find(line));
            var j = i + 1;
            for (; j < lines.Length && j <= i + ContextLines; j++)
            {
                var next = lines[j].TrimEnd();
                if (next.Length == 0 || ErrorRe().IsMatch(next)) break;
                if (!IsTraceLine(next)) break;
                err.Context.Add(next);
                mods.UnionWith(resolver.Find(next));
            }
            err.Mods.AddRange(mods.Order(StringComparer.Ordinal));

            var key = kind + "|" + NumberRe().Replace(msg, "#");
            if (byKey.TryGetValue(key, out var seen)) seen.Count++;
            else
            {
                byKey[key] = err;
                report.Errors.Add(err);
            }
            i = j - 1;
        }

        report.Crashed |= report.Errors.Any(e => e.Fatal);
        report.Suspects = report.Errors
            .SelectMany(e => e.Mods.Select(id => (id, e.Count, e.Fatal)))
            .GroupBy(x => x.id)
            .Select(g => new Suspect(g.Key, g.Sum(x => x.Count), g.Any(x => x.Fatal)))
            .OrderByDescending(s => s.InFatal).ThenByDescending(s => s.Hits).ThenBy(s => s.ModId, StringComparer.Ordinal)
            .ToList();
    }

    static bool IsTraceLine(string l)
    {
        var t = l.TrimStart();
        return t.StartsWith("stack traceback", StringComparison.OrdinalIgnoreCase) || t.StartsWith("[C]", StringComparison.Ordinal) ||
               t.StartsWith("in ", StringComparison.Ordinal) || t.StartsWith("at ", StringComparison.Ordinal) ||
               t.StartsWith("...", StringComparison.Ordinal) || (l.Length > 0 && char.IsWhiteSpace(l[0]));
    }

    static string Normalize(string kind) => kind switch
    {
        "ERROR" => "Error",
        "FATAL" or "Fatal error" => "Fatal error",
        "terminate called" or "Unhandled exception" or "Exception" => "Exception",
        _ => kind,
    };

    /// <summary>Turns paths and ids in a log line into installed mod ids.</summary>
    sealed class ModMatcher
    {
        readonly Dictionary<long, string> _byModIo = [];
        readonly Dictionary<string, string> _byFolder = new(StringComparer.OrdinalIgnoreCase);
        readonly IReadOnlyDictionary<string, Mod> _catalog;

        public ModMatcher(IReadOnlyDictionary<string, Mod> catalog)
        {
            _catalog = catalog;
            foreach (var m in catalog.Values)
            {
                if (m.ModIoId is { } id) _byModIo[id] = m.ModId;
                _byFolder.TryAdd(Path.GetFileName(Path.TrimEndingDirectorySeparator(m.Path)), m.ModId);
                _byFolder.TryAdd(m.ModId, m.ModId);
            }
        }

        public IEnumerable<string> Find(string line)
        {
            foreach (Match m in ModIoPathRe().Matches(line))
                if (long.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var id) && _byModIo.TryGetValue(id, out var mid))
                    yield return mid;
            foreach (Match m in ModDirRe().Matches(line))
                if (_byFolder.TryGetValue(m.Groups[1].Value, out var mid)) yield return mid;
            foreach (Match m in ModRefRe().Matches(line))
                if (_catalog.ContainsKey(m.Groups[1].Value)) yield return m.Groups[1].Value;
        }
    }
}
