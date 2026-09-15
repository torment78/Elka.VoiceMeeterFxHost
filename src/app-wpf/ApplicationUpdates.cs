using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Elka.VoiceMeeterFxHost.App;

internal enum UpdateChannel { Stable, Beta }

internal sealed record ApplicationUpdate(
    UpdateChannel Channel, ReleaseVersion Version, string AssetName, Uri DownloadUri, long Size, string? Digest);

internal sealed record AvailableUpdates(ApplicationUpdate? Stable, ApplicationUpdate? Beta);

internal sealed class ApplicationUpdates
{
    internal const string Repository = "torment78/Elka.VoiceMeeterFxHost";
    private const long MaximumInstallerSize = 256 * 1024 * 1024;
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;

    public ApplicationUpdates(HttpClient? client = null) => _client = client ?? SharedClient;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ElkaVoiceMeeterFxHost-Updater/1.0");
        return client;
    }

    public async Task<AvailableUpdates> CheckAsync(string currentVersion, CancellationToken cancellationToken)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out var current))
            throw new InvalidOperationException("The running application version could not be read.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var releases = new List<GitHubRelease>();
        for (var page = 1; page <= 10; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var batch = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(
                cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("GitHub returned an empty release response.");
            releases.AddRange(batch);
            if (batch.Count < 100)
                return SelectUpdates(releases, current);
        }
        throw new InvalidDataException("GitHub returned too many release pages to finish the update check.");
    }

    internal static AvailableUpdates SelectUpdates(IEnumerable<GitHubRelease> releases, ReleaseVersion current)
    {
        ApplicationUpdate? stable = null;
        ApplicationUpdate? beta = null;
        foreach (var release in releases)
        {
            if (release.Draft || !ReleaseVersion.TryParse(release.Tag, out var version) || version.CompareTo(current) <= 0)
                continue;

            var expectedName = $"ElkaVoiceMeeterFxHostSetup-v{version}.exe";
            var asset = release.Assets?.FirstOrDefault(asset =>
                string.Equals(asset.Name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                asset.State == "uploaded" && asset.Size > 0 && asset.Size <= MaximumInstallerSize);
            if (asset is null || !Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) ||
                !IsInstallerUri(uri, release.Tag!, expectedName))
                continue;

            var update = new ApplicationUpdate(release.Prerelease ? UpdateChannel.Beta : UpdateChannel.Stable,
                version, expectedName, uri, asset.Size, asset.Digest);
            if (release.Prerelease)
            {
                if (beta is null || version.CompareTo(beta.Version) > 0)
                    beta = update;
            }
            else if (stable is null || version.CompareTo(stable.Version) > 0)
            {
                stable = update;
            }
        }
        return new(stable, beta);
    }

    private static bool IsInstallerUri(Uri uri, string tag, string name) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath == $"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}";

    public async Task<string> DownloadAsync(ApplicationUpdate update, string downloadRoot,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        // Only a completed, verified file is given an executable filename.
        var directory = Path.Combine(downloadRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partialPath = Path.Combine(directory, "installer.download");
        var installerPath = Path.Combine(directory, update.AssetName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            using var response = await _client.GetAsync(update.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                long received = 0;
                var lastPercent = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > update.Size || received > MaximumInstallerSize)
                        throw new InvalidDataException("The installer download is larger than the published asset.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    var percent = (int)(received * 100 / update.Size);
                    if (percent != lastPercent)
                    {
                        progress?.Report(percent);
                        lastPercent = percent;
                    }
                }
                if (received != update.Size)
                    throw new InvalidDataException("The installer download is incomplete. Please try again.");
            }

            await VerifyDownloadAsync(partialPath, update, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partialPath, installerPath);
            return installerPath;
        }
        catch
        {
            File.Delete(partialPath);
            if (!File.Exists(installerPath))
                Directory.Delete(directory);
            throw;
        }
    }

    internal static async Task VerifyDownloadAsync(string path, ApplicationUpdate update, CancellationToken cancellationToken)
    {
        if (update.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            await using var file = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actual, digest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer checksum does not match GitHub. Installation was stopped.");
        }
        else if (update.Channel == UpdateChannel.Beta)
        {
            throw new InvalidDataException("This beta installer has no SHA-256 checksum on GitHub.");
        }

        if (update.Channel == UpdateChannel.Stable)
            await Task.Run(() => InstallerSignature.Verify(path), cancellationToken).ConfigureAwait(false);
    }

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? Tag { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? Url { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
