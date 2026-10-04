using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Museek;
using Museek.Controls;
using Museek.Models;
using Museek.Services;

namespace Museek.PerformanceChecks;

internal static class Program
{
    private const int HotPathIterations = 10_000;
    private const int TransitionIterations = 1_000;
    private const int ArtworkIterations = 3;
    private const int ArtworkSize = 4096;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var baseline = false;
            var output = Path.GetFullPath(Path.Combine("artifacts", "performance-checks.json"));
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--baseline": baseline = true; break;
                    case "--output" when index + 1 < args.Length:
                        output = Path.GetFullPath(args[++index]);
                        break;
                    default: throw new ArgumentException("Usage: PerformanceChecks [--baseline] [--output result.json]");
                }
            }

            var app = CreateApplication();
            var measurements = RunMeasurements();
            var report = new PerformanceReport(baseline ? "baseline" : "checks", DateTimeOffset.UtcNow,
                RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                ArtworkSize, ArtworkSize, SystemParameters.ClientAreaAnimation, measurements);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var measurement in measurements)
                Console.WriteLine($"{measurement.Name}: {measurement.AllocatedBytes:N0} bytes total, " +
                    $"{measurement.BytesPerIteration:N1} bytes/iteration, {measurement.ElapsedMilliseconds:N1} ms " +
                    $"({measurement.Iterations:N0} iterations)");
            Console.WriteLine($"Performance results: {output}");

            var failed = measurements.Where(measurement => !measurement.Passed).ToArray();
            if (!baseline)
                foreach (var measurement in failed)
                    Console.Error.WriteLine($"Allocation check failed: {measurement.Name} exceeds " +
                        $"{measurement.AllocationCeilingBytes:N0} bytes.");
            var exitCode = baseline || failed.Length == 0 ? 0 : 1;
            app.Shutdown(exitCode);
            return exitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static Application CreateApplication()
    {
        // Use real styles without starting Museek's registration or file-opening flow.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var markup = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ProductionResources.xaml"));
        var resources = markup.Root?.Element(presentation + "Application.Resources")
            ?? throw new InvalidOperationException("Production Application.Resources were not found.");
        app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            resources.Elements()).ToString());
        return app;
    }

    private static IReadOnlyList<Measurement> RunMeasurements()
    {
        var seek = new RangeSeekBar { Width = 600 };
        seek.SetDuration(300);
        Layout(seek, 600, 54);
        Action<int> moveSeek = index => seek.Position = (index % 1000) * 0.3;
        var seekResult = Measure("MovingSeekPosition", HotPathIterations, 1000,
            1_000_000, moveSeek);
        if (Math.Abs(seek.Position - 299.7) > 0.00001)
            throw new InvalidOperationException("The measured seek loop did not move to its final position.");

        var player = new AudioPlayerService(useDummyAudioOutput: true);
        MainWindow? window = null;
        Measurement tickResult;
        Measurement transitionResult;
        try
        {
            var settings = new AppSettingsService(Path.Combine(Path.GetTempPath(),
                "Museek.PerformanceChecks." + Guid.NewGuid().ToString("N"), "settings.json"));
            window = new MainWindow(settings: settings, editTagsContextMenuRegistration: _ => { }, audioPlayer: player);
            // The window is never shown, playback never starts, and its timer is driven explicitly.
            ReadField<DispatcherTimer>(window, "_timer").Stop();
            Find<Slider>(window, "VolumeSlider").Value = 0;
            // Keep the paused benchmark on the real native-player path. Startup
            // initialization is setup and stays outside measured timer updates.
            player.InitializeAsync().GetAwaiter().GetResult();
            player.Pause();
            SetField(window, "_sourcePath", "paused performance fixture.wav");
            SetField(window, "_stoppedPosition", 12.5);
            Find<RangeSeekBar>(window, "SeekBar").SetDuration(300);
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            Layout(content, window.Width, Math.Max(1, window.Height - 31));
            var tick = (typeof(MainWindow).GetMethod("Timer_Tick", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MainWindow.Timer_Tick was not found."))
                .CreateDelegate<EventHandler>(window);
            Action<int> pausedTick = _ => tick(null, EventArgs.Empty);
            tickResult = Measure("PausedPlayerTick", HotPathIterations, 1000, 128_000, pausedTick);
            if (Find<TextBlock>(window, "CurrentTime").Text != "0:12" ||
                Math.Abs(Find<RangeSeekBar>(window, "SeekBar").Position - 12.5) > 0.00001)
                throw new InvalidOperationException("The measured timer did not update the ready paused player.");
            transitionResult = CheckTrackTransitions(window);
        }
        finally
        {
            // Production close owns the injected player's disposal. No HWND is created.
            if (window is null) player.Dispose();
            else window.Close();
        }

        var artworkBytes = CreateArtwork();
        if (artworkBytes.Length == 0 || artworkBytes.Length > 8 * 1024 * 1024)
            throw new InvalidOperationException("The large PNG fixture must fit the supported artwork byte limit.");
        var patch = new TagEditPatch { ArtworkAction = ArtworkAction.Replace, ArtworkBytes = artworkBytes };
        var validate = (typeof(AudioTagService).GetMethod("ValidatePatch", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AudioTagService.ValidatePatch was not found."))
            .CreateDelegate<Func<TagEditPatch, string?>>();
        Action<int> validateArtwork = _ =>
        {
            if (validate(patch) != "image/png")
                throw new InvalidOperationException("The real artwork validator did not accept the valid large PNG.");
        };
        var artworkResult = Measure("LargeArtworkValidation", ArtworkIterations, 1,
            ArtworkIterations * 1_048_576L, validateArtwork);
        return [seekResult, tickResult, transitionResult, artworkResult];
    }

    private static Measurement CheckTrackTransitions(MainWindow window)
    {
        var presentation = Find<StackPanel>(window, "TrackPresentation");
        var translation = Find<TranslateTransform>(window, "TrackTranslation");
        var clockField = typeof(MainWindow).GetField("_trackTransitionClock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow._trackTransitionClock was not found.");
        var animate = (typeof(MainWindow).GetMethod("AnimateTrackTransition", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow.AnimateTrackTransition was not found."))
            .CreateDelegate<Action<int>>(window);
        var reset = (typeof(MainWindow).GetMethod("ResetTrackTransition", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow.ResetTrackTransition was not found."))
            .CreateDelegate<Action>(window);
        bool IsReset() => clockField.GetValue(window) is null &&
            !presentation.HasAnimatedProperties && !translation.HasAnimatedProperties &&
            Math.Abs(presentation.Opacity - 1) < 0.00001 && Math.Abs(translation.X) < 0.00001;

        // Rapid replacement is the worst case for retaining old clocks. Reflection
        // setup and dispatcher cleanup stay outside the allocation measurement.
        Action<int> replace = index => animate(index % 2 == 0 ? -1 : 1);
        var result = Measure("RapidTrackTransitions", TransitionIterations, 100,
            TransitionIterations * 8_192L, replace);
        reset();
        if (!IsReset()) throw new InvalidOperationException("Reset retained transition clocks or animated values.");

        animate(0);
        if (!IsReset()) throw new InvalidOperationException("Opening a file directly unexpectedly creates transition clocks.");

        if (SystemParameters.ClientAreaAnimation)
        {
            // Keep only weak samples of replaced clocks, then let the final clock
            // complete naturally. A rooted old clock fails collection after cleanup.
            var replacedClocks = CaptureReplacedClocks(window, clockField, animate);
            if (clockField.GetValue(window) is null || !presentation.HasAnimatedProperties || !translation.HasAnimatedProperties)
                throw new InvalidOperationException("Track transitions did not attach their finite render clocks.");
            PumpUntil(IsReset, TimeSpan.FromSeconds(2), "Track transition did not clean up on completion.");
            PumpUntil(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                return replacedClocks.All(clock => !clock.IsAlive);
            }, TimeSpan.FromSeconds(2), "Completed/replaced track transition clocks remain rooted.");
            Console.WriteLine("PASS: transition completion clears animated properties and releases replaced clocks.");
        }
        else
        {
            if (!IsReset()) throw new InvalidOperationException("Disabled system animations still attach transition clocks.");
            Console.WriteLine("PASS: disabled system animations create no transition clocks.");
        }
        return result;
    }

    private static WeakReference[] CaptureReplacedClocks(MainWindow window, FieldInfo clockField, Action<int> animate)
    {
        var clocks = new WeakReference[32];
        for (var index = 0; index < clocks.Length; index++)
        {
            animate(index % 2 == 0 ? -1 : 1);
            clocks[index] = new WeakReference(clockField.GetValue(window)
                ?? throw new InvalidOperationException("Track transition clock was not created."));
        }
        return clocks;
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var frame = new DispatcherFrame();
        var started = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (condition() || Stopwatch.GetElapsedTime(started) >= timeout) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!condition()) throw new InvalidOperationException(failure);
    }

    private static byte[] CreateArtwork()
    {
        // A uniform 16-megapixel grayscale image compresses well. Validation converts it
        // to BGRA, so a full-frame scratch buffer would allocate 64 MiB per validation.
        var pixels = new byte[ArtworkSize * ArtworkSize];
        var bitmap = BitmapSource.Create(ArtworkSize, ArtworkSize, 96, 96,
            PixelFormats.Gray8, null, pixels, ArtworkSize);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Measurement Measure(string name, int iterations, int warmupIterations,
        long allocationCeilingBytes, Action<int> operation)
    {
        for (var index = 0; index < warmupIterations; index++) operation(index);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < iterations; index++) operation(index);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return new Measurement(name, iterations, allocated, (double)allocated / iterations,
            elapsed, allocationCeilingBytes, allocated <= allocationCeilingBytes);
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static T Find<T>(FrameworkElement owner, string name) where T : class
        => owner.FindName(name) as T ?? throw new InvalidOperationException("Control was not found: " + name);

    private static T ReadField<T>(MainWindow window, string name)
        => (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException("MainWindow field was not found: " + name));

    private static void SetField(MainWindow window, string name, object value)
        => (typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindow field was not found: " + name)).SetValue(window, value);

    private sealed record Measurement(string Name, int Iterations, long AllocatedBytes,
        double BytesPerIteration, double ElapsedMilliseconds, long AllocationCeilingBytes, bool Passed);

    private sealed record PerformanceReport(string Mode, DateTimeOffset CapturedUtc, string Runtime,
        string OperatingSystem, int ArtworkWidth, int ArtworkHeight, bool TrackAnimationsEnabled,
        IReadOnlyList<Measurement> Measurements);
}
