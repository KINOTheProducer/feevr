using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tfmm.Core;

/// <summary>Writes made-up mods for trying the app without the game.</summary>
public static class Demo
{
    static JsonObject Dep(string id, string name, int lo = -1, int hi = -1, bool optional = false, bool loadBefore = true) => new()
    {
        ["mod"] = new JsonObject { ["modId"] = id, ["revisionMin"] = lo, ["revisionMax"] = hi },
        ["modInfo"] = new JsonObject { ["displayName"] = name },
        ["loadBefore"] = loadBefore, ["optional"] = optional,
    };

    static byte[] Bytes(int mb) => new byte[mb << 20];

    record Spec(string Id, string Name, string Summary, int Rev, string[] Tags, JsonObject Extra, Dictionary<string, object> Files);

    static readonly Spec[] Mods =
    [
        new("demo_core_lib", "Core Script Library", "Shared helpers other mods build on.", 4, ["Script", "Library"], [],
            new() { ["content/scripts/core.script.lua"] = "local M = {}\nreturn M\n" }),
        new("demo_ui_toolkit", "UI Toolkit", "Extra panels and widgets for script mods.", 2, ["Script", "UI"],
            new() { ["dependencies"] = new JsonArray(Dep("demo_core_lib", "Core Script Library", 3)) },
            new() { ["content/scripts/ui.script.lua"] = "return { guiUpdate = function() end }\n" }),
        new("demo_line_manager", "Advanced Line Manager", "Bulk edit lines, frequencies and vehicles.", 7, ["Script", "Lines"],
            new()
            {
                ["dependencies"] = new JsonArray(Dep("demo_ui_toolkit", "UI Toolkit"), Dep("demo_timetables", "Timetables", optional: true)),
                ["severityRemove"] = "Critical",
            },
            new() { ["content/scripts/lines.script.lua"] = "return { update = function() api.engine.forEachEntityWithComponent(f, c) api.engine.forEachEntity(g) end }\n" }),
        new("demo_timetables", "Timetables", "Departure times for every stop.", 3, ["Script", "Lines"],
            new() { ["severityAdd"] = "Warning", ["severityRemove"] = "Critical" },
            new() { ["content/scripts/tt.script.lua"] = "return { update = function() end, handleEvent = function() end }\n" }),
        new("demo_classic_trams", "Classic Trams Pack", "Twelve trams from the 1920s to the 1960s.", 5, ["Vehicle", "Tram"], [],
            new()
            {
                ["content/models/tram_a.mdl"] = new string('x', 2000),
                ["content/textures/tram_albedo.dds"] = Bytes(9), ["content/textures/tram_normal.dds"] = Bytes(3),
            }),
        new("demo_modern_tram", "Modern Tram Set", "Low-floor trams with new liveries.", 1, ["Vehicle", "Tram"],
            new() { ["incompatibilities"] = new JsonArray(new JsonObject { ["mod"] = new JsonObject { ["modId"] = "demo_classic_trams", ["revisionMax"] = 5 } }) },
            new() { ["content/textures/modern.dds"] = Bytes(2) }),
        new("demo_better_stations", "Better Stations", "Modular stations with more platform options.", 9, ["Construction", "Station"],
            new() { ["dependencies"] = new JsonArray(Dep("demo_core_lib", "Core Script Library", 5)) },
            new() { ["content/constructions/station.con"] = "-- construction\n" }),
        new("demo_autumn_trees", "Autumn Trees", "Seasonal foliage, cosmetic only.", 2, ["Environment"],
            new() { ["cosmetic"] = true }, new() { ["content/textures/leaves.dds"] = Bytes(1) }),
        new("demo_freight_wagons", "Freight Wagon Expansion", "Forty new wagons for bulk, tank and container cargo.", 3, ["Vehicle", "Train"], [],
            new() { ["content/textures/wagons.dds"] = Bytes(6), ["content/models/wagon.mdl"] = new string('x', 9000) }),
        new("demoNoUnderscore", "Untidy Mod With A Name That Is Too Long", new string('x', 120), 1, ["Misc"], [], []),
    ];

