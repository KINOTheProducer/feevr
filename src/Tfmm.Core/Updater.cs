using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tfmm.Core;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string? ExeUrl, string? ShaUrl);

/// <summary>Self-update from GitHub Releases of the public repo. Release tags are "vX.Y.Z" with assets
/// Junction.exe and Junction.exe.sha256 (built by .github/workflows/release.yml). Releases also carry Feevr.exe
/// for installs from before the rename, whose updater only knows that name.</summary>
public static class Updater
{
    public const string Repo = "KINOTheProducer/feevr";
    public const string ExeAsset = "Junction.exe";
    public const string ShaAsset = "Junction.exe.sha256";
    /// <summary>File names (no extension) an installed copy can have. Feevr = installed before the rename and updated in place.</summary>
    public static readonly string[] ExeNames = ["Junction", "Feevr"];

    /// <summary>App version from the assembly (set by -p:Version in the release build; 0.0.0 for local builds).</summary>
    public static Version Current(Assembly asm)
    {
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return TryParse(info.Split('+')[0], out var v) ? v : new Version(0, 0, 0);
    }

    public static bool TryParse(string? tag, out Version v)
    {
        v = new Version(0, 0, 0);
        var s = (tag ?? "").Trim().TrimStart('v', 'V').Split('-')[0];
        if (!Version.TryParse(s, out var parsed)) return false;
        v = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        return true;
    }

    /// <summary>Parses GitHub's "latest release" JSON. Null when it is a draft/prerelease or the tag is not a version.</summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name");
        if (!TryParse(tag, out var ver)) return null;
        string? exe = null, sha = null;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = Str(a, "name");
                var url = Str(a, "browser_download_url");
                if (url is null || !url.StartsWith("https://", StringComparison.Ordinal)) continue;
                if (string.Equals(name, ExeAsset, StringComparison.OrdinalIgnoreCase)) exe = url;
                else if (string.Equals(name, ShaAsset, StringComparison.OrdinalIgnoreCase)) sha = url;
            }
        return new UpdateInfo(ver, tag!, Str(r, "body") ?? "", Str(r, "html_url") ?? $"https://github.com/{Repo}/releases", exe, sha);
    }

    /// <summary>Newer release than <paramref name="current"/>, or null (also null on any network/parse error).</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, Version current, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("Junction-updater");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var info = ParseRelease(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return info is not null && info.Version > current ? info : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    /// <summary>Downloads the new exe next to the running one and checks it against the release's SHA-256.
    /// Returns the downloaded path. Refuses a release without a checksum.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo u, string dir, CancellationToken ct = default)
    {
        if (u.ExeUrl is null) throw new InvalidOperationException("This release has no Junction.exe to download.");
        if (u.ShaUrl is null) throw new InvalidOperationException("This release has no checksum, so it was not installed.");
        var expected = ParseSha(await GetString(http, u.ShaUrl, ct).ConfigureAwait(false));
        var path = Path.Combine(dir, "Junction.new.exe");
        await using (var src = await GetStream(http, u.ExeUrl, ct).ConfigureAwait(false))
        await using (var dst = File.Create(path))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        string actual;
        await using (var f = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(path);
            throw new InvalidOperationException("Download did not match its checksum. Nothing was changed.");
        }
        return path;
    }

    /// <summary>"hash  Junction.exe" (sha256sum format) or a bare hash.</summary>
    public static string ParseSha(string text)
    {
        var hash = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidOperationException("Checksum file is not a SHA-256 hash.");
        return hash.ToLowerInvariant();
    }

    /// <summary>Windows lets a running exe be renamed but not overwritten: move it aside, move the new one in.
    /// Rolls back if the second move fails.</summary>
    public static void Swap(string currentExe, string newExe)
    {
        var old = OldPath(currentExe);
        if (File.Exists(old)) File.Delete(old);
        File.Move(currentExe, old);
        try { File.Move(newExe, currentExe); }
        catch
        {
            File.Move(old, currentExe);
            throw;
        }
    }

    public static string OldPath(string exe) => Path.ChangeExtension(exe, ".old.exe");

    /// <summary>Removes the previous version left by <see cref="Swap"/>. Safe to call on every start.</summary>
    public static void CleanupOld(string exe)
    {
        try { if (File.Exists(OldPath(exe))) File.Delete(OldPath(exe)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static async Task<string> GetString(HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Junction-updater");
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    static async Task<Stream> GetStream(HttpClient http, string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Junction-updater");
        var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    static string? Str(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
}
