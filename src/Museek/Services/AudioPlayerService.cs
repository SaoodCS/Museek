using System.IO;
using LibVLCSharp.Shared;

namespace Museek.Services;

/// <summary>VLC lifetime and playback, isolated from the window and export logic.</summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly LibVLC _vlc;
    private readonly MediaPlayer _player;
    private Media? _media;

    public event EventHandler? PlaybackError;
    public bool IsPlaying => _player.IsPlaying;
    public bool HasEnded => _player.State == VLCState.Ended;
    public double Position => Math.Max(0, _player.Time / 1000.0);
    public int Volume { get => _player.Volume; set => _player.Volume = Math.Clamp(value, 0, 100); }

    public AudioPlayerService(bool useDummyAudioOutput = false)
    {
        var nativePath = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        LibVLCSharp.Shared.Core.Initialize(Directory.Exists(nativePath) ? nativePath : null);
        var options = new List<string> { "--no-video", "--quiet", "--no-osd", "--no-metadata-network-access" };
        // CI checks use the real decoder and playback clock without requiring an audio device.
        if (useDummyAudioOutput) options.Add("--aout=dummy");
        _vlc = new LibVLC(options.ToArray());
        _player = new MediaPlayer(_vlc);
        _player.EncounteredError += OnError;
        Volume = 75;
    }

    public void Open(string path)
    {
        _player.Stop();
        _media?.Dispose();
        _media = new Media(_vlc, new Uri(Path.GetFullPath(path)));
        _media.AddOption(":no-video");
        _player.Media = _media;
        if (!_player.Play()) throw new InvalidOperationException("This audio file could not be played.");
    }

    public void Play()
    {
        if (_media is null) return;
        if (HasEnded) _player.Stop();
        if (!_player.Play()) throw new InvalidOperationException("This audio file could not be played.");
    }

    public void Pause() => _player.SetPause(true);
    public void Stop() => _player.Stop();
    public void Seek(double seconds) => _player.Time = (long)(Math.Max(0, seconds) * 1000);
    private void OnError(object? sender, EventArgs args) => PlaybackError?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _player.EncounteredError -= OnError;
        _player.Stop();
        _player.Dispose();
        _media?.Dispose();
        _vlc.Dispose();
    }
}
