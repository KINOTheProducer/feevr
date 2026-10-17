using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Tfmm.Core;
using Xunit;

namespace Tfmm.Core.Tests;

public class ModIoTests
{
    sealed class FakeHttp(Func<string, string?> respond) : HttpMessageHandler
    {
        public List<string> Hits { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Hits.Add(url);
            var body = respond(url);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>Lays out …/mod.io/10640/mods/&lt;id&gt; like a real install, mod.json only (no _metadata).</summary>
    static string ModIoTree(TempDir t, params (long Id, string ModId)[] mods)
    {
        var modsDir = Path.Combine(t.Root, "mod.io", "10640", "mods");
        foreach (var (id, modId) in mods)
            t.Mod(id.ToString(), new JsonObject { ["modId"] = modId, ["revision"] = 3 }, modinfo: false, root: modsDir);
        return modsDir;
    }

    const string ApiJson = """
        { "data": [
          { "id": 6416560, "name": "Auto Signals", "summary": "Places signals for you.", "profile_url": "https://mod.io/g/transportfever3/m/auto-signals",
            "submitted_by": { "username": "brian" }, "tags": [ { "name": "Script" }, { "name": "Rail" } ] },
          { "id": 7, "name": "Bus Loops", "summary": "", "submitted_by": { "username": "kino" }, "tags": [] } ],
          "result_count": 2 }
        """;

    [Theory]
    [InlineData("auto_signals_1", "Auto Signals")]
    [InlineData("brian_rail_interchanges_1", "Brian Rail Interchanges")]
    [InlineData("apasz_dark_ui_1", "Apasz Dark UI")]
    [InlineData("colamods_cargo_stop", "Colamods Cargo Stop")]
    [InlineData("celmi_timetables", "Celmi Timetables")]
    [InlineData("x", "X")]
    public void Prettifies(string id, string expected) => Assert.Equal(expected, ModIo.Prettify(id));

    [Fact]
    public void ReadsIdFromPath()
    {
        Assert.Equal(6416560, ModIo.IdFromPath(Path.Combine("C:", "Users", "Public", "mod.io", "10640", "mods", "6416560")));
        Assert.Null(ModIo.IdFromPath(Path.Combine("C:", "mods", "kino_thing")));
        Assert.Null(ModIo.IdFromPath(Path.Combine("C:", "stuff", "mods", "123")));  // parent of mods must be a game id
    }

    [Fact]
    public void ScansModIoFoldersWithoutWarningsAndWithNiceNames()
    {
        using var t = new TempDir();
        var dir = ModIoTree(t, (6416560, "auto_signals_1"), (7, "bus_loops_1"));
        var s = new AppState(Path.Combine(t.Root, "home"));
        s.AddRoot(ModSource.Subscribed, dir);
        var m = s.Catalog["auto_signals_1"];
        Assert.Equal(6416560, m.ModIoId);
        Assert.Empty(m.Warnings);
        Assert.Equal("Auto Signals", m.DisplayName);
        Assert.True(m.NameGuessed);
    }

    [Fact]
    public void UsesModIoLocalStateWhenPresent()
    {
        using var t = new TempDir();
        var dir = ModIoTree(t, (6416560, "auto_signals_1"));
        var meta = Path.Combine(t.Root, "mod.io", "10640", "metadata");
        Directory.CreateDirectory(meta);
        File.WriteAllText(Path.Combine(meta, "state.json"), """
            { "Mods": [ { "ModID": 6416560, "Profile": { "name": "Automatic Signals Pro", "summary": "x", "submitted_by": { "username": "sig" } } } ] }
            """);
        var s = new AppState(Path.Combine(t.Root, "home"));
        s.AddRoot(ModSource.Subscribed, dir);
        var m = s.Catalog["auto_signals_1"];
        Assert.Equal("Automatic Signals Pro", m.Name);
        Assert.False(m.NameGuessed);
        Assert.Equal(["sig"], m.Authors);
    }

    [Fact]
    public void ParsesApiResponse()
    {
        var list = ModIo.ParseMods(ApiJson);
        Assert.Equal(2, list.Count);
        Assert.Equal("brian", list[0].Author);
        Assert.Equal(["Script", "Rail"], list[0].Tags);
    }

    [Fact]
    public async Task FetchesNamesWithApiKeyAndCachesThem()
    {
        using var t = new TempDir();
        var dir = ModIoTree(t, (6416560, "auto_signals_1"), (7, "bus_loops_1"));
        var home = Path.Combine(t.Root, "home");
        var s = new AppState(home);
        s.AddRoot(ModSource.Subscribed, dir);
        s.Config.ModIoApiKey = "k3y";

        var fake = new FakeHttp(url => url.Contains("api_key=k3y") ? ApiJson : null);
        Assert.Equal(2, await s.RefreshModIoAsync(new HttpClient(fake), onlyMissing: true));
        var hit = Assert.Single(fake.Hits);
        Assert.StartsWith("https://api.mod.io/v1/games/10640/mods?", hit);
        Assert.Contains("id-in=", hit);
        Assert.Contains("6416560", hit);

        var m = s.Catalog["auto_signals_1"];
        Assert.Equal("Auto Signals", m.Name);
        Assert.False(m.NameGuessed);
        Assert.Equal(["brian"], m.Authors);
        Assert.Contains("Rail", m.Tags);
        Assert.Equal("https://mod.io/g/transportfever3/m/auto-signals", m.ProfileUrl);

        // fresh cache: a second refresh asks for nothing; a restart reads names from the cache
        Assert.Equal(0, await s.RefreshModIoAsync(new HttpClient(fake), onlyMissing: true));
        Assert.Single(fake.Hits);
        var again = new AppState(home);
        Assert.Equal("Bus Loops", again.Catalog["bus_loops_1"].Name);
    }

    [Fact]
    public async Task BadKeyOrNoKeyChangesNothing()
    {
        using var t = new TempDir();
        var s = new AppState(Path.Combine(t.Root, "home"));
        s.AddRoot(ModSource.Subscribed, ModIoTree(t, (7, "bus_loops_1")));
        var fake = new FakeHttp(_ => null);
        Assert.Equal(0, await s.RefreshModIoAsync(new HttpClient(fake), false));
        Assert.Empty(fake.Hits);  // no key, no request
        s.Config.ModIoApiKey = "wrong";
        Assert.Equal(0, await s.RefreshModIoAsync(new HttpClient(fake), false));
        Assert.Equal("Bus Loops", s.Catalog["bus_loops_1"].DisplayName);  // still the readable fallback
    }

    [Fact]
    public void AutoAddsModIoFolderOnFirstRunOnly()
    {
        using var t = new TempDir();
        var dir = ModIoTree(t, (7, "bus_loops_1"));
        var s = new AppState(Path.Combine(t.Root, "home"));
        Assert.Equal(dir, s.AutoAddModIo([Path.Combine(t.Root, "nope"), Path.Combine(t.Root, "mod.io")]));
        Assert.Equal(ModSource.Subscribed, s.Catalog["bus_loops_1"].Source);
        Assert.Null(s.AutoAddModIo([Path.Combine(t.Root, "mod.io")]));  // already has folders
    }
}
