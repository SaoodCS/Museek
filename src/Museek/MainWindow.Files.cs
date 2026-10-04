using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Museek.Services;

namespace Museek;

public partial class MainWindow
{
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
        UpdateCurrentTime(0);
        TotalTime.Text = "0:00";
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
}
