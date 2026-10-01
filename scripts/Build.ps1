[CmdletBinding()]
param(
    [string]$DotNetPath,
    [string]$FfmpegDirectory,
    [switch]$SkipChecks
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = Join-Path $projectRoot 'dist\Museek'
$toolCache = Join-Path $projectRoot '.tools'

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
    & $DotNetPath publish 'src\Museek\Museek.csproj' -c Release -r win-x64 --self-contained true -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Museek publish failed.' }
    # Museek is x64. The NuGet package also ships an unused x86 VLC runtime.
    $x86 = [IO.Path]::GetFullPath((Join-Path $output 'libvlc\win-x86'))
    if (-not $x86.StartsWith([IO.Path]::GetFullPath($output) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid x86 cleanup path.' }
    if (Test-Path -LiteralPath $x86) { Remove-Item -LiteralPath $x86 -Recurse -Force }
    $tools = Join-Path $output 'tools'
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $FfmpegDirectory 'ffmpeg.exe'), (Join-Path $FfmpegDirectory 'ffprobe.exe') -Destination $tools -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $output -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1'), (Join-Path $PSScriptRoot 'Uninstall.ps1'), (Join-Path $PSScriptRoot 'Install.cmd') -Destination $output -Force
    $licenses = Join-Path $output 'licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') -File |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $licenses -Force }
    Get-ChildItem -LiteralPath (Split-Path -Parent $FfmpegDirectory) -File |
        Where-Object { $_.Name -match '^(LICENSE|COPYING)' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses ('FFmpeg-' + $_.Name)) -Force }
    & (Join-Path $tools 'ffmpeg.exe') -version | Select-Object -First 3 | Set-Content -LiteralPath (Join-Path $licenses 'FFmpeg-build.txt')
    if (-not $SkipChecks) {
        $originalPath = $env:PATH
        try {
            $env:PATH = $FfmpegDirectory + ';' + $originalPath
            foreach ($check in @('CoreChecks', 'ExportChecks', 'UiChecks')) {
                & $DotNetPath run --project (Join-Path $projectRoot "tests\$check") -c Release
                if ($LASTEXITCODE -ne 0) { throw "$check failed." }
            }
        } finally { $env:PATH = $originalPath }
    }
    Write-Host "Ready: $output\Museek.exe"
} finally { Pop-Location }
