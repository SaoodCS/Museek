[CmdletBinding()]
param(
    [string]$PayloadDirectory,
    [string]$OutputPath,
    [string]$MakensisPath,
    [string]$Version,
    [string]$ProductId = 'Museek',
    [string]$ProductName = 'Museek',
    [string]$DefaultInstallDirectory = '$LOCALAPPDATA\Programs\Museek',
    [int]$UpdateWaitTimeoutMilliseconds = 30000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($UpdateWaitTimeoutMilliseconds -lt 1 -or $UpdateWaitTimeoutMilliseconds -gt 30000) {
    throw 'The update-parent wait timeout must be between 1 and 30000 milliseconds.'
}
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $PayloadDirectory) { $PayloadDirectory = Join-Path $projectRoot 'dist\Museek' }
if (-not $OutputPath) { $OutputPath = Join-Path $projectRoot 'dist\setup.exe' }
$payload = [IO.Path]::GetFullPath($PayloadDirectory)
$payloadPrefix = $payload.TrimEnd('\') + '\'
$output = [IO.Path]::GetFullPath($OutputPath)
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\Museek\Museek.csproj') -Raw
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Version must have three or four numeric components.' }
$numericVersion = [Version]$Version
if (@($numericVersion.Major, $numericVersion.Minor, $numericVersion.Build, [Math]::Max(0, $numericVersion.Revision)) |
    Where-Object { $_ -gt 65535 }) { throw 'Version components must fit Windows version resources.' }
$quadVersion = '{0}.{1}.{2}.{3}' -f $numericVersion.Major, $numericVersion.Minor, $numericVersion.Build, [Math]::Max(0, $numericVersion.Revision)
if ($ProductId -notmatch '^[A-Za-z0-9._-]+$' -or $ProductName -notmatch '^[A-Za-z0-9 ._-]+$') {
    throw 'Product identifiers must be simple names without path separators.'
}
if (-not (Test-Path -LiteralPath (Join-Path $payload 'Museek.exe') -PathType Leaf)) {
    throw 'Publish Museek before building setup.exe.'
}
if ($output.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The setup executable must be outside the payload folder.'
}

function ConvertTo-NsisLiteral([string]$Value) {
    return $Value.Replace('$', '$$').Replace('"', '$\"').Replace("`r", '$\r').Replace("`n", '$\n')
}

if (-not $MakensisPath) {
    $MakensisPath = Join-Path $projectRoot '.tools\nsis-3.13\makensis.exe'
    if (-not (Test-Path -LiteralPath $MakensisPath -PathType Leaf)) {
        $toolRoot = Join-Path $projectRoot '.tools'
        New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
        $archive = Join-Path $toolRoot 'nsis-3.13.zip'
        $expectedHash = 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'
        if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
            Write-Host 'Downloading the portable NSIS 3.13 compiler...'
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri 'https://downloads.sourceforge.net/project/nsis/NSIS%203/3.13/nsis-3.13.zip' -OutFile $archive -UseBasicParsing
            if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
                # SourceForge can return a download page with a signed refresh link.
                $page = [IO.File]::ReadAllText($archive)
                $refresh = [regex]::Match($page, '(?i)<meta[^>]+http-equiv=["'']refresh["''][^>]+content=["''][^"'']*url=([^"'']+)')
                if (-not $refresh.Success) { throw 'NSIS download did not match the pinned SHA256.' }
                $downloadUri = [Net.WebUtility]::HtmlDecode($refresh.Groups[1].Value)
                $parsedUri = [Uri]$downloadUri
                if ($parsedUri.Scheme -ne 'https' -or $parsedUri.Host -ne 'downloads.sourceforge.net') {
                    throw 'NSIS download page returned an unexpected host.'
                }
                Invoke-WebRequest -Uri $downloadUri -OutFile $archive -UseBasicParsing
            }
        }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'NSIS checksum mismatch.' }
        Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot -Force
    }
}
if (-not (Test-Path -LiteralPath $MakensisPath -PathType Leaf)) { throw 'makensis.exe was not found.' }

