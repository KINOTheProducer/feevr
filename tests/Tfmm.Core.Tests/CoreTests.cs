using System.Text.Json.Nodes;
using Tfmm.Core;
using Xunit;

namespace Tfmm.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("tfmm-test-").FullName;
    public string Mods => Path.Combine(Root, "mods");
    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }

    public string Mod(string folder, object modJson, object? modinfo = null, Dictionary<string, object>? files = null, string? root = null)
    {
        var p = Path.Combine(root ?? Mods, folder);
        Directory.CreateDirectory(Path.Combine(p, "_metadata"));
        File.WriteAllText(Path.Combine(p, "mod.json"), modJson as string ?? ((JsonNode)modJson).ToJsonString());
        if (modinfo is not false)
            File.WriteAllText(Path.Combine(p, "_metadata", "modinfo.json"),
                ((JsonNode?)modinfo ?? new JsonObject { ["name"] = folder, ["authors"] = new JsonArray("a") }).ToJsonString());
        foreach (var (rel, c) in files ?? [])
        {
            var fp = Path.Combine(p, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
            if (c is byte[] b) File.WriteAllBytes(fp, b); else File.WriteAllText(fp, (string)c);
        }
        return p;
    }

    public Dictionary<string, Mod> Catalog() => ModScanner.Scan([(ModSource.Manual, Mods)]).Catalog;
}

static class J
{
    public static JsonObject M(string id, int rev = 1, JsonArray? deps = null, JsonArray? inc = null, string? sevAdd = null, string? sevRem = null)
    {
        var o = new JsonObject { ["modId"] = id, ["revision"] = rev };
        if (deps != null) o["dependencies"] = deps;
        if (inc != null) o["incompatibilities"] = inc;
        if (sevAdd != null) o["severityAdd"] = sevAdd;
        if (sevRem != null) o["severityRemove"] = sevRem;
        return o;
    }

    public static JsonObject Dep(string id, int lo = -1, int hi = -1, bool optional = false, bool? loadBefore = null)
    {
        var d = new JsonObject { ["mod"] = new JsonObject { ["modId"] = id, ["revisionMin"] = lo, ["revisionMax"] = hi }, ["optional"] = optional };
        if (loadBefore is { } lb) d["loadBefore"] = lb;
        return d;
    }

    public static JsonArray A(params JsonNode[] n) => new(n);
    public static JsonObject Inc(string id) => new() { ["mod"] = new JsonObject { ["modId"] = id } };
}

public class ScanTests
{
    [Fact]
    public void ParsesFullMod()
    {
        using var t = new TempDir();
        t.Mod("kino_a", J.M("kino_a", 3, J.A(J.Dep("lib_x", 2, loadBefore: false)), J.A(J.Inc("bad_y")), sevAdd: "Critical"));
        var m = t.Catalog()["kino_a"];
        Assert.Equal(3, m.Revision);
        Assert.Equal("Critical", m.SeverityAdd);
        Assert.Equal("Warning", m.SeverityRemove);  // wiki default
        Assert.Equal(2, m.Dependencies[0].Ref.RevMin);
        Assert.False(m.Dependencies[0].LoadBefore);
        Assert.Equal("bad_y", m.Incompatibilities[0].ModId);
        Assert.Empty(m.Warnings);
    }

    [Fact]
    public void ParsesLoadScripts()
    {
        // mod.json of a real mod.io mod (Curvy Cities)
        using var t = new TempDir();
        var p = t.Mod("6416560", """
            {
              "modId": "curvy_cities", "revision": 1, "severityAdd": "None", "severityRemove": "None",
              "visible": true, "cosmetic": false,
              "preRunScript": { "fileName": "curvy_cities::/mod.script@preRunFn" }
            }
            """, modinfo: false, files: new() { ["mod.script.lua"] = "function data.preRunFn() end" });
        var m = Assert.Single(ModScanner.Scan([(ModSource.Staging, t.Mods)]).Catalog.Values);
        Assert.Equal(new LoadScript("preRunScript", "mod.script", "preRunFn"), Assert.Single(m.LoadScripts));
        Assert.True(m.HasRootScript);
        Assert.DoesNotContain(m.Warnings, w => w.Contains("Script"));
        Assert.Equal(["preRunScript"], Auditor.Run(m).Hooks["mod.script.lua"]);

        File.Delete(Path.Combine(p, "mod.script.lua"));
        m = Assert.Single(ModScanner.Scan([(ModSource.Staging, t.Mods)]).Catalog.Values);
        Assert.Contains(m.Warnings, w => w.Contains("preRunScript file 'mod.script' not found"));
    }

