# Changelog

Version notes are maintained alongside the code. See [GitHub Releases](https://github.com/SaoodCS/Museek/releases) for published versions and installers.

## 1.4.0

- Added **Help → Check for updates** with the installed version, newer-release detection, download progress, and an explicit installation action.
- Verified update downloads against release checksums and installer versions before opening Setup.
- Made Setup wait for the updating Museek process to exit before replacing files.

See [the full version notes](releases/1.4.0.md).

## 1.3.2

- Packaged a validated VLC plugin cache to reduce first-playback decoder discovery.
- Preserved plugin timestamps during installation so the cache remains usable after updates.
- Bounded tag-editor artwork previews in both dimensions and checked source dimensions before decoding pixels.

See [the full version notes](releases/1.3.2.md).

## 1.3.1

- Centered Play in the app, including while trimming and resizing.
- Smoothed previous/next track changes with coordinated artwork and metadata, stable controls, and a brief transition that respects Windows animation settings.
- Deferred native playback initialization until audio is opened so the player window can appear sooner.
- Kept first-time decoder loading off the UI thread and reapplied the selected volume when audio output starts.

See [the full version notes](releases/1.3.1.md).

## 1.3.0

- Added a checked **Sort By** menu for Title, Artist, Album and Genre, with Title selected by default.
- Added Previous track and Next track buttons for the current audio file's folder, following the selected sort order. The buttons are hidden while trimming.

See [the full version notes](releases/1.3.0.md).

## 1.2.1

- Matched the app and installer icon to the green used by the in-app logo.

See [the full version notes](releases/1.2.1.md).

## 1.2.0

- Reduced playback and seeking allocations, bounded large-artwork validation buffers, and reused identical artwork in batch tag editing.
- Streamlined batch tag refreshes and grouped player window source by responsibility.
- Added bounded recovery for temporary Windows tag-save replacement failures, preserving source-conflict and cancellation checks.
- Removed obsolete planning documents and added repeatable allocation checks.

See [the full version notes](releases/1.2.0.md).

## 1.1.1

- About Museek now displays the app's release version, read from its build metadata.

See [the full version notes](releases/1.1.1.md).

## 1.1.0

- Added a conventional per-user Windows setup wizard, updates in the existing install folder and a native uninstaller.
- Added migration from the previous PowerShell-based installation while preserving settings and audio files.
- Includes playback and trimming, album artwork and artist/album display, stop controls, optional single-window mode and Explorer tag editing.

See [the full version notes](releases/1.1.0.md).
