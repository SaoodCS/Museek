Run on Windows x64 with .NET 10. The harness references the production WPF app and
loads its actual resources. It uses unshown controls and initializes the native VLC
decoder with muted dummy output before measuring paused timer updates. It uses an
isolated settings path and never opens audio, modifies settings or registers Explorer
commands. Decoder initialization is excluded from the measurements.

```powershell
dotnet run --project tests/PerformanceChecks -c Release -- --output artifacts/performance-checks.json
```

Use `--baseline` to record measurements before an optimization without enforcing
allocation ceilings. JSON is written in either mode, including measurements that
exceed a ceiling. The output path defaults to `artifacts/performance-checks.json`
relative to the current directory.

The harness measures current-thread managed allocations and elapsed time after
warmup, excluding fixture creation, reflection setup and initial layout:

- 10,000 moving playback positions must allocate at most 1,000,000 bytes.
- 10,000 stable paused-player timer updates must allocate at most 128,000 bytes.
- 1,000 rapidly replaced track transitions must allocate at most 8 KiB each.
  Replaced clocks must become collectible, and completion/reset must remove all
  animated properties. System animation preferences are respected and recorded.
- Validating a compressed 4096 x 4096 PNG must allocate at most 1 MiB per call.
  Three measured calls catch reintroduction of a full-frame managed scratch buffer.

It also verifies that the seek position and paused time display update, and that the
real tag validator accepts the image as PNG. Elapsed time is recorded for comparison;
it is not a pass/fail threshold because machines and CI runners differ. These are
allocation checks, not whole-process working-set or native decoder memory limits.
