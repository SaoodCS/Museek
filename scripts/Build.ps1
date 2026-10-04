[CmdletBinding()]
param(
    [string]$DotNetPath,
    [string]$FfmpegDirectory,
    [string]$MakensisPath,
    [switch]$SkipChecks
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = Join-Path $projectRoot 'dist\Museek'
$toolCache = Join-Path $projectRoot '.tools'
$dist = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
$staging = Join-Path $dist ('.museek-stage-' + [Guid]::NewGuid().ToString('N'))
$previous = Join-Path $dist ('.museek-previous-' + [Guid]::NewGuid().ToString('N'))

function Assert-BuildPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($dist + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build output path is outside the project dist folder: $absolute"
    }
    $cursor = $absolute
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Build output paths must not contain symbolic links or junctions: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    if (Test-Path -LiteralPath $absolute) {
        if (-not (Test-Path -LiteralPath $absolute -PathType Container)) {
            throw "Build output path is not a directory: $absolute"
        }
        # Check each directory before descending so a junction is never traversed.
        $directories = [Collections.Generic.Stack[string]]::new()
        $directories.Push($absolute)
        while ($directories.Count -gt 0) {
            foreach ($item in Get-ChildItem -LiteralPath $directories.Pop() -Force) {
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Build output contains a symbolic link or junction: $($item.FullName)"
                }
                if ($item.PSIsContainer) { $directories.Push($item.FullName) }
            }
        }
    }
    return $absolute
}

# Validate the existing payload before publishing or changing any build output.
[void](Assert-BuildPath $output)
[void](Assert-BuildPath $staging)
[void](Assert-BuildPath $previous)

if (-not $DotNetPath) {
    $portableSdk = Join-Path $toolCache 'dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $portableSdk) { $DotNetPath = $portableSdk }
    else { $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
if (-not (& $DotNetPath --list-sdks | Select-String '^10\.')) {
    throw 'Build requires the .NET 10 SDK. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
}

if (-not $FfmpegDirectory) {
    $mediaCache = Join-Path $toolCache 'ffmpeg'
    $cached = Get-ChildItem -LiteralPath $mediaCache -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cached) {
        Write-Host 'Downloading the FFmpeg essentials build and verifying its SHA256 checksum...'
        New-Item -ItemType Directory -Path $mediaCache -Force | Out-Null
        $archive = Join-Path $toolCache 'ffmpeg-essentials.zip'
        $buildUrl = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $buildUrl -OutFile $archive -UseBasicParsing
        $expected = ((Invoke-RestMethod -Uri ($buildUrl + '.sha256')).Trim() -split '\s+')[0]
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'FFmpeg checksum mismatch.' }
        Expand-Archive -LiteralPath $archive -DestinationPath $mediaCache -Force
        $cached = Get-ChildItem -LiteralPath $mediaCache -Filter ffmpeg.exe -Recurse | Select-Object -First 1
    }
    if (-not $cached) { throw 'FFmpeg executable was not found after extraction.' }
    $FfmpegDirectory = $cached.DirectoryName
}
foreach ($file in @('ffmpeg.exe', 'ffprobe.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $FfmpegDirectory $file) -PathType Leaf)) {
        throw "$file is missing from $FfmpegDirectory"
    }
}

Push-Location $projectRoot
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    & $DotNetPath publish 'src\Museek\Museek.csproj' -c Release -r win-x64 --self-contained true -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Museek publish failed.' }
    # Museek is x64. The NuGet package also ships an unused x86 VLC runtime.
    $x86 = Assert-BuildPath (Join-Path $staging 'libvlc\win-x86')
    if (Test-Path -LiteralPath $x86) { Remove-Item -LiteralPath $x86 -Recurse -Force }
    $tools = Join-Path $staging 'tools'
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $FfmpegDirectory 'ffmpeg.exe'), (Join-Path $FfmpegDirectory 'ffprobe.exe') -Destination $tools -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $staging -Force
    $licenses = Join-Path $staging 'licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') -File |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $licenses -Force }
    Get-ChildItem -LiteralPath (Split-Path -Parent $FfmpegDirectory) -File |
        Where-Object { $_.Name -match '^(LICENSE|COPYING)' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses ('FFmpeg-' + $_.Name)) -Force }
    & (Join-Path $tools 'ffmpeg.exe') -version | Select-Object -First 3 | Set-Content -LiteralPath (Join-Path $licenses 'FFmpeg-build.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Could not record the bundled FFmpeg build information.' }

    # A publish overlays its target. Promote only a complete fresh payload so removed
    # assemblies or old distribution files cannot survive into the next installer.
    [void](Assert-BuildPath $staging)
    [void](Assert-BuildPath $output)
    [void](Assert-BuildPath $previous)
    if (Test-Path -LiteralPath $output) { Move-Item -LiteralPath $output -Destination $previous }
    try {
        [void](Assert-BuildPath $staging)
        [void](Assert-BuildPath $output)
        Move-Item -LiteralPath $staging -Destination $output
    } catch {
        # The first rename leaves the prior payload intact until promotion succeeds.
        if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $output)) {
            [void](Assert-BuildPath $previous)
            [void](Assert-BuildPath $output)
            Move-Item -LiteralPath $previous -Destination $output
        }
        throw
    }
    if (Test-Path -LiteralPath $previous) {
        [void](Assert-BuildPath $previous)
        Remove-Item -LiteralPath $previous -Recurse -Force
    }
    if (-not $SkipChecks) {
        $originalPath = $env:PATH
        try {
            $env:PATH = $FfmpegDirectory + ';' + $originalPath
            foreach ($check in @('CoreChecks', 'TrackChecks', 'ExportChecks', 'ArtworkChecks', 'InstanceChecks', 'TagChecks', 'RegistrationChecks', 'UiChecks', 'PerformanceChecks')) {
                & $DotNetPath run --project (Join-Path $projectRoot "tests\$check") -c Release
                if ($LASTEXITCODE -ne 0) { throw "$check failed." }
            }
        } finally { $env:PATH = $originalPath }
    }
    & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -MakensisPath $MakensisPath
    if (-not $SkipChecks) {
        & (Join-Path $projectRoot 'tests\InstallerChecks.ps1') -NsisPath $MakensisPath -DotNetPath $DotNetPath
        & (Join-Path $projectRoot 'tests\ReleaseChecks.ps1') -DotNetPath $DotNetPath
    }
    Write-Host "Ready: $output\Museek.exe"
} finally {
    try {
        if (Test-Path -LiteralPath $staging) {
            [void](Assert-BuildPath $staging)
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    } finally { Pop-Location }
}
