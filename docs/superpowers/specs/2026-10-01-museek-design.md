# Museek

Build a simple native Windows 11 audio player and trimmer. The requested controls are song title, play/pause, volume, seek, and Trim. Trim mode replaces Trim with Save and Cancel and shows draggable start/end handles on the seek bar.

Use WPF on .NET 10, LibVLCSharp with bundled VLC for broad local audio playback, and bundled FFmpeg/FFprobe for metadata and precise exports. Target Windows 11 x64. Playback accepts a file argument from Explorer, an Open dialog, or drag and drop. Show embedded title with filename fallback. Encrypted/DRM media, corrupt files and non-audio files receive a readable error; no decoder can promise every possible file.

Use a compact dark window, green accent, native title bar and accessible labeled controls. Space toggles playback, Ctrl+O opens, and Escape cancels trimming. Seeking and volume are available in both modes. Trim handles never cross and require a positive range; playback previews the selected range and pauses at its end. Cancel leaves the source unchanged and returns to normal playback controls.

The 2026-10-02 UI update removes the Open file button, filename subtitle and trim slogan. Show embedded album artwork above the song title, with a simple placeholder when no artwork is available. Artwork is loaded locally and must clear when the file changes; unavailable or damaged artwork must not interrupt audio playback. Explorer, drag and drop and Ctrl+O remain available.

Display the artist name and album name on separate lines under the song title, reading global tags with first-audio-stream fallback. Hide unavailable names and clear them immediately when the file changes or cannot be opened.

Save As defaults to a new filename and chooses an output format from WAV, MP3, FLAC, M4A, OGG or Opus. Decode and re-encode for accurate boundaries. Never overwrite the original. Write a temporary output beside the destination and only replace the destination after successful completion. Cancel an in-progress save safely, and retain the selected range after failures.

Register supported audio extensions, Open With and default-app capabilities for the current user. Installation is optional and requires no administrator rights. Windows controls the user's choice of default app; provide a button that registers Museek and opens its Windows Settings page. Do not change this computer's associations during development.

Deliver source, build/install/uninstall scripts, README, third-party notices and a runnable self-contained publish folder at dist/Museek. Verify range invariants, real export durations/source preservation, argument handling, playback and trim-mode controls.
