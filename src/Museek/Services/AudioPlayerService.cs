using System.IO;
using LibVLCSharp.Shared;

namespace Museek.Services;

/// <summary>VLC lifetime and playback, isolated from the window and export logic.</summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly bool _useDummyAudioOutput;
    private readonly object _initializationLock = new();
    private Task? _initialization;
    private LibVLC? _vlc;
    private MediaPlayer? _player;
    private Media? _media;
    private int _volume = 75;
    private bool _disposed;

    public event EventHandler? PlaybackError;
    public event EventHandler? PlaybackStarted;
    public bool IsPlaying => _player?.IsPlaying ?? false;
    public bool HasEnded => _player?.State == VLCState.Ended;
    public double Position => Math.Max(0, (_player?.Time ?? 0) / 1000.0);
    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            if (_player is not null) _player.Volume = _volume;
        }
    }

    public AudioPlayerService(bool useDummyAudioOutput = false)
        => _useDummyAudioOutput = useDummyAudioOutput;

    /// <summary>Prepare native playback without blocking the owning UI thread.</summary>
    public Task InitializeAsync()
    {
        lock (_initializationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_player is not null) return Task.CompletedTask;
            // Failed native setup can be retried after its resources have been released.
            if (_initialization is { IsFaulted: true } or { IsCanceled: true }) _initialization = null;
            return _initialization ??= Task.Run(InitializePlayer);
        }
    }

    private MediaPlayer EnsureInitialized()
    {
        InitializeAsync().GetAwaiter().GetResult();
        return _player ?? throw new ObjectDisposedException(nameof(AudioPlayerService));
    }

    private void InitializePlayer()
    {
        // Native VLC loads many codec plugins. Defer that work until a file is played
        // so an empty window can appear without loading a decoder or audio device.
        var nativePath = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        LibVLCSharp.Shared.Core.Initialize(Directory.Exists(nativePath) ? nativePath : null);
        var options = new List<string> { "--no-video", "--quiet", "--no-osd", "--no-metadata-network-access" };
        // CI checks use the real decoder and playback clock without requiring an audio device.
        if (_useDummyAudioOutput) options.Add("--aout=dummy");
        var vlc = new LibVLC(options.ToArray());
        MediaPlayer? player = null;
        try
        {
            player = new MediaPlayer(vlc);
            player.EncounteredError += OnError;
            player.Playing += OnPlaying;
            lock (_initializationLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _vlc = vlc;
                _player = player;
            }
        }
        catch
        {
            if (player is not null)
            {
                player.EncounteredError -= OnError;
                player.Playing -= OnPlaying;
            }
            try { player?.Dispose(); }
            finally { vlc.Dispose(); }
            throw;
        }
    }

    public void Open(string path)
    {
        var player = EnsureInitialized();
        player.Stop();
        _media?.Dispose();
        _media = new Media(_vlc!, new Uri(Path.GetFullPath(path)));
        _media.AddOption(":no-video");
        player.Media = _media;
        // The chosen volume may have changed while native initialization ran.
        player.Volume = _volume;
        if (!player.Play()) throw new InvalidOperationException("This audio file could not be played.");
    }

    public void Play()
    {
        if (_media is null || _player is null) return;
        if (HasEnded) _player.Stop();
        if (!_player.Play()) throw new InvalidOperationException("This audio file could not be played.");
    }

    public void Pause() => _player?.SetPause(true);
    public void Stop() => _player?.Stop();
    public void Seek(double seconds)
    {
        if (_player is not null) _player.Time = (long)(Math.Max(0, seconds) * 1000);
    }
    private void OnError(object? sender, EventArgs args) => PlaybackError?.Invoke(this, EventArgs.Empty);
    // Consumers marshal to their owning context before calling back into VLC.
    private void OnPlaying(object? sender, EventArgs args) => PlaybackStarted?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        MediaPlayer? player;
        Media? media;
        LibVLC? vlc;
        lock (_initializationLock)
        {
            if (_disposed) return;
            _disposed = true;
            player = _player;
            media = _media;
            vlc = _vlc;
            _player = null;
            _media = null;
            _vlc = null;
            // An unfinished factory observes this flag before publishing and disposes
            // its own native objects; disposal never waits for codec discovery.
        }
        try
        {
            if (player is not null)
            {
                player.EncounteredError -= OnError;
                player.Playing -= OnPlaying;
                try { player.Stop(); }
                finally { player.Dispose(); }
            }
        }
        finally
        {
            try { media?.Dispose(); }
            finally { vlc?.Dispose(); }
        }
    }
}
