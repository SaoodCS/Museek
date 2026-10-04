# Startup and playback checks

Run the normal checks with:

```powershell
dotnet run --project tests/StartupChecks -c Release
```

`scripts/Build.ps1` runs these checks automatically. They verify that an idle
player loads no native decoder, controls remain usable during asynchronous
preparation, concurrent requests share preparation, cancellation stops waiting,
and closing before or during preparation is safe. Silent temporary WAV files
exercise actual VLC playback, volume retention, pause, resume, seek, stop,
replacement, EOF, replay, and repeated disposal using dummy audio output.

Optional checks:

```powershell
dotnet run --project tests/StartupChecks -c Release -- --window --output artifacts/startup-window.json
dotnet run --project tests/StartupChecks -c Release -- --real-output --output artifacts/startup-audio.json
```

`--window` measures construction and first rendered content in a fresh process,
using production styles, isolated settings, and no audio file. It checks that
native VLC stays unloaded. It excludes process launch, .NET runtime startup, and
style loading before window construction.

`--real-output` uses the Windows audio output with the same silent fixtures and
also verifies actual native output volume. The playback-start notification only
signals an asynchronous continuation; native calls never run inside VLC's event
callback. The app uses its dispatcher for the equivalent volume application.

`--baseline --output artifacts/startup-before.json` records constructor and
synchronous-open measurements while skipping the newer preparation/disposal
cases. It still exits unsuccessfully when checks fail. Previous-implementation
startup failures were captured before the asynchronous cases were added; each
report describes the source actually built for its run.

The player-construction measurement precedes native initialization. The
first-open measurement includes preparation for the measured player but follows
the close-during-preparation scenario, so native files may already be warm; it
is not a cold end-to-end launch measurement. Private-memory differences include
normal runtime variation. Loaded-module checks directly establish whether VLC
was initialized.
