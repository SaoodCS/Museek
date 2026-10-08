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

The distribution build also generates `libvlc/win-x64/plugins/plugins.dat`
with the exact bundled VLC core. This moves plugin catalog discovery to
packaging while keeping normal runtime scanning and its missing/corrupt-cache
fallback. The build helper stays in this checks project; it is not shipped.

Run the cache modes in separate processes:

```powershell
dotnet run --project tests/StartupChecks -c Release -- --generate-plugin-cache dist/Museek/libvlc/win-x64
dotnet run --no-build --project tests/StartupChecks -c Release -- --check-plugin-cache dist/Museek/libvlc/win-x64 --ffmpeg dist/Museek/tools/ffmpeg.exe --output artifacts/plugin-cache.json
```

The verifier checks the exact loaded core path, selected plugin-module counts,
WAV playback, optional generated MP3 playback, and an unchanged cache hash.
`--cache-only` disables scanning solely to prove that the cache contains the
required playback plugins. Production playback retains normal scanning.
Initialization measurements use fresh processes and include native loading and
decoder construction; filesystem caches may already be warm. They exclude
application launch and window rendering and are not reboot measurements.

`InstallerChecks.ps1 -VlcNativeDirectory <native-directory> -FfmpegPath <ffmpeg>`
installs the real cached runtime alongside its harmless app fixture. It checks
every plugin timestamp, cache bytes, normal/cache-only decoding, ZIP relocation,
and missing, corrupt, and stale-cache detection with working playback fallback.
It also verifies the installer removes its managed plugin cache on uninstall.
`Build.ps1` supplies these arguments automatically.
