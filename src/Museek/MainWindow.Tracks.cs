using System.Windows;
using System.Windows.Controls;
using Museek.Services;

namespace Museek;

public partial class MainWindow
{
    private readonly FolderTrackService _folderTrackService = new();
    private IReadOnlyList<FolderTrack> _folderTracks = [];
    private TrackSortBy _sortBy = TrackSortBy.Title;
    private CancellationTokenSource? _folderCancellation;
    private Task? _activeFolder;
    private bool _sorting;

    private async void SortBy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } || !Enum.TryParse<TrackSortBy>(value, out var sortBy)) return;
        _sortBy = sortBy;
        // WPF toggles a checkable item before Click. Restore the radio-style selection,
        // including when the user clicks the option that is already selected.
        SortTitleMenuItem.IsChecked = sortBy == TrackSortBy.Title;
        SortArtistMenuItem.IsChecked = sortBy == TrackSortBy.Artist;
        SortAlbumMenuItem.IsChecked = sortBy == TrackSortBy.Album;
        SortGenreMenuItem.IsChecked = sortBy == TrackSortBy.Genre;
        _activeFolder = RefreshFolderTracksAsync();
        await _activeFolder;
    }

    private async Task<bool> RefreshFolderTracksAsync()
    {
        if (_sourcePath is not { } source || _closing) return false;
        _folderCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _folderCancellation = cancellation;
        _sorting = true;
        UpdateTrackControls();
        try
        {
            var tracks = await _folderTrackService.LoadAsync(source, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closing || !string.Equals(source, _sourcePath, StringComparison.OrdinalIgnoreCase)) return false;
            _folderTracks = FolderTrackService.Sort(tracks, _sortBy);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested && !_closing)
            {
                _folderTracks = [];
                StatusText.Text = $"Couldn't read this folder: {ex.Message}";
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(_folderCancellation, cancellation))
            {
                _folderCancellation = null;
                _sorting = false;
                if (!_closing) UpdateTrackControls();
            }
        }
    }

    private int CurrentTrackIndex()
    {
        for (var index = 0; index < _folderTracks.Count; index++)
            if (string.Equals(_folderTracks[index].Path, _sourcePath, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private void UpdateTrackControls()
    {
        var ready = _sourcePath is not null && !_loading && !_exporting && !_closing && !_sorting && !_trimMode;
        var index = CurrentTrackIndex();
        PreviousTrackButton.IsEnabled = ready && index > 0;
        NextTrackButton.IsEnabled = ready && index >= 0 && index < _folderTracks.Count - 1;
        SortByMenu.IsEnabled = !_exporting && !_closing;
    }

    private async void PreviousTrackButton_Click(object sender, RoutedEventArgs e)
        => await MoveToTrackAsync(-1);

    private async void NextTrackButton_Click(object sender, RoutedEventArgs e)
        => await MoveToTrackAsync(1);

    private async Task MoveToTrackAsync(int offset)
    {
        if (_sourcePath is not { } source || _loading || _exporting || _closing || _sorting || _trimMode) return;
        // Refresh on every request so renamed, added, removed or retagged files affect the order.
        var refresh = RefreshFolderTracksAsync();
        _activeFolder = refresh;
        if (!await refresh || _trimMode || _exporting || _closing ||
            !string.Equals(source, _sourcePath, StringComparison.OrdinalIgnoreCase)) return;
        var index = CurrentTrackIndex();
        var target = index + offset;
        if (index >= 0 && target >= 0 && target < _folderTracks.Count)
            await OpenAsync(_folderTracks[target].Path);
    }
}
