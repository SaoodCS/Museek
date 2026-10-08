using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Museek.Services;

public interface IAppUpdateService
{
    Version CurrentVersion { get; }
    Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken);
    Task<DownloadedInstaller> DownloadAsync(UpdateRelease release, IProgress<double>? progress, CancellationToken cancellationToken);
    Task ValidateInstallerAsync(DownloadedInstaller installer, CancellationToken cancellationToken);
}

public sealed record UpdateRelease
{
    public Version Version { get; }
    public string Tag { get; }
    public string InstallerName { get; }
    public Uri ReleasePage { get; }
    internal long InstallerSize { get; }
    internal long ChecksumSize { get; }
    internal string? InstallerDigest { get; }
    internal string? ChecksumDigest { get; }
    internal Guid OwnerId { get; }
    internal UpdateRelease(Version version, string tag, string installerName, Uri releasePage, long installerSize, long checksumSize, string? installerDigest, string? checksumDigest, Guid ownerId)
        => (Version, Tag, InstallerName, ReleasePage, InstallerSize, ChecksumSize, InstallerDigest, ChecksumDigest, OwnerId) = (version, tag, installerName, releasePage, installerSize, checksumSize, installerDigest, checksumDigest, ownerId);
}

public sealed record DownloadedInstaller : IDisposable
{
    private readonly string? _downloadRoot;
    private int _disposed;
    public UpdateRelease Release { get; }
    public string InstallerPath { get; }
    internal string Sha256 { get; }
    internal Guid OwnerId { get; }
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal DownloadedInstaller(UpdateRelease release, string installerPath, string sha256, Guid ownerId, string? downloadRoot = null)
        => (Release, InstallerPath, Sha256, OwnerId, _downloadRoot) = (release, installerPath, sha256, ownerId, downloadRoot);

    /// <summary>Remove an abandoned download without traversing links or deleting unrelated files.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _downloadRoot is not null)
            AppUpdateService.CleanupDownload(_downloadRoot, InstallerPath);
    }
}

