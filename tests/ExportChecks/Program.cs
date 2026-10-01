using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Museek.Services;

var folder = Path.Combine(Path.GetTempPath(), "Museek ExportChecks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var source = Path.Combine(folder, "音楽 sample ' & source.wav");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "aevalsrc=if(lt(t\\,1)\\,0.25\\,if(lt(t\\,3)\\,-0.5\\,0.75)):s=48000:d=5", "-c:a", "pcm_s16le",
        "-metadata", "title=Metadata title", "-y", source);
    var sourceHash = await HashAsync(source);
    var service = new AudioExportService();
    var info = await service.ProbeAsync(source);
    Check(Math.Abs(info.Duration.TotalSeconds - 5) < 0.01, "probe reads duration");
    Check(info.Title == "Metadata title", "probe reads metadata title");
    Check(!string.IsNullOrWhiteSpace(info.Format), "probe identifies format");

    var noTitle = Path.Combine(folder, "Filename title.wav");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-i", source,
        "-map_metadata", "-1", "-c:a", "copy", "-y", noTitle);
    Check((await service.ProbeAsync(noTitle)).Title == "Filename title", "probe falls back to filename");

    var video = Path.Combine(folder, "video.mp4");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=size=32x32:rate=1:duration=1", "-an", "-y", video);
    await ThrowsAsync<InvalidDataException>(() => service.ProbeAsync(video), "probe rejects files without audio");

    var reports = new List<double>();
    var output = Path.Combine(folder, "trim.wav");
    await service.ExportAsync(source, output, TimeSpan.FromSeconds(1.234), TimeSpan.FromSeconds(2.789),
        new InlineProgress(value => reports.Add(value)));
    Check(Math.Abs((await service.ProbeAsync(output)).Duration.TotalSeconds - 1.555) < 1.0 / 48000,
        "WAV trim duration is accurate to one sample");
    var raw = Path.Combine(folder, "trim.raw");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-i", output, "-f", "s16le", "-y", raw);
    var samples = await File.ReadAllBytesAsync(raw);
    Check(samples.Length > 0 && BitConverter.ToInt16(samples, 0) == -16384 &&
        BitConverter.ToInt16(samples, samples.Length - 2) == -16384, "trim contains only selected audio");
    Check((await service.ProbeAsync(output)).Title == "Metadata title", "export preserves metadata");
    Check(reports.Count > 0 && reports[^1] == 1 && reports.All(value => value >= 0 && value <= 1),
        "export reports bounded progress through completion");
    var sourceHashAfterExport = await HashAsync(source);
    Check(sourceHash.SequenceEqual(sourceHashAfterExport), "export leaves source unchanged");

    foreach (var extension in new[] { ".mp3", ".flac", ".m4a", ".ogg", ".opus" })
    {
        var encoded = Path.Combine(folder, "trim" + extension);
        await service.ExportAsync(source, encoded, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        var encodedInfo = await service.ProbeAsync(encoded);
        Check(Math.Abs(encodedInfo.Duration.TotalSeconds - 1) < 0.15, extension + " exports the selected duration");
        Check(encodedInfo.Title == "Metadata title", extension + " keeps the title");
        if (extension == ".m4a") Check(encodedInfo.Format == "M4A", "M4A probe displays its audio container name");
    }

    var oggToMp3 = Path.Combine(folder, "stream metadata.mp3");
    await service.ExportAsync(Path.Combine(folder, "trim.ogg"), oggToMp3, TimeSpan.Zero, TimeSpan.FromSeconds(0.5));
    Check((await service.ProbeAsync(oggToMp3)).Title == "Metadata title", "stream metadata survives export to a global-tag container");

    var multi = Path.Combine(folder, "multiple streams.mkv");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-i", source, "-f", "lavfi", "-i",
        "sine=frequency=880:duration=5", "-f", "lavfi", "-i", "color=size=32x32:rate=1:duration=5",
        "-map", "0:a:0", "-map", "1:a:0", "-map", "2:v:0", "-c:a", "pcm_s16le", "-c:v", "ffv1", "-y", multi);
    var oneStream = Path.Combine(folder, "one stream.flac");
    await service.ExportAsync(multi, oneStream, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    var streamsText = await RunToolAsync("ffprobe", "-v", "error", "-show_entries", "stream=codec_type",
        "-of", "json", oneStream);
    using (var streamsJson = JsonDocument.Parse(streamsText))
    {
        var streams = streamsJson.RootElement.GetProperty("streams");
        Check(streams.GetArrayLength() == 1 && streams[0].GetProperty("codec_type").GetString() == "audio",
            "export maps first audio stream and removes video");
    }
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-i", oneStream, "-f", "s16le", "-y", raw);
    var firstStreamSamples = await File.ReadAllBytesAsync(raw);
    Check(BitConverter.ToInt16(firstStreamSamples, 0) == -16384 &&
        BitConverter.ToInt16(firstStreamSamples, firstStreamSamples.Length - 2) == -16384,
        "export selects the first audio stream's samples");

    await ThrowsAsync<ArgumentException>(() => service.ExportAsync(source, source.ToUpperInvariant(),
        TimeSpan.Zero, TimeSpan.FromSeconds(1)), "source cannot be overwritten regardless of path casing");
    if (OperatingSystem.IsWindows())
    {
        var alias = Path.Combine(folder, "source hardlink alias.wav");
        if (!NativeTestMethods.CreateHardLink(alias, source, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        await ThrowsAsync<ArgumentException>(() => service.ExportAsync(source, alias,
            TimeSpan.Zero, TimeSpan.FromSeconds(1)), "source cannot be overwritten through a file identity alias");
        var aliasHash = await HashAsync(alias);
        var sourceHashAfterAlias = await HashAsync(source);
        Check(sourceHash.SequenceEqual(aliasHash) && sourceHash.SequenceEqual(sourceHashAfterAlias),
            "rejected alias export leaves both source paths unchanged");

        var changingDestination = Path.Combine(folder, "destination changed during export.wav");
        await File.WriteAllBytesAsync(changingDestination, new byte[] { 1, 2, 3 });
        var aliasCreatedDuringExport = false;
        await ThrowsAsync<ArgumentException>(() => service.ExportAsync(source, changingDestination,
            TimeSpan.Zero, TimeSpan.FromSeconds(1), new InlineProgress(value =>
            {
                if (value <= 0 || aliasCreatedDuringExport) return;
                File.Delete(changingDestination);
                if (!NativeTestMethods.CreateHardLink(changingDestination, source, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                aliasCreatedDuringExport = true;
            })), "destination identity is checked again before committing export");
        var changedDestinationHash = await HashAsync(changingDestination);
        Check(aliasCreatedDuringExport && sourceHash.SequenceEqual(changedDestinationHash),
            "an alias created during export remains unchanged");
        Check(!Directory.EnumerateFiles(folder).Any(path => Path.GetFileName(path).Contains(".museek-")),
            "identity rejection after encoding removes the temporary output");
    }
    await ThrowsAsync<ArgumentOutOfRangeException>(() => service.ExportAsync(source, output,
        TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1)), "negative start is rejected");
    await ThrowsAsync<ArgumentOutOfRangeException>(() => service.ExportAsync(source, output,
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), "empty range is rejected");
    await ThrowsAsync<ArgumentOutOfRangeException>(() => service.ExportAsync(source, output,
        TimeSpan.Zero, TimeSpan.FromSeconds(6)), "range past duration is rejected");
    await ThrowsAsync<NotSupportedException>(() => service.ExportAsync(source, Path.Combine(folder, "trim.xyz"),
        TimeSpan.Zero, TimeSpan.FromSeconds(1)), "unsupported output format is rejected");

    var sentinel = new byte[] { 1, 2, 3, 4 };
    await File.WriteAllBytesAsync(output, sentinel);
    using (var locked = File.Open(output, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        await ThrowsDestinationFailureAsync(() => service.ExportAsync(source, output, TimeSpan.Zero,
            TimeSpan.FromSeconds(1)), "failed destination replacement preserves existing file");
    }
    var bytesAfterFailure = await File.ReadAllBytesAsync(output);
    Check(sentinel.SequenceEqual(bytesAfterFailure), "failed export keeps destination bytes");
    Check(!Directory.EnumerateFiles(folder).Any(path => Path.GetFileName(path).Contains(".museek-")),
        "failed export removes temporary files");

    var longer = Path.Combine(folder, "long audio.flac");
    await RunToolAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=440:sample_rate=48000:duration=120", "-c:a", "flac", "-y", longer);
    using var cancellation = new CancellationTokenSource();
    await ThrowsAsync<OperationCanceledException>(() => service.ExportAsync(longer, output, TimeSpan.Zero,
        TimeSpan.FromSeconds(120), new InlineProgress(value =>
        {
            if (value > 0 && value < 1) cancellation.Cancel();
        }), cancellation.Token), "active export can be cancelled");
    Check(cancellation.IsCancellationRequested, "cancellation test interrupts actual encoding progress");
    var bytesAfterCancellation = await File.ReadAllBytesAsync(output);
    Check(sentinel.SequenceEqual(bytesAfterCancellation), "cancelled export preserves destination");
    Check(!Directory.EnumerateFiles(folder).Any(path => Path.GetFileName(path).Contains(".museek-")),
        "cancelled export removes temporary files");
    await service.ExportAsync(source, output, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    Check(Math.Abs((await service.ProbeAsync(output)).Duration.TotalSeconds - 1) < 0.01,
        "successful export replaces the existing destination");
    Console.WriteLine("All export integration checks passed.");
}
finally
{
    // Only this runner's unique test directory is removed.
    Directory.Delete(folder, recursive: true);
}

static async Task<byte[]> HashAsync(string path)
{
    await using var stream = File.OpenRead(path);
    return await SHA256.HashDataAsync(stream);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

static async Task ThrowsAsync<T>(Func<Task> operation, string description) where T : Exception
{
    try { await operation(); }
    catch (T) { Console.WriteLine("PASS: " + description); return; }
    throw new Exception("FAIL: " + description);
}

static async Task ThrowsDestinationFailureAsync(Func<Task> operation, string description)
{
    try { await operation(); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine("PASS: " + description);
        return;
    }
    throw new Exception("FAIL: " + description);
}

static async Task<string> RunToolAsync(string executable, params string[] arguments)
{
    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo) ?? throw new Exception("Unable to start " + executable);
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var error = await stderr;
    if (process.ExitCode != 0) throw new Exception(executable + ": " + error);
    return await stdout;
}

sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}

static class NativeTestMethods
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateHardLink(string newPath, string existingPath, IntPtr securityAttributes);
}
