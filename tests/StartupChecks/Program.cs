using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;
using LibVLCSharp.Shared;
using Museek;
using Museek.Services;

namespace Museek.StartupChecks;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var baseline = args.Contains("--baseline");
            var windowCheck = args.Contains("--window");
            var outputIndex = Array.IndexOf(args, "--output");
            var output = Path.GetFullPath(outputIndex >= 0 && outputIndex + 1 < args.Length
                ? args[outputIndex + 1] : Path.Combine("artifacts", "startup-checks.json"));
            var report = windowCheck ? MeasureWindow()
                : CheckPlayerAsync(baseline, !args.Contains("--real-output")).GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return report.Failures.Count == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<Report> CheckPlayerAsync(bool baseline, bool useDummyAudioOutput)
    {
        var failures = new List<string>();
        var measurements = new Dictionary<string, double>();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var memoryBefore = process.PrivateMemorySize64;
        var started = Stopwatch.GetTimestamp();
        var idle = new AudioPlayerService(useDummyAudioOutput);
        measurements["ColdPlayerConstructionMilliseconds"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        measurements["IdlePrivateMemoryIncreaseBytes"] = process.PrivateMemorySize64 - memoryBefore;
        Check(!NativeVlcLoaded(), "Idle startup must not load the native decoder", failures);
        Check(!idle.IsPlaying && !idle.HasEnded && idle.Position == 0 && idle.Volume == 75,
            "The idle player exposes stable initial playback and volume state", failures);
        idle.Volume = -1;
        Check(idle.Volume == 0, "Volume is clamped before initialization", failures);
        idle.Volume = 137;
        Check(idle.Volume == 100, "High volume is clamped before initialization", failures);
        idle.Pause();
        idle.Stop();
        idle.Play();
        idle.Seek(2);
        Check(!idle.IsPlaying && idle.Position == 0 && !NativeVlcLoaded(),
            "Idle controls and clock queries must not initialize the decoder", failures);
        idle.Dispose();
        if (!baseline)
        {
            idle.Dispose();
            Check(!idle.IsPlaying && idle.Position == 0 && !NativeVlcLoaded(),
                "Closing before playback is safe and keeps the decoder unloaded", failures);
            try
            {
                idle.Open("closed player.wav");
                failures.Add("Opening a disposed player must throw ObjectDisposedException");
            }
            catch (ObjectDisposedException) { }

            // A synchronous implementation would finish native loading before
            // returning and freeze the owning UI thread, so this must stay pending.
            using var closing = new AudioPlayerService(useDummyAudioOutput);
            started = Stopwatch.GetTimestamp();
            var preparation = closing.InitializeAsync();
            measurements["BackgroundPreparationCallMilliseconds"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Check(!preparation.IsCompleted,
                "First decoder preparation returns while native work is still pending", failures);
            Check(ReferenceEquals(preparation, closing.InitializeAsync()),
                "Concurrent opening requests share one pending decoder preparation", failures);
            closing.Volume = 42;
            Check(!closing.IsPlaying && !closing.HasEnded && closing.Position == 0 && closing.Volume == 42,
                "Controls remain usable during background decoder preparation", failures);
            closing.Dispose();
            try { await preparation; }
            catch (ObjectDisposedException) { }
            Check(!closing.IsPlaying && closing.Position == 0,
                "Closing during preparation safely releases the eventual decoder", failures);
            try
            {
                closing.Open("closed during preparation.wav");
                failures.Add("Closing during preparation must prevent reopening");
            }
            catch (ObjectDisposedException) { }
        }

        var scratch = Path.Combine(Path.GetTempPath(), "Museek StartupChecks " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var first = WriteWave(scratch, "first.wav");
            var second = WriteWave(scratch, "second.wav");
            using var player = new AudioPlayerService(useDummyAudioOutput);
            player.Volume = 37;
            var playbackErrors = 0;
            var playbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.PlaybackError += (_, _) => Interlocked.Increment(ref playbackErrors);
            player.PlaybackStarted += (_, _) => playbackStarted.TrySetResult();
            started = Stopwatch.GetTimestamp();
            if (!baseline)
            {
                var preparation = player.InitializeAsync();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                if (!preparation.IsCompleted)
                {
                    try
                    {
                        await preparation.WaitAsync(cancellation.Token);
                        failures.Add("A cancelled opening request must stop waiting for preparation");
                    }
                    catch (OperationCanceledException) { }
                }
                player.Volume = 37;
                await preparation;
                await player.InitializeAsync();
            }
            player.Open(first);
            // Mirror the app's marshalled startup-volume application. Async
            // continuations never call VLC from its own native event callback.
            await playbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            player.Volume = player.Volume;
            await WaitForAsync(() => player.IsPlaying, "First playback did not start");
            measurements["FirstOpenToPlayingMilliseconds"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Check(NativeVlcLoaded() && player.Volume == 37,
                "First open initializes the real decoder and preserves the chosen volume", failures);
            if (!useDummyAudioOutput)
            {
                var nativePlayer = (MediaPlayer)typeof(AudioPlayerService)
                    .GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
                await WaitForAsync(() => nativePlayer.Volume == 37,
                    "The requested startup volume did not reach the real audio output");
                measurements["AppliedNativeVolume"] = nativePlayer.Volume;
                player.Volume = 64;
                await WaitForAsync(() => nativePlayer.Volume == 64,
                    "A live volume change did not reach the real audio output");
                Check(nativePlayer.Volume == 64, "Live volume changes reach the real audio output", failures);
                player.Volume = 37;
            }
            await WaitForAsync(() => player.Position > 0.2, "Playback clock did not advance");
            player.Pause();
            await WaitForAsync(() => !player.IsPlaying, "Pause did not stop playback");
            player.Play();
            await WaitForAsync(() => player.IsPlaying, "Paused playback did not resume");
            player.Seek(1.2);
            await WaitForAsync(() => player.Position >= 1, "Seek was not applied");
            player.Stop();
            Check(!player.IsPlaying, "Stop ends native playback", failures);
            started = Stopwatch.GetTimestamp();
            player.Open(second);
            await WaitForAsync(() => player.IsPlaying, "Replacement track did not start");
            measurements["SecondOpenToPlayingMilliseconds"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Check(player.Volume == 37, "Replacement playback retains the chosen volume", failures);
            await WaitForAsync(() => player.HasEnded, "Playback did not reach EOF");
            player.Play();
            await WaitForAsync(() => player.IsPlaying, "An ended track did not replay");
            if (!baseline)
            {
                player.Dispose();
                player.Dispose();
                Check(!player.IsPlaying && player.Position == 0, "Disposal resets playback safely", failures);
            }
            Check(playbackErrors == 0, "Real playback reported no decoder errors", failures);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
        return new Report("PlayerLifecycle", measurements, failures);
    }

    private static Report MeasureWindow()
    {
        var failures = new List<string>();
        var measurements = new Dictionary<string, double>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var markup = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ProductionResources.xaml"));
        var resources = markup.Root!.Element(presentation + "Application.Resources")!;
        app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            resources.Elements()).ToString());
        var settings = new AppSettingsService(Path.Combine(Path.GetTempPath(),
            "Museek.StartupChecks." + Guid.NewGuid().ToString("N"), "settings.json"));
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var memoryBefore = process.PrivateMemorySize64;
        var started = Stopwatch.GetTimestamp();
        var player = new AudioPlayerService(useDummyAudioOutput: true);
        var window = new MainWindow(settings: settings, editTagsContextMenuRegistration: _ => { }, audioPlayer: player);
        measurements["ColdWindowConstructionMilliseconds"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        window.ContentRendered += (_, _) =>
        {
            measurements["ColdWindowFirstRenderMilliseconds"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            process.Refresh();
            measurements["RenderedPrivateMemoryIncreaseBytes"] = process.PrivateMemorySize64 - memoryBefore;
            Check(!NativeVlcLoaded(), "An empty rendered window must leave the decoder unloaded", failures);
            window.Close();
        };
        window.Closed += (_, _) => app.Shutdown();
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timeout.Tick += (_, _) => { failures.Add("Initial window render timed out"); timeout.Stop(); window.Close(); };
        timeout.Start();
        window.Show();
        app.Run();
        timeout.Stop();
        return new Report("EmptyWindow", measurements, failures);
    }

    private static bool NativeVlcLoaded()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Any(module =>
            module.ModuleName.Equals("libvlc.dll", StringComparison.OrdinalIgnoreCase) ||
            module.ModuleName.Equals("libvlccore.dll", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitForAsync(Func<bool> condition, string message)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidOperationException(message);
            await Task.Delay(20);
        }
    }

    private static string WriteWave(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        const int sampleRate = 8000;
        const int dataLength = sampleRate * 2 * 3;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + dataLength); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
        writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(dataLength); writer.Write(new byte[dataLength]);
        return path;
    }

    private static void Check(bool passed, string description, List<string> failures)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {description}");
        if (!passed) failures.Add(description);
    }

    private sealed record Report(string Scenario, Dictionary<string, double> Measurements, List<string> Failures);
}