    [Fact]
    public void FlagsBadMods()
    {
        using var t = new TempDir();
        t.Mod("bad-name", new JsonObject { ["modId"] = "x y", ["severityAdd"] = "Huge" }, modinfo: false);
        // packaging checks are for mods you are building (Staging)
        var m = Assert.Single(ModScanner.Scan([(ModSource.Staging, t.Mods)]).Catalog.Values);
        var all = string.Join(" | ", m.Warnings);
        foreach (var s in new[] { "invalid modId", "folder name", "missing revision", "severityAdd", "modinfo.json" })
            Assert.Contains(s, all);
        // downloaded or unpacked mods don't get them
        Assert.Empty(Assert.Single(ModScanner.Scan([(ModSource.Subscribed, t.Mods)]).Catalog.Values).Warnings);
        Assert.Empty(Assert.Single(ModScanner.Scan([(ModSource.Manual, t.Mods)]).Catalog.Values).Warnings);
    }

    [Fact]
    public void SkipsBrokenJsonAndHandlesBom()
    {
        using var t = new TempDir();
        t.Mod("kino_broken", "{not json");
        t.Mod("kino_bom", "﻿" + J.M("kino_bom").ToJsonString());
        Assert.Equal(["kino_bom"], t.Catalog().Keys);
    }

    [Fact]
    public void SourcePriority()
    {
        using var t = new TempDir();
        var sub = Path.Combine(t.Root, "sub");
        t.Mod("kino_a", J.M("kino_a", 1), root: sub);
        t.Mod("kino_a", J.M("kino_a", 2));
        var (cat, shadowed) = ModScanner.Scan([(ModSource.Subscribed, sub), (ModSource.Manual, t.Mods)]);
        Assert.Equal(2, cat["kino_a"].Revision);
        Assert.Equal(ModSource.Subscribed, Assert.Single(shadowed).Source);
    }

    [Fact]
    public void RevisionRange()
    {
        var r = new ModRef("x", 2, 4);
        Assert.Equal([false, true, true, false], new[] { 1, 2, 4, 5 }.Select(r.Accepts));
        Assert.True(new ModRef("x").Accepts(999));
    }
}

public class ResolveTests : IDisposable
{
    readonly TempDir t = new();
    public void Dispose() => t.Dispose();

    public ResolveTests()
    {
        t.Mod("lib_core", J.M("lib_core", 2));
        t.Mod("lib_ui", J.M("lib_ui", 1, J.A(J.Dep("lib_core"))));
        t.Mod("kino_app", J.M("kino_app", 1, J.A(J.Dep("lib_ui"), J.Dep("lib_extra", optional: true))));
        t.Mod("kino_rival", J.M("kino_rival", 1, inc: J.A(J.Inc("kino_app"))));
    }

    [Fact]
    public void OrdersDepsFirst()
    {
        var rep = Resolver.Check(["kino_app", "lib_ui", "lib_core"], t.Catalog());
        Assert.True(rep.Ok);
        Assert.Equal(["lib_core", "lib_ui", "kino_app"], rep.Order);
        Assert.Contains(rep.Issues, i => i.Level == IssueLevel.Info && i.ModId == "kino_app");
    }

    [Fact]
    public void DepInstalledNotEnabled()
    {
        var rep = Resolver.Check(["kino_app"], t.Catalog());
        Assert.False(rep.Ok);
        Assert.Contains(rep.Issues, i => i.Message.Contains("installed but not enabled"));
    }

    [Fact]
    public void AutoAddDeps()
    {
        var rep = Resolver.Check(["kino_app"], t.Catalog(), autoAddDeps: true);
        Assert.True(rep.Ok);
        Assert.Equal(["lib_core", "lib_ui", "kino_app"], rep.Order);
    }

    [Fact]
    public void NotInstalled()
    {
        var rep = Resolver.Check(["ghost_mod"], t.Catalog());
        Assert.Equal(["ghost_mod"], rep.Missing);
        Assert.False(rep.Ok);
    }

