[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$PayloadDirectory,
    [string]$InstallerPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($ProjectRoot)
$info = & (Join-Path $PSScriptRoot 'Get-ReleaseInfo.ps1') -Tag $Tag -ProjectRoot $root
if (-not $PayloadDirectory) { $PayloadDirectory = Join-Path $root 'dist\Museek' }
if (-not $InstallerPath) { $InstallerPath = Join-Path $root 'dist\setup.exe' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist\release' }
$payload = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
$installer = [IO.Path]::GetFullPath($InstallerPath)
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$distPrefix = [IO.Path]::GetFullPath((Join-Path $root 'dist')).TrimEnd('\') + '\'
if (-not $output.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $output.Equals($payload, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $payload.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $installer.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must be a separate directory inside the project dist folder.'
}

function Assert-RegularPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release paths must not contain symbolic links or junctions: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Assert-BinaryVersion([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Build the release first; missing binary: $Path" }
    Assert-RegularPath $Path
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $expected = [Version]$info.Version
    if ($actual.FileMajorPart -ne $expected.Major -or $actual.FileMinorPart -ne $expected.Minor -or
        $actual.FileBuildPart -ne $expected.Build -or $actual.FilePrivatePart -ne 0) {
        throw "Binary version does not match $Tag`: $Path ($($actual.FileVersion)). Rebuild before releasing."
    }
}

Assert-RegularPath $output
Assert-BinaryVersion $installer
Assert-BinaryVersion (Join-Path $payload 'Museek.exe')
$licenses = Join-Path $payload 'licenses'
if (-not (Test-Path -LiteralPath $licenses -PathType Container)) { throw 'Published licenses are missing. Run Build.ps1 first.' }
Assert-RegularPath $licenses
$licenseFiles = @(Get-ChildItem -LiteralPath $licenses -Recurse -Force)
if (-not ($licenseFiles | Where-Object { -not $_.PSIsContainer })) { throw 'Published licenses are empty.' }
if ($licenseFiles | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
    throw 'Published licenses must not contain symbolic links or junctions.'
}
$documents = @('README.md', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md')
foreach ($document in $documents) {
    $path = Join-Path $root $document
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or [string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($path))) {
        throw "Required release document is missing or empty: $document"
    }
    Assert-RegularPath $path
}
Assert-RegularPath $info.NotesPath

# This generated directory contains a fixed set of release assets. Reject other contents
# before replacing anything; remove only files using our reserved distribution names.
$fixedNames = @('release-notes.md', 'README.md', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'licenses.zip', 'SHA256SUMS.txt')
$oldFiles = @()
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container)) { throw 'Release output is not a directory.' }
    $oldFiles = @(Get-ChildItem -LiteralPath $output -Force)
    foreach ($file in $oldFiles) {
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($file.Name -cnotin $fixedNames -and $file.Name -cnotmatch '^Museek-[0-9]+\.[0-9]+\.[0-9]+-setup\.exe$')) {
            throw "Release output contains an unrecognized file or folder: $($file.FullName)"
        }
    }
}

# Prepare everything before touching the previous asset set. Staging is on the same drive.
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
$stage = Join-Path (Split-Path -Parent $output) ('.release-stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Copy-Item -LiteralPath $installer -Destination (Join-Path $stage "Museek-$($info.Version)-setup.exe")
    Copy-Item -LiteralPath $info.NotesPath -Destination (Join-Path $stage 'release-notes.md')
    foreach ($document in $documents) { Copy-Item -LiteralPath (Join-Path $root $document) -Destination $stage }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($licenses, (Join-Path $stage 'licenses.zip'))
    $checksums = @(Get-ChildItem -LiteralPath $stage -File | Sort-Object Name | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
    })
    [IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    foreach ($file in $oldFiles) { Remove-Item -LiteralPath $file.FullName -Force }
    foreach ($file in Get-ChildItem -LiteralPath $stage -File) { Move-Item -LiteralPath $file.FullName -Destination $output }
} finally {
    # The exact staging directory was just created here; cleanup never recurses.
    if (Test-Path -LiteralPath $stage -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $stage -File) { Remove-Item -LiteralPath $file.FullName -Force }
        Remove-Item -LiteralPath $stage
    }
}
Write-Host "Release assets ready: $output"
$output
