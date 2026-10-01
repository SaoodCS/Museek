Run on Windows x64 with .NET 10 and FFmpeg/ffprobe available:

```powershell
.\.tools\dotnet\dotnet.exe run --project tests\UiChecks\UiChecks.csproj -c Release
```

The harness loads the production WPF resources and uses real VLC playback and FFmpeg export. It exercises Unicode file loading, autoplay, pause, seek, EOF replay, trim handles, preview bounds, cancellation, damaged files, and closing during an active export. Its audio fixture is silent and its volume is zero.

The window stays unshown. Rendered client-content snapshots are saved as `artifacts/normal.png` and `artifacts/trim.png` beside this project. An optional command-line argument chooses the artifacts directory. Test fixtures are removed after each run.

Save-file dialogs and Windows default-app registration require separate interactive checks; this harness never invokes them.
