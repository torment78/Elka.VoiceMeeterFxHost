using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Elka.VoiceMeeterFxHost.App;
using static Elka.VoiceMeeterFxHost.App.ApplicationUpdates;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS: {name}");
    passed++;
}
ReleaseVersion Version(string text)
{
    if (!ReleaseVersion.TryParse(text, out var version)) throw new Exception(text);
    return version;
}
async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (InvalidDataException) { Check(true, name); return; }
    throw new Exception($"Expected rejection: {name}");
}
GitHubRelease Release(string version, bool beta = false) => new()
{
    Tag = $"v{version}", Prerelease = beta,
    Assets = [new GitHubAsset
    {
        Name = $"ElkaVoiceMeeterFxHostSetup-v{version}.exe", State = "uploaded", Size = 1234,
        Url = $"https://github.com/{Repository}/releases/download/v{version}/ElkaVoiceMeeterFxHostSetup-v{version}.exe"
    }]
};

Check(Version("0.8.1.1.10").CompareTo(Version("0.8.1.1.2")) > 0, "five-part versions compare numerically");
Check(Version("0.8.1.2").CompareTo(Version("0.8.1.1.0")) > 0, "new development version follows installed local test version");
var developmentUpdate = SelectUpdates([Release("0.8.1.2", true), Release("0.8.1.0")], Version("0.8.1.1.0"));
Check(developmentUpdate.Stable is null && developmentUpdate.Beta?.Version.ToString() == "0.8.1.2",
    "installed local test build is offered 0.8.1.2 on beta channel only");
Check(Version("v0.8.1.1.0+hash").CompareTo(Version("0.8.1.1")) == 0, "metadata and trailing zero do not produce false updates");
Check(Version("0.9.0.0").CompareTo(Version("0.8.1.1.100")) > 0, "new stable line follows long development versions");
Check(!ReleaseVersion.TryParse("0.8.-1.1", out _) && !ReleaseVersion.TryParse("latest", out _), "invalid version tags rejected");
var draft = Release("9.0.0"); draft.Draft = true;
var missing = Release("8.0.0"); missing.Assets!.Clear();
var malicious = Release("7.0.0"); malicious.Assets![0].Url = "https://example.com/installer.exe";
var wrongRepo = Release("6.0.0"); wrongRepo.Assets![0].Url = wrongRepo.Assets[0].Url!.Replace(Repository, "someone/other");
var unpublished = Release("5.0.0"); unpublished.Assets![0].State = "new";
var result = SelectUpdates([Release("0.8.1.1.10", true), Release("0.8.1.0"), draft, missing, malicious,
    Release("0.8.1.1.2", true), wrongRepo, unpublished], Version("0.8.0.0"));
Check(result.Stable?.Version.ToString() == "0.8.1.0", "stable channel ignores drafts, beta, missing/unsafe/unpublished installers");
Check(result.Beta?.Version.ToString() == "0.8.1.1.10", "beta channel chooses highest version, independent of API ordering");
result = SelectUpdates([Release("0.8.1.1.0", true), Release("0.8.1.0")], Version("0.8.1.1.0"));
Check(result.Stable is null && result.Beta is null, "installed and older releases never offered as updates");

using var pagedClient = new HttpClient(new FakeHandler(request =>
{
    var first = request.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal);
    var page = first ? Enumerable.Range(0, 100).Select(_ => Release("0.7.0")).ToArray() : [Release("0.9.0")];
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(page) };
}));
result = await new ApplicationUpdates(pagedClient).CheckAsync("0.8.0", CancellationToken.None);
Check(result.Stable?.Version.ToString() == "0.9.0", "checks later API pages");

var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "update-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var payload = "an installer-shaped test download"u8.ToArray();
var update = new ApplicationUpdate(UpdateChannel.Beta, Version("0.9.0"), "installer.exe",
    new Uri($"https://github.com/{Repository}/releases/download/v0.9.0/installer.exe"), payload.Length,
    "sha256:" + Convert.ToHexString(SHA256.HashData(payload)));
using var downloadClient = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new ByteArrayContent(payload)
}));
var downloader = new ApplicationUpdates(downloadClient);
var downloaded = await downloader.DownloadAsync(update, root, null, CancellationToken.None);
Check(File.ReadAllBytes(downloaded).SequenceEqual(payload), "complete beta download matches checksum");
await Reject(() => downloader.DownloadAsync(update with { Digest = "sha256:" + new string('0', 64) }, root, null, CancellationToken.None), "tampered download rejected");
await Reject(() => downloader.DownloadAsync(update with { Size = payload.Length + 1 }, root, null, CancellationToken.None), "truncated download rejected");
await Reject(() => downloader.DownloadAsync(update with { Size = payload.Length - 1 }, root, null, CancellationToken.None), "oversized download rejected");
await Reject(() => downloader.DownloadAsync(update with { Digest = null }, root, null, CancellationToken.None), "unsigned beta without checksum rejected");
await Reject(() => downloader.DownloadAsync(update with { Channel = UpdateChannel.Stable }, root, null, CancellationToken.None), "stable installer requires valid Authenticode signature even with valid checksum");
using var canceled = new CancellationTokenSource();
canceled.Cancel();
try
{
    await downloader.DownloadAsync(update, root, null, canceled.Token);
    throw new Exception("Expected cancellation");
}
catch (OperationCanceledException) { Check(true, "download respects cancellation"); }
Check(Directory.GetFiles(root, "*.download", SearchOption.AllDirectories).Length == 0, "failed and canceled downloads leave no partial files");
Check(Directory.GetFiles(root, "*.exe", SearchOption.AllDirectories).Length == 1, "only verified successful download becomes an executable");

if (args.Contains("--live"))
{
    var live = new ApplicationUpdates();
    var available = await live.CheckAsync("0.8.0.0", CancellationToken.None);
    Check(available.Stable is not null && available.Beta is not null, "live GitHub stable and beta installers found");
    foreach (var candidate in new[] { available.Stable!, available.Beta! })
    {
        var path = await live.DownloadAsync(candidate, root, null, CancellationToken.None);
        Check(File.Exists(path), $"live {candidate.Channel} {candidate.Version} downloaded and verified (not launched)");
    }
}
Console.WriteLine($"{passed} checks passed. Test downloads: {root}");

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(handler(request));
    }
}
