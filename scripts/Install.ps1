[CmdletBinding()]
param(
    [string]$Source
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $Source) {
    $Source = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Museek.exe') -PathType Leaf) {
        $PSScriptRoot
    } else {
        Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\Museek'
    }
}

function Assert-NoReparsePath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to install through a junction or symbolic link: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

$localData = [Environment]::GetFolderPath('LocalApplicationData')
if ([string]::IsNullOrWhiteSpace($localData)) { throw 'LocalApplicationData is unavailable.' }
$programsDirectory = [IO.Path]::GetFullPath((Join-Path $localData 'Programs'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $programsDirectory 'Museek'))
$expectedDirectory = [IO.Path]::Combine($programsDirectory, 'Museek')
if (-not [string]::Equals($installDirectory, $expectedDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The install directory is outside the expected per-user Programs directory.'
}
Assert-NoReparsePath $installDirectory

$sourceDirectory = (Resolve-Path -LiteralPath $Source).ProviderPath
if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) { throw 'Source must be a directory.' }
Assert-NoReparsePath $sourceDirectory
if ([string]::Equals($sourceDirectory.TrimEnd('\'), $installDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
    $sourceDirectory.StartsWith($installDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $installDirectory.StartsWith($sourceDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source and install directories must not overlap.'
}
$payload = @(Get-ChildItem -LiteralPath $sourceDirectory -Force -Recurse)
if ($payload | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
    throw 'The source contains a junction or symbolic link.'
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'Museek.exe') -PathType Leaf)) {
    throw "Museek.exe was not found in '$sourceDirectory'. Run the publish script first."
}
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -PathType Leaf)) {
    throw 'Uninstall.ps1 must be beside Install.ps1.'
}

$executable = Join-Path $installDirectory 'Museek.exe'
if (Get-Process -Name Museek -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }) {
    throw 'Close the installed Museek app before installing an update.'
}
$markerPath = Join-Path $installDirectory '.museek-install.json'
if (Test-Path -LiteralPath $installDirectory) {
    if (-not (Test-Path -LiteralPath $installDirectory -PathType Container) -or
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw 'The existing Museek folder is not an installation created by this installer.'
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.AppId -ne 'Museek' -or $marker.SchemaVersion -ne 1 -or
        -not [string]::Equals($marker.InstallDirectory, $installDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The existing Museek installation marker is invalid.'
    }
    if (Get-ChildItem -LiteralPath $installDirectory -Force -Recurse |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
        throw 'The existing installation contains a junction or symbolic link.'
    }
}

$startMenu = [Environment]::GetFolderPath('Programs')
if ([string]::IsNullOrWhiteSpace($startMenu)) { throw 'The per-user Start menu is unavailable.' }
Assert-NoReparsePath $startMenu
$shortcutPath = Join-Path $startMenu 'Museek.lnk'
Assert-NoReparsePath $shortcutPath
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
if ((Test-Path -LiteralPath $shortcutPath) -and
    -not [string]::Equals($shortcut.TargetPath, $executable, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'An existing Museek Start menu shortcut points to another application.'
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
@{ AppId = 'Museek'; SchemaVersion = 1; InstallDirectory = $installDirectory } |
    ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
foreach ($item in Get-ChildItem -LiteralPath $sourceDirectory -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $installDirectory -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination (Join-Path $installDirectory 'Uninstall.ps1') -Force
@{ AppId = 'Museek'; SchemaVersion = 1; InstallDirectory = $installDirectory } |
    ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8

$registration = Start-Process -FilePath $executable -ArgumentList '--register' -PassThru -Wait -WindowStyle Hidden
if ($registration.ExitCode -ne 0) { throw "Museek registration failed with exit code $($registration.ExitCode)." }

New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.IconLocation = "$executable,0"
$shortcut.Description = 'Play your local music with Museek'
$shortcut.Save()

$uninstallCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installDirectory 'Uninstall.ps1') + '"'
$uninstallKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\Museek')
try {
    $uninstallKey.SetValue('DisplayName', 'Museek')
    $uninstallKey.SetValue('DisplayVersion', [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion)
    $uninstallKey.SetValue('DisplayIcon', "$executable,0")
    $uninstallKey.SetValue('InstallLocation', $installDirectory)
    $uninstallKey.SetValue('UninstallString', $uninstallCommand)
    $uninstallKey.SetValue('NoModify', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
    $uninstallKey.SetValue('NoRepair', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
} finally { $uninstallKey.Dispose() }

Write-Host "Installed Museek for your account at $installDirectory"
Write-Host 'Open Museek from Start. Tools contains Default Apps and the optional Edit Tags context menu setting.'
Write-Host 'When enabled, right-click selected audio files and choose Show more options > Edit Tags.'
