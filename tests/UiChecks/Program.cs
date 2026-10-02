using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;
using Museek;
using Museek.Controls;
using Museek.Models;
using Museek.Services;

namespace Museek.UiChecks;

internal static class Program
{
    private static int _checks;
    private const string FixtureArtist = "Museek artist — 音楽";
    private const string FixtureAlbum = "Museek album";

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
                Console.WriteLine($"All {_checks} silent WPF UI and native playback checks passed.");
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
                "-metadata", "title=" + title, "-metadata", "artist=" + FixtureArtist,
                "-metadata", "album=" + FixtureAlbum, "-y", source);
            var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var untagged = Path.Combine(fixtureDirectory, "metadata-free audio.wav");
            await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-i", source,
                "-map_metadata", "-1", "-c:a", "copy", "-y", untagged);
            var artistOnly = Path.Combine(fixtureDirectory, "artist-only audio.wav");
            await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-i", untagged,
                "-c:a", "copy", "-metadata", "artist=Artist only", "-y", artistOnly);
            var albumOnly = Path.Combine(fixtureDirectory, "album-only audio.wav");
            await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-i", untagged,
                "-c:a", "copy", "-metadata", "album=Album only", "-y", albumOnly);
            var cover = Path.Combine(fixtureDirectory, "cover image.png");
            await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                "color=c=0x238fa8:s=64x64:d=1", "-frames:v", "1", "-pix_fmt", "rgb24", "-update", "1", "-y", cover);
            var covered = Path.Combine(fixtureDirectory, "音楽 embedded cover.mp3");
            await RunFfmpegAsync("-hide_banner", "-loglevel", "error", "-i", source, "-i", cover,
                "-map", "0:a:0", "-map", "1:v:0", "-c:a", "libmp3lame", "-q:a", "2", "-c:v", "copy",
                "-id3v2_version", "3", "-disposition:v:0", "attached_pic", "-metadata", "title=Covered UI fixture",
                "-metadata", "artist=Covered artist", "-metadata", "album=Covered album",
                "-metadata:s:v", "title=Album cover", "-metadata:s:v", "comment=Cover (front)", "-y", covered);

            var settingsPath = Path.Combine(fixtureDirectory, "isolated settings.json");
            var settings = new AppSettingsService(settingsPath);
            var contextMenuRegistration = new List<bool>();
            window = new MainWindow(initialPath: untagged, settings: settings,
                editTagsContextMenuRegistration: enabled => contextMenuRegistration.Add(enabled));
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
            var surface = new Border
            {
                Child = content, Background = window.Background,
                Width = window.Width, Height = Math.Max(1, window.Height - 31)
            };
            TextElement.SetFontFamily(surface, window.FontFamily);
            TextElement.SetFontSize(surface, window.FontSize);
            TextElement.SetForeground(surface, window.Foreground);
            Layout(surface);

            var seek = Find<RangeSeekBar>(window, "SeekBar");
            var play = Find<Button>(window, "PlayButton");
            var stop = Find<Button>(window, "StopButton");
            var trim = Find<Button>(window, "TrimButton");
            var save = Find<Button>(window, "SaveButton");
            var cancel = Find<Button>(window, "CancelButton");
            var playhead = Find<Thumb>(seek, "Playhead");
            var start = Find<Thumb>(seek, "StartHandle");
            var end = Find<Thumb>(seek, "EndHandle");
            var track = Find<Canvas>(seek, "Surface");
            Check(window.FindName("OpenButton") is null && window.FindName("FileSubtitle") is null,
                "the simplified player omits the Open file button and filename subtitle");
            Check(window.FindName("ModeLabel") is null,
                "the player omits the mode slogan in both playback and trim layouts");
            CheckArtworkPlaceholder(window, "an empty player shows the artwork placeholder");
            CheckSongMetadata(window, null, null, "an empty player collapses the artist and album lines");
            Check(!play.IsEnabled && !stop.IsEnabled && !trim.IsEnabled && !seek.IsEnabled,
                "empty player disables playback, Stop, seeking, and trimming");
            CheckMenus(window, surface, artifacts, settings, settingsPath, contextMenuRegistration);
            CheckContextMenuRegistration(fixtureDirectory);
            CheckContextMenuSelection(source, covered);
            await CheckTagEditorAsync(source, covered, fixtureDirectory, artifacts);

            Check(window.AcceptOpenRequest(covered) && window.AcceptOpenRequest(source) &&
                ReadField<string?>(window, "_queuedOpenPath") == source && ReadField<string?>(window, "_sourcePath") is null,
                "launches received before Loaded queue only the latest file");
            InvokeWindowMethod(window, "Window_Loaded", window, new RoutedEventArgs());
            Check(!stop.IsEnabled && !play.IsEnabled && !seek.IsEnabled,
                "loading audio disables Stop and the other playback controls");
            await WaitUntilAsync(() => ReadField<string?>(window, "_sourcePath") == source &&
                !ReadField<bool>(window, "_loading"),
                "Loaded opens the latest queued launch instead of the constructor's initial file");
            Check(Find<TextBlock>(window, "SongTitle").Text == title && window.Title.Contains(title),
                "opening a Unicode path displays its metadata title");
            CheckSongMetadata(window, FixtureArtist, FixtureAlbum,
                "opening tagged audio displays its Unicode artist and album");
            CheckMetadataLayout(window, surface, "artist and album appear in order below the title");
            Check(Math.Abs(seek.Duration - 12) < 0.01 && Find<TextBlock>(window, "TotalTime").Text == "0:12",
                "opening audio updates both seek duration and total time");
            Check(play.IsEnabled && stop.IsEnabled && trim.IsEnabled && seek.IsEnabled,
                "loaded audio enables playback, Stop, seeking, and trimming");
            CheckArtworkPlaceholder(window, "audio without embedded artwork keeps the placeholder");
            Check(window.AcceptOpenRequest(null) && ReadField<string?>(window, "_sourcePath") == source &&
                ReadField<string?>(window, "_queuedOpenPath") is null,
                "an activation-only launch preserves the loaded song and clears no source state");
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
            Check(window.FindName("ModeLabel") is null,
                "entering trim keeps its slogan absent");
            CheckSongMetadata(window, FixtureArtist, FixtureAlbum,
                "entering trim preserves the displayed artist and album");
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
            CheckSongMetadata(window, FixtureArtist, FixtureAlbum,
                "cancelling trim preserves the displayed artist and album");
            Click(trim);
            Check(seek.SelectionStart == 0 && Math.Abs(seek.SelectionEnd - 12) < 0.01,
                "entering trim again resets the selection to the whole original");
            Click(cancel);

            Click(play);
            await WaitUntilAsync(() => player.IsPlaying, "normal playback resumes before the EOF seek check");
            Drag(playhead, 100_000);
            await WaitUntilAsync(() => player.HasEnded && AutomationProperties.GetName(play) == "Play",
                "seeking to the file end reaches EOF and exposes replay",
                () => PlaybackState(window));
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

            await CheckStopAsync(window, surface);
            await CheckQueuedEofTrimAsync(window, surface);
            await CheckQueuedOpenRequestsAsync(window, source, untagged, covered);
            await CheckArtworkChangesAsync(window, source, untagged, artistOnly, albumOnly, covered, surface, artifacts);
            await OpenAsync(window, Path.Combine(fixtureDirectory, "missing audio.wav"));
            CheckErrorState(window, "missing audio shows an error and disables stale playback controls");
            CheckArtworkPlaceholder(window, "a missing file clears the previous song's artwork");
            CheckSongMetadata(window, null, null, "a missing file clears the previous artist and album");
            var corrupt = Path.Combine(fixtureDirectory, "壊れた audio.wav");
            await File.WriteAllTextAsync(corrupt, "This is not an audio file.");
            await OpenAsync(window, covered);
            await WaitForArtworkAsync(window, "embedded artwork can load again after an error");
            CheckSongMetadata(window, "Covered artist", "Covered album",
                "recovering from an error restores the covered song's artist and album");
            await OpenAsync(window, corrupt);
            CheckErrorState(window, "corrupt audio shows an error and disables stale playback controls");
            CheckArtworkPlaceholder(window, "a corrupt file clears the previous song's artwork");
            CheckSongMetadata(window, null, null, "a corrupt file clears the previous artist and album");
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

    private static async Task CheckStopAsync(MainWindow window, FrameworkElement surface)
    {
        var player = ReadField<AudioPlayerService>(window, "_player");
        var seek = Find<RangeSeekBar>(window, "SeekBar");
        var play = Find<Button>(window, "PlayButton");
        var stop = Find<Button>(window, "StopButton");
        var trim = Find<Button>(window, "TrimButton");
        var cancel = Find<Button>(window, "CancelButton");
        var playhead = Find<Thumb>(seek, "Playhead");
        var start = Find<Thumb>(seek, "StartHandle");
        var end = Find<Thumb>(seek, "EndHandle");
        Layout(surface);
        var trackWidth = Find<Canvas>(seek, "Surface").ActualWidth - 24;

        Click(play);
        await WaitUntilAsync(() => player.IsPlaying, "Stop setup resumes native playback");
        Click(stop);
        await CheckStoppedAsync(window, 0, "0:00", "Stop during playback resets the song and stays at zero");
        Click(play);
        await WaitUntilAsync(() => player.IsPlaying && player.Position > 0.15 && player.Position < 1.2,
            "Play after Stop restarts the song from its beginning");
        Click(play);
        await WaitUntilAsync(() => !player.IsPlaying, "Stop setup pauses native playback");
        Drag(playhead, -100_000);
        Drag(playhead, trackWidth / 4);
        await WaitUntilAsync(() => Math.Abs(player.Position - 3) < 0.2,
            "paused Stop setup seeks away from the song start");
        Click(stop);
        await CheckStoppedAsync(window, 0, "0:00", "Stop while paused resets the song to zero");

        Drag(playhead, trackWidth / 3);
        await Task.Delay(200);
        Check(!player.IsPlaying && Math.Abs(seek.Position - 4) < 0.01 &&
            Find<TextBlock>(window, "CurrentTime").Text == "0:04",
            "seeking after Stop moves the cursor without starting playback");
        Click(play);
        Click(play); // Both clicks precede a dispatcher tick: the queued seek has not applied.
        await CheckStoppedAsync(window, 4, "0:04", "rapid Play then Pause preserves a stopped song seek");
        Click(play);
        await WaitUntilAsync(() => player.IsPlaying && player.Position >= 3.9 && player.Position < 4.8,
            "Play after a stopped seek starts at the requested position");
        Drag(playhead, 100_000);
        await WaitUntilAsync(() => player.HasEnded && AutomationProperties.GetName(play) == "Play",
            "Stop setup reaches the end of the song", () => PlaybackState(window));
        Click(stop);
        await CheckStoppedAsync(window, 0, "0:00", "Stop after EOF clears the ended state and resets the song");

        Click(trim);
        Drag(start, trackWidth / 4);
        Drag(end, -trackWidth / 4);
        Check(Math.Abs(seek.SelectionStart - 3) < 0.01 && Math.Abs(seek.SelectionEnd - 9) < 0.01,
            "trim Stop setup selects three through nine seconds");
        Click(play);
        await WaitUntilAsync(() => player.IsPlaying && player.Position >= 2.9 && player.Position < 3.8,
            "a stopped trim preview starts at the selected start");
        Click(stop);
        await CheckStoppedAsync(window, 3, "0:03", "Stop during trim resets exactly to its selected start");
        Drag(start, trackWidth / 6);
        await CheckStoppedAsync(window, 5, "0:05", "changing the trim start after Stop clamps the stopped cursor");
        Drag(playhead, trackWidth / 12);
        await Task.Delay(200);
        Check(!player.IsPlaying && Math.Abs(seek.Position - 6) < 0.01 &&
            Find<TextBlock>(window, "CurrentTime").Text == "0:06",
            "seeking after a trim Stop moves the cursor within the selection silently");
        Click(play);
        Click(play);
        await CheckStoppedAsync(window, 6, "0:06", "rapid Play then Pause preserves a stopped trim seek");
        Click(play);
        await WaitUntilAsync(() => player.IsPlaying && player.Position >= 5.9 && player.Position < 6.8,
            "Play after a stopped trim seek starts at the newly requested position");
        Click(stop);
        await CheckStoppedAsync(window, 5, "0:05", "a later trim Stop returns to the updated selection start");
        Click(cancel);
        Click(stop);
        await CheckStoppedAsync(window, 0, "0:00", "Stop after cancelling trim resets to the whole song start");
        CheckSongMetadata(window, FixtureArtist, FixtureAlbum,
            "Stop and seeking after Stop preserve the current song's artist and album");
    }

    private static async Task CheckStoppedAsync(MainWindow window, double position, string time, string description)
    {
        var player = ReadField<AudioPlayerService>(window, "_player");
        await WaitUntilAsync(() => !player.IsPlaying && !player.HasEnded && player.Position == 0,
            description + " (native player stopped)");
        await Task.Delay(200); // More than two timer ticks: a stale native time must not overwrite the reset.
        Check(Math.Abs(Find<RangeSeekBar>(window, "SeekBar").Position - position) < 0.01 &&
            Find<TextBlock>(window, "CurrentTime").Text == time &&
            AutomationProperties.GetName(Find<Button>(window, "PlayButton")) == "Play", description);
    }

    private static async Task CheckQueuedEofTrimAsync(MainWindow window, FrameworkElement surface)
    {
        var player = ReadField<AudioPlayerService>(window, "_player");
        var timer = ReadField<DispatcherTimer>(window, "_timer");
        var seek = Find<RangeSeekBar>(window, "SeekBar");
        var play = Find<Button>(window, "PlayButton");
        Layout(surface);
        var trackWidth = Find<Canvas>(seek, "Surface").ActualWidth - 24;
        Click(play);
        await WaitUntilAsync(() => player.IsPlaying && ReadField<double?>(window, "_pendingSeek") is null,
            "queued EOF trim setup starts native playback");
        Drag(Find<Thumb>(seek, "Playhead"), 100_000);
        await WaitUntilAsync(() => player.HasEnded && AutomationProperties.GetName(play) == "Play",
            "queued EOF trim setup reaches real native EOF", () => PlaybackState(window));

        // Freeze the timer to recreate EOF arriving before a queued seek is consumed.
        timer.Stop();
        try
        {
            Click(Find<Button>(window, "TrimButton"));
            SetField(window, "_pendingSeek", seek.Duration);
            SetField(window, "_wantsPlayback", true);
            Drag(Find<Thumb>(seek, "EndHandle"), -trackWidth / 4);
            Check(player.HasEnded && Math.Abs(seek.SelectionEnd - 9) < 0.01 &&
                Math.Abs(ReadField<double?>(window, "_pendingSeek")!.Value - 9) < 0.01,
                "moving the trim end clamps an unconsumed EOF seek inside the ended file");
        }
        finally { timer.Start(); }

        await WaitUntilAsync(() => !player.IsPlaying && !player.HasEnded &&
            ReadField<double?>(window, "_pendingSeek") is null && Math.Abs(player.Position - 9) < 0.5 &&
            Math.Abs(seek.Position - 9) < 0.03 && AutomationProperties.GetName(play) == "Play",
            "an EOF seek clamped into trim restarts, reaches the selected end, and exposes Play",
            () => PlaybackState(window));
        Click(Find<Button>(window, "CancelButton"));
    }

    private static void CheckMenus(MainWindow window, FrameworkElement surface, string artifacts,
        AppSettingsService settings, string settingsPath, List<bool> contextMenuRegistration)
    {
        var menu = Find<Menu>(window, "MenuBar");
        var tools = Find<MenuItem>(window, "ToolsMenu");
        var help = Find<MenuItem>(window, "HelpMenu");
        var toolsEntries = tools.Items.OfType<MenuItem>().ToArray();
        var helpEntries = help.Items.OfType<MenuItem>().ToArray();
        var singleWindow = Find<MenuItem>(window, "SingleWindowModeMenuItem");
        var editTags = Find<MenuItem>(window, "EditTagsContextMenuItem");
        Check(menu.Items.Count == 2 && ReferenceEquals(menu.Items[0], tools) &&
            ReferenceEquals(menu.Items[1], help) &&
            (tools.Header?.ToString() ?? "").Replace("_", "") == "Tools" &&
            toolsEntries.Length == 3 && toolsEntries[0].Header?.ToString() == "Choose Museek as default…" &&
            ReferenceEquals(toolsEntries[1], singleWindow) && singleWindow.IsCheckable &&
            singleWindow.Header?.ToString() == "Single-window mode" && ReferenceEquals(toolsEntries[2], editTags) &&
            editTags.IsCheckable && editTags.Header?.ToString() == "'Edit Tags' context menu",
            "Tools contains Default Apps and checkable Single-window and Edit Tags settings");
        Check((help.Header?.ToString() ?? "").Replace("_", "") == "Help" && helpEntries.Length == 1 &&
            helpEntries[0].Header?.ToString() == "About Museek",
            "Help contains only About Museek");
        Layout(surface);
        Check(menu.TranslatePoint(new Point(), surface).Y <= 8 && menu.ActualHeight > 0,
            "Tools and Help sit at the top of the client content below the native title bar");
        var entries = toolsEntries.Concat(helpEntries).ToArray();
        foreach (var entry in entries) entry.ApplyTemplate();
        Check(IsDark(menu.Background) && IsLight(menu.Foreground) && IsLight(tools.Foreground) && IsLight(help.Foreground) &&
            entries.All(entry => IsDark(entry.Background) && IsLight(entry.Foreground)),
            "both menus and their entries use matching dark backgrounds and readable text");

        var helpBody = LayoutClosedMenu(help);
        Snapshot(helpBody, Path.Combine(artifacts, "help-menu.png"), layout: false);
        var toolsBody = LayoutClosedMenu(tools);
        Check(entries.All(entry => entry.ActualWidth > 100 && entry.ActualHeight > 0),
            "both closed menu popups lay out their action labels offscreen");

        Check(!settings.SingleWindowMode && !window.SingleWindowMode && !singleWindow.IsChecked,
            "an isolated fresh settings file initializes single-window mode unchecked");
        var glyph = singleWindow.Template.FindName("CheckGlyph", singleWindow) as System.Windows.Shapes.Path
            ?? throw new Exception("Single-window mode's visible checkmark was not found.");
        Check(glyph.Visibility != Visibility.Visible, "unchecked single-window mode hides its checkmark");
        Snapshot(toolsBody, Path.Combine(artifacts, "tools-menu.png"), layout: false);

        var changes = 0;
        window.SingleWindowModeChanged += (_, _) => changes++;
        var toggle = new MenuItemAutomationPeer(singleWindow).GetPattern(PatternInterface.Toggle) as IToggleProvider
            ?? throw new Exception("Single-window mode's WPF toggle provider was not found.");
        toggle.Toggle();
        toolsBody = LayoutClosedMenu(tools);
        Check(singleWindow.IsChecked && window.SingleWindowMode && ReadSingleWindowMode(settingsPath) && changes == 1,
            "toggling the menu enables and persists single-window mode and raises its change event");
        Check(glyph.Visibility == Visibility.Visible && HasRenderedInk(glyph),
            "checked single-window mode renders its actual checkmark geometry");
        Snapshot(toolsBody, Path.Combine(artifacts, "tools-menu-checked.png"), layout: false);
        toggle.Toggle();
        Check(!singleWindow.IsChecked && !window.SingleWindowMode && !ReadSingleWindowMode(settingsPath) &&
            changes == 2 && glyph.Visibility != Visibility.Visible,
            "toggling the menu off persists disabled mode and removes its checkmark");
        var tagGlyph = editTags.Template.FindName("CheckGlyph", editTags) as System.Windows.Shapes.Path
            ?? throw new Exception("Edit Tags mode's visible checkmark was not found.");
        Check(!settings.EditTagsContextMenu && !editTags.IsChecked && tagGlyph.Visibility != Visibility.Visible,
            "a fresh isolated settings file initializes Edit Tags context menu unchecked");
        var tagToggle = new MenuItemAutomationPeer(editTags).GetPattern(PatternInterface.Toggle) as IToggleProvider
            ?? throw new Exception("Edit Tags context menu's WPF toggle provider was not found.");
        tagToggle.Toggle();
        toolsBody = LayoutClosedMenu(tools);
        var reloaded = new AppSettingsService(settingsPath);
        reloaded.Reload();
        Check(editTags.IsChecked && reloaded.EditTagsContextMenu && !reloaded.SingleWindowMode &&
            contextMenuRegistration.SequenceEqual([true]) && tagGlyph.Visibility == Visibility.Visible && HasRenderedInk(tagGlyph),
            "enabling Edit Tags persists its setting, calls registration once, and renders a tick");
        Snapshot(toolsBody, Path.Combine(artifacts, "tools-edit-tags-checked.png"), layout: false);
        tagToggle.Toggle();
        reloaded.Reload();
        Check(!editTags.IsChecked && !reloaded.EditTagsContextMenu && contextMenuRegistration.SequenceEqual([true, false]) &&
            tagGlyph.Visibility != Visibility.Visible,
            "disabling Edit Tags unregisters its context action and removes the persisted tick");
        Check(!tools.IsSubmenuOpen && !help.IsSubmenuOpen && new WindowInteropHelper(window).Handle == IntPtr.Zero,
            "menu toggles and renders create no native popup or player window");
    }

    private static async Task CheckQueuedOpenRequestsAsync(MainWindow window, string source, string untagged, string covered)
    {
        SetField(window, "_exporting", true);
        try
        {
            Check(window.AcceptOpenRequest(untagged) && window.AcceptOpenRequest(covered) &&
                ReadField<string?>(window, "_sourcePath") == source && ReadField<string?>(window, "_queuedOpenPath") == covered,
                "launches during saving retain the current song and queue only the latest replacement");
            InvokeWindowMethod(window, "OpenQueuedFile");
            Check(ReadField<string?>(window, "_queuedOpenPath") == covered && ReadField<string?>(window, "_sourcePath") == source,
                "queued audio cannot open while export cleanup is still busy");
        }
        finally { SetField(window, "_exporting", false); }
        InvokeWindowMethod(window, "OpenQueuedFile");
        await WaitUntilAsync(() => ReadField<string?>(window, "_sourcePath") == covered &&
            !ReadField<bool>(window, "_loading") && ReadField<AudioPlayerService>(window, "_player").IsPlaying,
            "saving cleanup consumes the latest queued file and starts its native playback");
        Check(ReadField<string?>(window, "_queuedOpenPath") is null &&
            Find<TextBlock>(window, "SongTitle").Text == "Covered UI fixture",
            "opening a queued file clears its queue and displays the new title");

        SetField(window, "_fileDialogOpen", true);
        try
        {
            Check(window.AcceptOpenRequest(source) && window.AcceptOpenRequest(untagged) && window.AcceptOpenRequest(null) &&
                ReadField<string?>(window, "_queuedOpenPath") == untagged && ReadField<string?>(window, "_sourcePath") == covered,
                "a busy file picker retains the latest file request across activation-only launches");
            InvokeWindowMethod(window, "OpenQueuedFile");
            Check(ReadField<string?>(window, "_queuedOpenPath") == untagged,
                "queued audio waits until the file picker finishes");
        }
        finally { SetField(window, "_fileDialogOpen", false); }
        InvokeWindowMethod(window, "OpenQueuedFile");
        await WaitUntilAsync(() => ReadField<string?>(window, "_sourcePath") == untagged &&
            !ReadField<bool>(window, "_loading") && ReadField<string?>(window, "_queuedOpenPath") is null,
            "file picker cleanup opens and clears its latest queued request");
        CheckSongMetadata(window, null, null, "a queued untagged song clears the replaced song's metadata");
    }

    private static void CheckContextMenuRegistration(string fixtureDirectory)
    {
        var registryPath = @"Software\Museek\Tests\TagMenu-" + Guid.NewGuid().ToString("N");
        var executable = Path.Combine(fixtureDirectory, "Museek.exe");
        File.WriteAllBytes(executable, [0]);
        try
        {
            using var isolatedRoot = Registry.CurrentUser.CreateSubKey(registryPath);
            using (var unrelated = isolatedRoot.CreateSubKey(@"Software\Classes\SystemFileAssociations\.mp3\shell\Unrelated.Test"))
                unrelated.SetValue("", "Preserve this unrelated command");
            Check(!TagContextMenuService.IsRegistered(executable, isolatedRoot),
                "a fresh isolated registry contains no Edit Tags action");
            TagContextMenuService.Register(executable, isolatedRoot);
            Check(TagContextMenuService.IsRegistered(executable, isolatedRoot),
                "registering Edit Tags is detectable under the isolated registry");
            foreach (var extension in WindowsIntegrationService.SupportedExtensions)
            {
                using var verb = isolatedRoot.OpenSubKey($@"Software\Classes\SystemFileAssociations\{extension}\shell\Museek.EditTags");
                using var command = verb?.OpenSubKey("command");
                Check(verb?.GetValue("")?.ToString() == "Edit Tags" &&
                    command?.GetValue("DelegateExecute")?.ToString()?.Equals(TagContextMenuService.CommandClassId.ToString("B"),
                        StringComparison.OrdinalIgnoreCase) == true,
                    extension + " has an Edit Tags command delegated to the full-selection handler");
            }
            using (var wildcard = isolatedRoot.OpenSubKey(@"Software\Classes\*\shell\Museek.EditTags"))
                Check(wildcard is null, "Edit Tags registration does not add an all-files wildcard command");
            TagContextMenuService.Register(executable, isolatedRoot);
            TagContextMenuService.Unregister(executable, isolatedRoot);
            Check(!TagContextMenuService.IsRegistered(executable, isolatedRoot),
                "idempotent isolated registration can be fully removed");
            using var retained = isolatedRoot.OpenSubKey(@"Software\Classes\SystemFileAssociations\.mp3\shell\Unrelated.Test");
            Check(retained?.GetValue("")?.ToString() == "Preserve this unrelated command",
                "removing Museek's context action preserves unrelated Explorer commands");
        }
        finally
        {
            // Exactly this random test namespace is removed. Production Classes keys are never written.
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
        }
    }

    private static async Task CheckTagEditorAsync(string source, string covered, string fixtureDirectory, string artifacts)
    {
        var first = Path.Combine(fixtureDirectory, "editor first.wav");
        var second = Path.Combine(fixtureDirectory, "editor second.mp3");
        File.Copy(source, first);
        File.Copy(covered, second);
        var service = new AudioTagService();
        var beforeFirst = await service.ReadAsync(first);
        var beforeSecond = await service.ReadAsync(second);
        var editor = new TagEditorWindow([first, second]);
        try
        {
            await editor.LoadAsync();
            Check(!editor.IsVisible && new WindowInteropHelper(editor).Handle == IntPtr.Zero,
                "loading the tag editor for a batch creates no native window");
            var fieldNames = new[] { "Title", "Artist", "Album", "AlbumArtist", "Genre", "Year", "TrackNumber", "DiscNumber", "Comment" };
            Check(fieldNames.All(name => !Find<CheckBox>(editor, name + "Apply").IsChecked.GetValueOrDefault()) &&
                Find<Button>(editor, "SaveTagsButton").IsEnabled,
                "batch editing starts with every field unselected so mixed file values are preserved");
            var keep = editor.BuildPatch();
            Check(keep.Title is null && keep.Artist is null && keep.Album is null && keep.AlbumArtist is null &&
                keep.Genre is null && keep.Year is null && keep.TrackNumber is null && keep.DiscNumber is null &&
                keep.Comment is null && keep.ArtworkAction == ArtworkAction.Keep,
                "an untouched editor builds an empty patch that keeps all tags and artwork");
            Find<CheckBox>(editor, "GenreApply").IsChecked = true;
            Find<TextBox>(editor, "GenreValue").Text = "Editor batch genre 音楽";
            Find<CheckBox>(editor, "YearApply").IsChecked = true;
            Find<TextBox>(editor, "YearValue").Text = "2026";
            Find<TextBox>(editor, "ArtistValue").Text = "Typed but unchecked";
            Find<CheckBox>(editor, "ArtistApply").IsChecked = false;
            var patch = editor.BuildPatch();
            Check(patch.Genre == "Editor batch genre 音楽" && patch.Year == 2026 && patch.Title is null &&
                patch.Artist is null && patch.Album is null && patch.ArtworkAction == ArtworkAction.Keep,
                "the editor builds a patch from only checked fields");
            Find<TextBox>(editor, "YearValue").Text = "not a year";
            var invalidRejected = false;
            try { editor.BuildPatch(); }
            catch (ArgumentException) { invalidRejected = true; }
            catch (FormatException) { invalidRejected = true; }
            catch (InvalidDataException) { invalidRejected = true; }
            Check(invalidRejected, "checked numeric fields reject invalid input before saving");
            Find<TextBox>(editor, "YearValue").Text = "10000";
            var excessiveYearRejected = false;
            try { editor.BuildPatch(); }
            catch (ArgumentException) { excessiveYearRejected = true; }
            Check(excessiveYearRejected, "the editor rejects a year above 9999 before saving");
            Find<TextBox>(editor, "YearValue").Text = "2026";

            var content = (UIElement)editor.Content;
            editor.Content = null;
            var surface = new Border { Child = content, Background = editor.Background, Resources = editor.Resources,
                Width = editor.Width, Height = Math.Max(1, editor.Height - 31) };
            TextElement.SetFontFamily(surface, editor.FontFamily);
            TextElement.SetFontSize(surface, editor.FontSize);
            TextElement.SetForeground(surface, editor.Foreground);
            Layout(surface);
            Check(Find<FrameworkElement>(editor, "FieldsPanel").ActualHeight > 0 &&
                Find<FrameworkElement>(editor, "FilesList").ActualHeight > 0 &&
                Find<Button>(editor, "SaveTagsButton").ActualWidth > 40,
                "tag editor renders its selected-file list, tag fields, and save controls");
            Snapshot(surface, Path.Combine(artifacts, "edit-tags-batch.png"));
            await editor.SaveAsync();
            var afterFirst = await service.ReadAsync(first);
            var afterSecond = await service.ReadAsync(second);
            Check(afterFirst.Genre == patch.Genre && afterSecond.Genre == patch.Genre &&
                afterFirst.Year == 2026 && afterSecond.Year == 2026 &&
                afterFirst.Title == beforeFirst.Title && afterSecond.Title == beforeSecond.Title &&
                afterFirst.Artist == beforeFirst.Artist && afterSecond.Artist == beforeSecond.Artist &&
                afterFirst.Album == beforeFirst.Album && afterSecond.Album == beforeSecond.Album,
                "saving a batch updates selected fields while preserving each file's different title, artist, and album");
            Check(afterSecond.ArtworkBytes?.SequenceEqual(beforeSecond.ArtworkBytes ?? []) == true &&
                afterFirst.ArtworkBytes is null && !editor.IsVisible && new WindowInteropHelper(editor).Handle == IntPtr.Zero,
                "batch saving keeps per-file artwork and stays offscreen");
            Click(Find<Button>(editor, "RemoveArtworkButton"));
            Check(editor.BuildPatch().ArtworkAction == ArtworkAction.Remove &&
                Find<Image>(editor, "ArtworkImage").Source is null,
                "the editor Remove action requests removal and updates its artwork preview");
            Click(Find<Button>(editor, "KeepArtworkButton"));
            Check(editor.BuildPatch().ArtworkAction == ArtworkAction.Keep,
                "Keep original artwork cancels an unsaved removal request");
            Click(Find<Button>(editor, "RemoveArtworkButton"));
            await editor.SaveAsync();
            Check((await service.ReadAsync(first)).ArtworkBytes is null && (await service.ReadAsync(second)).ArtworkBytes is null,
                "saving the editor's Remove action clears artwork from the selected batch");
            var selectedArtwork = await File.ReadAllBytesAsync(Path.Combine(fixtureDirectory, "cover image.png"));
            editor.SetArtwork(selectedArtwork);
            var artworkPatch = editor.BuildPatch();
            Check(artworkPatch.ArtworkAction == ArtworkAction.Replace &&
                artworkPatch.ArtworkBytes?.SequenceEqual(selectedArtwork) == true &&
                Find<Image>(editor, "ArtworkImage").Source is BitmapSource { PixelWidth: > 0 },
                "choosing new artwork previews it and builds an artwork-only replacement patch");
            Snapshot(surface, Path.Combine(artifacts, "edit-tags-artwork.png"));
            await editor.SaveAsync();
            Check((await service.ReadAsync(first)).ArtworkBytes?.SequenceEqual(selectedArtwork) == true &&
                (await service.ReadAsync(second)).ArtworkBytes?.SequenceEqual(selectedArtwork) == true &&
                (await service.ReadAsync(second)).Title == beforeSecond.Title,
                "saving artwork through the editor embeds it in every selected file and keeps their tags");
        }
        finally { editor.Close(); }

        var single = new TagEditorWindow([second]);
        try
        {
            await single.LoadAsync();
            Check(Find<TextBox>(single, "TitleValue").Text == beforeSecond.Title &&
                Find<TextBox>(single, "ArtistValue").Text == beforeSecond.Artist &&
                Find<TextBox>(single, "AlbumValue").Text == beforeSecond.Album,
                "a single-file editor preloads its actual title, artist, and album");
            Check(Find<Image>(single, "ArtworkImage").Source is BitmapSource { PixelWidth: > 0 },
                "the single-file editor previews its embedded album artwork");
            Find<CheckBox>(single, "TitleApply").IsChecked = true;
            Find<TextBox>(single, "TitleValue").Text = "";
            Find<CheckBox>(single, "TrackNumberApply").IsChecked = true;
            Find<TextBox>(single, "TrackNumberValue").Text = "";
            var clear = single.BuildPatch();
            Check(clear.Title == "" && clear.TrackNumber == 0 && clear.Artist is null && clear.ArtworkAction == ArtworkAction.Keep,
                "checked blank text and number fields request clearing while unchecked values remain untouched");
            var cancelHash = SHA256.HashData(await File.ReadAllBytesAsync(second));
            Click(Find<Button>(single, "CancelTagsButton"));
            Check(SHA256.HashData(await File.ReadAllBytesAsync(second)).SequenceEqual(cancelHash),
                "cancelling unsaved tag edits preserves the audio file byte-for-byte");
        }
        finally { single.Close(); }
        await CheckTagEditorCloseDuringLoadAsync(first, fixtureDirectory);
    }

    private static async Task CheckTagEditorCloseDuringLoadAsync(string source, string fixtureDirectory)
    {
        var paths = Enumerable.Range(0, 12).Select(index => Path.Combine(fixtureDirectory, $"close during load {index}.wav")).ToArray();
        foreach (var path in paths) File.Copy(source, path);
        var before = await Task.WhenAll(paths.Select(async path => SHA256.HashData(await File.ReadAllBytesAsync(path))));
        foreach (var useCancelButton in new[] { true, false })
        {
            var editor = new TagEditorWindow(paths);
            var closed = false;
            editor.Closed += (_, _) => closed = true;
            var loading = editor.LoadAsync();
            Check(!loading.IsCompleted && !Find<Button>(editor, "SaveTagsButton").IsEnabled,
                "the close-during-load check interrupts active asynchronous tag reading");
            if (useCancelButton) Click(Find<Button>(editor, "CancelTagsButton"));
            else editor.Close();
            await loading;
            await WaitUntilAsync(() => closed,
                (useCancelButton ? "Cancel" : "Close") + " finishes active tag reading before closing the editor");
            Check(!editor.IsVisible && new WindowInteropHelper(editor).Handle == IntPtr.Zero,
                "cancelling or closing a loading editor creates no native window");
            var after = await Task.WhenAll(paths.Select(async path => SHA256.HashData(await File.ReadAllBytesAsync(path))));
            Check(after.Zip(before).All(pair => pair.First.SequenceEqual(pair.Second)) &&
                !Directory.EnumerateFiles(fixtureDirectory, ".museek-tags-*").Any(),
                "closing during tag loading preserves every file byte-for-byte and leaves no temporary outputs");
        }
    }

    private static void CheckContextMenuSelection(string first, string second)
    {
        var paths = new[] { first, second };
        var itemIds = new IntPtr[paths.Length];
        TagContextMenuService.IShellItemArray? selection = null;
        try
        {
            for (var index = 0; index < paths.Length; index++)
                Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[index], IntPtr.Zero, out itemIds[index], 0, out _));
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)itemIds.Length, itemIds, out selection));
            IReadOnlyList<string>? received = null;
            var callbacks = 0;
            var command = new TagContextMenuService.TagCommand(value => { received = value; callbacks++; });
            Check(command.Execute() != 0 && callbacks == 0,
                "an Explorer tag command without a selection rejects execution");
            Check(command.SetSelection(selection) == 0 && command.Execute() == 0 && callbacks == 1 &&
                received?.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase) == true,
                "a real Shell selection hands Unicode and quoted paths to one editor callback in order");
            command.SetNoShowUI(true);
            Check(command.Execute() != 0 && callbacks == 1,
                "a Shell no-UI invocation cannot accidentally open a tag editor");

            var classId = Guid.NewGuid();
            using var registration = TagContextMenuService.RegisterServer(value => { received = value; callbacks++; }, classId);
            var executeId = typeof(TagContextMenuService.IExecuteCommand).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, IntPtr.Zero, 4, ref executeId, out var pointer));
            try
            {
                var activated = Marshal.GetObjectForIUnknown(pointer);
                var selectionSink = (TagContextMenuService.IObjectWithSelection)activated;
                var executor = (TagContextMenuService.IExecuteCommand)activated;
                Check(selectionSink.SetSelection(selection) == 0 && executor.Execute() == 0 && callbacks == 2 &&
                    received?.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase) == true,
                    "the native COM local-server factory preserves a full multi-file selection");
            }
            finally { Marshal.Release(pointer); }
        }
        finally
        {
            if (selection is not null && Marshal.IsComObject(selection)) Marshal.FinalReleaseComObject(selection);
            foreach (var itemId in itemIds) if (itemId != IntPtr.Zero) Marshal.FreeCoTaskMem(itemId);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr itemId, uint attributes,
        out uint attributesOut);
    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, [In] IntPtr[] itemIds,
        [MarshalAs(UnmanagedType.Interface)] out TagContextMenuService.IShellItemArray selection);
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId,
        out IntPtr result);

    private static Border LayoutClosedMenu(MenuItem item)
    {
        item.ApplyTemplate();
        var popup = item.Template.FindName("PART_Popup", item) as Popup
            ?? throw new Exception("The menu popup template was not found.");
        var body = popup.Child as Border ?? throw new Exception("The menu popup body was not found.");
        Check(!item.IsSubmenuOpen && !popup.IsOpen && IsDark(body.Background),
            item.Header + " has a dark popup surface while remaining closed");
        body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        body.Arrange(new Rect(new Point(), body.DesiredSize));
        body.UpdateLayout();
        return body;
    }

    private static bool ReadSingleWindowMode(string path)
    {
        var settings = new AppSettingsService(path);
        settings.Reload();
        return settings.SingleWindowMode;
    }

    private static bool HasRenderedInk(FrameworkElement element)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        if (width <= 0 || height <= 0) return false;
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return Enumerable.Range(0, width * height).Count(pixel => pixels[pixel * 4 + 3] > 0) >= 3;
    }

    private static bool IsDark(Brush brush)
        => brush is SolidColorBrush { Color: var color } && color.A > 0 && Math.Max(color.R, Math.Max(color.G, color.B)) < 90;

    private static bool IsLight(Brush brush)
        => brush is SolidColorBrush { Color: var color } && Math.Min(color.R, Math.Min(color.G, color.B)) > 140;

    private static async Task CheckArtworkChangesAsync(MainWindow window, string noArtwork, string untagged,
        string artistOnly, string albumOnly, string covered, FrameworkElement surface, string artifacts)
    {
        await OpenAsync(window, covered);
        await WaitForArtworkAsync(window, "an embedded MP3 cover replaces the artwork placeholder");
        Check(Find<TextBlock>(window, "SongTitle").Text == "Covered UI fixture",
            "artwork loading preserves the new song's metadata title");
        CheckSongMetadata(window, "Covered artist", "Covered album",
            "artwork loading preserves the covered song's artist and album");
        CheckMetadataLayout(window, surface, "covered audio places artist and album below its title");
        Snapshot(surface, Path.Combine(artifacts, "artwork.png"));

        var changing = OpenAsync(window, noArtwork);
        CheckArtworkPlaceholder(window, "changing files immediately clears the previous artwork");
        CheckSongMetadata(window, null, null, "changing files immediately clears the previous artist and album");
        await changing;
        CheckArtworkPlaceholder(window, "changing to a song without artwork retains its placeholder");
        CheckSongMetadata(window, FixtureArtist, FixtureAlbum,
            "changing audio replaces the artist and album with the new song's metadata");

        var clearing = OpenAsync(window, untagged);
        CheckSongMetadata(window, null, null, "opening metadata-free audio immediately clears stale names");
        await clearing;
        CheckSongMetadata(window, null, null, "audio without artist or album keeps both lines empty and collapsed");
        await OpenAsync(window, artistOnly);
        CheckSongMetadata(window, "Artist only", null, "artist-only audio displays its artist and collapses the album line");
        await OpenAsync(window, albumOnly);
        CheckSongMetadata(window, null, "Album only", "album-only audio displays its album and collapses the artist line");

        // Leave real artwork loaded so the subsequent file-error checks can catch stale covers.
        await OpenAsync(window, covered);
        await WaitForArtworkAsync(window, "returning to a covered song restores its embedded artwork");
        CheckSongMetadata(window, "Covered artist", "Covered album",
            "returning to covered audio restores its artist and album");
    }

    private static void CheckSongMetadata(MainWindow window, string? artist, string? album, string description)
    {
        var artistLine = Find<TextBlock>(window, "ArtistName");
        var albumLine = Find<TextBlock>(window, "AlbumName");
        Check(artistLine.Text == (artist ?? "") && artistLine.Visibility == (artist is null ? Visibility.Collapsed : Visibility.Visible) &&
            albumLine.Text == (album ?? "") && albumLine.Visibility == (album is null ? Visibility.Collapsed : Visibility.Visible), description);
    }

    private static void CheckMetadataLayout(MainWindow window, FrameworkElement surface, string description)
    {
        Layout(surface);
        var title = Find<TextBlock>(window, "SongTitle");
        var artist = Find<TextBlock>(window, "ArtistName");
        var album = Find<TextBlock>(window, "AlbumName");
        var play = Find<Button>(window, "PlayButton");
        var titleBottom = title.TranslatePoint(new Point(0, title.ActualHeight), surface).Y;
        var artistTop = artist.TranslatePoint(new Point(), surface).Y;
        var artistBottom = artist.TranslatePoint(new Point(0, artist.ActualHeight), surface).Y;
        var albumTop = album.TranslatePoint(new Point(), surface).Y;
        var albumBottom = album.TranslatePoint(new Point(0, album.ActualHeight), surface).Y;
        var playTop = play.TranslatePoint(new Point(), surface).Y;
        Check(artist.ActualHeight > 0 && album.ActualHeight > 0 && artistTop >= titleBottom - 0.1 &&
            albumTop >= artistBottom - 0.1 && albumBottom <= playTop + 0.1, description);
    }

    private static Task WaitForArtworkAsync(MainWindow window, string description)
        => WaitUntilAsync(() => Find<Image>(window, "AlbumArtwork") is
                { Visibility: Visibility.Visible, Source: BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } } &&
            Find<Border>(window, "ArtworkPlaceholder").Visibility == Visibility.Collapsed, description);

    private static void CheckArtworkPlaceholder(MainWindow window, string description)
        => Check(Find<Image>(window, "AlbumArtwork") is { Visibility: Visibility.Collapsed, Source: null } &&
            Find<Border>(window, "ArtworkPlaceholder").Visibility == Visibility.Visible, description);

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
        Check(!window.AcceptOpenRequest(null), "a closing window rejects subsequent handoff requests");
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
            !Find<Button>(window, "PlayButton").IsEnabled && !Find<Button>(window, "StopButton").IsEnabled &&
            !Find<Button>(window, "TrimButton").IsEnabled &&
            !seek.IsEnabled && seek.Duration == 0 && !seek.IsTrimMode &&
            Find<Button>(window, "SaveButton").Visibility == Visibility.Collapsed, description);
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class
        => root.FindName(name) as T ?? throw new Exception("Control not found: " + name);

    private static Task OpenAsync(MainWindow window, string path)
        => (Task)(typeof(MainWindow).GetMethod("OpenAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(window, [path]) ?? throw new Exception("MainWindow.OpenAsync was not found."));

    private static void InvokeWindowMethod(MainWindow window, string name, params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Exception("MainWindow method was not found: " + name);
        method.Invoke(window, arguments);
    }

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
        surface.Measure(new Size(surface.Width, surface.Height));
        surface.Arrange(new Rect(0, 0, surface.Width, surface.Height));
        surface.UpdateLayout();
    }

    private static void Snapshot(FrameworkElement surface, string path, bool layout = true)
    {
        if (layout) Layout(surface);
        var width = (int)Math.Ceiling(surface.ActualWidth);
        var height = (int)Math.Ceiling(surface.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var colors = new HashSet<int>();
        for (var offset = 0; offset < pixels.Length; offset += 4) colors.Add(BitConverter.ToInt32(pixels, offset));
        Check(colors.Count > 16, Path.GetFileName(path) + " renders real controls and text");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static string PlaybackState(MainWindow window)
    {
        var player = ReadField<AudioPlayerService>(window, "_player");
        return $"playing={player.IsPlaying}, ended={player.HasEnded}, native={player.Position:F3}, " +
            $"UI={Find<RangeSeekBar>(window, "SeekBar").Position:F3}, " +
            $"pending={ReadField<double?>(window, "_pendingSeek")}, stopped={ReadField<double?>(window, "_stoppedPosition")}, " +
            $"action={AutomationProperties.GetName(Find<Button>(window, "PlayButton"))}";
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description, Func<string>? diagnostics = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(40);
        var passed = condition();
        Check(passed, !passed && diagnostics is not null ? description + " (" + diagnostics() + ")" : description);
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
        _checks++;
        Console.WriteLine("PASS: " + description);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

}