    /// <summary>Fake recorded sessions and a fake probe file, so the performance screen has something to show.</summary>
    public static void WritePerf(AppState state, string probePath)
    {
        var rng = new Random(7);
        var t0 = new DateTime(2026, 10, 10, 19, 0, 0, DateTimeKind.Local);
        (string id, string name, string[] mods, double cpu, double ram, int mins)[] runs =
        [
            ("main-campaign", "Main Campaign", ["demo_line_manager", "demo_classic_trams", "demo_better_stations"], 31, 6200, 95),
            ("main-campaign", "Main Campaign", ["demo_line_manager", "demo_classic_trams", "demo_better_stations"], 34, 6650, 140),
            ("vanilla-plus", "Vanilla Plus", ["demo_autumn_trees", "demo_freight_wagons"], 19, 4300, 120),
            ("vanilla-plus", "Vanilla Plus", ["demo_autumn_trees", "demo_freight_wagons"], 18, 4150, 60),
        ];
        for (int r = 0; r < runs.Length; r++)
        {
            var run = runs[r];
            var start = t0.AddDays(r).AddMinutes(r * 37);
            var s = new PerfSession { Id = start.ToString("yyyyMMdd-HHmmss"), PlaysetId = run.id, PlaysetName = run.name, Mods = [.. run.mods], Process = "TransportFever3", Start = start };
            // one sample per 10 s keeps demo files small; real sessions are 1 Hz
            for (int i = 0; i < run.mins * 6; i++)
            {
                var growth = i / (run.mins * 6.0);
                var spike = rng.NextDouble() < 0.04 ? 25 : 0;
                s.Samples.Add(new PerfSample(start.AddSeconds(i * 10),
                    Math.Round(Math.Clamp(run.cpu + rng.NextDouble() * 12 - 6 + spike, 0, 100), 1),
                    Math.Round(run.ram * (0.8 + 0.25 * growth) + rng.Next(-60, 60), 1), 48 + rng.Next(0, 6)));
            }
            s.End = s.Samples[^1].T;
            state.Sessions.Save(s);
        }
        File.WriteAllText(probePath, """
            -- written by perf-probe (demo data)
            return {
              version = 1, frames = 36000,
              mods = {
                { id = "demo_line_manager", load_ms = 41.5, tick_ms = 0.62, gui_ms = 0.08, ticks = 36000 },
                { id = "demo_timetables", load_ms = 12.0, tick_ms = 0.21, gui_ms = 0.0, ticks = 36000 },
                { id = "demo_ui_toolkit", load_ms = 8.4, tick_ms = 0.0, gui_ms = 0.11, ticks = 36000 },
                { id = "demo_core_lib", load_ms = 3.1, tick_ms = 0.0, gui_ms = 0.0, ticks = 0 },
                { id = "demo_classic_trams", load_ms = 220.0, tick_ms = 0.0, gui_ms = 0.0, ticks = 0 },
              },
            }
            """);
    }

    /// <summary>Two crash copies and a clean live log, in TF2's stdout style (TF3's format is unverified).</summary>
    public static string WriteCrashLogs(string dir, string modsRoot)
    {
        Directory.CreateDirectory(dir);
        var mods = modsRoot.Replace('\\', '/');
        void Log(string name, DateTime when, string text)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllText(p, text.Replace("{mods}", mods));
            File.SetLastWriteTime(p, when);
        }
        var now = DateTime.Now;
        Log("stdout.txt", now.AddMinutes(-20), """
            Transport Fever 3
            Loading mods...
            Loaded 4 mods
            Saving game... done
            """);
        Log($"stdout_{now.AddDays(-1):yyyy-MM-dd_HH-mm-ss}.txt", now.AddDays(-1), """
            Transport Fever 3
            Loading mods...
            Lua error: {mods}/demo_line_manager/res/scripts/line_panel.lua:212: attempt to index a nil value (local 'line')
            stack traceback:
            	[C]: in function 'error'
            	{mods}/demo_line_manager/res/scripts/line_panel.lua:212: in function 'refresh'
            	{mods}/demo_ui_toolkit/res/scripts/panel.lua:88: in function 'guiUpdate'
            Lua error: {mods}/demo_line_manager/res/scripts/line_panel.lua:212: attempt to index a nil value (local 'line')
            stack traceback:
            	[C]: in function 'error'
            Error: texture not found: res/textures/vehicle/tram/old_12.dds
            Error message: Assertion `vehicle != nullptr' failed.
            	in {mods}/demo_line_manager/res/scripts/line_panel.lua:230
            """);
        Log($"stdout_{now.AddDays(-4):yyyy-MM-dd_HH-mm-ss}.txt", now.AddDays(-4), """
            Transport Fever 3
            Loading mods...
            Error message: Out of memory while loading models
            	in {mods}/demo_classic_trams/res/models/model/vehicle/tram/tram_1925.mdl
            """);
        return dir;
    }

    public static string Write(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var s in Mods)
        {
            var p = Path.Combine(root, s.Id);
            Directory.CreateDirectory(Path.Combine(p, "_metadata"));
            var mod = new JsonObject { ["modId"] = s.Id, ["revision"] = s.Rev, ["visible"] = true };
            foreach (var kv in s.Extra) mod[kv.Key] = kv.Value?.DeepClone();
            File.WriteAllText(Path.Combine(p, "mod.json"), mod.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var meta = new JsonObject
            {
                ["name"] = s.Name, ["summary"] = s.Summary,
                ["authors"] = new JsonArray(new JsonObject { ["name"] = "demo" }),
                ["tags"] = new JsonArray(s.Tags.Select(t => (JsonNode)t).ToArray()),
            };
            File.WriteAllText(Path.Combine(p, "_metadata", "modinfo.json"), meta.ToJsonString());
            foreach (var (rel, content) in s.Files)
            {
                var fp = Path.Combine(p, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
                if (content is byte[] b) File.WriteAllBytes(fp, b); else File.WriteAllText(fp, (string)content);
            }
        }
        return root;
    }
}
