using Tfmm.Core;
using Xunit;

namespace Tfmm.Core.Tests;

public class CrashTests
{
    // Shaped like a TF2 stdout.txt; TF3's real format is unverified.
    const string Log = """
        Transport Fever 3 build 41234
        Loading mods...
        mod: curvy_cities (rev 1)
        Lua error: C:/Users/Public/mod.io/10640/mods/6416560/mod.script.lua:12: attempt to index a nil value (field 'config')
        stack traceback:
        	[C]: in function 'error'
        	C:/Users/Public/mod.io/10640/mods/6416560/mod.script.lua:12: in function 'preRunFn'
        game continues
        Lua error: C:/Users/Public/mod.io/10640/mods/6416560/mod.script.lua:12: attempt to index a nil value (field 'config')
        stack traceback:
        	[C]: in function 'error'
        Error: texture not found: res/textures/kino_bus/body_2.dds
        Error message: Assertion `false' failed. curvy_cities::/mod.script
        	in res/scripts/street_util.lua
        """;

    static Dictionary<string, Mod> Catalog() => new()
    {
        ["curvy_cities"] = new Mod { ModId = "curvy_cities", Path = @"C:/Users/Public/mod.io/10640/mods/6416560", ModIoId = 6416560 },
        ["kino_bus"] = new Mod { ModId = "kino_bus", Path = "/mods/kino_bus_1" },
    };

    [Fact]
    public void FindsErrorsAndBlamesMods()
    {
        var r = new CrashReport { Path = "stdout.txt" };
        CrashLogs.Parse(Log, r, Catalog());
        Assert.True(r.Crashed);
        Assert.Equal(["Lua error", "Error", "Error message"], r.Errors.Select(e => e.Kind));
        var lua = r.Errors[0];
        Assert.Equal(2, lua.Count);  // repeat folded
        Assert.False(lua.Fatal);
        Assert.Equal(["curvy_cities"], lua.Mods);
        Assert.Equal(3, lua.Context.Count);
        Assert.Empty(r.Errors[1].Mods);  // res/textures/kino_bus is not a mod folder path
        Assert.True(r.Errors[2].Fatal);
        var top = r.Suspects[0];
        Assert.Equal(("curvy_cities", 3, true), (top.ModId, top.Hits, top.InFatal));
        Assert.Equal("\tin res/scripts/street_util.lua", r.Tail[^1]);
    }

    [Fact]
    public void CleanLogIsNotACrash()
    {
        var r = new CrashReport { Path = "stdout.txt" };
        CrashLogs.Parse("Loading mods...\nall good\n", r, Catalog());
        Assert.False(r.Crashed);
        Assert.Empty(r.Errors);
        Assert.Empty(r.Suspects);
    }

    [Fact]
    public void FindsFilesNewestFirstAndReadsSharedFile()
    {
        using var t = new TempDir();
        var dir = Path.Combine(t.Root, "crash_dump");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stdout.txt"), "ok\n");
        File.WriteAllText(Path.Combine(dir, "stdout_2026-10-16_21-04-11.txt"), Log);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a log");
        File.SetLastWriteTimeUtc(Path.Combine(dir, "stdout.txt"), DateTime.UtcNow.AddHours(-1));
        var files = CrashLogs.Find([t.Root]);
        Assert.Equal(["stdout_2026-10-16_21-04-11.txt", "stdout.txt"], files.Select(f => f.Name));

        // the game holds stdout.txt open while it runs
        using var held = new FileStream(files[1].FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.False(CrashLogs.Read(files[1].FullName, Catalog()).Crashed);
        var crash = CrashLogs.Read(files[0].FullName, Catalog());
        Assert.True(crash.Crashed);
        Assert.Equal("curvy_cities", crash.Suspects[0].ModId);
    }

    [Fact]
    public void TimestampedCopyCountsAsCrash()
    {
        using var t = new TempDir();
        var p = Path.Combine(t.Root, "stdout_2026-10-16.txt");
        File.WriteAllText(p, "nothing logged\n");
        Assert.True(CrashLogs.Read(p, Catalog()).Crashed);
    }
}
