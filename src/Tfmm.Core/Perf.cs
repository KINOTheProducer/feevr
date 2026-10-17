using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tfmm.Core;

/// <summary>One reading of the game process. Cpu is percent of the whole machine (0-100).</summary>
public sealed record PerfSample(DateTime T, double Cpu, double RamMb, int Threads);

/// <summary>One play session, tagged with the playset that was active when it started.</summary>
public sealed class PerfSession
{
    public string Id { get; set; } = "";
    public string? PlaysetId { get; set; }
    public string PlaysetName { get; set; } = "No playset";
    public List<string> Mods { get; set; } = [];
    public string Process { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public List<PerfSample> Samples { get; set; } = [];

    [JsonIgnore] public TimeSpan Duration => End - Start;
    [JsonIgnore] public double AvgCpu => Samples.Count == 0 ? 0 : Samples.Average(s => s.Cpu);
    [JsonIgnore] public double P95Cpu => Percentile(Samples.Select(s => s.Cpu), 0.95);
    [JsonIgnore] public double AvgRam => Samples.Count == 0 ? 0 : Samples.Average(s => s.RamMb);
    [JsonIgnore] public double PeakRam => Samples.Count == 0 ? 0 : Samples.Max(s => s.RamMb);

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var v = values.Order().ToArray();
        if (v.Length == 0) return 0;
        var idx = (int)Math.Ceiling(p * v.Length) - 1;
        return v[Math.Clamp(idx, 0, v.Length - 1)];
    }
}

/// <summary>Per-playset rollup across its recorded sessions.</summary>
public sealed record PlaysetPerf(string Name, int Sessions, TimeSpan PlayTime, double AvgCpu, double P95Cpu, double PeakRam, double AvgRam);

public static class PerfStats
{
    /// <summary>Averages weighted by sample count, so a 3 hour session counts more than a 2 minute one.</summary>
    public static List<PlaysetPerf> ByPlayset(IEnumerable<PerfSession> sessions) =>
        sessions.Where(s => s.Samples.Count > 0)
            .GroupBy(s => s.PlaysetName)
            .Select(g =>
            {
                var all = g.SelectMany(s => s.Samples).ToList();
                return new PlaysetPerf(g.Key, g.Count(), TimeSpan.FromTicks(g.Sum(s => s.Duration.Ticks)),
                    all.Average(x => x.Cpu), PerfSession.Percentile(all.Select(x => x.Cpu), 0.95),
                    all.Max(x => x.RamMb), all.Average(x => x.RamMb));
            })
            .OrderByDescending(p => p.P95Cpu).ToList();
}

/// <summary>Samples a process from outside: no mod needed. Name(s) come from config because
/// the TF3 executable name is unverified.</summary>
public sealed class ProcessSampler : IDisposable
{
    readonly Func<string[]> _names;
    Process? _proc;
    TimeSpan _lastCpu;
    DateTime _lastWall;

    public ProcessSampler(Func<string[]> names) => _names = names;

    public string? ProcessName => _proc?.ProcessName;

    /// <summary>Null when no matching process is running. The first reading after attaching has no CPU baseline, so it returns null too.</summary>
    public PerfSample? Sample(DateTime now)
    {
        try
        {
            if (_proc is { HasExited: true }) Detach();
            if (_proc is null)
            {
                _proc = _names().Select(n => n.Trim()).Where(n => n.Length > 0)
                    .SelectMany(n => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(n))).FirstOrDefault();
                if (_proc is null) return null;
                _lastCpu = _proc.TotalProcessorTime;
                _lastWall = now;
                return null;
            }
            _proc.Refresh();
            var cpu = _proc.TotalProcessorTime;
            var wall = (now - _lastWall).TotalMilliseconds;
            var pct = wall <= 0 ? 0 : (cpu - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100;
            _lastCpu = cpu;
            _lastWall = now;
            return new PerfSample(now, Math.Round(Math.Clamp(pct, 0, 100), 1),
                Math.Round(_proc.WorkingSet64 / 1048576.0, 1), _proc.Threads.Count);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Detach();
            return null;
        }
    }

