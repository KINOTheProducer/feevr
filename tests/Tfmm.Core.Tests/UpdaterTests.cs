using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tfmm.Core;
using Xunit;

namespace Tfmm.Core.Tests;

public class UpdaterTests
{
    /// <summary>Serves fixed bodies by URL, like a tiny fake GitHub.</summary>
    sealed class FakeHttp(Dictionary<string, byte[]> routes) : HttpMessageHandler
    {
        public List<string> Hits { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Hits.Add(url);
            return Task.FromResult(routes.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    const string Latest = "https://api.github.com/repos/KINOTheProducer/feevr/releases/latest";
    const string ExeUrl = "https://github.com/KINOTheProducer/feevr/releases/download/v1.2.0/Junction.exe";
    const string ShaUrl = "https://github.com/KINOTheProducer/feevr/releases/download/v1.2.0/Junction.exe.sha256";

    static string ReleaseJson(string tag = "v1.2.0", bool draft = false, bool pre = false, bool withSha = true) => $$"""
        { "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(pre ? "true" : "false")}},
          "html_url": "https://github.com/KINOTheProducer/feevr/releases/tag/{{tag}}", "body": "Faster charts.",
          "assets": [ { "name": "Junction.exe", "browser_download_url": "{{ExeUrl}}" }
                      {{(withSha ? $", {{ \"name\": \"Junction.exe.sha256\", \"browser_download_url\": \"{ShaUrl}\" }}" : "")}} ] }
        """;

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("v0.1.0-beta", 0, 1, 0)]
    public void ParsesVersions(string tag, int a, int b, int c)
    {
        Assert.True(Updater.TryParse(tag, out var v));
        Assert.Equal(new Version(a, b, c), v);
    }

    [Fact]
    public void RejectsJunkTags() => Assert.False(Updater.TryParse("junction-latest", out _));

    [Fact]
    public void ParsesRelease()
    {
        var u = Updater.ParseRelease(ReleaseJson())!;
        Assert.Equal(new Version(1, 2, 0), u.Version);
        Assert.Equal(ExeUrl, u.ExeUrl);
        Assert.Equal(ShaUrl, u.ShaUrl);
        Assert.Equal("Faster charts.", u.Notes);
        Assert.Null(Updater.ParseRelease(ReleaseJson(draft: true)));
        Assert.Null(Updater.ParseRelease(ReleaseJson(pre: true)));
        Assert.Null(Updater.ParseRelease(ReleaseJson(tag: "nightly")));
    }

    [Fact]
    public async Task OnlyOffersNewer()
    {
        var http = new HttpClient(new FakeHttp(new() { [Latest] = Encoding.UTF8.GetBytes(ReleaseJson()) }));
        Assert.NotNull(await Updater.CheckAsync(http, new Version(1, 1, 9)));
        Assert.Null(await Updater.CheckAsync(http, new Version(1, 2, 0)));
        Assert.Null(await Updater.CheckAsync(http, new Version(2, 0, 0)));
    }

    [Fact]
    public async Task NetworkTroubleIsJustNoUpdate()
    {
        var http = new HttpClient(new FakeHttp([]));
        Assert.Null(await Updater.CheckAsync(http, new Version(0, 0, 1)));
        var bad = new HttpClient(new FakeHttp(new() { [Latest] = "not json"u8.ToArray() }));
        Assert.Null(await Updater.CheckAsync(bad, new Version(0, 0, 1)));
    }

    [Fact]
    public async Task DownloadsAndVerifies()
    {
        using var t = new TempDir();
        var exe = "MZ fake exe bytes"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant() + "  Junction.exe\n";
        var http = new HttpClient(new FakeHttp(new() { [ExeUrl] = exe, [ShaUrl] = Encoding.UTF8.GetBytes(sha) }));
        var path = await Updater.DownloadAsync(http, Updater.ParseRelease(ReleaseJson())!, t.Root);
        Assert.Equal(exe, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task RejectsTamperedOrUnsignedDownloads()
    {
        using var t = new TempDir();
        var wrong = new string('a', 64) + "  Junction.exe";
        var http = new HttpClient(new FakeHttp(new() { [ExeUrl] = "MZ evil"u8.ToArray(), [ShaUrl] = Encoding.UTF8.GetBytes(wrong) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updater.DownloadAsync(http, Updater.ParseRelease(ReleaseJson())!, t.Root));
        Assert.False(File.Exists(Path.Combine(t.Root, "Junction.new.exe")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updater.DownloadAsync(http, Updater.ParseRelease(ReleaseJson(withSha: false))!, t.Root));
    }

    [Fact]
    public void SwapsAndCleansUp()
    {
        using var t = new TempDir();
        var cur = Path.Combine(t.Root, "Junction.exe");
        var neu = Path.Combine(t.Root, "Junction.new.exe");
        File.WriteAllText(cur, "old");
        File.WriteAllText(neu, "new");
        Updater.Swap(cur, neu);
        Assert.Equal("new", File.ReadAllText(cur));
        Assert.Equal("old", File.ReadAllText(Updater.OldPath(cur)));
        Updater.CleanupOld(cur);
        Assert.False(File.Exists(Updater.OldPath(cur)));
    }

    [Fact]
    public void SwapRollsBackWhenNewFileIsMissing()
    {
        using var t = new TempDir();
        var cur = Path.Combine(t.Root, "Junction.exe");
        File.WriteAllText(cur, "old");
        Assert.ThrowsAny<IOException>(() => Updater.Swap(cur, Path.Combine(t.Root, "missing.exe")));
        Assert.Equal("old", File.ReadAllText(cur));
    }
}
