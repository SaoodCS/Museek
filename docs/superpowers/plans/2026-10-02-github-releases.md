# GitHub Releases Implementation Plan

**Goal:** Publish a tested Windows installer, version, release notes, checksums and supporting documents when the maintainer pushes an explicit version tag.

**Architecture:** A `vX.Y.Z` tag is the release trigger. Validate it against `src/Museek/Museek.csproj` and `releases/X.Y.Z.md`, run the existing Windows build/checks, stage release assets, then publish through GitHub's built-in token. Main-branch pushes do not publish releases. Do not create or push a real tag/release during implementation.

**Tech stack:** PowerShell, GitHub Actions, GitHub CLI, existing .NET/NSIS build.

## Tasks

- [x] Add release metadata validation and asset staging scripts. Reject invalid/mismatched tags, missing notes and installer version mismatches. Package versioned setup, README, notices, licenses and SHA256SUMS.
- [x] Add a tag-only workflow with read-only build permissions, a separate release publishing job, required checks and source-revision checkout. Publish notes and all staged files for that tag.
- [x] Add current-version notes, a changelog and a short release guide. Document version/notes changes and explicit tag commands.
- [x] Verify scripts with positive and negative cases; validate workflow configuration and assets; review the integrated flow without publishing.

Verification: full Build.ps1 passed, including 222 UI/playback checks, 104 installer checks and 174 release checks. Release checks also passed in Windows PowerShell 5.1; actionlint and isolated publication previews passed. Headless VLC playback passed with dummy output, enabled only for CI tests. No real GitHub tag, workflow run or release was created.
