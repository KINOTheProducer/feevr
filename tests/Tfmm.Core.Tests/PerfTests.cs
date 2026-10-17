using System.Diagnostics;
using Tfmm.Core;
using Xunit;

namespace Tfmm.Core.Tests;

public class LuaTests
{
    [Fact]
    public void ParsesTablesStringsNumbersAndComments()
    {
        var v = (Dictionary<string, object?>)LuaTable.Parse("""
            -- header
            return {
              a = 1, b = -2.5e1, c = "x\"y\n", ['d e'] = true, f = nil,
              list = { 10, 'two', { z = false }; },
              --[[ block
              comment ]] h = 0x10,
            }
            """)!;
        Assert.Equal(1.0, v["a"]);
        Assert.Equal(-25.0, v["b"]);
        Assert.Equal("x\"y\n", v["c"]);
        Assert.Equal(true, v["d e"]);
        Assert.Null(v["f"]);
        Assert.Equal(16.0, v["h"]);
        var list = (Dictionary<string, object?>)v["list"]!;
        Assert.Equal(10.0, list["1"]);
        Assert.Equal("two", list["2"]);
        Assert.Equal(false, ((Dictionary<string, object?>)list["3"]!)["z"]);
    }

    [Theory]
    [InlineData("return {")]
    [InlineData("return { a = }")]
    [InlineData("return { 'x }")]
    [InlineData("return {} junk")]
    public void RejectsBrokenInput(string src) => Assert.Throws<FormatException>(() => LuaTable.Parse(src));

    [Fact]
    public void ReadsProbeFile()
    {
        using var t = new TempDir();
        var f = Path.Combine(t.Root, "probe.lua");
        File.WriteAllText(f, "return { version = 1, mods = { { id = \"a\", load_ms = 2.5, tick_ms = 0.4, gui_ms = 0.1, ticks = 600 }, { load_ms = 1 } } }");
        var r = ProbeReader.Read(f);
        var a = Assert.Single(r).Value;
        Assert.Equal(0.4, a.TickMs);
        Assert.Equal(600, a.Ticks);
    }
}

public class SessionTests
{
    static PerfSample S(int sec, double cpu, double ram = 1000) => new(new DateTime(2026, 1, 1).AddSeconds(sec), cpu, ram, 10);

    [Fact]
    public void StatsAndPercentile()
    {
        var s = new PerfSession { Start = S(0, 0).T, End = S(100, 0).T, Samples = Enumerable.Range(1, 100).Select(i => S(i, i, i * 10)).ToList() };
        Assert.Equal(50.5, s.AvgCpu);
        Assert.Equal(95, s.P95Cpu);
        Assert.Equal(1000, s.PeakRam);
        Assert.Equal(0, PerfSession.Percentile([], 0.95));
    }

    [Fact]
    public void RecorderStartsSavesAndEndsSessions()
    {
        using var t = new TempDir();
        var store = new SessionStore(Path.Combine(t.Root, "sessions"));
        var rec = new SessionRecorder(store, () => ("ps", "My Set", ["m1"]));

        Assert.False(rec.Push(null, null, S(0, 0).T));
        Assert.Null(rec.Current);
        for (int i = 1; i <= 40; i++) rec.Push(S(i, 20), "game", S(i, 0).T);
        Assert.Equal("My Set", rec.Current!.PlaysetName);
        Assert.Single(store.All());  // autosaved after 30 s

        for (int i = 1; i < SessionRecorder.EndAfterMisses; i++) Assert.False(rec.Push(null, null, S(40 + i, 0).T));
        Assert.True(rec.Push(null, null, S(50, 0).T));
        Assert.Null(rec.Current);
        var saved = Assert.Single(store.All());
        Assert.Equal(40, saved.Samples.Count);
        Assert.Equal(["m1"], saved.Mods);
        Assert.Equal(TimeSpan.FromSeconds(39), saved.Duration);
    }

    [Fact]
    public void ShortStallDoesNotSplitSession()
    {
        using var t = new TempDir();
        var store = new SessionStore(Path.Combine(t.Root, "s"));
        var rec = new SessionRecorder(store, () => (null, "x", []));
        rec.Push(S(1, 5), "g", S(1, 0).T);
        rec.Push(null, null, S(2, 0).T);
        rec.Push(S(3, 5), "g", S(3, 0).T);
        Assert.Equal(2, rec.Current!.Samples.Count);
    }

    [Fact]
    public void GroupsByPlaysetWeightedBySamples()
    {
        var a1 = new PerfSession { PlaysetName = "A", Samples = [S(0, 10), S(1, 10), S(2, 10)] };
        var a2 = new PerfSession { PlaysetName = "A", Samples = [S(0, 50, 9000)] };
        var b = new PerfSession { PlaysetName = "B", Samples = [S(0, 5)] };
        var g = PerfStats.ByPlayset([a1, a2, b, new PerfSession { PlaysetName = "empty" }]);
        Assert.Equal(["A", "B"], g.Select(x => x.Name));
        Assert.Equal(20, g[0].AvgCpu);
        Assert.Equal(9000, g[0].PeakRam);
        Assert.Equal(2, g[0].Sessions);
    }

    [Fact]
    public void SamplesARealProcess()
    {
        var self = Process.GetCurrentProcess().ProcessName;
        using var sampler = new ProcessSampler(() => ["definitely-not-running-xyz", self]);
        var t0 = DateTime.Now;
        Assert.Null(sampler.Sample(t0));  // attach, no CPU baseline yet
        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 200) { }
        var s = sampler.Sample(t0.AddMilliseconds(250));
        Assert.NotNull(s);
        Assert.True(s!.RamMb > 1);
        Assert.True(s.Threads > 0);
        Assert.InRange(s.Cpu, 0, 100);
        Assert.Equal(self, sampler.ProcessName);
    }

    [Fact]
    public void NoProcessGivesNull()
    {
        using var sampler = new ProcessSampler(() => ["definitely-not-running-xyz"]);
        Assert.Null(sampler.Sample(DateTime.Now));
        Assert.Null(sampler.ProcessName);
    }
}
