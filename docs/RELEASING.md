# Releasing Museek

Releases are deliberate: push a tag named `vX.Y.Z` when a version is ready. Pushing changes to `main` alone does not publish a release.

## Prepare the version

For example, to release **1.2.0**:

1. Set `<Version>1.2.0</Version>` in `src/Museek/Museek.csproj`. Use three numeric components; the tag, project version and notes filename must match.
2. Add `releases/1.2.0.md` describing the changes, fixes and any upgrade instructions. Use `releases/1.1.0.md` as a starting point. These notes become the GitHub release description.
3. Add a `1.2.0` entry at the top of `CHANGELOG.md`. Update README and third-party notices/licenses when the changes require it.
4. On Windows 11 x64 with the .NET 10 SDK, validate and build from the repository root:

```powershell
.\scripts\Get-ReleaseInfo.ps1 -Tag v1.2.0
.\scripts\Build.ps1
.\scripts\Prepare-Release.ps1 -Tag v1.2.0
```

Build runs the app and installer checks. Resolve any failures before tagging. Review the files in `dist/release` and the release notes; do not use `-SkipChecks` for a release.

## Publish the version

Commit the prepared files and push them to `main`, then tag that same commit. Replace the example version if needed:

```powershell
git add src/Museek/Museek.csproj releases/1.2.0.md CHANGELOG.md
git commit -m "Prepare Museek 1.2.0"
git push origin main
git tag -a v1.2.0 -m "Museek 1.2.0"
git push origin v1.2.0
```

Include any other changed release files in the commit. The commands assume you are on `main` and have reviewed the changes being committed. Push only the specific release tag.

GitHub Actions checks out the tagged commit, validates the version/notes, builds and tests Museek, then publishes [the release](https://github.com/SaoodCS/Museek/releases). It uses GitHub's automatically provided token; no personal access token is required. Actions must be enabled, and the publishing job must be allowed `contents: write` permission.

Each release contains:

- `Museek-1.2.0-setup.exe` — the Windows installer, with its version in the filename.
- `release-notes.md`, `README.md` and `CHANGELOG.md` — changes and usage information.
- `THIRD-PARTY-NOTICES.md` and `licenses.zip` — bundled component notices and licenses.
- `SHA256SUMS.txt` — checksums for the other release files.

GitHub also provides source archives for the tag. The installer includes the app's runtimes and installs for the current Windows account without administrator rights. Museek supports Windows 11 x64 and is currently unsigned. To update an installed copy, close Museek and run the newer installer. There is no in-app updater.

## If a release fails

Inspect the failed job under GitHub **Actions**. For a temporary download or service failure, rerun the failed workflow after the problem is resolved. The retry still builds the original tagged commit; later pushes to `main` do not change it.

If the tagged code, version or release notes need changes, fix them on `main` and prepare a new version/tag. Do not move or overwrite a published release tag. Confirm the successful workflow and installer assets on the Releases page before announcing the version.