    [Fact]
    public void RevisionMismatch()
    {
        t.Mod("kino_old", J.M("kino_old", 1, J.A(J.Dep("lib_core", -1, 1))));
        var rep = Resolver.Check(["kino_old", "lib_core"], t.Catalog());
        Assert.Contains(rep.Issues, i => i.Message.Contains("installed rev 2"));
        Assert.False(rep.Ok);
    }

    [Fact]
    public void Incompatible()
    {
        var rep = Resolver.Check(["kino_rival", "kino_app"], t.Catalog(), autoAddDeps: true);
        Assert.Contains(rep.Issues, i => i.Message.StartsWith("Incompatible"));
    }

    [Fact]
    public void LoadBeforeFalseIsNoConstraint()
    {
        t.Mod("kino_late", J.M("kino_late", 1, J.A(J.Dep("lib_core", loadBefore: false))));
        Assert.Equal(["kino_late", "lib_core"], Resolver.Check(["kino_late", "lib_core"], t.Catalog()).Order);
    }

    [Fact]
    public void Cycle()
    {
        t.Mod("cyc_a", J.M("cyc_a", 1, J.A(J.Dep("cyc_b"))));
        t.Mod("cyc_b", J.M("cyc_b", 1, J.A(J.Dep("cyc_a"))));
        var rep = Resolver.Check(["cyc_a", "cyc_b"], t.Catalog());
        Assert.Contains(rep.Issues, i => i.Message.Contains("cycle"));
        Assert.Equal(["cyc_a", "cyc_b"], rep.Order.Order());
    }

    [Fact]
    public void CriticalSeverityWarns()
    {
        t.Mod("kino_crit", J.M("kino_crit", sevRem: "Critical"));
        var rep = Resolver.Check(["kino_crit"], t.Catalog());
        Assert.True(rep.Ok);
        Assert.Contains(rep.Issues, i => i.Level == IssueLevel.Warning && i.ModId == "kino_crit");
    }
}

public class AuditTests
{
    [Fact]
    public void FindsHooksAndFlags()
    {
        using var t = new TempDir();
        t.Mod("kino_heavy", J.M("kino_heavy"), files: new()
        {
            ["content/scripts/a.script.lua"] = "return { update = function() api.engine.forEachEntity(f) end }",
            ["content/scripts/b.script.lua"] = "function data.handleEvent(s, e) end",
            ["content/scripts/c.script.lua"] = "local function myupdate() end",
            ["content/tex/big.dds"] = new byte[Auditor.BigTexture + 1],
        });
        var a = Auditor.Run(t.Catalog()["kino_heavy"]);
        Assert.Equal(["update"], a.Hooks["content/scripts/a.script.lua"]);
        Assert.Equal(["handleEvent"], a.Hooks["content/scripts/b.script.lua"]);
        Assert.False(a.Hooks.ContainsKey("content/scripts/c.script.lua"));
        Assert.Equal(["content/scripts/a.script.lua"], a.TickHookFiles);
        Assert.Contains(a.Flags, f => f.StartsWith("Large texture"));
        Assert.Contains(a.Flags, f => f.Contains("throttled"));
        Assert.True(a.Score > 0);
    }
}

public class StateTests
{
    [Fact]
    public void PlaysetLifecycle()
    {
        using var t = new TempDir();
        Demo.Write(t.Mods);
        var s = new AppState(Path.Combine(t.Root, "home"));
        s.AddRoot(ModSource.Manual, t.Mods);
        Assert.True(s.Catalog.Count >= 9);

        var ps = s.CreatePlayset("My Set!");
        Assert.Equal("my-set", ps.Id);
        Assert.Equal("my-set-2", s.CreatePlayset("my set").Id);

        s.Toggle(ps, "demo_line_manager", true);
        Assert.False(s.Check(ps).Ok);
        Assert.Equal(2, s.ApplyDeps(ps));
        Assert.True(s.Check(s.GetPlayset("my-set")).Ok);

        s.SetActive(ps.Id);
        var again = new AppState(s.Home);
        Assert.Equal("my-set", again.Active?.Id);
        Assert.Single(again.Roots());

        again.DeletePlayset("my-set");
        Assert.Null(again.Active);
        Assert.Single(again.Playsets());
    }

    [Fact]
    public void SlugBlocksTraversal()
    {
        Assert.Equal("etc-passwd", AppState.Slug("../../etc/passwd"));
        Assert.Equal("playset", AppState.Slug("..."));
    }