    void Detach()
    {
        _proc?.Dispose();
        _proc = null;
    }

    public void Dispose() => Detach();
}

/// <summary>Turns a stream of samples into saved sessions. Call <see cref="Push"/> once a second.</summary>
public sealed class SessionRecorder
{
    readonly SessionStore _store;
    readonly Func<(string? Id, string Name, List<string> Mods)> _playset;
    DateTime _lastSave;
    int _misses;

    public PerfSession? Current { get; private set; }
    public static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);
    /// <summary>Consecutive empty readings before a session counts as over (the game can stall while loading).</summary>
    public const int EndAfterMisses = 5;

    public SessionRecorder(SessionStore store, Func<(string? Id, string Name, List<string> Mods)> playset)
    {
        _store = store;
        _playset = playset;
    }

    /// <summary>Returns true when a session just finished and was saved.</summary>
    public bool Push(PerfSample? s, string? process, DateTime now)
    {
        if (s is null)
        {
            if (Current is null || ++_misses < EndAfterMisses) return false;
            Finish();
            return true;
        }
        _misses = 0;
        if (Current is null)
        {
            var (id, name, mods) = _playset();
            Current = new PerfSession
            {
                Id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), PlaysetId = id, PlaysetName = name,
                Mods = mods, Process = process ?? "", Start = now,
            };
            _lastSave = now;
        }
        Current.Samples.Add(s);
        Current.End = now;
        if (now - _lastSave >= SaveEvery)
        {
            _store.Save(Current);  // crash safety: a session survives the app dying mid-game
            _lastSave = now;
        }
        return false;
    }

    public void Finish()
    {
        if (Current is { Samples.Count: > 0 }) _store.Save(Current);
        Current = null;
        _misses = 0;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PerfSession))]
internal partial class PerfJson : JsonSerializerContext;

public sealed class SessionStore(string dir)
{
    public string Dir { get; } = dir;

    public void Save(PerfSession s)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, AppState.Slug(s.Id) + ".json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, PerfJson.Default.PerfSession));
        File.Move(tmp, path, overwrite: true);
    }

    public List<PerfSession> All()
    {
        if (!Directory.Exists(Dir)) return [];
        var list = new List<PerfSession>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllText(f), PerfJson.Default.PerfSession) is { } s) list.Add(s);
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        return list.OrderByDescending(s => s.Start).ToList();
    }

    public void Delete(string id)
    {
        var p = Path.Combine(Dir, AppState.Slug(id) + ".json");
        if (File.Exists(p)) File.Delete(p);
    }
}

/// <summary>Per-mod timings from the in-game probe mod. Format is ours (see perf-probe/), unverified in game.</summary>
public sealed record ModTiming(string ModId, double LoadMs, double TickMs, double GuiMs, long Ticks);

public static class ProbeReader
{
    /// <summary>Reads the probe's userdata file: <c>return { version = 1, mods = { { id = "x", load_ms = 1.2, tick_ms = 0.3, gui_ms = 0, ticks = 600 }, ... } }</c>.</summary>
    public static Dictionary<string, ModTiming> Read(string path)
    {
        var root = LuaTable.Parse(File.ReadAllText(path)) as Dictionary<string, object?>
            ?? throw new FormatException("probe file is not a table");
        var result = new Dictionary<string, ModTiming>();
        if (root.GetValueOrDefault("mods") is not Dictionary<string, object?> mods) return result;
        foreach (var entry in mods.Values)
        {
            if (entry is not Dictionary<string, object?> m || m.GetValueOrDefault("id") is not string id) continue;
            result[id] = new ModTiming(id, Num(m, "load_ms"), Num(m, "tick_ms"), Num(m, "gui_ms"), (long)Num(m, "ticks"));
        }
        return result;
    }

    static double Num(Dictionary<string, object?> m, string k) => m.GetValueOrDefault(k) is double d ? d : 0;
}