$items = @(Get-ChildItem -LiteralPath $payload -Recurse -Force)
if ($items | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) {
    throw 'The installer payload cannot contain symbolic links or junctions.'
}
$files = @($items | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
$generated = Join-Path $projectRoot ('artifacts\installer-build\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $generated -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
$installLines = [Collections.Generic.List[string]]::new()
$checkLines = [Collections.Generic.List[string]]::new()
$manifestLines = [Collections.Generic.List[string]]::new()
$installLines.Add('!macro MuseekInstallPayload')
$checkLines.Add('!macro MuseekCheckPayload')
$manifestLines.Add('!macro MuseekWriteManifest Manifest')
$manifestLines.Add('ClearErrors')
$manifestLines.Add('FileOpen $0 "${Manifest}" w')
$manifestLines.Add('FileWriteWord $0 0xFEFF')
$manifestLines.Add('FileClose $0')
$manifestLines.Add('IfErrors install_failed')
$manifestLines.Add('WriteINIStr "${Manifest}" "Files" "Count" "' + $files.Count + '"')
$manifestLines.Add('IfErrors install_failed')
$fileIndex = 0
foreach ($file in $files) {
    if (-not $file.FullName.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload file is outside the publish folder: $($file.FullName)"
    }
    $relative = $file.FullName.Substring($payloadPrefix.Length)
    if ($relative -match '[=\r\n]' -or $relative.StartsWith('..\') -or [IO.Path]::IsPathRooted($relative) -or
        $relative -in @('uninstall.exe', '.museek-install.ini', '.museek-files.ini')) {
        throw "Invalid or reserved payload filename: $relative"
    }
    $relativeLiteral = ConvertTo-NsisLiteral $relative
    $parent = [IO.Path]::GetDirectoryName($relative)
    $parentLiteral = if ($parent) { '\' + (ConvertTo-NsisLiteral $parent) } else { '' }
    $filename = ConvertTo-NsisLiteral $file.Name
    $source = ConvertTo-NsisLiteral $file.FullName
    $checkLines.Add('Push "$INSTDIR\' + $relativeLiteral + '"')
    $checkLines.Add('Call ValidatePath')
    $checkLines.Add('Call CheckWritable')
    $installLines.Add('SetOutPath "$INSTDIR' + $parentLiteral + '"')
    $installLines.Add('IfErrors install_failed')
    $installLines.Add('File "/oname=' + $filename + '" "' + $source + '"')
    $installLines.Add('IfErrors install_failed')
    $manifestLines.Add('WriteINIStr "${Manifest}" "Files" "' + $fileIndex + '" "' + $relativeLiteral + '"')
    $manifestLines.Add('WriteINIStr "${Manifest}" "OwnedFiles" "' + $relativeLiteral + '" "1"')
    $manifestLines.Add('IfErrors install_failed')
    $fileIndex++
}
$installLines.Add('!macroend')
$checkLines.Add('!macroend')
$manifestLines.Add('!macroend')
$payloadInclude = Join-Path $generated 'Payload.nsh'
$manifestInclude = Join-Path $generated 'Manifest.nsh'
[IO.File]::WriteAllLines($payloadInclude, @($checkLines.ToArray()) + @($installLines.ToArray()), [Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllLines($manifestInclude, $manifestLines.ToArray(), [Text.UTF8Encoding]::new($true))
$payloadKb = [Math]::Ceiling(($files | Measure-Object -Property Length -Sum).Sum / 1024)
$legacyValidation = @'
$ErrorActionPreference='Stop';try{$m=Get-Content -LiteralPath "$env:MUSEEK_SETUP_TARGET\.museek-install.json" -Raw|ConvertFrom-Json;if($m.AppId -eq 'Museek' -and $m.SchemaVersion -eq 1 -and $m.InstallDirectory -ieq $env:MUSEEK_SETUP_TARGET){exit 0}}catch{};exit 1
'@
$defines = [ordered]@{
    PRODUCT_ID = $ProductId; PRODUCT_NAME = $ProductName; APP_VERSION = $Version; APP_VERSION_QUAD = $quadVersion
    UPDATE_WAIT_TIMEOUT_MS = $UpdateWaitTimeoutMilliseconds
    SETUP_OUTPUT = ConvertTo-NsisLiteral $output; DEFAULT_INSTALL_DIR = $DefaultInstallDirectory
    PAYLOAD_INCLUDE = ConvertTo-NsisLiteral $payloadInclude; MANIFEST_INCLUDE = ConvertTo-NsisLiteral $manifestInclude
    APP_ICON = ConvertTo-NsisLiteral (Join-Path $projectRoot 'src\Museek\Assets\Museek.ico'); PAYLOAD_KB = $payloadKb
    LEGACY_VALIDATION = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($legacyValidation))
}
$arguments = @('/V2', '/NOCONFIG')
foreach ($define in $defines.GetEnumerator()) { $arguments += '/D' + $define.Key + '=' + $define.Value }
$arguments += Join-Path $projectRoot 'installer\Museek.nsi'
& $MakensisPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'NSIS setup compilation failed.' }
Write-Host "Ready: $output"