    [Fact]
    public void LaunchDefaultsToSteam()
    {
        var c = new Config();
        Assert.Equal("steam://rungameid/3493540", c.LaunchCommand);
        c.Launch = "  ";
        Assert.Equal(Config.DefaultLaunch, c.LaunchCommand);
        c.Launch = @"C:\Games\TF3\TransportFever3.exe";
        Assert.Equal(c.Launch, c.LaunchCommand);
    }

    [Fact]
    public void WritesActiveModsIntoSettingsLua()
    {
        const string lua = "function data()\nreturn { \n\t\tmainMenuState = { \n\t\t\tactiveModsParamsState = { [\"\"] = { [\"map.size\"] = 2, }, },\n\t\t\tactiveModsState = { \"kino_fleet_foreman_1\", },\n\t\t\tmapGenParamsState = { },\n\t\t},\n\t\tvsync = true,\n\t}\nend\n";
        var outp = GameSettings.WithActiveMods(lua, ["a_one", "b_two"]);
        Assert.Contains("activeModsState = { \"a_one\", \"b_two\", }", outp);
        Assert.DoesNotContain("kino_fleet_foreman_1", outp);
        Assert.Contains("[\"map.size\"] = 2", outp);
        Assert.Contains("mapGenParamsState = { }", outp);
        Assert.Contains("activeModsState = { }", GameSettings.WithActiveMods(lua, []));

        var noState = GameSettings.WithActiveMods("function data()\nreturn { mainMenuState = { }, vsync = true, }\nend", ["x_y"]);
        Assert.Contains("mainMenuState = { activeModsState = { \"x_y\", },", noState);
        var noMenu = GameSettings.WithActiveMods("function data()\nreturn { vsync = true, }\nend", ["x_y"]);
        Assert.Contains("mainMenuState = { activeModsState = { \"x_y\", }, },", noMenu);
        Assert.Throws<FormatException>(() => GameSettings.WithActiveMods("garbage", ["x_y"]));
    }

    [Fact]
    public void LaunchWritesActivePlayset()
    {
        using var t = new TempDir();
        t.Mod("a_base", new JsonObject { ["modId"] = "a_base", ["revision"] = 1 });
        t.Mod("b_addon", new JsonObject { ["modId"] = "b_addon", ["revision"] = 1,
            ["dependencies"] = new JsonArray(new JsonObject { ["mod"] = new JsonObject { ["modId"] = "a_base" } }) });
        var s = new AppState(Path.Combine(t.Root, "home"));
        s.AddRoot(ModSource.Manual, t.Mods);
        var settings = Path.Combine(t.Root, "settings.lua");
        File.WriteAllText(settings, "function data()\nreturn { mainMenuState = { activeModsState = { }, }, }\nend");
        s.Config.GameSettings = settings;
        s.Config.GameProcess = "junction-no-such-process";
        Assert.Null(s.WriteActivePlayset());

        var ps = s.CreatePlayset("Set", ["b_addon", "a_base"]);
        s.SetActive(ps.Id);
        Assert.Equal(2, s.WriteActivePlayset());
        Assert.Contains("activeModsState = { \"a_base\", \"b_addon\", }", File.ReadAllText(settings));
        Assert.True(File.Exists(settings + ".bak"));
    }
}

public class MigrateHomeTests
{
    [Fact]
    public void MovesOldFolderOnce()
    {
        using var t = new TempDir();
        string legacy = Path.Combine(t.Root, "feevr"), home = Path.Combine(t.Root, "junction");
        Directory.CreateDirectory(Path.Combine(legacy, "playsets"));
        File.WriteAllText(Path.Combine(legacy, "config.json"), "{}");

        Assert.Equal(home, AppState.MigrateHome(legacy, home));
        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(home, "config.json")));
        Assert.True(Directory.Exists(Path.Combine(home, "playsets")));
    }

    [Fact]
    public void LeavesBothAloneWhenNewFolderExists()
    {
        using var t = new TempDir();
        string legacy = Path.Combine(t.Root, "feevr"), home = Path.Combine(t.Root, "junction");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(home);

        Assert.Equal(home, AppState.MigrateHome(legacy, home));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void FreshInstallUsesNewFolder()
    {
        using var t = new TempDir();
        var home = Path.Combine(t.Root, "junction");
        Assert.Equal(home, AppState.MigrateHome(Path.Combine(t.Root, "feevr"), home));
        Assert.False(Directory.Exists(home));
    }
}
