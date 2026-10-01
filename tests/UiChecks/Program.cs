using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Museek;
using Museek.Controls;
using Museek.Services;

namespace Museek.UiChecks;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // App queues its production OnStartup under any dispatcher loop. Load its real resources
        // into a plain Application so the harness cannot activate the app or registration flow.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var markup = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "ProductionResources.xaml"));
        var resources = markup.Root?.Element(presentation + "Application.Resources")
            ?? throw new Exception("Production Application.Resources were not found.");
        app.Resources = (ResourceDictionary)XamlReader.Parse(new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            resources.Elements()).ToString());
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var exitCode = 1;
        var artifacts = Path.GetFullPath(args.Length > 0 ? args[0] :
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts"));
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                await RunChecksAsync(artifacts);
                exitCode = 0;
                Console.WriteLine("All silent WPF UI and native playback checks passed.");
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                app.Shutdown(exitCode);
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task RunChecksAsync(string artifacts)
    {
        Directory.CreateDirectory(artifacts);
        var fixtureDirectory = Path.Combine(artifacts, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        MainWindow? window = null;
        try
        {
            var source = Path.Combine(fixtureDirectory, "音楽 sample ' & audio.wav");
            const string title = "Museek UI fixture — 音楽";
            // The fixture is silent as well as muted: a regression in volume cannot make noise.
            await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                "anullsrc=r=48000:cl=mono", "-t", "12", "-c:a", "pcm_s16le",
                "-metadata", "title=" + title, "-y", source);
            var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));

            window = new MainWindow();
            var volume = Find<Slider>(window, "VolumeSlider");
            volume.Value = 0;
            var player = (AudioPlayerService)(typeof(MainWindow).GetField("_player",
                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window)
                ?? throw new Exception("MainWindow player was not found."));
            Check(player.Volume == 0 && Find<TextBlock>(window, "VolumeLabel").Text == "0%",
                "volume slider immediately mutes native playback and updates its label");

            // Detach the content into an offscreen layout surface. No HWND or desktop capture is used.
            var content = (UIElement)window.Content;
            window.Content = null;
            var surface = new Border { Child = content, Background = window.Background, Width = 570, Height = 410 };
            TextElement.SetFontFamily(surface, window.FontFamily);
            TextElement.SetFontSize(surface, window.FontSize);
            TextElement.SetForeground(surface, window.Foreground);
            Layout(surface);

            var seek = Find<RangeSeekBar>(window, "SeekBar");
            var play = Find<Button>(window, "PlayButton");
            var trim = Find<Button>(window, "TrimButton");
            var save = Find<Button>(window, "SaveButton");
            var cancel = Find<Button>(window, "CancelButton");
            var playhead = Find<Thumb>(seek, "Playhead");
            var start = Find<Thumb>(seek, "StartHandle");
            var end = Find<Thumb>(seek, "EndHandle");
            var track = Find<Canvas>(seek, "Surface");
            Check(!play.IsEnabled && !trim.IsEnabled && !seek.IsEnabled,
                "empty player disables playback, seeking, and trimming");

            await OpenAsync(window, source);
            Check(Find<TextBlock>(window, "SongTitle").Text == title && window.Title.Contains(title),
                "opening a Unicode path displays its metadata title");
            Check(Math.Abs(seek.Duration - 12) < 0.01 && Find<TextBlock>(window, "TotalTime").Text == "0:12",
                "opening audio updates both seek duration and total time");
            Check(play.IsEnabled && trim.IsEnabled && seek.IsEnabled &&
                Find<TextBlock>(window, "FileSubtitle").Text.Contains(Path.GetFileName(source)),
                "loaded audio enables controls and identifies the opened file");
            await WaitUntilAsync(() => player.IsPlaying && player.Position > 0.15,
                "opening audio autoplays through the native VLC player");
            Check(AutomationProperties.GetName(play) == "Pause", "autoplay exposes the pause action");

            Click(play);
            await WaitUntilAsync(() => !player.IsPlaying, "play button pauses native playback");
            var pausedAt = player.Position;
            await Task.Delay(300);
            Check(Math.Abs(player.Position - pausedAt) < 0.15 && AutomationProperties.GetName(play) == "Play",
                "paused audio stays still and exposes the play action");
            Layout(surface);
            Check(track.ActualWidth > 100, "seek track is laid out at a usable width");
            Drag(playhead, -100_000);
            await WaitUntilAsync(() => player.Position < 0.15, "dragging before the track clamps the native seek to zero");
            Drag(playhead, (track.ActualWidth - 24) / 4);
            await WaitUntilAsync(() => Math.Abs(player.Position - 3) < 0.2,
                "dragging a quarter of the track seeks native playback to three seconds");
            Check(Math.Abs(seek.Position - 3) < 0.2 && Find<TextBlock>(window, "CurrentTime").Text == "0:03",
                "seeking updates the position control and current-time label");
            Click(play);
            await WaitUntilAsync(() => player.IsPlaying && player.Position > 3.2,
                "play button resumes playback from the seeked position");
            Click(play);
            await WaitUntilAsync(() => !player.IsPlaying, "resumed playback can be paused again");
            Snapshot(surface, Path.Combine(artifacts, "normal.png"));

            Click(trim);
            Layout(surface);
            Check(trim.Visibility == Visibility.Collapsed && save.Visibility == Visibility.Visible &&
                cancel.Visibility == Visibility.Visible && seek.IsTrimMode,
                "trim replaces its action with Save copy and Cancel");
            Check(start.Visibility == Visibility.Visible && end.Visibility == Visibility.Visible &&
                Find<TextBlock>(window, "SelectionLabel").Visibility == Visibility.Visible,
                "trim reveals both boundary thumbs and the selected range");
            Drag(start, (track.ActualWidth - 24) / 6);
            Drag(end, -(track.ActualWidth - 24) / 4);
            Check(Math.Abs(seek.SelectionStart - 2) < 0.01 && Math.Abs(seek.SelectionEnd - 9) < 0.01 && save.IsEnabled,
                "dragging the trim thumbs changes the selected range to two through nine seconds");
            Check(Find<TextBlock>(window, "SelectionLabel").Text.Contains("0:02.00") &&
                Find<TextBlock>(window, "SelectionLabel").Text.Contains("0:09.00"),
                "thumb changes update the visible selection times");
            Snapshot(surface, Path.Combine(artifacts, "trim.png"));

            // Narrow the preview to two seconds and exercise both stop and replay at its boundary.
            Drag(end, -(track.ActualWidth - 24) * 5 / 12);
            Drag(playhead, -100_000);
            await WaitUntilAsync(() => Math.Abs(player.Position - 2) < 0.2,
                "trim seeking clamps to the selected start");
            Click(play);
            await WaitUntilAsync(() => player.IsPlaying, "trim preview starts native playback");
            await WaitUntilAsync(() => !player.IsPlaying && AutomationProperties.GetName(play) == "Play",
                "trim preview stops automatically at its selected end");
            // Native VLC timestamps update coarsely; the UI boundary is exact, while preview
            // stopping allows one native clock update plus the dispatcher timer interval.
            Check(Math.Abs(player.Position - 4) < 0.5 && Math.Abs(seek.Position - 4) < 0.03,
                $"trim preview stops at four seconds rather than the file end (native={player.Position:F3}, UI={seek.Position:F3}, end={seek.SelectionEnd:F3})");
            Click(play);
            await WaitUntilAsync(() => player.IsPlaying && player.Position >= 2 && player.Position < 2.8,
                "replaying a finished trim preview restarts at its selected start");
            Click(play);
            await WaitUntilAsync(() => !player.IsPlaying, "replayed trim preview can be paused");

            Click(cancel);
            Check(!seek.IsTrimMode && trim.Visibility == Visibility.Visible &&
                save.Visibility == Visibility.Collapsed && cancel.Visibility == Visibility.Collapsed &&
                start.Visibility == Visibility.Collapsed && end.Visibility == Visibility.Collapsed &&
                Math.Abs(seek.Duration - 12) < 0.01,
                "Cancel restores normal playback controls and the original duration");
            Click(trim);
            Check(seek.SelectionStart == 0 && Math.Abs(seek.SelectionEnd - 12) < 0.01,
                "entering trim again resets the selection to the whole original");
            Click(cancel);

            Click(play);
            await WaitUntilAsync(() => player.IsPlaying, "normal playback resumes before the EOF seek check");
            Drag(playhead, 100_000);
            await WaitUntilAsync(() => player.HasEnded && AutomationProperties.GetName(play) == "Play",
                "seeking to the file end reaches EOF and exposes replay");
            Drag(playhead, -(track.ActualWidth - 24) / 2);
            await WaitUntilAsync(() => player.IsPlaying && player.Position >= 5.9 && player.Position < 6.8,
                "seeking after EOF restarts native playback at the requested midpoint");
            Click(play);
            await WaitUntilAsync(() => !player.IsPlaying, "playback restarted after EOF can be paused");

            Click(trim);
            Drag(start, (track.ActualWidth - 24) * 5 / 12);
            Drag(end, -(track.ActualWidth - 24) / 6);
            SetField(window, "_pendingSeek", 5.0);
            Drag(start, (track.ActualWidth - 24) / 4);
            Check(Math.Abs(seek.SelectionStart - 8) < 0.01 && Math.Abs(seek.SelectionEnd - 10) < 0.01 &&
                ReadField<double?>(window, "_pendingSeek") >= 8,
                "moving a trim start clamps a queued replay seek into the new selection");
            Drag(playhead, (track.ActualWidth - 24) / 12);
            Check(Math.Abs(ReadField<double?>(window, "_pendingSeek")!.Value - 9) < 0.01,
                "a new drag replaces the queued seek with the latest requested position");
            SetField(window, "_pendingSeek", 5.0);
            Click(play);
            await WaitUntilAsync(() => player.IsPlaying && player.Position >= 7.9 && player.Position < 8.8 &&
                ReadField<double?>(window, "_pendingSeek") is null,
                "the playback timer clamps a stale queued seek before applying it to VLC");
            Click(play);
            await WaitUntilAsync(() => !player.IsPlaying, "pending-seek regression playback can be paused");
            Click(cancel);

            await OpenAsync(window, Path.Combine(fixtureDirectory, "missing audio.wav"));
            CheckErrorState(window, "missing audio shows an error and disables stale playback controls");
            var corrupt = Path.Combine(fixtureDirectory, "壊れた audio.wav");
            await File.WriteAllTextAsync(corrupt, "This is not an audio file.");
            await OpenAsync(window, corrupt);
            CheckErrorState(window, "corrupt audio shows an error and disables stale playback controls");
            var finalSourceBytes = await File.ReadAllBytesAsync(source);
            Check(originalHash.SequenceEqual(SHA256.HashData(finalSourceBytes)),
                "playback, trimming, cancellation, and errors leave the original file unchanged");
            Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero,
                "the entire check run creates no visible window or window handle");
            await CheckCloseDuringExportAsync(window, fixtureDirectory);
            window = null;
            Console.WriteLine("Rendered UI artifacts: " + artifacts);
        }
        finally
        {
            if (window is not null)
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
            // This unique child directory contains only fixtures created by this runner.
            if (Directory.Exists(fixtureDirectory) && string.Equals(Path.GetDirectoryName(fixtureDirectory),
                artifacts, StringComparison.OrdinalIgnoreCase)) Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    private static async Task CheckCloseDuringExportAsync(MainWindow window, string fixtureDirectory)
    {
        var longer = Path.Combine(fixtureDirectory, "long silent closing source.flac");
        await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            "anullsrc=r=48000:cl=stereo", "-t", "1800", "-c:a", "flac", "-y", longer);
        var destination = Path.Combine(fixtureDirectory, "existing closing output.mp3");
        byte[] sentinel = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(destination, sentinel);
        await OpenAsync(window, longer);
        var player = ReadField<AudioPlayerService>(window, "_player");
        await WaitUntilAsync(() => player.IsPlaying, "long audio loads for the close-during-export check");
        Click(Find<Button>(window, "PlayButton"));
        await WaitUntilAsync(() => !player.IsPlaying, "export setup pauses the long audio");

        // Reproduce the state after SaveFileDialog returns, using the real backend and cancellation.
        // The native file picker itself is never opened by this automated harness.
        using var cancellation = new CancellationTokenSource();
        var encodingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = ReadField<AudioExportService>(window, "_export");
        SetField(window, "_exporting", true);
        SetField(window, "_saveCancellation", cancellation);
        var export = backend.ExportAsync(longer, destination, TimeSpan.Zero, TimeSpan.FromSeconds(1800),
            new InlineProgress(value => { if (value > 0 && value < 1) encodingStarted.TrySetResult(); }), cancellation.Token);
        SetField(window, "_activeExport", export);
        await encodingStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Check(!export.IsCompleted && Directory.EnumerateFiles(fixtureDirectory, ".museek-*").Any(),
            "the close check interrupts a real FFmpeg encode with a temporary output");

        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        var wasCancelled = false;
        try { await export; }
        catch (OperationCanceledException) { wasCancelled = true; }
        await WaitUntilAsync(() => closed, "closing waits for the active encoder before the window closes");
        Check(cancellation.IsCancellationRequested && wasCancelled,
            "closing the window cancels its active export");
        var afterClose = await File.ReadAllBytesAsync(destination);
        Check(afterClose.SequenceEqual(sentinel) && !Directory.EnumerateFiles(fixtureDirectory, ".museek-*").Any(),
            "closing during export preserves the existing destination and removes temporary outputs");
    }

    private static void CheckErrorState(MainWindow window, string description)
    {
        var seek = Find<RangeSeekBar>(window, "SeekBar");
        Check(Find<TextBlock>(window, "SongTitle").Text.Contains("Couldn't open") &&
            !string.IsNullOrWhiteSpace(Find<TextBlock>(window, "StatusText").Text) &&
            !Find<Button>(window, "PlayButton").IsEnabled && !Find<Button>(window, "TrimButton").IsEnabled &&
            !seek.IsEnabled && seek.Duration == 0 && !seek.IsTrimMode &&
            Find<Button>(window, "SaveButton").Visibility == Visibility.Collapsed, description);
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class
        => root.FindName(name) as T ?? throw new Exception("Control not found: " + name);

    private static Task OpenAsync(MainWindow window, string path)
        => (Task)(typeof(MainWindow).GetMethod("OpenAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(window, [path]) ?? throw new Exception("MainWindow.OpenAsync was not found."));

    private static T ReadField<T>(MainWindow window, string name)
        => (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("MainWindow field not found: " + name)).GetValue(window)!;

    private static void SetField(MainWindow window, string name, object? value)
        => (typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("MainWindow field not found: " + name)).SetValue(window, value);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static void Drag(Thumb thumb, double pixels)
        => thumb.RaiseEvent(new DragDeltaEventArgs(pixels, 0) { RoutedEvent = Thumb.DragDeltaEvent });

    private static void Layout(FrameworkElement surface)
    {
        surface.Measure(new Size(570, 410));
        surface.Arrange(new Rect(0, 0, 570, 410));
        surface.UpdateLayout();
    }

    private static void Snapshot(FrameworkElement surface, string path)
    {
        Layout(surface);
        var bitmap = new RenderTargetBitmap(570, 410, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[570 * 410 * 4];
        bitmap.CopyPixels(pixels, 570 * 4, 0);
        var colors = new HashSet<int>();
        for (var offset = 0; offset < pixels.Length; offset += 4) colors.Add(BitConverter.ToInt32(pixels, offset));
        Check(colors.Count > 16, Path.GetFileName(path) + " renders real controls and text");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(40);
        Check(condition(), description);
    }

    private static async Task RunFfmpegAsync(params string[] arguments)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        var start = new ProcessStartInfo(File.Exists(bundled) ? bundled : "ffmpeg.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Exception("Could not start FFmpeg.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await output;
        if (process.ExitCode != 0) throw new Exception("FFmpeg fixture creation failed: " + await error);
        await error;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

}
