using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Museek.Services;

namespace Museek.UpdateChecks;

internal static class Program
{
    private const string Api = "https://api.github.com/repos/SaoodCS/Museek/releases/latest";
    private const string InstallerUrl = "https://github.com/SaoodCS/Museek/releases/download/v1.4.0/Museek-1.4.0-setup.exe";
    private const string SumsUrl = "https://github.com/SaoodCS/Museek/releases/download/v1.4.0/SHA256SUMS.txt";
    private static byte[] _fixture = [];
    private static string _hash = "";
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0) return await LiveAsync(args);
        _fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "installer-fixture.exe"));
        _hash = Convert.ToHexString(SHA256.HashData(_fixture)).ToLowerInvariant();

        await Test("newer stable release exposes the exact installer", async () =>
        {
            using var test = new Scenario();
            var release = await test.Service.CheckAsync(default);
            Require(release?.Version == new Version(1, 4, 0), "Expected newer version 1.4.0.");
            Require(release!.InstallerName == "Museek-1.4.0-setup.exe", "Unexpected installer name.");
            Require(release.ReleasePage.AbsoluteUri == "https://github.com/SaoodCS/Museek/releases/tag/v1.4.0", "Unexpected release page.");
        });
        foreach (var current in new[] { new Version(1, 4, 0), new Version(1, 5, 0) })
            await Test($"current {current} does not offer an older/equal release", async () =>
            {
                using var test = new Scenario(current: current);
                Require(await test.Service.CheckAsync(default) is null, "Should be up to date.");
            });
        foreach (var tag in new[] { "1.4.0", "v01.4.0", "v1.4", "v1.4.0.1", "v1.4.0-beta", "v65536.0.0", "v999999999999.0.0" })
            await RejectCheck($"invalid tag {tag}", () => Metadata(tag: tag));
        await RejectCheck("draft release", () => Metadata(draft: true));
        await RejectCheck("prerelease", () => Metadata(prerelease: true));
        await RejectCheck("wrong repository installer URL", () => Metadata(installerUrl: "https://github.com/other/Museek/releases/download/v1.4.0/Museek-1.4.0-setup.exe"));
        await RejectCheck("missing checksum asset", () => Metadata(includeSums: false));
        await RejectCheck("duplicate installer asset", () => Metadata(duplicateInstaller: true));
        await RejectCheck("oversized installer metadata", () => Metadata(installerSize: 536870913));
        await RejectCheck("zero installer size", () => Metadata(installerSize: 0));
        await RejectCheck("invalid API digest", () => Metadata(installerDigest: "sha512:abcd"));
        await Test("metadata body is bounded without Content-Length", async () =>
        {
            using var test = new Scenario(metadata: new string(' ', 1048577), unknownLength: true);
            await Reject(() => test.Service.CheckAsync(default));
        });
        await Test("API redirects do not leave the fixed endpoint", async () =>
        {
            using var test = new Scenario();
            test.Handler.ApiRedirect = new Uri("https://github.com/other");
            await Reject(() => test.Service.CheckAsync(default));
        });

        await Test("verified installer download reports progress and revalidates", async () =>
        {
            using var test = new Scenario();
            var progress = new ProgressValues();
            var release = (await test.Service.CheckAsync(default))!;
            using var downloaded = await test.Service.DownloadAsync(release, progress, default);
            Require(await File.ReadAllBytesAsync(downloaded.InstallerPath) is { Length: > 0 }, "Installer missing.");
            Require(downloaded.Release == release, "Release identity lost.");
            Require(progress.Values.Count >= 2 && progress.Values[0] == 0 && progress.Values[^1] == 1, "Missing download progress.");
            await test.Service.ValidateInstallerAsync(downloaded, default);
        });
        await Test("disposing an abandoned download removes only its owned file/directory", async () =>
        {
            using var test = new Scenario();
            var downloaded = await test.Service.DownloadAsync((await test.Service.CheckAsync(default))!, null, default);
            var path = downloaded.InstallerPath;
            downloaded.Dispose();
            Require(!File.Exists(path) && !Directory.Exists(Path.GetDirectoryName(path)), "Owned download was retained.");
            Require(Directory.Exists(test.Root), "Download root should remain.");
        });
        await Test("installer mutation after download is rejected before launch", async () =>
        {
            using var test = new Scenario();
            using var downloaded = await test.Service.DownloadAsync((await test.Service.CheckAsync(default))!, null, default);
            await File.WriteAllBytesAsync(downloaded.InstallerPath, new byte[_fixture.Length]);
            await Reject(() => test.Service.ValidateInstallerAsync(downloaded, default));
        });
        await Test("disposed downloads cannot be revalidated", async () =>
        {
            using var test = new Scenario();
            var downloaded = await test.Service.DownloadAsync((await test.Service.CheckAsync(default))!, null, default);
            downloaded.Dispose();
            await Reject(() => test.Service.ValidateInstallerAsync(downloaded, default));
        });
        await Test("abandoned cleanup preserves unrelated files in its directory", async () =>
        {
            using var test = new Scenario();
            var downloaded = await test.Service.DownloadAsync((await test.Service.CheckAsync(default))!, null, default);
            var sibling = Path.Combine(Path.GetDirectoryName(downloaded.InstallerPath)!, "unrelated.txt");
            await File.WriteAllTextAsync(sibling, "keep");
            downloaded.Dispose();
            Require(await File.ReadAllTextAsync(sibling) == "keep", "Cleanup deleted an unrelated file.");
            Require(!File.Exists(downloaded.InstallerPath), "Cleanup retained the owned installer.");
        });
        await Test("replaced download directory cannot redirect validation or cleanup", async () =>
        {
            using var test = new Scenario();
            var downloaded = await test.Service.DownloadAsync((await test.Service.CheckAsync(default))!, null, default);
            var child = Path.GetDirectoryName(downloaded.InstallerPath)!;
            var target = Path.Combine(test.Root, "outside");
            Directory.CreateDirectory(target);
            var outsideFile = Path.Combine(target, downloaded.Release.InstallerName);
            File.Move(downloaded.InstallerPath, outsideFile);
            Directory.Delete(child);
            CreateJunction(child, target);
            try
            {
                await Reject(() => test.Service.ValidateInstallerAsync(downloaded, default));
                downloaded.Dispose();
                Require(File.Exists(outsideFile), "Cleanup followed a replacement junction.");
            }
            finally { Directory.Delete(child); }
        });
        foreach (var sums in new[] { $"{new string('0', 64)}  Museek-1.4.0-setup.exe\n", $"{_hash}  ../Museek-1.4.0-setup.exe\n", $"{_hash}  Museek-1.4.0-setup.exe\n{_hash}  Museek-1.4.0-setup.exe\n", "bad checksum\n" })
            await RejectDownload("invalid/missing/duplicate installer checksum", sums: sums, metadata: Metadata(checksumSize: Encoding.UTF8.GetByteCount(sums)));
        await RejectDownload("corrupted installer bytes fail the checksum", installer: new byte[_fixture.Length]);
        await RejectDownload("installer API digest disagrees with checksum", metadata: Metadata(installerDigest: "sha256:" + new string('0', 64)));
        await RejectDownload("checksum API digest disagrees with bytes", metadata: Metadata(checksumDigest: "sha256:" + new string('0', 64)));
        await RejectDownload("truncated installer", installer: _fixture[..^1], unknownLength: true);
        await RejectDownload("installer exceeds declared size", installer: [.. _fixture, 0], unknownLength: true);
        await RejectDownload("checksum document exceeds its advertised size", sums: DefaultSums() + "\n", metadata: Metadata());
        await RejectDownload("installer numeric version differs despite matching hash", metadata: Metadata(tag: "v1.5.0", installerName: "Museek-1.5.0-setup.exe", installerUrl: "https://github.com/SaoodCS/Museek/releases/download/v1.5.0/Museek-1.5.0-setup.exe"), sums: $"{_hash}  Museek-1.5.0-setup.exe\n");
        await Test("unknown installer Content-Length is accepted within metadata bounds", async () =>
        {
            using var test = new Scenario(unknownLength: true);
            using var downloaded = await test.Service.DownloadAsync((await test.Service.CheckAsync(default))!, null, default);
            await test.Service.ValidateInstallerAsync(downloaded, default);
        });
        await Test("mandatory checksums work when GitHub has no API digest", async () =>
        {
            using var test = new Scenario(metadata: Metadata(includeInstallerDigest: false));
            using var downloaded = await test.Service.DownloadAsync((await test.Service.CheckAsync(default))!, null, default);
            await test.Service.ValidateInstallerAsync(downloaded, default);
        });
        await Test("binary-mode checksum entries are accepted", async () =>
        {
            using var test = new Scenario(sums: $"{_hash} *Museek-1.4.0-setup.exe\n");
            using var downloaded = await test.Service.DownloadAsync((await test.Service.CheckAsync(default))!, null, default);
            await test.Service.ValidateInstallerAsync(downloaded, default);
        });
        await Test("asset redirect to the exact GitHub release CDN is accepted", async () =>
        {
            using var test = new Scenario();
            test.Handler.AssetRedirect = new Uri("https://release-assets.githubusercontent.com/asset?token=test");
            using var downloaded = await test.Service.DownloadAsync((await test.Service.CheckAsync(default))!, null, default);
            await test.Service.ValidateInstallerAsync(downloaded, default);
        });
        foreach (var uri in new[] { "http://release-assets.githubusercontent.com/asset", "https://release-assets.githubusercontent.com.evil.test/asset", "https://github.com:444/asset", "https://user@github.com/asset", "https://api.github.com/asset", "https://github.com/asset#fragment" })
            await Test($"unsafe redirect {uri} is rejected", async () =>
            {
                using var test = new Scenario();
                test.Handler.AssetRedirect = new Uri(uri);
                await Reject(() => test.Service.DownloadAsync((test.Service.CheckAsync(default).GetAwaiter().GetResult())!, null, default));
                Require(!Directory.EnumerateFileSystemEntries(test.Root).Any(), "Failed redirect left files.");
            });
        await Test("redirect loops are bounded", async () =>
        {
            using var test = new Scenario();
            test.Handler.RedirectLoop = true;
            await Reject(() => test.Service.DownloadAsync((test.Service.CheckAsync(default).GetAwaiter().GetResult())!, null, default));
        });
        await Test("cancelled streaming download removes partial files", async () =>
        {
            using var test = new Scenario();
            using var cts = new CancellationTokenSource();
            test.Handler.CancelOnInstallerRead = cts;
            var release = (await test.Service.CheckAsync(default))!;
            await Expect<OperationCanceledException>(() => test.Service.DownloadAsync(release, null, cts.Token));
            Require(!Directory.EnumerateFileSystemEntries(test.Root).Any(), "Cancellation left partial files.");
        });
        await Test("cancelled checks remain cancellation errors", async () =>
        {
            using var test = new Scenario();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Expect<OperationCanceledException>(() => test.Service.CheckAsync(cts.Token));
        });
        await Test("another service cannot download a release it did not validate", async () =>
        {
            using var first = new Scenario();
            using var second = new Scenario();
            var release = (await first.Service.CheckAsync(default))!;
            await Reject(() => second.Service.DownloadAsync(release, null, default));
        });
        await Test("a junction download root is rejected before writing", async () =>
        {
            using var test = new Scenario();
            var target = Path.Combine(test.Root, "target");
            var link = Path.Combine(test.Root, "junction");
            Directory.CreateDirectory(target);
            CreateJunction(link, target);
            try
            {
                using var service = new AppUpdateService(new Version(1, 3, 2), test.Client, link);
                var release = (await service.CheckAsync(default))!;
                await Reject(() => service.DownloadAsync(release, null, default));
                Require(!Directory.EnumerateFileSystemEntries(target).Any(), "Junction target was written.");
            }
            finally { Directory.Delete(link); }
        });
        Console.WriteLine($"Update checks: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static Task RejectCheck(string name, Func<string> metadata) => Test(name, async () =>
    {
        using var test = new Scenario(metadata: metadata());
        await Reject(() => test.Service.CheckAsync(default));
    });

    private static Task RejectDownload(string name, string? metadata = null, string? sums = null, byte[]? installer = null, bool unknownLength = false) => Test(name, async () =>
    {
        using var test = new Scenario(metadata: metadata, sums: sums, installer: installer, unknownLength: unknownLength);
        var release = (await test.Service.CheckAsync(default))!;
        await Reject(() => test.Service.DownloadAsync(release, null, default));
        Require(!Directory.EnumerateFileSystemEntries(test.Root).Any(), "Failed download left files.");
    });

    private static async Task Test(string name, Func<Task> action)
    {
        try { await action(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception error) { _failed++; Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}"); }
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or HttpRequestException) { return; }
        throw new Exception("Unsafe input was accepted.");
    }

    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string DefaultSums() => $"{_hash}  Museek-1.4.0-setup.exe\n";

    private static string Metadata(string tag = "v1.4.0", bool draft = false, bool prerelease = false, string installerName = "Museek-1.4.0-setup.exe", string installerUrl = InstallerUrl, long? installerSize = null, bool includeSums = true, bool duplicateInstaller = false, string? installerDigest = null, string? checksumDigest = null, long? checksumSize = null, bool includeInstallerDigest = true)
    {
        var assets = new List<object>();
        var installer = new { name = installerName, state = "uploaded", size = installerSize ?? _fixture.Length, digest = includeInstallerDigest ? installerDigest ?? "sha256:" + _hash : null, browser_download_url = installerUrl };
        assets.Add(installer);
        if (duplicateInstaller) assets.Add(installer);
        if (includeSums) assets.Add(new { name = "SHA256SUMS.txt", state = "uploaded", size = checksumSize ?? Encoding.UTF8.GetByteCount(DefaultSums()), digest = checksumDigest, browser_download_url = tag == "v1.5.0" ? "https://github.com/SaoodCS/Museek/releases/download/v1.5.0/SHA256SUMS.txt" : SumsUrl });
        return JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, html_url = "https://github.com/SaoodCS/Museek/releases/tag/" + tag, assets });
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MuseekUpdateChecks-" + Guid.NewGuid().ToString("N"));
        public FakeHandler Handler { get; }
        public HttpClient Client { get; }
        public AppUpdateService Service { get; }
        public Scenario(string? metadata = null, string? sums = null, byte[]? installer = null, Version? current = null, bool unknownLength = false)
        {
            Directory.CreateDirectory(Root);
            Handler = new FakeHandler(metadata ?? Metadata(), sums ?? DefaultSums(), installer ?? _fixture, unknownLength);
            Client = new HttpClient(Handler);
            Service = new AppUpdateService(current ?? new Version(1, 3, 2), Client, Root);
        }
        public void Dispose() { Service.Dispose(); Client.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class FakeHandler(string metadata, string sums, byte[] installer, bool unknownLength) : HttpMessageHandler
    {
        public Uri? ApiRedirect { get; set; }
        public Uri? AssetRedirect { get; set; }
        public bool RedirectLoop { get; set; }
        public CancellationTokenSource? CancelOnInstallerRead { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == Api && ApiRedirect is not null) return Task.FromResult(Redirect(ApiRedirect));
            if (uri != Api && (RedirectLoop || AssetRedirect is not null && request.RequestUri.Host == "github.com"))
                return Task.FromResult(Redirect(RedirectLoop ? request.RequestUri : AssetRedirect!));
            byte[] bytes;
            var isInstaller = false;
            if (uri == Api) bytes = Encoding.UTF8.GetBytes(metadata);
            else if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal) || request.RequestUri.Host == "release-assets.githubusercontent.com" && request.Headers.Accept.Any(x => x.MediaType == "text/plain")) bytes = Encoding.UTF8.GetBytes(sums);
            else if (request.RequestUri.AbsolutePath.EndsWith("-setup.exe", StringComparison.Ordinal) || request.RequestUri.Host == "release-assets.githubusercontent.com") { bytes = installer; isInstaller = true; }
            else throw new Exception("Unexpected network destination: " + uri);
            var stream = isInstaller && CancelOnInstallerRead is not null ? new CancellingStream(bytes, CancelOnInstallerRead) : new MemoryStream(bytes);
            HttpContent content = unknownLength || CancelOnInstallerRead is not null ? new StreamContent(new NonSeekableStream(stream)) : new ByteArrayContent(bytes);
            if (unknownLength || CancelOnInstallerRead is not null) content.Headers.ContentLength = null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
        private static HttpResponseMessage Redirect(Uri uri) => new(HttpStatusCode.Found) { Headers = { Location = uri } };
    }

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource source) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class ProgressValues : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) => Values.Add(value);
    }

    private static void CreateJunction(string link, string target)
    {
        // Test-only filesystem fixture; mklink is never used by production.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Require(process.ExitCode == 0, "Could not create test junction.");
    }

    private static async Task<int> LiveAsync(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("--live-check" or "--live-download") || args[1] != "--current-version" || !Version.TryParse(args[2], out var current) || current.Build < 0 || current.Revision >= 0)
        {
            Console.Error.WriteLine("Usage: UpdateChecks --live-check|--live-download --current-version X.Y.Z");
            return 2;
        }
        // Explicit opt-in uses the fixed production source; no URL override and no installer launch.
        var root = Path.Combine(Path.GetTempPath(), "MuseekLiveUpdateChecks-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            using var service = new AppUpdateService(current, downloadDirectory: root);
            var release = await service.CheckAsync(cancellation.Token);
            if (release is null) { Console.WriteLine("No newer stable release."); return 0; }
            Console.WriteLine($"Verified release metadata: {release.Tag}, {release.InstallerName}.");
            if (args[0] == "--live-check") return 0;
            using var installer = await service.DownloadAsync(release, null, cancellation.Token);
            await service.ValidateInstallerAsync(installer, cancellation.Token);
            await using var file = new FileStream(installer.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation.Token));
            Console.WriteLine($"Verified installer: {new FileInfo(installer.InstallerPath).Length} bytes, SHA256 {digest}. It was not executed and will be removed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine($"Live update verification failed: {error.GetType().Name}: {error.Message}"); return 1; }
        finally
        {
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
    }
}
