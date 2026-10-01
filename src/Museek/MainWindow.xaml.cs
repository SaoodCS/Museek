using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Museek.Services;

namespace Museek;

public partial class MainWindow : Window
{
    private readonly AudioPlayerService _player;
    private readonly AudioExportService _export = new();
    private readonly DispatcherTimer _timer;
    private readonly string? _initialPath;
    private string? _sourcePath;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _saveCancellation;
    private bool _trimMode;
    private bool _closing;
    private bool _loading;
    private bool _exporting;
    private bool _wantsPlayback;
    private double? _pendingSeek;
    private bool _allowClose;
    private Task? _activeProbe;
    private Task? _activeExport;

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
        _initialPath = initialPath;
        _player = new AudioPlayerService();
        _player.PlaybackError += Player_PlaybackError;
        _player.Volume = (int)VolumeSlider.Value;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        if (!string.IsNullOrWhiteSpace(_initialPath)) await OpenAsync(_initialPath);
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
        _loading = true;
        SetTrimMode(false);
        SeekBar.SetDuration(0);
        SongTitle.Text = "Opening audio…";
        FileSubtitle.Text = Path.GetFileName(path);
        StatusText.Text = "Reading audio…";
        CurrentTime.Text = TotalTime.Text = "0:00";
        UpdateControls();
        try
        {
            var fullPath = Path.GetFullPath(path);
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
            FileSubtitle.Text = $"{Path.GetFileName(fullPath)}  ·  {info.Format}";
            FileSubtitle.ToolTip = fullPath;
            TotalTime.Text = FormatTime(info.Duration.TotalSeconds);
            Title = $"{info.Title} — Museek";
            StatusText.Text = "Space to pause. Trim to keep your favorite part.";
            _wantsPlayback = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested && !_closing)
            {
                SongTitle.Text = "Couldn't open this file.";
                FileSubtitle.Text = "Try another audio file.";
                StatusText.Text = ex.Message;
                Title = "Museek";
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
                _activeProbe = null;
                _loading = false;
                if (!_closing) UpdateControls();
            }
        }
    }

    private void UpdateControls()
    {
        var ready = _sourcePath is not null && !_loading && !_exporting;
        PlayButton.IsEnabled = SeekBar.IsEnabled = TrimButton.IsEnabled = ready;
        SaveButton.IsEnabled = ready && SeekBar.SelectionEnd > SeekBar.SelectionStart;
        OpenButton.IsEnabled = !_exporting;
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
        var position = _player.Position;
        // VLC starts asynchronously. Apply a pending preview seek as soon as it is playing.
        if (_pendingSeek is { } pending && _player.IsPlaying)
        {
            pending = _trimMode ? Math.Clamp(pending, SeekBar.SelectionStart, SeekBar.SelectionEnd) : pending;
            _player.Seek(pending);
            _pendingSeek = null;
            position = pending;
        }
        if (_trimMode && _wantsPlayback && _pendingSeek is null && position >= SeekBar.SelectionEnd - 0.015)
        {
            _player.Pause();
            _wantsPlayback = false;
            position = SeekBar.SelectionEnd;
        }
        if (_player.HasEnded && _pendingSeek is null)
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
                _player.Pause();
                _wantsPlayback = false;
                _pendingSeek = null;
            }
            else
            {
                var restartTrim = _trimMode && (_player.HasEnded || SeekBar.Position >= SeekBar.SelectionEnd - 0.03 || SeekBar.Position < SeekBar.SelectionStart);
                _player.Play();
                if (restartTrim)
                {
                    _player.Seek(SeekBar.SelectionStart);
                    _pendingSeek = SeekBar.SelectionStart;
                }
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
        ModeLabel.Text = active ? "KEEP THE PART YOU LOVE." : "YOUR AUDIO, SIMPLY.";
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
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null) _player.Volume = (int)e.NewValue;
        if (VolumeLabel is not null) VolumeLabel.Text = $"{e.NewValue:0}%";
    }
    private void SeekBar_SeekRequested(object? sender, double seconds)
    {
        if (_sourcePath is null) return;
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
        if (dialog.ShowDialog(this) != true) return;
        var start = TimeSpan.FromSeconds(SeekBar.SelectionStart);
        var end = TimeSpan.FromSeconds(SeekBar.SelectionEnd);
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
            }
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var patterns = string.Join(';', WindowsIntegrationService.SupportedExtensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog { Title = "Open audio", Filter = $"Audio files|{patterns}|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await OpenAsync(dialog.FileName);
    }
    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
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
            OpenButton_Click(OpenButton, new RoutedEventArgs()); e.Handled = true;
        }
        else if (e.Key == Key.Escape && _trimMode)
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
        try { await Task.WhenAll(_activeProbe ?? Task.CompletedTask, _activeExport ?? Task.CompletedTask); }
        catch (Exception) { /* The load/save handler reports errors while the window is open. */ }
        _player.PlaybackError -= Player_PlaybackError;
        _player.Dispose();
        _allowClose = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
