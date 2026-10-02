using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Museek.Services;

namespace Museek;

public partial class MainWindow : Window
{
    private readonly AudioPlayerService _player;
    private readonly AudioExportService _export = new();
    private readonly AlbumArtworkService _artwork = new();
    private readonly DispatcherTimer _timer;
    private readonly string? _initialPath;
    private readonly AppSettingsService _settings;
    private readonly Action<bool> _editTagsContextMenuRegistration;
    private string? _sourcePath;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _saveCancellation;
    private bool _trimMode;
    private bool _closing;
    private bool _loading;
    private bool _exporting;
    private bool _wantsPlayback;
    private double? _pendingSeek;
    private double? _stoppedPosition;
    private bool _allowClose;
    private bool _updatingSettings;
    private bool _fileDialogOpen;
    private string? _queuedOpenPath;
    private Task? _activeProbe;
    private Task? _activeExport;
    private Task? _activeArtwork;

    public event EventHandler? SingleWindowModeChanged;
    public bool SingleWindowMode => _settings.SingleWindowMode;
    public void ShowStatus(string message) => StatusText.Text = message;

    public MainWindow(string? initialPath = null, AppSettingsService? settings = null,
        Action<bool>? editTagsContextMenuRegistration = null)
    {
        _settings = settings ?? new AppSettingsService();
        _editTagsContextMenuRegistration = editTagsContextMenuRegistration ?? (enabled =>
        {
            if (enabled) TagContextMenuService.Register(Environment.ProcessPath!);
            else TagContextMenuService.Unregister();
        });
        InitializeComponent();
        RefreshSingleWindowMode();
        _initialPath = initialPath;
        _player = new AudioPlayerService();
        _player.PlaybackError += Player_PlaybackError;
        _player.Volume = (int)VolumeSlider.Value;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) => WindowThemeService.Apply(this);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var path = _queuedOpenPath ?? _initialPath;
        _queuedOpenPath = null;
        if (!string.IsNullOrWhiteSpace(path)) await OpenAsync(path);
    }

    private void Window_Activated(object? sender, EventArgs e) => RefreshSingleWindowMode();

    private void RefreshSingleWindowMode()
    {
        var previous = _settings.SingleWindowMode;
        try { _settings.Reload(); }
        catch (Exception ex) { StatusText.Text = $"Couldn't read Museek settings: {ex.Message}"; }
        SetSingleWindowCheck(_settings.SingleWindowMode);
        if (previous != _settings.SingleWindowMode) SingleWindowModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetSingleWindowCheck(bool enabled)
    {
        _updatingSettings = true;
        try
        {
            SingleWindowModeMenuItem.IsChecked = enabled;
            EditTagsContextMenuItem.IsChecked = _settings.EditTagsContextMenu;
        }
        finally { _updatingSettings = false; }
    }

    private void SingleWindowMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSettings) return;
        try
        {
            _settings.SetSingleWindowMode(SingleWindowModeMenuItem.IsChecked);
            SetSingleWindowCheck(_settings.SingleWindowMode);
            SingleWindowModeChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SetSingleWindowCheck(_settings.SingleWindowMode);
            StatusText.Text = $"Couldn't save Museek settings: {ex.Message}";
        }
    }

    private void EditTagsContextMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSettings) return;
        var previous = _settings.EditTagsContextMenu;
        var enabled = EditTagsContextMenuItem.IsChecked;
        try
        {
            _editTagsContextMenuRegistration(enabled);
            try { _settings.SetEditTagsContextMenu(enabled); }
            catch
            {
                _editTagsContextMenuRegistration(previous);
                throw;
            }
            SetSingleWindowCheck(_settings.SingleWindowMode);
            SingleWindowModeChanged?.Invoke(this, EventArgs.Empty);
            StatusText.Text = enabled ? "Edit Tags is available in Explorer under Show more options."
                : "Edit Tags was removed from the Explorer context menu.";
        }
        catch (Exception ex)
        {
            SetSingleWindowCheck(_settings.SingleWindowMode);
            StatusText.Text = $"Couldn't update the Edit Tags option: {ex.Message}";
        }
    }

    // Accept promptly so an Explorer launch can exit before metadata or artwork finishes loading.
    public bool AcceptOpenRequest(string? path)
    {
        if (_closing) return false;
        if (!string.IsNullOrWhiteSpace(path)) _queuedOpenPath = path;
        // Return acceptance before any synchronous native playback or settings work begins.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_closing) return;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            if (string.IsNullOrWhiteSpace(path)) return;
            if (_exporting) StatusText.Text = "The new audio file will open when saving finishes.";
            if (IsLoaded) OpenQueuedFile();
        });
        return true;
    }

    private void OpenQueuedFile()
    {
        if (_closing || _exporting || _fileDialogOpen || _queuedOpenPath is null) return;
        var path = _queuedOpenPath;
        _queuedOpenPath = null;
        _ = OpenAsync(path);
    }

    private async Task OpenAsync(string path)
    {
        if (_exporting || _closing) return;
        _loadCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _player.Stop();
        _sourcePath = null;
        _wantsPlayback = false;
        _pendingSeek = null;
        _stoppedPosition = null;
        _loading = true;
        SetTrimMode(false);
        SeekBar.SetDuration(0);
        SetArtwork(null);
        SetTrackDetails(null, null);
        SongTitle.Text = "Opening audio…";
        StatusText.Text = "Reading audio…";
        CurrentTime.Text = TotalTime.Text = "0:00";
        UpdateControls();
        Task<byte[]?>? artwork = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            artwork = _artwork.LoadAsync(fullPath, cancellation.Token);
            _activeArtwork = artwork;
            var probe = _export.ProbeAsync(fullPath, cancellation.Token);
            _activeProbe = probe;
            var info = await probe;
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closing) return;
            _player.Open(fullPath);
            _sourcePath = fullPath;
            SeekBar.SetDuration(info.Duration.TotalSeconds);
            SongTitle.Text = info.Title;
            SongTitle.ToolTip = info.Title;
            SetTrackDetails(info.Artist, info.Album);
            TotalTime.Text = FormatTime(info.Duration.TotalSeconds);
            Title = $"{info.Title} — Museek";
            StatusText.Text = "Space to pause. Trim to keep your favorite part.";
            _wantsPlayback = true;
            _loading = false;
            UpdateControls();
            var artworkBytes = await artwork;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_closing) SetArtwork(artworkBytes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested && !_closing)
            {
                SongTitle.Text = "Couldn't open this file.";
                StatusText.Text = ex.Message;
                Title = "Museek";
            }
        }
        finally
        {
            cancellation.Cancel();
            if (artwork is not null)
            {
                try { await artwork; }
                catch (OperationCanceledException) { }
            }
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
                _activeProbe = null;
                _activeArtwork = null;
                _loading = false;
                if (!_closing) UpdateControls();
            }
        }
    }

    private void UpdateControls()
    {
        var ready = _sourcePath is not null && !_loading && !_exporting;
        PlayButton.IsEnabled = StopButton.IsEnabled = SeekBar.IsEnabled = TrimButton.IsEnabled = ready;
        SaveButton.IsEnabled = ready && SeekBar.SelectionEnd > SeekBar.SelectionStart;
        CancelButton.Content = _exporting ? "Stop saving" : "Cancel";
        UpdatePlayIcon();
    }

    private void UpdatePlayIcon()
    {
        PlayIcon.Data = System.Windows.Media.Geometry.Parse(_wantsPlayback
            ? "M 0,0 L 5,0 L 5,20 L 0,20 Z M 11,0 L 16,0 L 16,20 L 11,20 Z"
            : "M 0,0 L 0,20 L 16,10 Z");
        PlayIcon.Margin = new Thickness(_wantsPlayback ? 0 : 4, 0, 0, 0);
        AutomationProperties.SetName(PlayButton, _wantsPlayback ? "Pause" : "Play");
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_sourcePath is null || _loading || _exporting || _closing) return;
        // A stopped VLC player has no seekable clock. Keep the selected cursor until Play.
        var position = _stoppedPosition ?? _pendingSeek ?? _player.Position;
        // VLC starts asynchronously. Apply a pending preview seek as soon as it is playing.
        if (_pendingSeek is { } pending && _player.IsPlaying)
        {
            pending = _trimMode ? Math.Clamp(pending, SeekBar.SelectionStart, SeekBar.SelectionEnd) : pending;
            _player.Seek(pending);
            _pendingSeek = null;
            position = pending;
        }
        // A seek to EOF can finish before the next timer tick observes native playback.
        if (_player.HasEnded && _pendingSeek is { } endTarget)
        {
            if (endTarget >= SeekBar.Duration - 0.03) _pendingSeek = null;
            else if (_wantsPlayback)
            {
                // Trim bounds may have moved the queued EOF seek back inside the file.
                try { _player.Play(); }
                catch (Exception ex) { _pendingSeek = null; _wantsPlayback = false; StatusText.Text = ex.Message; }
            }
        }
        if (_trimMode && _wantsPlayback && _pendingSeek is null && position >= SeekBar.SelectionEnd - 0.015)
        {
            _player.Pause();
            _wantsPlayback = false;
            position = SeekBar.SelectionEnd;
        }
        if (_player.HasEnded && _pendingSeek is null && _stoppedPosition is null)
        {
            _wantsPlayback = false;
            position = _trimMode ? SeekBar.SelectionEnd : SeekBar.Duration;
        }
        SeekBar.Position = position;
        CurrentTime.Text = FormatTime(position);
        UpdatePlayIcon();
    }

    private void TogglePlayback()
    {
        if (!PlayButton.IsEnabled) return;
        try
        {
            if (_wantsPlayback)
            {
                if (_pendingSeek is { } pending)
                {
                    // Pausing before VLC finishes starting must retain the requested cursor.
                    _player.Stop();
                    _stoppedPosition = _trimMode ? Math.Clamp(pending, SeekBar.SelectionStart, SeekBar.SelectionEnd) : pending;
                    SeekBar.Position = _stoppedPosition.Value;
                    CurrentTime.Text = FormatTime(_stoppedPosition.Value);
                }
                else _player.Pause();
                _wantsPlayback = false;
                _pendingSeek = null;
            }
            else
            {
                var stoppedAt = _stoppedPosition;
                var restartTrim = _trimMode && (_player.HasEnded || SeekBar.Position >= SeekBar.SelectionEnd - 0.03 || SeekBar.Position < SeekBar.SelectionStart);
                _player.Play();
                if (stoppedAt is not null || restartTrim)
                {
                    var target = restartTrim ? SeekBar.SelectionStart : stoppedAt!.Value;
                    _player.Seek(target);
                    _pendingSeek = target;
                }
                _stoppedPosition = null;
                _wantsPlayback = true;
            }
            UpdatePlayIcon();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; _wantsPlayback = false; UpdatePlayIcon(); }
    }

    private void SetTrimMode(bool active)
    {
        _trimMode = active;
        SeekBar.IsTrimMode = active;
        TrimButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.Visibility = CancelButton.Visibility = SelectionLabel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active) UpdateSelectionLabel();
    }

    private void UpdateSelectionLabel()
    {
        SelectionLabel.Text = $"{FormatTime(SeekBar.SelectionStart, precise: true)}  –  {FormatTime(SeekBar.SelectionEnd, precise: true)}";
        if (_trimMode && !_exporting) StatusText.Text = "Drag the green handles. Play to preview your selection.";
    }

    private static string FormatTime(double seconds, bool precise = false)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.ToString(time.TotalHours >= 1
            ? (precise ? @"h\:mm\:ss\.ff" : @"h\:mm\:ss")
            : (precise ? @"m\:ss\.ff" : @"m\:ss"), CultureInfo.InvariantCulture);
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlayback();
    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!StopButton.IsEnabled) return;
        _player.Stop();
        _wantsPlayback = false;
        _pendingSeek = null;
        _stoppedPosition = _trimMode ? SeekBar.SelectionStart : 0;
        SeekBar.Position = _stoppedPosition.Value;
        CurrentTime.Text = FormatTime(_stoppedPosition.Value);
        UpdatePlayIcon();
    }
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null) _player.Volume = (int)e.NewValue;
        if (VolumeLabel is not null) VolumeLabel.Text = $"{e.NewValue:0}%";
    }
    private void SeekBar_SeekRequested(object? sender, double seconds)
    {
        if (_sourcePath is null) return;
        if (_stoppedPosition is not null)
        {
            _stoppedPosition = seconds;
            CurrentTime.Text = FormatTime(seconds);
            return;
        }
        if (_player.HasEnded)
        {
            _player.Play();
            _pendingSeek = seconds;
            _wantsPlayback = true;
        }
        else if (_pendingSeek is not null) _pendingSeek = seconds;
        _player.Seek(seconds);
        CurrentTime.Text = FormatTime(seconds);
    }
    private void SeekBar_SelectionChanged(object? sender, EventArgs e)
    {
        if (SelectionLabel is null) return;
        UpdateSelectionLabel();
        if (_trimMode && _sourcePath is not null)
        {
            if (_stoppedPosition is { } stopped)
            {
                _stoppedPosition = Math.Clamp(stopped, SeekBar.SelectionStart, SeekBar.SelectionEnd);
                SeekBar.Position = _stoppedPosition.Value;
                CurrentTime.Text = FormatTime(_stoppedPosition.Value);
                return;
            }
            if (_pendingSeek is { } pending)
                _pendingSeek = Math.Clamp(pending, SeekBar.SelectionStart, SeekBar.SelectionEnd);
            var position = Math.Clamp(_player.Position, SeekBar.SelectionStart, SeekBar.SelectionEnd);
            if (position != _player.Position) _player.Seek(position);
            SeekBar.Position = position;
        }
    }
    private void TrimButton_Click(object sender, RoutedEventArgs e)
    {
        SeekBar.ResetSelection();
        SetTrimMode(true);
        UpdateControls();
    }
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting) { _saveCancellation?.Cancel(); CancelButton.IsEnabled = false; return; }
        SetTrimMode(false);
        StatusText.Text = "Trim cancelled. Your original is unchanged.";
        UpdateControls();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _exporting) return;
        var source = _sourcePath;
        var start = TimeSpan.FromSeconds(SeekBar.SelectionStart);
        var end = TimeSpan.FromSeconds(SeekBar.SelectionEnd);
        string[] extensions = [".wav", ".mp3", ".flac", ".m4a", ".ogg", ".opus"];
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var filterIndex = Array.IndexOf(extensions, extension);
        var dialog = new SaveFileDialog
        {
            Title = "Save trimmed audio",
            Filter = "WAV audio|*.wav|MP3 audio|*.mp3|FLAC audio|*.flac|M4A audio|*.m4a|OGG audio|*.ogg|Opus audio|*.opus",
            FilterIndex = filterIndex >= 0 ? filterIndex + 1 : 1,
            FileName = Path.GetFileNameWithoutExtension(source) + " (trimmed)",
            DefaultExt = filterIndex >= 0 ? extension : ".wav",
            InitialDirectory = Path.GetDirectoryName(source),
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true
        };
        bool? save;
        _fileDialogOpen = true;
        try { save = dialog.ShowDialog(this); }
        finally { _fileDialogOpen = false; }
        if (save != true) { OpenQueuedFile(); return; }
        _player.Pause();
        _wantsPlayback = false;
        _exporting = true;
        using var cancellation = new CancellationTokenSource();
        _saveCancellation = cancellation;
        ExportProgress.Value = 0;
        ExportProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Saving trimmed copy…";
        UpdateControls();
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (_closing || !_exporting) return;
                ExportProgress.Value = value;
                StatusText.Text = $"Saving trimmed copy… {value:P0}";
            });
            _activeExport = _export.ExportAsync(source, dialog.FileName, start, end, progress, cancellation.Token);
            await _activeExport;
            if (!_closing)
            {
                SetTrimMode(false);
                StatusText.Text = $"Saved {Path.GetFileName(dialog.FileName)}. Your original is unchanged.";
            }
        }
        catch (OperationCanceledException) { if (!_closing) StatusText.Text = "Save stopped. Adjust the selection or try again."; }
        catch (Exception ex) { if (!_closing) StatusText.Text = ex.Message; }
        finally
        {
            _saveCancellation = null;
            _activeExport = null;
            _exporting = false;
            if (!_closing)
            {
                CancelButton.IsEnabled = true;
                ExportProgress.Visibility = Visibility.Collapsed;
                UpdateControls();
                OpenQueuedFile();
            }
        }
    }

    private async void OpenAudioFile()
    {
        var patterns = string.Join(';', WindowsIntegrationService.SupportedExtensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog { Title = "Open audio", Filter = $"Audio files|{patterns}|All files|*.*", CheckFileExists = true };
        _fileDialogOpen = true;
        try
        {
            var chosen = dialog.ShowDialog(this);
            _fileDialogOpen = false;
            if (chosen == true) await OpenAsync(dialog.FileName);
        }
        finally { _fileDialogOpen = false; OpenQueuedFile(); }
    }
    private void DefaultApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowsIntegrationService.Register(Environment.ProcessPath!);
            WindowsIntegrationService.OpenDefaultAppsSettings();
            StatusText.Text = "Choose the audio file types you want Museek to open in Windows Settings.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Museek 1.0\nA simple audio player and trimmer.\n\nPlayback: VLC / LibVLCSharp\nTrimming: FFmpeg\n\nThird-party notices are included beside Museek.exe.", "About Museek", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_exporting && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_exporting && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await OpenAsync(files[0]);
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control && !_exporting)
        {
            OpenAudioFile(); e.Handled = true;
        }
        else if (e.Key == Key.Escape && _trimMode && !HelpMenu.IsSubmenuOpen && !ToolsMenu.IsSubmenuOpen && !MenuBar.IsKeyboardFocusWithin)
        {
            CancelButton_Click(CancelButton, new RoutedEventArgs()); e.Handled = true;
        }
        else if (e.Key == Key.Space && e.OriginalSource is not Button && e.OriginalSource is not MenuItem)
        {
            TogglePlayback(); e.Handled = true;
        }
    }
    private void Player_PlaybackError(object? sender, EventArgs e)
    {
        // Never call back into VLC on its event thread.
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            _wantsPlayback = false;
            StatusText.Text = "This audio could not be played. It may be damaged, protected, or use an unsupported codec.";
            UpdatePlayIcon();
        });
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        _timer.Stop();
        _loadCancellation?.Cancel();
        _saveCancellation?.Cancel();
        // Let FFmpeg finish its cancellation cleanup before ending the process.
        try { await Task.WhenAll(_activeProbe ?? Task.CompletedTask, _activeExport ?? Task.CompletedTask, _activeArtwork ?? Task.CompletedTask); }
        catch (Exception) { /* The load/save handler reports errors while the window is open. */ }
        _player.PlaybackError -= Player_PlaybackError;
        _player.Dispose();
        _allowClose = true;
        _ = Dispatcher.BeginInvoke(Close);
    }

    private void SetArtwork(byte[]? bytes)
    {
        BitmapImage? bitmap = null;
        if (bytes is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 384;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException or ArgumentException)
            {
                bitmap = null;
            }
        }
        AlbumArtwork.Source = bitmap;
        AlbumArtwork.Visibility = bitmap is null ? Visibility.Collapsed : Visibility.Visible;
        ArtworkPlaceholder.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetTrackDetails(string? artist, string? album)
    {
        ArtistName.Text = artist ?? string.Empty;
        ArtistName.ToolTip = artist;
        ArtistName.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;
        AlbumName.Text = album ?? string.Empty;
        AlbumName.ToolTip = album;
        AlbumName.Visibility = string.IsNullOrWhiteSpace(album) ? Visibility.Collapsed : Visibility.Visible;
        var hasDetails = ArtistName.Visibility == Visibility.Visible || AlbumName.Visibility == Visibility.Visible;
        TrackDetails.Visibility = hasDetails ? Visibility.Visible : Visibility.Collapsed;
        SongTitle.Margin = new Thickness(0, 0, 0, hasDetails ? 6 : 18);
    }
}
