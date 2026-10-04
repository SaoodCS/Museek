# Folder track checks

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet run --project tests/TrackChecks/TrackChecks.csproj -c Release
```

The 38 checks cover Title/Artist/Album/Genre ordering, missing tags, case-insensitive comparisons, deterministic ties, metadata versus filenames, and WAV RIFF/ID3 precedence. Disposable silent WAV files also verify direct-folder boundaries, excluded files/subfolders, corrupt or locked metadata fallback, unknown-extension current files, directory changes, cancellation, and reported source/folder errors.

Fixtures are created in a unique temporary directory and removed afterward. No music library, settings, registry, player, or audio output is used; file hashes verify that loading and sorting preserve fixture bytes. The project links the production services without starting Museek.
