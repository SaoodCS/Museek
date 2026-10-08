using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using LibVLCSharp.Shared;

namespace Museek.StartupChecks;

internal static class VlcPluginCacheChecks
{
    internal static Program.Report Generate(string directory)
    {
        var nativeDirectory = ValidateDirectory(directory);
        var pluginDirectory = Path.Combine(nativeDirectory, "plugins");
        var normalized = 0;
        foreach (var plugin in Directory.EnumerateFiles(pluginDirectory, "*_plugin.dll", SearchOption.AllDirectories))
        {
            // NuGet already uses ZIP-safe times. Normalize only finer timestamps so
            // the same cache remains valid after an installer or ZIP round trip.
            var timestamp = File.GetLastWriteTimeUtc(plugin);
            var remainder = timestamp.Ticks % (2 * TimeSpan.TicksPerSecond);
            if (remainder == 0) continue;
            File.SetLastWriteTimeUtc(plugin, timestamp.AddTicks(-remainder));
            normalized++;
        }
        ClearNativeOverrides();
        LibVLCSharp.Shared.Core.Initialize(nativeDirectory);
        using (var vlc = new LibVLC("--reset-plugins-cache", "--quiet", "--no-video", "--no-audio",
                   "--no-metadata-network-access")) { }
        var cache = Path.Combine(pluginDirectory, "plugins.dat");
        var failures = new List<string>();
        Check(File.Exists(cache) && new FileInfo(cache).Length > 0,
            "The bundled native core generated a plugin cache", failures);
        return new Program.Report("GeneratePluginCache", new Dictionary<string, double>
        {
            ["NormalizedPluginTimestamps"] = normalized,
            ["PluginCacheBytes"] = File.Exists(cache) ? new FileInfo(cache).Length : 0
        }, failures);
    }

