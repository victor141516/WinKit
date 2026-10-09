using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinKit;

internal sealed record ManagedRelease(string Version, ReleaseAsset Asset, string Sha256);
internal sealed record ReleaseContract(int Schema, bool Background, bool SingleInstance, bool SelfContained, ContractPackage[] Packages);
internal sealed record ContractPackage(string Architecture, string Asset, string Executable, string Sha256);

internal interface IReleaseSource
{
    Task<ManagedRelease> ResolveAsync(AppSpec app, CancellationToken cancellationToken);
    Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<int> progress, CancellationToken cancellationToken);
}

internal sealed class GitHubReleases : IReleaseSource, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public GitHubReleases()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinKit/0.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<ManagedRelease> ResolveAsync(AppSpec app, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string json = await _http.GetStringAsync(app.LatestReleaseUrl, timeout.Token);
        var release = JsonSerializer.Deserialize<Release>(json, JsonOptions) ?? throw new InvalidDataException("GitHub returned an empty release.");
        if (release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.Tag))
            throw new InvalidDataException("A stable release is required.");
        var contractAsset = release.Assets.SingleOrDefault(asset => asset.Name == "winkit.json")
            ?? throw new InvalidDataException("The latest release does not yet support managed startup. A compatible release needs to be published.");
        ValidateAssetUrl(app, contractAsset);
        if (contractAsset.Size > 64 * 1024) throw new InvalidDataException("The release manifest is too large.");
        string contractJson = await _http.GetStringAsync(contractAsset.Url, timeout.Token);
        var contract = JsonSerializer.Deserialize<ReleaseContract>(contractJson, JsonOptions)
            ?? throw new InvalidDataException("The release manifest is empty.");
        return SelectPackage(app, release, contract, "x64");
    }

    internal static ManagedRelease SelectPackage(AppSpec app, Release release, ReleaseContract contract, string architecture)
    {
        if (contract.Schema != 1 || !contract.Background || !contract.SingleInstance || !contract.SelfContained)
            throw new InvalidDataException("The release must support background startup, one instance, and self-contained deployment.");
        var package = contract.Packages.SingleOrDefault(p => p.Architecture == architecture)
            ?? throw new InvalidDataException($"The release does not contain a {architecture} package.");
        if (!string.Equals(package.Executable, app.Executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The release executable does not match this utility.");
        if (package.Sha256.Length != 64 || package.Sha256.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("A valid SHA-256 checksum is required.");
        var asset = release.Assets.SingleOrDefault(a => a.Name == package.Asset)
            ?? throw new InvalidDataException("The package named in the manifest is missing.");
        ValidateAssetUrl(app, asset);
        if (!asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || asset.Size is <= 0 or > 512 * 1024 * 1024)
            throw new InvalidDataException("The release must contain a portable ZIP package smaller than 512 MB.");
        return new ManagedRelease(release.Tag, asset, package.Sha256.ToLowerInvariant());
    }

    private static void ValidateAssetUrl(AppSpec app, ReleaseAsset asset)
    {
        string prefix = app.RepositoryUrl + "/releases/download/";
        if (!asset.Url.StartsWith(prefix, StringComparison.Ordinal) || !Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidDataException("The download must belong to this GitHub repository.");
    }

    public async Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<int> progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != asset.Size)
            throw new InvalidDataException("The download size does not match GitHub.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        byte[] buffer = new byte[65536];
        long total = 0;
        int previous = -1;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > asset.Size) throw new InvalidDataException("The download exceeds its expected size.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            int percentage = (int)(total * 100 / asset.Size);
            if (percentage != previous) { progress.Report(percentage); previous = percentage; }
        }
        if (total != asset.Size) throw new InvalidDataException("The download was interrupted.");
    }
    public void Dispose() => _http.Dispose();
}

internal static class PackageExtractor
{
    public static async Task<string> ExtractAsync(string archivePath, string destination, string expectedExecutable, string expectedHash, CancellationToken token)
    {
        await using (var input = File.OpenRead(archivePath))
        {
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded package failed its SHA-256 check.");
        }
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!StateStore.IsInside(target, destination) || entry.FullName.Contains(':') || entry.FullName.Contains('\\') || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("The archive contains an unsafe path or symbolic link.");
            expanded += entry.Length;
            if (expanded > 1024L * 1024 * 1024) throw new InvalidDataException("The expanded package exceeds 1 GB.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await source.CopyToAsync(output, token);
        }
        string executable = Path.Combine(destination, expectedExecutable);
        if (!File.Exists(executable)) throw new InvalidDataException($"The package does not contain {expectedExecutable} at its root.");
        using var pe = File.OpenRead(executable);
        if (pe.ReadByte() != 'M' || pe.ReadByte() != 'Z') throw new InvalidDataException("The package does not contain a Windows executable.");
        return executable;
    }
}
