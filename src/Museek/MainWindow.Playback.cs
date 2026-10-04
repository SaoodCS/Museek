using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace Museek;

public partial class MainWindow
{
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
        if (_renderedPlaybackIntent == _wantsPlayback) return;
        PlayIcon.Data = _wantsPlayback ? PauseGeometry : PlayGeometry;
        PlayIcon.Margin = new Thickness(_wantsPlayback ? 0 : 4, 0, 0, 0);
        AutomationProperties.SetName(PlayButton, _wantsPlayback ? "Pause" : "Play");
        _renderedPlaybackIntent = _wantsPlayback;
    }

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private void UpdateCurrentTime(double seconds)
    {
        var wholeSecond = TimeSpan.FromSeconds(Math.Max(0, seconds)).Ticks / TimeSpan.TicksPerSecond;
        if (_displayedSecond == wholeSecond) return;
        CurrentTime.Text = FormatTime(seconds);
        _displayedSecond = wholeSecond;
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
        UpdateCurrentTime(position);
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
                    UpdateCurrentTime(_stoppedPosition.Value);
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
        UpdateCurrentTime(_stoppedPosition.Value);
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
            UpdateCurrentTime(seconds);
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
        UpdateCurrentTime(seconds);
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
                UpdateCurrentTime(_stoppedPosition.Value);
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
}
