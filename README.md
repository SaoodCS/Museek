# Museek

A small native Windows 11 audio player with precise trimming and tag editing. Built for **Windows 11 x64**.

## Run

Run `dist\setup.exe` to install Museek, then open Museek from Start. The .NET runtime, VLC and FFmpeg are included. A portable copy also remains available at `dist\Museek\Museek.exe`; keep that whole folder together.

- Press **Ctrl+O**, drag an audio file into the window, or open a file from Explorer with Museek.
- Embedded album artwork appears above the song title; files without artwork show a simple placeholder.
- The artist and album names appear below the title when those tags are available.
- Play/pause, adjust the volume, and click or drag the seek bar.
- **Stop** stops playback and returns to the song start, or to the selected start in trim mode.
- Enable **Tools → Single-window mode** to reuse the current window when opening audio from Explorer. The tick and setting are remembered; turn it off to allow separate windows. A new file waits for any active save to finish.
- Enable **Tools → 'Edit Tags' context menu** to add **Edit Tags** to Explorer's **Show more options** menu. Select one or more audio files, right-click, then choose **Show more options → Edit Tags**.
- The tag editor changes title, artist, album, album artist, genre, year, track/disc number and comment. Only checked fields are applied; check an empty field to clear it. Batch editing preserves each file's other tags. Separate multiple artists or genres with semicolons.
- Add or change artwork with a JPEG/PNG image, remove artwork, or keep each file's existing artwork. Click **Save tags** to update the selected originals without re-encoding audio. **Cancel** discards unsaved edits. **Stop saving** stops the remaining batch; files already saved keep their changes. Files that cannot be edited are reported individually.
- Click **Trim**, then drag the green start/end handles. Play previews the selected range.
- **Save copy** opens Save As. Choose WAV, MP3, FLAC, M4A, OGG or Opus. **Cancel** leaves trim mode.
- Saving creates a separate audio file. The original cannot be overwritten. Lossy outputs are re-encoded; WAV and FLAC avoid an additional lossy encode.

Common formats and many less common ones are supported through VLC/FFmpeg. Protected/DRM audio, corrupt files and formats without an available decoder cannot be played.

## Install and choose defaults

Double-click **`dist\setup.exe`**. The setup wizard installs for your Windows account at `%LOCALAPPDATA%\Programs\Museek`, adds a Start menu shortcut and registers Museek in **Open with** and Windows **Installed apps**. Administrator rights are not required.

Run a newer **`setup.exe`** to update the existing installation in place. The same Start menu entry and install folder are reused. Existing installations made by the previous PowerShell installer are upgraded too. Your settings and audio files are preserved. Close Museek and its tag editor windows before installing an update.

In Museek, open **Tools → Choose Museek as default…** and choose your audio file types in Windows Settings. You can also right-click an audio file in Explorer, select **Open with → Choose another app → Museek**, then choose **Always**. Windows requires you to choose defaults yourself.

To uninstall, run **`%LOCALAPPDATA%\Programs\Museek\uninstall.exe`**, or choose **Museek → Uninstall** in Windows **Settings → Apps → Installed apps**. This removes installed program files, the Start menu shortcut and Museek's owned Windows registrations. Audio files, unrelated files placed in the install folder and settings in `%LOCALAPPDATA%\Museek` are kept.

Setup and uninstall also accept `/S` for silent operation. Windows file defaults remain your choice.

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

The script restores the pinned VLC and TagLibSharp packages, downloads FFmpeg essentials with checksum verification, publishes a self-contained x64 app into `dist\Museek` and builds **`dist\setup.exe`** with NSIS 3.13. The portable compiler downloads into `.tools` with a pinned SHA256; no tool installation is needed. Supply `-MakensisPath C:\path\to\makensis.exe` to use an existing compiler, or `-FfmpegDirectory C:\path\to\bin` for an existing FFmpeg build. A local SDK at `.tools\dotnet` is detected automatically.

Build runs the core, audio-export, artwork, single-window, tag-editing, registration and native UI checks, plus real setup/update/uninstall tests using a separate test app and isolated Windows registrations. Tests require FFmpeg/FFprobe on PATH; Build supplies it. Use `-SkipChecks` when only packaging an already verified build. To rebuild setup from an existing published app, run `scripts\Build-Installer.ps1`.

Source lives in `src\Museek`. The small selection model is in `src\Museek.Core`. Test screenshots are written into `artifacts`.

The app is unsigned. There is no Microsoft Store package or code-signing certificate included.