    internal static async Task<Program.Report> VerifyAsync(string directory, bool cacheOnly, string? ffmpeg)
    {
        var nativeDirectory = ValidateDirectory(directory);
        var pluginDirectory = Path.Combine(nativeDirectory, "plugins");
        var cache = Path.Combine(pluginDirectory, "plugins.dat");
        var failures = new List<string>();
        var measurements = new Dictionary<string, double>();
        var originalCache = File.Exists(cache) ? SHA256.HashData(File.ReadAllBytes(cache)) : null;
        Check(originalCache is not null, "The published runtime contains a plugin cache", failures);
        measurements["BundledPluginCount"] = Directory.EnumerateFiles(pluginDirectory, "*_plugin.dll",
            SearchOption.AllDirectories).Count();

        // Each mode runs in a new process. A shared VLC module bank would hide a cache miss.
        ClearNativeOverrides();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var memoryBefore = process.PrivateMemorySize64;
        var started = Stopwatch.GetTimestamp();
        LibVLCSharp.Shared.Core.Initialize(nativeDirectory);
        var options = new List<string>
        {
            "--no-video", "--quiet", "--no-osd", "--no-metadata-network-access", "--aout=dummy"
        };
        if (cacheOnly) options.Add("--no-plugins-scan");
        using var vlc = new LibVLC(options.ToArray());
        using var player = new MediaPlayer(vlc);
        measurements["NativeInitializationMilliseconds"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        measurements["InitializedPrivateMemoryIncreaseBytes"] = process.PrivateMemorySize64 - memoryBefore;
        var loaded = LoadedPluginCount(pluginDirectory);
        measurements["InitializationLoadedPluginCount"] = loaded;
        Check(loaded <= 32 && loaded < measurements["BundledPluginCount"],
            "Plugin discovery loads only selected plugins", failures);
        Check(process.Modules.Cast<ProcessModule>().Any(module =>
                module.ModuleName.Equals("libvlccore.dll", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFullPath(module.FileName).Equals(Path.Combine(nativeDirectory, "libvlccore.dll"),
                    StringComparison.OrdinalIgnoreCase)),
            "The check uses the exact published native runtime", failures);

        var scratch = Path.Combine(Path.GetTempPath(), "Museek CacheChecks " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var wave = Program.WriteWave(scratch, "cache-check.wav");
            await CheckPlaybackAsync(vlc, player, wave, "WAV", failures);
            if (ffmpeg is not null)
            {
                var mp3 = Path.Combine(scratch, "cache-check.mp3");
                await GenerateMp3Async(ffmpeg, wave, mp3);
                await CheckPlaybackAsync(vlc, player, mp3, "MP3", failures);
            }
            measurements["PlaybackLoadedPluginCount"] = LoadedPluginCount(pluginDirectory);
            Check(measurements["PlaybackLoadedPluginCount"] <= 80 &&
                    measurements["PlaybackLoadedPluginCount"] < measurements["BundledPluginCount"],
                "Playback retains only the selected native plugins", failures);
            Check(originalCache is not null && File.Exists(cache) &&
                    originalCache.SequenceEqual(SHA256.HashData(File.ReadAllBytes(cache))),
                "Normal playback leaves the packaged cache unchanged", failures);
        }
        finally { Directory.Delete(scratch, recursive: true); }
        return new Program.Report(cacheOnly ? "PluginCacheOnly" : "PluginCache", measurements, failures);
    }

    private static string ValidateDirectory(string directory)
    {
        var absolute = Path.GetFullPath(directory);
        if (!Directory.Exists(absolute)) throw new DirectoryNotFoundException(absolute);
        var cursor = absolute;
        while (cursor is not null)
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Native runtime paths cannot contain symbolic links or junctions.");
            cursor = Path.GetDirectoryName(cursor);
        }
        foreach (var name in new[] { "libvlc.dll", "libvlccore.dll", "plugins" })
            if (!Path.Exists(Path.Combine(absolute, name)))
                throw new FileNotFoundException($"The native runtime is missing {name}.");
        var directories = new Stack<string>();
        directories.Push(absolute);
        while (directories.Count > 0)
            foreach (var item in new DirectoryInfo(directories.Pop()).EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Native runtimes cannot contain symbolic links or junctions.");
                if (item is DirectoryInfo child) directories.Push(child.FullName);
            }
        return absolute;
    }

    private static void ClearNativeOverrides()
    {
        Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", null);
        Environment.SetEnvironmentVariable("VLC_DATA_PATH", null);
    }

    private static async Task CheckPlaybackAsync(LibVLC vlc, MediaPlayer player, string path,
        string format, List<string> failures)
    {
        using var media = new Media(vlc, new Uri(path));
        player.Media = media;
        Check(player.Play(), $"Cached plugins can start {format} playback", failures);
        var deadline = Stopwatch.StartNew();
        while (player.Time < 100 && player.State != VLCState.Error && deadline.Elapsed < TimeSpan.FromSeconds(8))
            await Task.Delay(20);
        Check(player.Time >= 100, $"Cached plugins decode {format} and advance the playback clock", failures);
        player.Stop();
        player.Media = null;
    }

    private static async Task GenerateMp3Async(string ffmpeg, string wave, string output)
    {
        var executable = Path.GetFullPath(ffmpeg);
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("Pass --ffmpeg with a regular FFmpeg executable.", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", wave,
                     "-codec:a", "libmp3lame", "-b:a", "64k", output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start.");
        var errors = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("FFmpeg did not finish the MP3 fixture within 20 seconds.");
        }
        await stdout;
        if (process.ExitCode != 0) throw new InvalidOperationException($"MP3 fixture generation failed: {await errors}");
        await errors;
    }

    private static int LoadedPluginCount(string pluginDirectory)
    {
        using var process = Process.GetCurrentProcess();
        var prefix = pluginDirectory + Path.DirectorySeparatorChar;
        return process.Modules.Cast<ProcessModule>().Count(module =>
            module.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            module.ModuleName.EndsWith("_plugin.dll", StringComparison.OrdinalIgnoreCase));
    }

    private static void Check(bool passed, string description, List<string> failures)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {description}");
        if (!passed) failures.Add(description);
    }
}
