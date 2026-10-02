[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-NoReparsePath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to uninstall through a junction or symbolic link: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Test-SamePath([object]$Left, [string]$Right) {
    return [string]::Equals([string]$Left, $Right, [StringComparison]::OrdinalIgnoreCase)
}

$localData = [Environment]::GetFolderPath('LocalApplicationData')
if ([string]::IsNullOrWhiteSpace($localData)) { throw 'LocalApplicationData is unavailable.' }
$programsDirectory = [IO.Path]::GetFullPath((Join-Path $localData 'Programs'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $programsDirectory 'Museek'))
$expectedDirectory = [IO.Path]::Combine($programsDirectory, 'Museek')
if (-not (Test-SamePath $installDirectory $expectedDirectory)) {
    throw 'The uninstall directory is outside the expected per-user Programs directory.'
}
Assert-NoReparsePath $installDirectory

$executable = Join-Path $installDirectory 'Museek.exe'
if (Get-Process -Name Museek -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }) {
    throw 'Close the installed Museek app before uninstalling it.'
}
if (Test-Path -LiteralPath $installDirectory) {
    $markerPath = Join-Path $installDirectory '.museek-install.json'
    if (-not (Test-Path -LiteralPath $installDirectory -PathType Container) -or
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw 'Refusing to remove a folder without a Museek installation marker.'
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($marker.AppId -ne 'Museek' -or $marker.SchemaVersion -ne 1 -or
        -not (Test-SamePath $marker.InstallDirectory $installDirectory)) {
        throw 'Refusing to remove a folder with an invalid Museek installation marker.'
    }
    if (Get-ChildItem -LiteralPath $installDirectory -Force -Recurse |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
        throw 'Refusing to remove an installation containing a junction or symbolic link.'
    }
}

$shortcut = $null
$shortcutPath = $null
$startMenu = [Environment]::GetFolderPath('Programs')
if (-not [string]::IsNullOrWhiteSpace($startMenu)) {
    $shortcutPath = Join-Path $startMenu 'Museek.lnk'
    if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
        Assert-NoReparsePath $shortcutPath
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
    }
}

if (-not ('Museek.UninstallShellNotification' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Museek {
    public static class UninstallShellNotification {
        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
'@
}

$registry = [Microsoft.Win32.Registry]::CurrentUser

# The optional tag verb has its own owner marker: it may exist without default-app registration.
# Preserve another Museek copy's verb and every neighboring application key.
$systemAssociations = $registry.OpenSubKey('Software\Classes\SystemFileAssociations', $true)
if ($systemAssociations) {
    try {
        foreach ($extension in $systemAssociations.GetSubKeyNames()) {
            if (-not $extension.StartsWith('.')) { continue }
            $verbPath = $extension + '\shell\Museek.EditTags'
            $verb = $systemAssociations.OpenSubKey($verbPath)
            try {
                $ownsVerb = $verb -and $verb.GetValue('MuseekOwner') -eq 'Museek.EditTags.v1' -and
                    (Test-SamePath $verb.GetValue('MuseekExecutable') $executable)
            } finally { if ($verb) { $verb.Dispose() } }
            if ($ownsVerb) { $systemAssociations.DeleteSubKeyTree($verbPath, $false) }
        }
    } finally { $systemAssociations.Dispose() }
}
$tagClassPath = 'Software\Classes\CLSID\{5A7C0D7E-9755-4F2E-ABC2-793EE90650BC}'
$tagClass = $registry.OpenSubKey($tagClassPath)
try {
    $ownsTagClass = $tagClass -and $tagClass.GetValue('MuseekOwner') -eq 'Museek.EditTags.v1' -and
        (Test-SamePath $tagClass.GetValue('MuseekExecutable') $executable)
} finally { if ($tagClass) { $tagClass.Dispose() } }
if ($ownsTagClass) { $registry.DeleteSubKeyTree($tagClassPath, $false) }

$expectedCommand = '"' + $executable + '" "%1"'
$appKey = $registry.OpenSubKey('Software\Museek')
try { $ownsRegistration = $appKey -and (Test-SamePath $appKey.GetValue('ExecutablePath') $executable) }
finally { if ($appKey) { $appKey.Dispose() } }
$progIdCommand = $registry.OpenSubKey('Software\Classes\Museek.Audio\shell\open\command')
try { $ownsProgId = $progIdCommand -and (Test-SamePath $progIdCommand.GetValue('') $expectedCommand) }
finally { if ($progIdCommand) { $progIdCommand.Dispose() } }

if ($ownsProgId) {
    # Remove only our named value. Keep extension keys and every other application's entries.
    $classes = $registry.OpenSubKey('Software\Classes', $true)
    try {
        foreach ($extension in $classes.GetSubKeyNames()) {
            if (-not $extension.StartsWith('.')) { continue }
            $openWith = $classes.OpenSubKey($extension + '\OpenWithProgids', $true)
            if ($openWith) {
                try { $openWith.DeleteValue('Museek.Audio', $false) }
                finally { $openWith.Dispose() }
            }
        }
    } finally { if ($classes) { $classes.Dispose() } }
    $registry.DeleteSubKeyTree('Software\Classes\Museek.Audio', $false)
}

$applicationCommand = $registry.OpenSubKey('Software\Classes\Applications\Museek.exe\shell\open\command')
try { $ownsApplication = $applicationCommand -and (Test-SamePath $applicationCommand.GetValue('') $expectedCommand) }
finally { if ($applicationCommand) { $applicationCommand.Dispose() } }
if ($ownsApplication) { $registry.DeleteSubKeyTree('Software\Classes\Applications\Museek.exe', $false) }

if ($ownsRegistration) {
    $registered = $registry.OpenSubKey('Software\RegisteredApplications', $true)
    if ($registered) {
        try {
            if (Test-SamePath $registered.GetValue('Museek') 'Software\Museek\Capabilities') {
                $registered.DeleteValue('Museek', $false)
            }
        } finally { $registered.Dispose() }
    }
    $registry.DeleteSubKeyTree('Software\Museek\Capabilities', $false)
    $appKey = $registry.OpenSubKey('Software\Museek', $true)
    if ($appKey) {
        try {
            $appKey.DeleteValue('ExecutablePath', $false)
            $empty = $appKey.SubKeyCount -eq 0 -and $appKey.ValueCount -eq 0
        } finally { $appKey.Dispose() }
        if ($empty) { $registry.DeleteSubKey('Software\Museek', $false) }
    }
}

$uninstallPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Museek'
$uninstallKey = $registry.OpenSubKey($uninstallPath)
try { $ownsUninstall = $uninstallKey -and (Test-SamePath $uninstallKey.GetValue('InstallLocation') $installDirectory) }
finally { if ($uninstallKey) { $uninstallKey.Dispose() } }
if ($ownsUninstall) { $registry.DeleteSubKeyTree($uninstallPath, $false) }

if ($shortcut -and (Test-SamePath $shortcut.TargetPath $executable)) {
    Remove-Item -LiteralPath $shortcutPath -Force
}

if (Test-Path -LiteralPath $installDirectory -PathType Container) {
    # The absolute target, owner marker, ancestors, and entire tree were validated above.
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}

[Museek.UninstallShellNotification]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host 'Museek was uninstalled for your account. Your audio files were kept.'
