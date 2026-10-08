using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Museek.Services;

namespace Museek;

public partial class MainWindow : Window
{
    private static readonly Geometry PlayGeometry = CreateFrozenGeometry("M 0,0 L 0,20 L 16,10 Z");
    private static readonly Geometry PauseGeometry = CreateFrozenGeometry("M 0,0 L 5,0 L 5,20 L 0,20 Z M 11,0 L 16,0 L 16,20 L 11,20 Z");
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
    private bool? _renderedPlaybackIntent;
    private long? _displayedSecond;
    private double? _pendingSeek;
    private double? _stoppedPosition;
    private bool _allowClose;
    private bool _updatingSettings;
    private bool _fileDialogOpen;
    private string? _queuedOpenPath;
    private Task? _activeProbe;
    private Task? _activeExport;
    private Task? _activeArtwork;
    private Task? _activePlayerInitialization;

    public event EventHandler? SingleWindowModeChanged;
    public bool SingleWindowMode => _settings.SingleWindowMode;
    public void ShowStatus(string message) => StatusText.Text = message;

    public MainWindow(string? initialPath = null, AppSettingsService? settings = null,
        Action<bool>? editTagsContextMenuRegistration = null, AudioPlayerService? audioPlayer = null)
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
        _player = audioPlayer ?? new AudioPlayerService();
        _player.PlaybackError += Player_PlaybackError;
        _player.PlaybackStarted += Player_PlaybackStarted;
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

    private void Window_Activated(object? sender, EventArgs e)
    {
        RefreshSingleWindowMode();
        if (_sourcePath is not null && !_loading && !_exporting && !_closing && !_sorting)
            _activeFolder = RefreshFolderTracksAsync();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control && !_exporting)
        {
            OpenAudioFile(); e.Handled = true;
        }
        else if (e.Key == Key.Escape && _trimMode && !HelpMenu.IsSubmenuOpen && !ToolsMenu.IsSubmenuOpen && !SortByMenu.IsSubmenuOpen && !MenuBar.IsKeyboardFocusWithin)
        {
            CancelButton_Click(CancelButton, new RoutedEventArgs()); e.Handled = true;
        }
        else if (e.Key == Key.Space && e.OriginalSource is not Button && e.OriginalSource is not MenuItem)
        {
            TogglePlayback(); e.Handled = true;
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        var updateCompletion = _updateWindow?.Completion ?? Task.CompletedTask;
        _updateWindow?.Close();
        ResetTrackTransition();
        IsEnabled = false;
        _timer.Stop();
        _loadCancellation?.Cancel();
        _folderCancellation?.Cancel();
        _saveCancellation?.Cancel();
        // Let FFmpeg finish its cancellation cleanup before ending the process.
        try
        {
            await Task.WhenAll(_activeProbe ?? Task.CompletedTask, _activeExport ?? Task.CompletedTask,
                _activeArtwork ?? Task.CompletedTask, _activeFolder ?? Task.CompletedTask,
                _activePlayerInitialization ?? Task.CompletedTask, updateCompletion);
        }
        catch (Exception) { /* The load/save handler reports errors while the window is open. */ }
        _player.PlaybackError -= Player_PlaybackError;
        _player.PlaybackStarted -= Player_PlaybackStarted;
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

    private void SetTrackDetails(string? artist, string? album, bool reserveSpace = false)
    {
        ArtistName.Text = artist ?? string.Empty;
        ArtistName.ToolTip = artist;
        ArtistName.Visibility = string.IsNullOrWhiteSpace(artist)
            ? (reserveSpace ? Visibility.Hidden : Visibility.Collapsed) : Visibility.Visible;
        AlbumName.Text = album ?? string.Empty;
        AlbumName.ToolTip = album;
        AlbumName.Visibility = string.IsNullOrWhiteSpace(album)
            ? (reserveSpace ? Visibility.Hidden : Visibility.Collapsed) : Visibility.Visible;
        var hasDetails = ArtistName.Visibility == Visibility.Visible || AlbumName.Visibility == Visibility.Visible;
        TrackDetails.Visibility = hasDetails || reserveSpace ? Visibility.Visible : Visibility.Collapsed;
        SongTitle.Margin = new Thickness(0, 0, 0, hasDetails || reserveSpace ? 6 : 18);
    }
}