/// <summary>Minimal Lua table literal parser: tables, strings, numbers, booleans, nil, comments.
/// Array items get keys "1", "2", ... like Lua. Enough for data files, not for code.</summary>
public sealed class LuaTable
{
    readonly string _s;
    int _i;
    LuaTable(string s) => _s = s;

    public static object? Parse(string src)
    {
        var p = new LuaTable(src);
        p.Ws();
        if (p.TryWord("return")) p.Ws();
        var v = p.Value();
        p.Ws();
        if (p._i < p._s.Length) throw p.Err("trailing text");
        return v;
    }

    FormatException Err(string what) => new($"lua: {what} at {_i}");

    void Ws()
    {
        while (_i < _s.Length)
        {
            if (char.IsWhiteSpace(_s[_i])) _i++;
            else if (_s.AsSpan(_i).StartsWith("--[["))
            {
                var end = _s.IndexOf("]]", _i + 4, StringComparison.Ordinal);
                _i = end < 0 ? _s.Length : end + 2;
            }
            else if (_s.AsSpan(_i).StartsWith("--"))
            {
                while (_i < _s.Length && _s[_i] != '\n') _i++;
            }
            else break;
        }
    }

    bool TryWord(string w)
    {
        if (!_s.AsSpan(_i).StartsWith(w)) return false;
        var after = _i + w.Length;
        if (after < _s.Length && (char.IsLetterOrDigit(_s[after]) || _s[after] == '_')) return false;
        _i = after;
        return true;
    }

    object? Value()
    {
        Ws();
        if (_i >= _s.Length) throw Err("unexpected end");
        var c = _s[_i];
        if (c == '{') return Table();
        if (c == '"' || c == '\'') return Str();
        if (TryWord("true")) return true;
        if (TryWord("false")) return false;
        if (TryWord("nil")) return null;
        return Number();
    }

    Dictionary<string, object?> Table()
    {
        _i++;
        var t = new Dictionary<string, object?>();
        int n = 1;
        while (true)
        {
            Ws();
            if (_i >= _s.Length) throw Err("unclosed table");
            if (_s[_i] == '}') { _i++; return t; }
            string? key = null;
            if (_s[_i] == '[')
            {
                _i++;
                var k = Value();
                Ws();
                Expect(']');
                Ws();
                Expect('=');
                key = k is double d ? d.ToString(CultureInfo.InvariantCulture) : k?.ToString() ?? throw Err("nil key");
            }
            else if (char.IsLetter(_s[_i]) || _s[_i] == '_')
            {
                var start = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                var word = _s[start.._i];
                Ws();
                if (_i < _s.Length && _s[_i] == '=' && (_i + 1 >= _s.Length || _s[_i + 1] != '='))
                {
                    _i++;
                    key = word;
                }
                else _i = start;  // bare true/false/nil value
            }
            var v = Value();
            t[key ?? (n++).ToString(CultureInfo.InvariantCulture)] = v;
            Ws();
            if (_i < _s.Length && (_s[_i] == ',' || _s[_i] == ';')) _i++;
        }
    }

    void Expect(char c)
    {
        if (_i >= _s.Length || _s[_i] != c) throw Err($"expected '{c}'");
        _i++;
    }

    string Str()
    {
        var q = _s[_i++];
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= _s.Length) throw Err("unclosed string");
            var c = _s[_i++];
            if (c == q) return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (_i >= _s.Length) throw Err("bad escape");
            var e = _s[_i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '\n': sb.Append('\n'); break;
                default:
                    if (char.IsDigit(e))
                    {
                        var start = _i - 1;
                        while (_i < _s.Length && _i - start < 3 && char.IsDigit(_s[_i])) _i++;
                        sb.Append((char)int.Parse(_s[start.._i], CultureInfo.InvariantCulture));
                    }
                    else sb.Append(e);
                    break;
            }
        }
    }

    double Number()
    {
        var start = _i;
        if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '.' ||
               ((_s[_i] == '-' || _s[_i] == '+') && (_s[_i - 1] == 'e' || _s[_i - 1] == 'E')))) _i++;
        var tok = _s[start.._i];
        if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(tok[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
        if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw Err($"bad value '{tok}'");
    }
}
