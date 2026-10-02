These checks generate disposable audio and image fixtures in a unique temporary
directory. They never modify the music library, user settings, Explorer registration,
or Windows defaults, and they do not play audio.

Run through `scripts/Build.ps1`, or run the project with the bundled FFmpeg directory
on `PATH`.

Coverage includes MP3, FLAC, WAV, M4A, OGG and Opus tag updates, untouched fields,
PNG/JPEG artwork changes, independent FFprobe reads, exact decoded-audio hashes,
partial batches, cancellation, read-only and locked files, hard links, and external
source replacement while saving. The WPF editor, Tools checkbox and isolated native
Shell selection checks are in `UiChecks`.
