# Changelog

Version notes are maintained alongside the code. See [GitHub Releases](https://github.com/SaoodCS/Museek/releases) for published versions and installers.

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
