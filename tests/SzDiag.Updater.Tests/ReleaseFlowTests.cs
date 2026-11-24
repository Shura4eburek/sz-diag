using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SzDiag.Contracts;
using SzDiag.Contracts.Signpost;
using SzDiag.Updater;

namespace SzDiag.Updater.Tests;

public class ReleaseFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szrel-" + Guid.NewGuid().ToString("N"));
    private readonly Net _net = new();

    public ReleaseFlowTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Net : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Files.TryGetValue(request.RequestUri!.ToString(), out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static byte[] Zip(string version)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(z.CreateEntry("version.txt").Open())) w.Write(version);
            using (var w = new StreamWriter(z.CreateEntry(BuildInfo.FileName).Open()))
                w.Write(JsonSerializer.Serialize(new BuildInfo(version, DateTimeOffset.UtcNow), SignpostJson.Options));
        }
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static ReleaseLocation Location(string version, string sha, DateTimeOffset built, params string[] urls)
        => new(new ReleaseRecord(version, built, sha, 1, urls, 1), "signpost", urls, []);

    private SignedReleaseUpdater Updater() => new(new HttpClient(_net), _dir);

    [Fact]
    public async Task Good_package_is_applied()
    {
        var zip = Zip("v2");
        _net.Files["https://a/p.zip"] = zip;

        var r = await Updater().ApplyAsync(Location("v2", Sha(zip), DateTimeOffset.UtcNow, "https://a/p.zip"), "v1");

        Assert.Equal(ReleaseOutcome.Applied, r.Outcome);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(_dir, "version.txt")));
    }

    [Fact]
    public async Task Package_not_matching_signed_sha_is_not_applied_and_next_url_is_tried()
    {
        var good = Zip("v2");
        _net.Files["https://evil/p.zip"] = Zip("evil");
        _net.Files["https://mirror/p.zip"] = good;

        var r = await Updater().ApplyAsync(
            Location("v2", Sha(good), DateTimeOffset.UtcNow, "https://evil/p.zip", "https://mirror/p.zip"), "v1");

        Assert.Equal(ReleaseOutcome.Applied, r.Outcome);
        Assert.Contains(r.Notes, n => n.Contains("evil") && n.Contains("не совпал"));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(_dir, "version.txt")));
    }

    [Fact]
    public async Task Only_bad_packages_means_download_failed_and_nothing_written()
    {
        _net.Files["https://evil/p.zip"] = Zip("evil");
        var r = await Updater().ApplyAsync(Location("v2", new string('a', 64), DateTimeOffset.UtcNow, "https://evil/p.zip"), "v1");

        Assert.Equal(ReleaseOutcome.DownloadFailed, r.Outcome);
        Assert.False(File.Exists(Path.Combine(_dir, "version.txt")));
    }

    [Fact]
    public async Task Older_release_than_installed_is_refused()
    {
        File.WriteAllText(Path.Combine(_dir, BuildInfo.FileName),
            JsonSerializer.Serialize(new BuildInfo("v5", new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero)), SignpostJson.Options));
        var zip = Zip("v4");
        _net.Files["https://a/p.zip"] = zip;

        var r = await Updater().ApplyAsync(
            Location("v4", Sha(zip), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "https://a/p.zip"), "v5");

        Assert.Equal(ReleaseOutcome.Older, r.Outcome);
    }

    [Fact]
    public async Task Same_version_is_up_to_date_without_download()
    {
        var r = await Updater().ApplyAsync(Location("v1", new string('a', 64), DateTimeOffset.UtcNow, "https://nowhere/p.zip"), "v1");
        Assert.Equal(ReleaseOutcome.UpToDate, r.Outcome);
    }

    [Fact]
    public void Sign_key_comes_from_config_or_file_next_to_exe()
    {
        Assert.Empty(new UpdaterOptions().ResolveSignKeys(_dir));
        File.WriteAllText(Path.Combine(_dir, "fleet-sign.pub"), "PEM-FILE");
        Assert.Equal(["PEM-FILE"], new UpdaterOptions().ResolveSignKeys(_dir));
        Assert.Equal(["a\nb"], new UpdaterOptions { SignPublicKey = "a\\nb" }.ResolveSignKeys(_dir));
    }
}