/// <summary>Checks the fixed public release source and downloads a verified, per-user installer.</summary>
public sealed class AppUpdateService : IAppUpdateService, IDisposable
{
    private const int MetadataLimit = 1024 * 1024;
    private const int ChecksumLimit = 64 * 1024;
    private const long InstallerLimit = 512L * 1024 * 1024;
    private const int BufferSize = 64 * 1024;
    private static readonly Uri LatestRelease = new("https://api.github.com/repos/SaoodCS/Museek/releases/latest");
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly Regex TagPattern = new(@"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _downloadRoot;
    private readonly Guid _ownerId = Guid.NewGuid();
    private bool _disposed;

    public Version CurrentVersion { get; }

    public AppUpdateService(Version? currentVersion = null, HttpClient? httpClient = null, string? downloadDirectory = null)
    {
        var version = currentVersion ?? typeof(AppUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        CurrentVersion = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        _downloadRoot = Path.GetFullPath(downloadDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Museek", "Updates"));
        if (_downloadRoot.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("The update folder must be on a local drive.");
        _ownsClient = httpClient is null;
        _client = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            using var response = await SendAsync(LatestRelease, "application/vnd.github+json", false, timeout.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(response, MetadataLimit, null, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("The release is not a stable public release.");
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseTag(tag);
            var releasePage = new Uri($"https://github.com/SaoodCS/Museek/releases/tag/{tag}");
            if (root.GetProperty("html_url").GetString() != releasePage.AbsoluteUri)
                throw new InvalidDataException("The release page does not belong to Museek.");
            var installerName = $"Museek-{version}-setup.exe";
            var installer = ReadAsset(root, tag, installerName, InstallerLimit);
            var checksums = ReadAsset(root, tag, "SHA256SUMS.txt", ChecksumLimit);
            if (version <= CurrentVersion) return null;
            return new UpdateRelease(version, tag, installerName, releasePage, installer.Size, checksums.Size, installer.Digest, checksums.Digest, _ownerId);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The release response is invalid.", error);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The update check timed out.", error);
        }
    }

    public async Task<DownloadedInstaller> DownloadAsync(UpdateRelease release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(release);
        if (release.OwnerId != _ownerId) throw new InvalidDataException("Check this release before downloading it.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        string? ownedPath = null;
        var completed = false;
        try
        {
            string expectedHash;
            using (var checksumTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                checksumTimeout.CancelAfter(CheckTimeout);
                using var sumsResponse = await SendAsync(AssetUri(release.Tag, "SHA256SUMS.txt"), "text/plain", true, checksumTimeout.Token).ConfigureAwait(false);
                var sums = await ReadBoundedAsync(sumsResponse, ChecksumLimit, release.ChecksumSize, checksumTimeout.Token).ConfigureAwait(false);
                VerifyDigest(sums, release.ChecksumDigest);
                expectedHash = ReadChecksum(sums, release.InstallerName);
                if (release.InstallerDigest is not null && !SameHash(expectedHash, release.InstallerDigest))
                    throw new InvalidDataException("The installer checksums disagree.");
            }

            timeout.Token.ThrowIfCancellationRequested();
            EnsureRegularPath(_downloadRoot, true);
            Directory.CreateDirectory(_downloadRoot);
            EnsureRegularPath(_downloadRoot, true);
            var directory = Path.Combine(_downloadRoot, Guid.NewGuid().ToString("N"));
            ownedPath = Path.Combine(directory, release.InstallerName + ".part");
            Directory.CreateDirectory(directory);
            EnsureRegularPath(directory, true);
            progress?.Report(0);
            string actualHash;
            using (var response = await SendAsync(AssetUri(release.Tag, release.InstallerName), "application/octet-stream", true, timeout.Token).ConfigureAwait(false))
            {
                ValidateLength(response, InstallerLimit, release.InstallerSize);
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var destination = new FileStream(ownedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                long total = 0;
                var lastProgress = Stopwatch.GetTimestamp();
                try
                {
                    int count;
                    while ((count = await source.ReadAsync(buffer.AsMemory(0, BufferSize), timeout.Token).ConfigureAwait(false)) != 0)
                    {
                        total += count;
                        if (total > release.InstallerSize || total > InstallerLimit)
                            throw new InvalidDataException("The installer exceeds its advertised size.");
                        hash.AppendData(buffer, 0, count);
                        await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                        if (Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 100)
                        {
                            progress?.Report((double)total / release.InstallerSize);
                            lastProgress = Stopwatch.GetTimestamp();
                        }
                    }
                    if (total != release.InstallerSize) throw new InvalidDataException("The installer download is incomplete.");
                    actualHash = Convert.ToHexString(hash.GetHashAndReset());
                    if (!SameHash(expectedHash, actualHash)) throw new InvalidDataException("The installer checksum does not match.");
                    await destination.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            EnsureRegularPath(ownedPath, false);
            ValidateVersion(ownedPath, release.Version);
            timeout.Token.ThrowIfCancellationRequested();
            var finalPath = Path.Combine(directory, release.InstallerName);
            File.Move(ownedPath, finalPath);
            ownedPath = finalPath;
            EnsureRegularPath(finalPath, false);
            var installer = new DownloadedInstaller(release, finalPath, actualHash, _ownerId, _downloadRoot);
            await ValidateInstallerAsync(installer, timeout.Token).ConfigureAwait(false);
            progress?.Report(1);
            completed = true;
            return installer;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The update download timed out.", error);
        }
        finally
        {
            // A completed return transfers cleanup ownership to DownloadedInstaller.
            if (ownedPath is not null && !completed) CleanupDownload(_downloadRoot, ownedPath);
        }
    }

    public async Task ValidateInstallerAsync(DownloadedInstaller installer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(installer);
        if (installer.OwnerId != _ownerId || installer.Release.OwnerId != _ownerId || installer.IsDisposed)
            throw new InvalidDataException("The installer is not owned by this update check.");
        EnsureOwnedPath(_downloadRoot, installer.InstallerPath);
        if (Path.GetFileName(installer.InstallerPath) != installer.Release.InstallerName)
            throw new InvalidDataException("The installer path is invalid.");
        EnsureRegularPath(installer.InstallerPath, false);
        await using var file = new FileStream(installer.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != installer.Release.InstallerSize || file.Length > InstallerLimit)
            throw new InvalidDataException("The installer size has changed.");
        var digest = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        if (!SameHash(installer.Sha256, Convert.ToHexString(digest)))
            throw new InvalidDataException("The installer has changed since it was downloaded.");
        cancellationToken.ThrowIfCancellationRequested();
        ValidateVersion(installer.InstallerPath, installer.Release.Version);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri initial, string mediaType, bool asset, CancellationToken cancellationToken)
    {
        var uri = initial;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            ValidateUri(uri, asset);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
            request.Headers.UserAgent.ParseAdd("Museek/" + CurrentVersion);
            if (!asset) request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            try
            {
                if (response.RequestMessage?.RequestUri is { } finalUri) ValidateUri(finalUri, asset);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (!asset || redirects == 5 || response.Headers.Location is null)
                        throw new InvalidDataException("The update server returned an unexpected redirect.");
                    uri = new Uri(uri, response.Headers.Location);
                    ValidateUri(uri, true);
                    response.Dispose();
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("The update server redirected too many times.");
    }

    private static void ValidateUri(Uri uri, bool asset)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            (asset ? uri.IdnHost is not ("github.com" or "release-assets.githubusercontent.com") : uri.AbsoluteUri != LatestRelease.AbsoluteUri))
            throw new InvalidDataException("The update server address is not trusted.");
    }

    private static Uri AssetUri(string tag, string name) => new($"https://github.com/SaoodCS/Museek/releases/download/{tag}/{name}");

    private static Version ParseTag(string tag)
    {
        var match = TagPattern.Match(tag);
        if (!match.Success) throw new InvalidDataException("The release version is invalid.");
        var numbers = new int[3];
        for (var index = 0; index < numbers.Length; index++)
            if (!int.TryParse(match.Groups[index + 1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]) || numbers[index] > ushort.MaxValue)
                throw new InvalidDataException("The release version is outside the supported range.");
        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    private static (long Size, string? Digest) ReadAsset(JsonElement root, string tag, string name, long limit)
    {
        JsonElement? found = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            if (found is not null) throw new InvalidDataException("The release contains duplicate update assets.");
            found = asset;
        }
        if (found is not { } item) throw new InvalidDataException("The release is missing an update asset.");
        var size = item.GetProperty("size").GetInt64();
        if (size <= 0 || size > limit || item.GetProperty("state").GetString() != "uploaded" || item.GetProperty("browser_download_url").GetString() != AssetUri(tag, name).AbsoluteUri)
            throw new InvalidDataException("The update asset is invalid.");
        string? digest = null;
        if (item.TryGetProperty("digest", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            var text = value.GetString() ?? "";
            if (!text.StartsWith("sha256:", StringComparison.Ordinal) || !IsHash(text.AsSpan(7)))
                throw new InvalidDataException("The update asset checksum is invalid.");
            digest = text[7..];
        }
        return (size, digest);
    }

    private static void ValidateLength(HttpResponseMessage response, long limit, long? expected)
    {
        if (response.Content.Headers.ContentLength is { } length && (length < 0 || length > limit || expected is not null && length != expected))
            throw new InvalidDataException("The update response size is invalid.");
        if (response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException("Encoded update responses are not supported.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, long? expected, CancellationToken cancellationToken)
    {
        ValidateLength(response, limit, expected);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (destination.Length + count > limit || expected is not null && destination.Length + count > expected)
                    throw new InvalidDataException("The update response exceeds its size limit.");
                destination.Write(buffer, 0, count);
            }
            if (expected is not null && destination.Length != expected || response.Content.Headers.ContentLength is { } length && destination.Length != length)
                throw new InvalidDataException("The update response is incomplete.");
            return destination.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string ReadChecksum(byte[] bytes, string installerName)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("The checksum file is invalid.", error); }
        string? found = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Length < 67 || !IsHash(line.AsSpan(0, 64)) || line[64] != ' ' || line[65] is not (' ' or '*'))
                throw new InvalidDataException("The checksum file contains an invalid entry.");
            var name = line[66..];
            if (name != installerName) continue;
            if (found is not null) throw new InvalidDataException("The checksum file contains duplicate installer entries.");
            found = line[..64];
        }
        return found ?? throw new InvalidDataException("The installer checksum is missing.");
    }

    private static bool IsHash(ReadOnlySpan<char> value)
    {
        if (value.Length != 64) return false;
        foreach (var character in value) if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }

    private static bool SameHash(string first, string second)
        => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static void VerifyDigest(byte[] bytes, string? expected)
    {
        if (expected is not null && !SameHash(expected, Convert.ToHexString(SHA256.HashData(bytes))))
            throw new InvalidDataException("The checksum file digest does not match.");
    }

    private static void ValidateVersion(string path, Version expected)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart != expected.Major || info.FileMinorPart != expected.Minor || info.FileBuildPart != expected.Build || info.FilePrivatePart != 0 ||
            info.ProductName != "Museek Setup" || !Version.TryParse(info.ProductVersion, out var product) || product.Major != expected.Major || product.Minor != expected.Minor || product.Build != expected.Build || product.Revision > 0)
            throw new InvalidDataException("The installer version does not match the release.");
    }

    private static void EnsureRegularPath(string path, bool directory)
    {
        var cursor = Path.GetFullPath(path);
        var first = true;
        while (cursor is not null)
        {
            try
            {
                var attributes = File.GetAttributes(cursor);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0 || isDirectory != (!first || directory))
                    throw new IOException("The update path contains a link or an unexpected file type.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            cursor = Path.GetDirectoryName(cursor);
            first = false;
        }
    }

    private static void EnsureOwnedPath(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var child = Path.GetDirectoryName(fullPath);
        if (child is null || !string.Equals(Path.GetDirectoryName(child), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(child), "N", out _))
            throw new IOException("The installer is outside its owned update folder.");
    }

    internal static void CleanupDownload(string root, string path)
    {
        try
        {
            EnsureOwnedPath(root, path);
            EnsureRegularPath(path, false);
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            EnsureRegularPath(directory, true);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _client.Dispose();
    }
}
