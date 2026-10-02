# Museek

A small native Windows 11 audio player with precise trimming. Built for **Windows 11 x64**.

## Run

Open `dist\Museek\Museek.exe`. Keep the whole `Museek` folder together; the .NET runtime, VLC and FFmpeg are included.

- Press **Ctrl+O**, drag an audio file into the window, or open a file from Explorer with Museek.
- Embedded album artwork appears above the song title; files without artwork show a simple placeholder.
- The artist and album names appear below the title when those tags are available.
- Play/pause, adjust the volume, and click or drag the seek bar.
- Click **Trim**, then drag the green start/end handles. Play previews the selected range.
- **Save copy** opens Save As. Choose WAV, MP3, FLAC, M4A, OGG or Opus. **Cancel** leaves trim mode.
- Saving creates a separate audio file. The original cannot be overwritten. Lossy outputs are re-encoded; WAV and FLAC avoid an additional lossy encode.

Common formats and many less common ones are supported through VLC/FFmpeg. Protected/DRM audio, corrupt files and formats without an available decoder cannot be played.

## Install and choose defaults

Double-click `dist\Museek\Install.cmd` to install for your account without administrator rights. The optional installer registers Museek in **Open with** and adds a Start menu shortcut. Or run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install.ps1
```

For a standalone copy of the published folder, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\dist\Museek\Install.ps1 -Source .\dist\Museek
```

In Museek, open **··· → Choose Museek as default…** and choose your audio file types in Windows Settings. You can also right-click an audio file in Explorer, select **Open with → Choose another app → Museek**, then choose **Always**. Windows requires you to choose defaults yourself.

To uninstall, use Windows **Settings → Apps → Installed apps → Museek**, or run `scripts\Uninstall.ps1`. Audio files are not deleted.

## Keyboard

| Key | Action |
| --- | --- |
| Ctrl+O | Open audio |
| Space | Play / pause |
| Escape | Cancel trimming or stop an export |
| Left / Right on seek handle | Seek 0.25 seconds |
| Left / Right on trim handle | Adjust endpoint 0.25 seconds |
| Shift + arrow | Move 5 seconds |
| Home / End on seek handle | Seek to start / end |

## Build

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
```

The script restores the pinned VLC packages, downloads FFmpeg essentials with checksum verification, publishes a self-contained x64 app into `dist\Museek`, and runs the core, audio-export, artwork and native UI checks. Supply `-FfmpegDirectory C:\path\to\bin` to use an existing FFmpeg build. Tests also require FFmpeg/FFprobe on PATH; the UI harness uses the bundled tools when available. A local SDK at `.tools\dotnet` is detected automatically.

Source lives in `src\Museek`. The small selection model is in `src\Museek.Core`. Test screenshots are written into `artifacts`.

The app is unsigned. There is no Microsoft Store package or code-signing certificate included.
