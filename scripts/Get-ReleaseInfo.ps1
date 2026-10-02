[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($ProjectRoot)
if (-not [regex]::IsMatch($Tag, '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')) {
    throw 'Release tags must be vX.Y.Z, for example v1.2.0, with no leading zeroes.'
}
$version = $Tag.Substring(1)
$numericVersion = [Version]$version
if (@($numericVersion.Major, $numericVersion.Minor, $numericVersion.Build) | Where-Object { $_ -gt 65535 }) {
    throw 'Release version components must fit Windows version resources (0 through 65535).'
}
[xml]$project = [IO.File]::ReadAllText((Join-Path $root 'src\Museek\Museek.csproj'))
$versions = @($project.SelectNodes('/Project/PropertyGroup/Version') | ForEach-Object { $_.InnerText.Trim() })
if ($versions.Count -ne 1 -or $versions[0] -cne $version) {
    throw "Release tag $Tag must match the single Version in src/Museek/Museek.csproj."
}
$notesPath = Join-Path $root "releases\$version.md"
if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) { throw "Release notes are missing: $notesPath" }
$notes = [IO.File]::ReadAllText($notesPath)
if ([string]::IsNullOrWhiteSpace($notes) -or $notes -match '(?im)\b(TODO|TBD|CHANGEME)\b' -or
    [string]::IsNullOrWhiteSpace(($notes -replace '(?m)^\s*#+[^\r\n]*', '').Trim())) {
    throw 'Release notes must describe the changes and contain no TODO, TBD or CHANGEME placeholders.'
}

[pscustomobject]@{
    Version = $version
    Tag = $Tag
    Title = "Museek $version"
    NotesPath = $notesPath
}
