# Museek

A simple audio player, trimmer and tag editor for **Windows 11 x64**.

## Features

- Play many audio formats, including MP3, FLAC, WAV, M4A, OGG and WMA.
- Open audio from File Explorer, drag files into Museek, or press **Ctrl+O**.
- Display the song title, artist, album and embedded album artwork.
- Play/pause, adjust volume and seek through a song. **Stop** returns to the song start or the selected trim start.
- Use **Previous track** and **Next track** to play audio in the current file's folder. Choose alphabetical **Sort By → Title, Artist, Album or Genre**; **Title** is selected by default. Navigation stops at the folder's first or last track, and the buttons are hidden while trimming.
- Trim a selected range and save a separate WAV, MP3, FLAC, M4A, OGG or Opus copy.
- Choose Museek as your default audio app through **Tools → Choose Museek as default…**.
- Enable **Tools → Single-window mode** to reuse the current player when opening another file.
- Enable **Tools → 'Edit Tags' context menu** to edit one or more files from Explorer's **Show more options** menu. Update title, artist, album, genre and other tags, and add, change or remove album artwork without re-encoding the audio.

Protected/DRM audio and files without a supported decoder cannot be played.

## Installation

You do not need VS Code or the .NET SDK to use Museek. The installer includes the required runtimes and audio tools. Current builds are unsigned.

**Install**

1. Open the [Museek Releases page](https://github.com/SaoodCS/Museek/releases).
2. Open the latest release and expand **Assets**.
3. Download `Museek-X.Y.Z-setup.exe`, where `X.Y.Z` is the version number.
4. Double-click the downloaded file and follow the setup wizard.
5. Open **Museek** from the Windows Start menu.

Setup installs for your Windows account at `%LOCALAPPDATA%\Programs\Museek` without administrator rights. To open audio with Museek, right-click a file and choose **Open with → Museek**. To make it the default, use **Tools → Choose Museek as default…** and select your audio file types in Windows Settings.

**Update**

Choose **Help → Check for updates** to look for a newer stable release. If one is available, choose **Download update**, then **Install update**. Museek verifies the download, closes, and opens Setup; follow its installation steps. Close any other Museek or tag editor windows before installing. Your settings and audio files are kept. Checks and installation happen only when you request them.

You can also close Museek, download the newer installer from the same Releases page, and run it.

**Uninstall**

Open Windows **Settings → Apps → Installed apps**, find **Museek**, and choose **Uninstall**. Alternatively, press **Win+R**, enter `%LOCALAPPDATA%\Programs\Museek\uninstall.exe`, and press Enter. Your audio files and settings are kept.

## Development

Development means editing the source code and running your changes. Use a Windows 11 x64 computer.

1. Install [Visual Studio Code](https://code.visualstudio.com/), the [.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0), and [Git for Windows](https://git-scm.com/download/win). Choose the **SDK**, not only the runtime.
2. In VS Code, install Microsoft's [C# extension](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csharp). Restart VS Code after installing the SDK and Git.
3. Open **Terminal → New Terminal** in VS Code and choose **PowerShell**. Download the project:

```powershell
git clone https://github.com/SaoodCS/Museek.git
```

4. Use **File → Open Folder** to open the downloaded `Museek` folder. Open a new PowerShell terminal there. It should contain `Museek.sln`, `src` and `scripts`.
5. Allow the project's local PowerShell scripts in this terminal, then prepare the audio tools once:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
.\scripts\Build.ps1 -SkipChecks
```

The execution policy command applies only to this terminal and needs no administrator rights. Repeat it in a new terminal before running the project's scripts. The build command downloads dependencies and creates a local build; the first run can take several minutes. It does not install Museek on your computer.

6. In that terminal, run:

```powershell
$env:PATH = "$((Resolve-Path .\dist\Museek\tools).Path);$env:PATH"
dotnet watch --project .\src\Museek\Museek.csproj
```

The first line lets the development app find FFmpeg and FFprobe. Repeat it when opening a new terminal. The second line builds and starts Museek, then watches your source files for changes.

Supported **C#** edits can hot reload while the app is running. Other edits may require a restart. **XAML layout changes are not automatically hot reloaded in this VS Code flow**; press **Ctrl+R** in the terminal to rebuild and restart after saving them. Press **Ctrl+C** to stop development. See the [dotnet watch documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-watch) for supported changes.

To run without watching for changes, use `dotnet run --project .\src\Museek\Museek.csproj` instead, after setting the audio tools path above. The app code is in `src\Museek`; its trim-selection model is in `src\Museek.Core`. The player window code is grouped by playback, file operations, and settings. Disposable integration checks live in `tests`; generated tools, builds, and test artifacts are ignored by Git.

## Build

Building creates the app files and installer you can run or share.

1. Complete the Development prerequisites and open the project folder in VS Code.
2. Stop the development app with **Ctrl+C** and close any copy running from `dist`.
3. In VS Code's PowerShell terminal, run:

```powershell
.\scripts\Build.ps1
```

The script restores dependencies, includes the .NET runtime and audio tools, runs the app/installer/release checks, and builds the installer. NSIS, the installer-building tool, is downloaded automatically; you do not need to install it separately.

After a successful build:

- Run `dist\Museek\Museek.exe` to try the built app directly. Keep the entire `dist\Museek` folder together.
- Run `dist\setup.exe` to install or update your local copy.

You can start the built app from the same terminal:

```powershell
.\dist\Museek\Museek.exe
```

`-SkipChecks` makes a faster build while developing. Use the full command above when preparing a release.

The full build also checks managed allocations during playback updates, seeking, and large-artwork validation. To record those measurements separately, run `dotnet run --project tests/PerformanceChecks -c Release`. Results are saved in `artifacts/performance-checks.json`; see [the performance checks](tests/PerformanceChecks/README.md) for details.

## Releasing

GitHub releases are triggered by pushing an explicit **`vX.Y.Z` version tag**. Normal pushes to `main` do not publish a release.

For example, to release **1.2.0**:

1. Put the finished changes on `main` and review them.
2. In `src\Museek\Museek.csproj`, change the version to `<Version>1.2.0</Version>`.
3. Create `releases\1.2.0.md` with the changes and any upgrade instructions, and add the version to `CHANGELOG.md`. Update other documentation and licenses when needed.
4. Validate, build and prepare the release files in VS Code's PowerShell terminal:

```powershell
.\scripts\Get-ReleaseInfo.ps1 -Tag v1.2.0
.\scripts\Build.ps1
.\scripts\Prepare-Release.ps1 -Tag v1.2.0
```

Resolve any errors, then review the files in `dist\release`. The app version, tag and release notes filename must match.

5. Commit the reviewed changes on `main`, push them, then create and push that version tag:

```powershell
git add .
git commit -m "Prepare Museek 1.2.0"
git push origin main
git tag -a v1.2.0 -m "Museek 1.2.0"
git push origin v1.2.0
```

GitHub Actions builds and checks that tagged version, then publishes **Museek 1.2.0** with its installer, release notes, README, changelog, third-party notices, licenses and SHA256 checksums. GitHub also supplies source archives. Check the workflow under **Actions** and confirm the files on the **Releases** page.

No personal access token is needed for the workflow; it uses GitHub's built-in token. Existing published releases are not overwritten. If a workflow fails, review its Actions log before retrying. Fix source or packaging problems in a new commit and use a new version tag; never move a published tag or replace its assets.
