[CmdletBinding()]
param(
    [string]$NsisPath,
    [string]$DotNetPath,
    [string]$VlcNativeDirectory,
    [string]$FfmpegPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$script:checkCount = 0
$script:lastNativeError = ''

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Installer check failed: $Message. $script:lastNativeError" }
    $script:checkCount++
}

function Assert-WorkspacePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Test path is outside the workspace: $absolute"
    }
    $cursor = $absolute
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Test path contains a reparse point: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $absolute
}

function Invoke-Native([string]$Executable, [string]$Arguments, [int]$TimeoutSeconds = 45) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "Test helper did not finish within $TimeoutSeconds seconds: $Executable"
        }
        $script:lastNativeError = ''
        if ($process.ExitCode -ne 0) {
            $script:lastNativeError = "Exit code: $($process.ExitCode)."
            $logs = @($script:productIds | ForEach-Object {
                $path = Join-Path ([IO.Path]::GetTempPath()) ($_ + '.Setup-error.log')
                if (Test-Path -LiteralPath $path -PathType Leaf) { Get-Item -LiteralPath $path }
            } | Sort-Object LastWriteTimeUtc -Descending)
            if ($logs.Count -gt 0) {
                $detail = [IO.File]::ReadAllText($logs[0].FullName).Trim()
                $script:lastNativeError += " $detail"
                [IO.File]::AppendAllText((Join-Path $runDirectory 'expected-refusals.log'),
                    $Executable + ': ' + $script:lastNativeError + [Environment]::NewLine)
            }
        }
        return $process.ExitCode
    } finally { $process.Dispose() }
}

function Read-UninstallRegistration([string]$ProductId) {
    $key = $script:currentUser64.OpenSubKey($script:uninstallPrefix + '\' + $ProductId)
    if (-not $key) { return $null }
    try {
        $values = @{}
        foreach ($name in $key.GetValueNames()) { $values[$name] = $key.GetValue($name) }
        return $values
    } catch {
        # The uninstaller can delete a key after OpenSubKey succeeds while this poll reads it.
        # ERROR_KEY_DELETED (1018) means registration is already gone; other errors still fail.
        if (($_.Exception.GetBaseException().HResult -band 0xffff) -eq 1018) { return $null }
        throw
    } finally { $key.Dispose() }
}

function Compile-Setup([string]$Payload, [string]$Version, [string]$Output,
                       [string]$ProductId, [string]$ProductName, [string]$InstallDirectory,
                       [int]$UpdateWaitTimeoutMilliseconds = 30000) {
    foreach ($path in @($Payload, $Output, $InstallDirectory)) {
        [void](Assert-WorkspacePath $path)
    }
    & (Join-Path $projectRoot 'scripts\Build-Installer.ps1') -PayloadDirectory $Payload `
        -OutputPath $Output -MakensisPath $NsisPath -ProductId $ProductId -ProductName $ProductName `
        -DefaultInstallDirectory $InstallDirectory -Version $Version `
        -UpdateWaitTimeoutMilliseconds $UpdateWaitTimeoutMilliseconds
    Assert-Check (Test-Path -LiteralPath $Output -PathType Leaf) 'a runnable setup executable was built'
}

function Read-FixtureLog {
    if (-not (Test-Path -LiteralPath $script:logPath -PathType Leaf)) { return @() }
    return @(Get-Content -LiteralPath $script:logPath | ForEach-Object { $_ | ConvertFrom-Json })
}

function Test-UpdateParentWait {
    $setupInitial = Join-Path $runDirectory 'setup-update-parent-initial.exe'
    $setupUpdate = Join-Path $runDirectory 'setup-update-parent.exe'
    $setupTimeout = Join-Path $runDirectory 'setup-update-parent-timeout.exe'
    Compile-Setup $payload1 '1.0.0' $setupInitial $updateProductId $updateProductName $updateDirectory
    Compile-Setup $payload2 '1.1.0' $setupUpdate $updateProductId $updateProductName $updateDirectory
    Compile-Setup $payload2 '1.1.0' $setupTimeout $updateProductId $updateProductName $updateDirectory 500
    Assert-Check ((Invoke-Native $setupInitial '/S') -eq 0) 'the update-parent fixture installs its first version'
    $ready = Join-Path $runDirectory 'update-parent-ready'
    $stop = Join-Path $runDirectory 'update-parent-stop'
    $parent = Start-Process -FilePath (Join-Path $updateDirectory 'Museek.exe') `
        -ArgumentList @('--hold', ('"' + $ready + '"'), ('"' + $stop + '"')) -PassThru -WindowStyle Hidden
    $setup = $null
    try {
        Assert-Check (Wait-Until { Test-Path -LiteralPath $ready -PathType Leaf }) 'the update-parent process is alive'
        $initialMarkerHash = (Get-FileHash -LiteralPath (Join-Path $updateDirectory '.museek-install.ini')).Hash
        $initialManifestHash = (Get-FileHash -LiteralPath (Join-Path $updateDirectory '.museek-files.ini')).Hash
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        Assert-Check ((Invoke-Native $setupTimeout "/S /UPDATEPID=$($parent.Id)") -ne 0) 'a live update parent is refused after the bounded wait expires'
        Assert-Check ($elapsed.Elapsed.TotalSeconds -lt 5 -and $script:lastNativeError -match 'still closing') 'the fixture timeout is bounded and reports the pending shutdown'
        Assert-Check ([IO.File]::ReadAllText((Join-Path $updateDirectory 'payload-version.txt')) -eq 'v1' -and
            (Read-UninstallRegistration $updateProductId)['DisplayVersion'] -eq '1.0.0') 'a parent timeout preserves the old payload and registration'
        $setup = Start-Process -FilePath $setupUpdate -ArgumentList "/S /UPDATEPID=$($parent.Id)" -PassThru -WindowStyle Hidden
        Assert-Check (-not $setup.WaitForExit(1000)) 'setup waits for the requested parent process before updating'
        Assert-Check ([IO.File]::ReadAllText((Join-Path $updateDirectory 'payload-version.txt')) -eq 'v1') 'waiting for the parent preserves the original payload'
        Assert-Check ((Read-UninstallRegistration $updateProductId)['DisplayVersion'] -eq '1.0.0') 'waiting for the parent preserves its registered version'
        Assert-Check ((Get-FileHash -LiteralPath (Join-Path $updateDirectory '.museek-install.ini')).Hash -eq $initialMarkerHash -and
            (Get-FileHash -LiteralPath (Join-Path $updateDirectory '.museek-files.ini')).Hash -eq $initialManifestHash) 'a timeout and pending parent leave ownership records unchanged'
        $exitedParentId = $parent.Id
        [IO.File]::WriteAllText($stop, 'stop')
        Assert-Check ($parent.WaitForExit(10000) -and $parent.ExitCode -eq 0) 'the update parent exits normally'
        Assert-Check ($setup.WaitForExit(10000) -and $setup.ExitCode -eq 0) 'setup completes after the update parent exits'
        Assert-Check ([IO.File]::ReadAllText((Join-Path $updateDirectory 'payload-version.txt')) -eq 'v2') 'parent-aware setup installs the new payload'
    } finally {
        [IO.File]::WriteAllText($stop, 'stop')
        [void]$parent.WaitForExit(10000)
        $parent.Dispose()
        if ($setup) { [void]$setup.WaitForExit(10000); $setup.Dispose() }
    }

    Assert-Check ((Invoke-Native $setupUpdate "/S /UPDATEPID=$exitedParentId") -eq 0) 'a parent that exited before setup starts does not prevent the update'
    Assert-Check ($null -eq (Get-Process -Id 2147483647 -ErrorAction SilentlyContinue)) 'the absent-parent fixture PID does not identify a live process'
    Assert-Check ((Invoke-Native $setupUpdate '/S /UPDATEPID=2147483647') -eq 0) 'a valid already-absent parent PID does not prevent the update'
    $markerHash = (Get-FileHash -LiteralPath (Join-Path $updateDirectory '.museek-install.ini')).Hash
    $manifestHash = (Get-FileHash -LiteralPath (Join-Path $updateDirectory '.museek-files.ini')).Hash
    foreach ($argument in @('/UPDATEPID', '/UPDATEPID=', '/UPDATEPID=0', '/UPDATEPID=-1', '/UPDATEPID=abc',
        '/UPDATEPID=1abc', '/UPDATEPID=+123', '/UPDATEPID=0123', '/UPDATEPID=0x7b', '/UPDATEPID=2147483648',
        '/UPDATEPID=999999999999999999999999', '/UPDATEPID=1 /UPDATEPID=2', '/UPDATEPID=2147483647/garbage',
        '/UPDATEPID =2147483647', '/UPDATEPID= 2147483647')) {
        Assert-Check ((Invoke-Native $setupInitial ('/S ' + $argument)) -ne 0) "an invalid update-parent argument is refused: $argument"
        Assert-Check ($script:lastNativeError -match 'invalid update process ID') 'invalid parent arguments report their format problem'
        Assert-Check ([IO.File]::ReadAllText((Join-Path $updateDirectory 'payload-version.txt')) -eq 'v2' -and
            (Read-UninstallRegistration $updateProductId)['DisplayVersion'] -eq '1.1.0') 'invalid parent arguments cannot downgrade the payload or registration'
        Assert-Check ((Get-FileHash -LiteralPath (Join-Path $updateDirectory '.museek-install.ini')).Hash -eq $markerHash -and
            (Get-FileHash -LiteralPath (Join-Path $updateDirectory '.museek-files.ini')).Hash -eq $manifestHash) 'invalid parent arguments leave both ownership records unchanged'
    }

    $otherReady = Join-Path $runDirectory 'update-other-ready'
    $otherStop = Join-Path $runDirectory 'update-other-stop'
    $requestedReady = Join-Path $runDirectory 'update-requested-ready'
    $requestedStop = Join-Path $runDirectory 'update-requested-stop'
    $other = Start-Process -FilePath (Join-Path $updateDirectory 'Museek.exe') `
        -ArgumentList @('--hold', ('"' + $otherReady + '"'), ('"' + $otherStop + '"')) -PassThru -WindowStyle Hidden
    $requested = $null
    $blockedSetup = $null
    try {
        Assert-Check (Wait-Until { Test-Path -LiteralPath $otherReady -PathType Leaf }) 'another installed app instance is running'
        $requested = Start-Process -FilePath (Join-Path $updateDirectory 'Museek.exe') `
            -ArgumentList @('--hold', ('"' + $requestedReady + '"'), ('"' + $requestedStop + '"')) -PassThru -WindowStyle Hidden
        Assert-Check (Wait-Until { Test-Path -LiteralPath $requestedReady -PathType Leaf }) 'the requested update parent is also running'
        $blockedSetup = Start-Process -FilePath $setupInitial -ArgumentList "/S /UPDATEPID=$($requested.Id)" -PassThru -WindowStyle Hidden
        Assert-Check (-not $blockedSetup.WaitForExit(500)) 'setup first waits for its requested parent even when another instance exists'
        [IO.File]::WriteAllText($requestedStop, 'stop')
        Assert-Check ($requested.WaitForExit(10000) -and $requested.ExitCode -eq 0) 'the requested parent exits while another instance stays alive'
        Assert-Check ($blockedSetup.WaitForExit(10000) -and $blockedSetup.ExitCode -ne 0) 'another app instance is still refused after the requested parent exits'
        Assert-Check (-not $other.HasExited -and [IO.File]::ReadAllText((Join-Path $updateDirectory 'payload-version.txt')) -eq 'v2') 'the remaining instance is not terminated and its payload remains unchanged'
        Assert-Check ((Read-UninstallRegistration $updateProductId)['DisplayVersion'] -eq '1.1.0' -and
            (Get-FileHash -LiteralPath (Join-Path $updateDirectory '.museek-install.ini')).Hash -eq $markerHash -and
            (Get-FileHash -LiteralPath (Join-Path $updateDirectory '.museek-files.ini')).Hash -eq $manifestHash) 'a remaining instance preserves registration and ownership records'
    } finally {
        [IO.File]::WriteAllText($requestedStop, 'stop')
        [IO.File]::WriteAllText($otherStop, 'stop')
        if ($requested) { [void]$requested.WaitForExit(10000); $requested.Dispose() }
        [void]$other.WaitForExit(10000)
        $other.Dispose()
        if ($blockedSetup) { [void]$blockedSetup.WaitForExit(10000); $blockedSetup.Dispose() }
    }
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Test-PayloadUnlocked([string]$Directory) {
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse -Force) {
        try {
            $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Write,
                ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $stream.Dispose()
        } catch { return $false }
    }
    return $true
}

function Invoke-CacheCheck([string]$Directory, [string]$Scenario, [switch]$CacheOnly) {
    $reportPath = Join-Path $runDirectory ("plugin-cache-$Scenario.json")
    $arguments = @('run', '--no-build', '--project', (Join-Path $PSScriptRoot 'StartupChecks'), '-c', 'Release',
        '--', '--check-plugin-cache', $Directory, '--output', $reportPath)
    if ($CacheOnly) { $arguments += '--cache-only' }
    if ($FfmpegPath) { $arguments += @('--ffmpeg', $FfmpegPath) }
    & $DotNetPath @arguments | Out-Null
    $exitCode = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "Plugin-cache check did not produce a report: $Scenario"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Report = (Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json) }
}

function Test-InstalledVlcCache {
    $native = Join-Path $installDirectory 'libvlc\win-x64'
    $cache = Join-Path $native 'plugins\plugins.dat'
    $sourceCache = Join-Path $VlcNativeDirectory 'plugins\plugins.dat'
    Assert-Check ((Get-FileHash -LiteralPath $cache).Hash -eq (Get-FileHash -LiteralPath $sourceCache).Hash) `
        'setup installs the generated plugin cache without changing its bytes'
    $plugins = @(Get-ChildItem -LiteralPath (Join-Path $VlcNativeDirectory 'plugins') -Filter '*_plugin.dll' -File -Recurse)
    foreach ($plugin in $plugins) {
        $relative = $plugin.FullName.Substring($VlcNativeDirectory.Length).TrimStart('\')
        $installed = Get-Item -LiteralPath (Join-Path $native $relative)
        Assert-Check ($installed.LastWriteTimeUtc -eq $plugin.LastWriteTimeUtc) 'setup preserves a cached plugin timestamp'
    }
    $normal = Invoke-CacheCheck $native 'installed'
    Assert-Check ($normal.ExitCode -eq 0) 'installed cache prevents eager loading and decodes audio with normal plugin scanning'
    $cacheOnly = Invoke-CacheCheck $native 'installed-only' -CacheOnly
    Assert-Check ($cacheOnly.ExitCode -eq 0) 'installed cache alone contains the plugins needed for WAV and MP3 playback'

    # An archive changes the absolute root and uses ZIP timestamp precision.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $runDirectory 'native-cache.zip'
    $extracted = Join-Path $runDirectory 'Extracted Native 音楽'
    [IO.Compression.ZipFile]::CreateFromDirectory($native, $archive)
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
    $relocated = Invoke-CacheCheck $extracted 'zip-relocated'
    Assert-Check ($relocated.ExitCode -eq 0) 'plugin cache survives ZIP extraction and relocation'

    $cacheBytes = [IO.File]::ReadAllBytes($cache)
    try {
        Remove-Item -LiteralPath $cache -Force
        $missing = Invoke-CacheCheck $native 'missing'
        Assert-Check ($missing.ExitCode -ne 0 -and $missing.Report.Measurements.InitializationLoadedPluginCount -eq $plugins.Count) `
            'the regression check detects missing cache and eager plugin discovery'
        Assert-Check (@($missing.Report.Failures | Where-Object { $_ -match 'start .* playback|decode .*clock' }).Count -eq 0) `
            'normal playback falls back safely when the cache is missing'
        [IO.File]::WriteAllBytes($cache, [Text.Encoding]::ASCII.GetBytes('invalid-cache'))
        $corrupt = Invoke-CacheCheck $native 'corrupt'
        Assert-Check ($corrupt.ExitCode -ne 0 -and $corrupt.Report.Measurements.InitializationLoadedPluginCount -eq $plugins.Count) `
            'the regression check detects corrupted cache and eager plugin discovery'
        Assert-Check (@($corrupt.Report.Failures | Where-Object { $_ -match 'start .* playback|decode .*clock' }).Count -eq 0) `
            'normal playback falls back safely when the cache is corrupted'
    } finally { [IO.File]::WriteAllBytes($cache, $cacheBytes) }

    $timestamps = @{}
    try {
        foreach ($plugin in Get-ChildItem -LiteralPath (Join-Path $native 'plugins') -Filter '*_plugin.dll' -File -Recurse) {
            $timestamps[$plugin.FullName] = $plugin.LastWriteTimeUtc
            [IO.File]::SetLastWriteTimeUtc($plugin.FullName, $plugin.LastWriteTimeUtc.AddSeconds(2))
        }
        $stale = Invoke-CacheCheck $native 'stale'
        Assert-Check ($stale.ExitCode -ne 0 -and $stale.Report.Measurements.InitializationLoadedPluginCount -eq $plugins.Count) `
            'the regression check detects stale plugin timestamps and eager discovery'
        Assert-Check (@($stale.Report.Failures | Where-Object { $_ -match 'start .* playback|decode .*clock' }).Count -eq 0) `
            'normal playback falls back safely when cache timestamps are stale'
    } finally {
        foreach ($path in $timestamps.Keys) { [IO.File]::SetLastWriteTimeUtc($path, $timestamps[$path]) }
    }
    Assert-Check ((Invoke-CacheCheck $native 'restored').ExitCode -eq 0) 'restoring packaged timestamps and cache restores efficient discovery'
}

if (-not $DotNetPath) { $DotNetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) {
    $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source
}
if (-not $NsisPath) {
    $NsisPath = Get-ChildItem -LiteralPath (Join-Path $projectRoot '.tools') -Filter makensis.exe -File -Recurse |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $NsisPath -or -not (Test-Path -LiteralPath $NsisPath -PathType Leaf)) {
    throw 'Pass -NsisPath pointing to the verified NSIS makensis.exe compiler.'
}
$DotNetPath = [IO.Path]::GetFullPath($DotNetPath)
$NsisPath = [IO.Path]::GetFullPath($NsisPath)
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'scripts\Build-Installer.ps1') -PathType Leaf)) {
    throw 'scripts\Build-Installer.ps1 is missing.'
}
if (-not ('MuseekInstallerShellLink' -as [type])) {
    Add-Type -LiteralPath (Join-Path $PSScriptRoot 'InstallerShellLink.cs')
}

$token = [Guid]::NewGuid().ToString('N')
$runDirectory = Assert-WorkspacePath (Join-Path $projectRoot "artifacts\installer-checks-$token")
$installDirectory = Assert-WorkspacePath (Join-Path $runDirectory 'Installed 音楽 App')
$alternateDirectory = Assert-WorkspacePath (Join-Path $runDirectory 'Unexpected Alternate App')
$foreignDirectory = Assert-WorkspacePath (Join-Path $runDirectory 'Foreign Folder')
$legacyDirectory = Assert-WorkspacePath (Join-Path $runDirectory 'Legacy Installed App')
$repairDirectory = Assert-WorkspacePath (Join-Path $runDirectory 'Repair Installed App')
$updateDirectory = Assert-WorkspacePath (Join-Path $runDirectory 'Update Parent Installed App')
$payload1 = Assert-WorkspacePath (Join-Path $runDirectory 'payload-v1')
$payload2 = Assert-WorkspacePath (Join-Path $runDirectory 'payload-v2')
$setup1 = Join-Path $runDirectory 'setup-v1.exe'
$setup2 = Join-Path $runDirectory 'setup-v2.exe'
$productId = "Museek.InstallerChecks.$token"
$productName = "Museek Installer Check $token"
$foreignProductId = "$productId.Foreign"
$foreignProductName = "$productName Foreign"
$legacyProductId = "$productId.Legacy"
$legacyProductName = "$productName Legacy"
$repairProductId = "$productId.Repair"
$repairProductName = "$productName Repair"
$updateProductId = "$productId.UpdateParent"
$updateProductName = "$productName UpdateParent"
$script:productIds = @($productId, $foreignProductId, $legacyProductId, $repairProductId, $updateProductId)
$script:uninstallPrefix = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
$script:currentUser64 = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
    [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
$script:logPath = Join-Path $runDirectory 'fixture-calls.jsonl'
$startMenuDirectory = [Environment]::GetFolderPath('Programs')
$shortcutPath = Join-Path $startMenuDirectory ($productName + '.lnk')
$foreignShortcutPath = Join-Path $startMenuDirectory ($foreignProductName + '.lnk')
$legacyShortcutPath = Join-Path $startMenuDirectory ($legacyProductName + '.lnk')
$repairShortcutPath = Join-Path $startMenuDirectory ($repairProductName + '.lnk')
$updateShortcutPath = Join-Path $startMenuDirectory ($updateProductName + '.lnk')
$actualSettingsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Museek\settings.json'
$actualSettingsHash = if (Test-Path -LiteralPath $actualSettingsPath -PathType Leaf) {
    (Get-FileHash -LiteralPath $actualSettingsPath -Algorithm SHA256).Hash
} else { $null }
$previousDotnetRoot = $env:DOTNET_ROOT
$previousFixtureLog = $env:MUSEEK_INSTALLER_FIXTURE_LOG
$previousFixtureRegistrationFailure = $env:MUSEEK_INSTALLER_FIXTURE_FAIL_REGISTER
$holder = $null
$stopPath = Join-Path $runDirectory 'release-holder'
$lockStream = $null

New-Item -ItemType Directory -Path $runDirectory, $payload1, $payload2, $foreignDirectory -Force | Out-Null
try {
    $env:DOTNET_ROOT = Split-Path -Parent $DotNetPath
    $env:MUSEEK_INSTALLER_FIXTURE_LOG = $script:logPath
    $env:MUSEEK_INSTALLER_FIXTURE_FAIL_REGISTER = $null
    & $DotNetPath publish (Join-Path $PSScriptRoot 'InstallerFixture\InstallerFixture.csproj') -c Release `
        -r win-x64 --self-contained false -o $payload1
    if ($LASTEXITCODE -ne 0) { throw 'The harmless installer fixture failed to publish.' }
    New-Item -ItemType Directory -Path (Join-Path $payload1 'nested') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $payload1 'payload-version.txt'), 'v1')
    [IO.File]::WriteAllText((Join-Path $payload1 'obsolete-managed.txt'), 'This belongs only to version 1.')
    [IO.File]::WriteAllText((Join-Path $payload1 'nested\managed.txt'), 'nested-v1')
    [IO.File]::WriteAllText((Join-Path $payload1 'nested\音楽データ.txt'), 'unicode-v1')
    foreach ($item in Get-ChildItem -LiteralPath $payload1 -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $payload2 -Recurse -Force
    }
    Remove-Item -LiteralPath (Join-Path $payload2 'obsolete-managed.txt') -Force
    [IO.File]::WriteAllText((Join-Path $payload2 'payload-version.txt'), 'v2')
    [IO.File]::WriteAllText((Join-Path $payload2 'nested\managed.txt'), 'nested-v2')
    [IO.File]::WriteAllText((Join-Path $payload2 'nested\音楽データ.txt'), 'unicode-v2')
    [IO.File]::WriteAllText((Join-Path $payload2 'new-managed.txt'), 'This belongs to version 2.')
    if ($VlcNativeDirectory) {
        $VlcNativeDirectory = Assert-WorkspacePath $VlcNativeDirectory
        Assert-Check (Test-Path -LiteralPath (Join-Path $VlcNativeDirectory 'plugins\plugins.dat') -PathType Leaf) `
            'the native installer fixture starts with a generated plugin cache'
        $nativeParent = Join-Path $payload2 'libvlc'
        New-Item -ItemType Directory -Path $nativeParent -Force | Out-Null
        Copy-Item -LiteralPath $VlcNativeDirectory -Destination (Join-Path $nativeParent 'win-x64') -Recurse
    }

    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    Assert-Check (@(Get-ChildItem -LiteralPath $installDirectory -Force).Count -eq 0) 'fresh installation starts with an existing empty destination folder'
    Compile-Setup $payload1 '1.0.0' $setup1 $productId $productName $installDirectory
    Compile-Setup $payload2 '1.1.0' $setup2 $productId $productName $installDirectory
    Assert-Check ((Invoke-Native $setup1 '/S') -eq 0) 'fresh installation returns success'
    Assert-Check (Test-Path -LiteralPath (Join-Path $installDirectory 'Museek.exe') -PathType Leaf) 'fresh installation contains the executable'
    Assert-Check (Test-Path -LiteralPath (Join-Path $installDirectory 'uninstall.exe') -PathType Leaf) 'fresh installation creates uninstall.exe'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $installDirectory 'payload-version.txt')) -eq 'v1') 'fresh installation extracts the first payload'
    $registration = Read-UninstallRegistration $productId
    Assert-Check ($null -ne $registration) 'fresh installation registers an Installed apps entry'
    Assert-Check ($registration['DisplayName'] -eq $productName) 'the Installed apps name matches the product'
    Assert-Check ($registration['DisplayVersion'] -eq '1.0.0') 'the Installed apps entry contains the first version'
    Assert-Check ($registration['InstallLocation'] -eq $installDirectory) 'the Installed apps entry contains the installation folder'
    Assert-Check ($registration['UninstallString'] -like '*uninstall.exe*') 'the Installed apps uninstaller uses uninstall.exe'
    Assert-Check ($registration['QuietUninstallString'] -like '*uninstall.exe* /S*') 'the Installed apps entry supports silent uninstall'
    Assert-Check (Test-Path -LiteralPath $shortcutPath -PathType Leaf) 'fresh installation creates a Start menu shortcut'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    Copy-Item -LiteralPath $shortcutPath -Destination (Join-Path $runDirectory 'installed-shortcut.lnk') -Force
    $shortcutTarget = [MuseekInstallerShellLink]::GetTarget($shortcutPath)
    Assert-Check ($shortcutTarget -eq (Join-Path $installDirectory 'Museek.exe')) `
        ('the Start menu shortcut points to the installed executable. Actual target: ' + $shortcutTarget +
        '. Expected target: ' + (Join-Path $installDirectory 'Museek.exe'))
    $calls = Read-FixtureLog
    Assert-Check (@($calls | Where-Object { ($_.Arguments -join ' ') -eq '--register --quiet' }).Count -eq 1) 'fresh installation performs quiet app registration once'

    $unrelated = Join-Path $installDirectory 'user-notes.txt'
    $settings = Join-Path $installDirectory 'nested\user-settings.json'
    [IO.File]::WriteAllText($unrelated, 'Keep my personal notes.')
    [IO.File]::WriteAllText($settings, '{"keep":true}')
    $notesHash = (Get-FileHash -LiteralPath $unrelated -Algorithm SHA256).Hash
    $settingsHash = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash

    # A running executable must not be replaced. Release the harmless fixture normally afterward.
    $readyPath = Join-Path $runDirectory 'holder-ready'
    $holder = Start-Process -FilePath (Join-Path $installDirectory 'Museek.exe') `
        -ArgumentList @('--hold', ('"' + $readyPath + '"'), ('"' + $stopPath + '"')) `
        -PassThru -WindowStyle Hidden
    Assert-Check (Wait-Until { Test-Path -LiteralPath $readyPath -PathType Leaf }) 'the harmless running-app fixture started'
    Assert-Check ((Invoke-Native $setup2 '/S') -ne 0) 'updating a running app is refused'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $installDirectory 'payload-version.txt')) -eq 'v1') 'refusing a running update preserves the installed payload'
    $registration = Read-UninstallRegistration $productId
    Assert-Check ($registration['DisplayVersion'] -eq '1.0.0') 'refusing a running update preserves the registered version'
    $uninstallExe = Join-Path $installDirectory 'uninstall.exe'
    Assert-Check ((Invoke-Native $uninstallExe "/S _?=$installDirectory") -ne 0) 'uninstalling a running app is refused'
    Assert-Check (Test-Path -LiteralPath (Join-Path $installDirectory 'Museek.exe') -PathType Leaf) 'a running uninstall refusal preserves the executable'
    Assert-Check ($null -ne (Read-UninstallRegistration $productId)) 'a running uninstall refusal preserves Installed apps registration'
    [IO.File]::WriteAllText($stopPath, 'stop')
    Assert-Check ($holder.WaitForExit(10000) -and $holder.ExitCode -eq 0) 'the running-app fixture closes normally'
    $holder.Dispose()
    $holder = $null

    # Hold a managed data file without delete/write sharing to exercise preflight before mutation.
    $lockStream = [IO.File]::Open((Join-Path $installDirectory 'nested\managed.txt'),
        [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Assert-Check ((Invoke-Native $setup2 '/S') -ne 0) 'updating a locked managed file is refused'
        Assert-Check ([IO.File]::ReadAllText((Join-Path $installDirectory 'payload-version.txt')) -eq 'v1') 'a locked-file refusal preserves other installed files'
        Assert-Check ((Invoke-Native $uninstallExe "/S _?=$installDirectory") -ne 0) 'uninstalling a locked managed file is refused'
        Assert-Check (Test-Path -LiteralPath (Join-Path $installDirectory 'Museek.exe') -PathType Leaf) 'a locked-file uninstall refusal preserves the executable'
        Assert-Check ($null -ne (Read-UninstallRegistration $productId)) 'a locked-file uninstall refusal preserves Installed apps registration'
    } finally { $lockStream.Dispose(); $lockStream = $null }

    # /D must not create a second copy when a matching installation already exists.
    Assert-Check ((Invoke-Native $setup2 "/S /D=$alternateDirectory") -eq 0) 'rerunning setup updates the existing installation'
    Assert-Check (-not (Test-Path -LiteralPath $alternateDirectory)) 'the updater ignores an alternate destination for an existing product'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $installDirectory 'payload-version.txt')) -eq 'v2') 'update replaces the payload in the original folder'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $installDirectory 'nested\managed.txt')) -eq 'nested-v2') 'update replaces nested managed files'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $installDirectory 'nested\音楽データ.txt')) -eq 'unicode-v2') 'update replaces Unicode-named files in a Unicode installation folder'
    Assert-Check (Test-Path -LiteralPath (Join-Path $installDirectory 'new-managed.txt') -PathType Leaf) 'update adds new managed files'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'obsolete-managed.txt'))) 'update removes obsolete managed files'
    Assert-Check ((Get-FileHash -LiteralPath $unrelated -Algorithm SHA256).Hash -eq $notesHash) 'update preserves unrelated files'
    Assert-Check ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -eq $settingsHash) 'update preserves unrelated nested settings'
    $registration = Read-UninstallRegistration $productId
    Assert-Check ($registration['DisplayVersion'] -eq '1.1.0') 'update changes the Installed apps version'
    Assert-Check ($registration['InstallLocation'] -eq $installDirectory) 'update retains the original registered folder'
    $uninstallParent = $script:currentUser64.OpenSubKey($script:uninstallPrefix)
    try {
        Assert-Check (@($uninstallParent.GetSubKeyNames() | Where-Object { $_ -eq $productId }).Count -eq 1) 'update retains one Installed apps entry'
    } finally { $uninstallParent.Dispose() }
    $calls = Read-FixtureLog
    Assert-Check (@($calls | Where-Object { ($_.Arguments -join ' ') -eq '--register --quiet' }).Count -eq 2) 'update registers only after the new payload is installed'
    if ($VlcNativeDirectory) { Test-InstalledVlcCache }

    # Run uninstall.exe itself. NSIS copies to TEMP; polling waits for its child to finish.
    $uninstallExe = Join-Path $installDirectory 'uninstall.exe'
    Assert-Check ((Invoke-Native $uninstallExe '/S') -eq 0) 'uninstall.exe launches successfully'
    Assert-Check (Wait-Until { -not (Test-Path -LiteralPath $uninstallExe) -and
        $null -eq (Read-UninstallRegistration $productId) } 20) 'uninstall completes and removes itself and the Installed apps entry'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'Museek.exe'))) 'uninstall removes the managed executable'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'new-managed.txt'))) 'uninstall removes new managed files'
    if ($VlcNativeDirectory) {
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'libvlc\win-x64\plugins\plugins.dat'))) `
            'uninstall removes the managed plugin cache'
    }
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'nested\managed.txt'))) 'uninstall removes nested managed files'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'nested\音楽データ.txt'))) 'uninstall removes Unicode-named managed files'
    Assert-Check (-not (Test-Path -LiteralPath $shortcutPath)) 'uninstall removes its Start menu shortcut'
    Assert-Check ((Get-FileHash -LiteralPath $unrelated -Algorithm SHA256).Hash -eq $notesHash) 'uninstall preserves unrelated files'
    Assert-Check ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -eq $settingsHash) 'uninstall preserves unrelated nested settings'
    $calls = Read-FixtureLog
    Assert-Check (@($calls | Where-Object { ($_.Arguments -join ' ') -eq '--unregister --quiet' }).Count -eq 1) 'uninstall invokes quiet app cleanup once before deleting the executable'
    if ($VlcNativeDirectory) {
        # Later migration/repair cases test installer ownership rather than VLC.
        # Keep their independent setup fixtures small after the native round trip.
        $fixtureNative = Assert-WorkspacePath (Join-Path $payload2 'libvlc')
        Remove-Item -LiteralPath $fixtureNative -Recurse -Force
    }

    # Existing foreign folders must not be overwritten merely because /D names them.
    $foreignFile = Join-Path $foreignDirectory 'not-museek.txt'
    [IO.File]::WriteAllText($foreignFile, 'A different app or user owns this folder.')
    $foreignHash = (Get-FileHash -LiteralPath $foreignFile -Algorithm SHA256).Hash
    $foreignSetup = Join-Path $runDirectory 'setup-foreign.exe'
    Compile-Setup $payload1 '1.0.0' $foreignSetup $foreignProductId $foreignProductName $foreignDirectory
    Assert-Check ((Invoke-Native $foreignSetup '/S') -ne 0) 'installing into an unowned nonempty folder is refused'
    Assert-Check ((Get-FileHash -LiteralPath $foreignFile -Algorithm SHA256).Hash -eq $foreignHash) 'refusing an unowned folder preserves its contents'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $foreignDirectory 'Museek.exe'))) 'an unowned folder receives no app payload'
    Assert-Check (@(Get-ChildItem -LiteralPath $foreignDirectory -Force).Count -eq 1) 'an unowned-folder refusal leaves no installer markers or files'
    Assert-Check ($null -eq (Read-UninstallRegistration $foreignProductId)) 'an unowned-folder refusal creates no Installed apps entry'
    Assert-Check (-not (Test-Path -LiteralPath $foreignShortcutPath)) 'an unowned-folder refusal creates no shortcut'

    # A stale ARP entry and executable do not authorize migration of a bad legacy marker.
    foreach ($item in Get-ChildItem -LiteralPath $payload1 -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $foreignDirectory -Recurse -Force
    }
    $foreignExecutableHash = (Get-FileHash -LiteralPath (Join-Path $foreignDirectory 'Museek.exe') -Algorithm SHA256).Hash
    $foreignKey = $script:currentUser64.CreateSubKey($script:uninstallPrefix + '\' + $foreignProductId)
    try {
        $foreignKey.SetValue('DisplayName', $foreignProductName)
        $foreignKey.SetValue('DisplayVersion', '9.9.9')
        $foreignKey.SetValue('InstallLocation', $foreignDirectory)
    } finally { $foreignKey.Dispose() }
    $invalidLegacyMarkers = @(
        (@{ AppId = 'OtherApp'; SchemaVersion = 1; InstallDirectory = $foreignDirectory } | ConvertTo-Json),
        (@{ AppId = 'Museek'; SchemaVersion = 2; InstallDirectory = $foreignDirectory } | ConvertTo-Json),
        (@{ AppId = 'Museek'; SchemaVersion = 1; InstallDirectory = $alternateDirectory } | ConvertTo-Json),
        '{malformed json'
    )
    foreach ($invalidMarker in $invalidLegacyMarkers) {
        [IO.File]::WriteAllText((Join-Path $foreignDirectory '.museek-install.json'), $invalidMarker)
        Assert-Check ((Invoke-Native $foreignSetup '/S') -ne 0) 'a foreign or malformed legacy marker cannot authorize upgrade'
        Assert-Check ((Get-FileHash -LiteralPath (Join-Path $foreignDirectory 'Museek.exe') -Algorithm SHA256).Hash -eq $foreignExecutableHash) 'a bad legacy-marker refusal preserves the existing executable'
        Assert-Check ((Read-UninstallRegistration $foreignProductId)['DisplayVersion'] -eq '9.9.9') 'a bad legacy-marker refusal preserves existing registration'
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $foreignDirectory 'uninstall.exe'))) 'a bad legacy-marker refusal writes no native uninstaller'
    }
    Assert-Check ((Get-FileHash -LiteralPath $foreignFile -Algorithm SHA256).Hash -eq $foreignHash) 'bad legacy markers cannot modify unrelated files'

    # Migrate an installation made by the former PowerShell installer, using an isolated ARP ID.
    New-Item -ItemType Directory -Path $legacyDirectory -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $payload1 -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $legacyDirectory -Recurse -Force
    }
    @{ AppId = 'Museek'; SchemaVersion = 1; InstallDirectory = $legacyDirectory } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $legacyDirectory '.museek-install.json') -Encoding UTF8
    foreach ($file in @('Install.cmd', 'Install.ps1', 'Uninstall.ps1')) {
        [IO.File]::WriteAllText((Join-Path $legacyDirectory $file), '# Harmless old-installer fixture')
    }
    $legacyNotes = Join-Path $legacyDirectory 'user-notes.txt'
    [IO.File]::WriteAllText($legacyNotes, 'Keep legacy-install user data.')
    $legacyKey = $script:currentUser64.CreateSubKey($script:uninstallPrefix + '\' + $legacyProductId)
    try {
        $legacyKey.SetValue('DisplayName', $legacyProductName)
        $legacyKey.SetValue('DisplayVersion', '1.0.0')
        $legacyKey.SetValue('InstallLocation', $legacyDirectory)
    } finally { $legacyKey.Dispose() }
    $legacySetup = Join-Path $runDirectory 'setup-legacy.exe'
    Compile-Setup $payload2 '1.1.0' $legacySetup $legacyProductId $legacyProductName $legacyDirectory
    Assert-Check ((Invoke-Native $legacySetup '/S') -eq 0) 'setup upgrades a valid legacy PowerShell installation'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $legacyDirectory 'payload-version.txt')) -eq 'v2') 'legacy migration replaces the payload in its original folder'
    Assert-Check (Test-Path -LiteralPath (Join-Path $legacyDirectory 'uninstall.exe') -PathType Leaf) 'legacy migration adds the native uninstaller'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $legacyDirectory '.museek-install.json'))) 'legacy migration retires the old installation marker'
    foreach ($file in @('Install.cmd', 'Install.ps1', 'Uninstall.ps1')) {
        Assert-Check (-not (Test-Path -LiteralPath (Join-Path $legacyDirectory $file))) "legacy migration retires $file"
    }
    $legacyRegistration = Read-UninstallRegistration $legacyProductId
    Assert-Check ($legacyRegistration['DisplayVersion'] -eq '1.1.0') 'legacy migration updates the existing Installed apps entry'
    Assert-Check ($legacyRegistration['UninstallString'] -like '*uninstall.exe*') 'legacy migration replaces the old uninstall command'
    Assert-Check ([IO.File]::ReadAllText($legacyNotes) -eq 'Keep legacy-install user data.') 'legacy migration preserves unrelated user files'
    Assert-Check ((Invoke-Native (Join-Path $legacyDirectory 'uninstall.exe') '/S') -eq 0) 'the migrated installation can launch native uninstall'
    Assert-Check (Wait-Until { $null -eq (Read-UninstallRegistration $legacyProductId) -and
        -not (Test-Path -LiteralPath (Join-Path $legacyDirectory 'uninstall.exe')) } 20) 'native uninstall removes the migrated installation entry and itself'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $legacyDirectory 'Museek.exe'))) 'native uninstall removes the migrated executable'
    Assert-Check ([IO.File]::ReadAllText($legacyNotes) -eq 'Keep legacy-install user data.') 'native uninstall preserves legacy user files'

    # A failed registration must leave an owned installation that setup can repair.
    $repairSetup = Join-Path $runDirectory 'setup-repair.exe'
    Compile-Setup $payload2 '1.1.0' $repairSetup $repairProductId $repairProductName $repairDirectory
    $env:MUSEEK_INSTALLER_FIXTURE_FAIL_REGISTER = '1'
    try {
        Assert-Check ((Invoke-Native $repairSetup '/S') -ne 0) 'a registration failure is reported as installation failure'
        Assert-Check (Test-Path -LiteralPath (Join-Path $repairDirectory '.museek-install.ini') -PathType Leaf) 'a failed install retains its validated ownership marker'
        Assert-Check (Test-Path -LiteralPath (Join-Path $repairDirectory '.museek-files.ini') -PathType Leaf) 'a failed install retains its recovery file list'
    } finally { $env:MUSEEK_INSTALLER_FIXTURE_FAIL_REGISTER = $null }
    Assert-Check (Wait-Until { Test-PayloadUnlocked $repairDirectory }) 'the failed helper releases its payload files before setup is retried'
    Assert-Check ((Invoke-Native $repairSetup '/S') -eq 0) 'rerunning setup repairs a failed owned installation'
    Assert-Check ((Read-UninstallRegistration $repairProductId)['DisplayVersion'] -eq '1.1.0') 'repair retains the correct Installed apps version'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $repairDirectory 'payload-version.txt')) -eq 'v2') 'repair retains the complete current payload'
    Assert-Check (Test-Path -LiteralPath $repairShortcutPath -PathType Leaf) 'repair creates its Start menu shortcut'
    Remove-Item -LiteralPath $repairShortcutPath -Force
    Assert-Check ((Invoke-Native (Join-Path $repairDirectory 'uninstall.exe') '/S') -eq 0) 'the repaired installation can launch uninstall.exe'
    Assert-Check (Wait-Until { $null -eq (Read-UninstallRegistration $repairProductId) -and
        -not (Test-Path -LiteralPath $repairDirectory) } 20) 'uninstall removes the repaired installation completely'
    Assert-Check (-not (Test-Path -LiteralPath $repairShortcutPath)) 'uninstall tolerates an already-absent Start menu shortcut'

    Test-UpdateParentWait
    $currentActualSettingsHash = if (Test-Path -LiteralPath $actualSettingsPath -PathType Leaf) {
        (Get-FileHash -LiteralPath $actualSettingsPath -Algorithm SHA256).Hash
    } else { $null }
    Assert-Check ($currentActualSettingsHash -eq $actualSettingsHash) 'isolated installer checks leave real Museek settings untouched'
    Write-Host "Installer checks passed: $script:checkCount"
    Write-Host "Evidence: $runDirectory"
    @{ Passed = $script:checkCount; ProductId = $productId; InstallDirectory = $installDirectory } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'result.json') -Encoding UTF8
} finally {
    if ($lockStream) { $lockStream.Dispose() }
    if ($holder) {
        [IO.File]::WriteAllText($stopPath, 'stop')
        [void]$holder.WaitForExit(10000)
        $holder.Dispose()
    }
    # Cleanup is scoped to random product IDs and workspace paths. Evidence is retained.
    foreach ($item in @(@($productId, $installDirectory), @($foreignProductId, $foreignDirectory),
        @($legacyProductId, $legacyDirectory), @($repairProductId, $repairDirectory), @($updateProductId, $updateDirectory))) {
        $id = [string]$item[0]
        $directory = Assert-WorkspacePath ([string]$item[1])
        $uninstaller = Join-Path $directory 'uninstall.exe'
        $registration = Read-UninstallRegistration $id
        if ($registration -and $registration['InstallLocation'] -eq $directory -and
            (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
            [void](Invoke-Native $uninstaller '/S')
            [void](Wait-Until { $null -eq (Read-UninstallRegistration $id) } 20)
        }
        # Only our GUID-named entry may be removed if a failed test could not run its uninstaller.
        if ($id.StartsWith('Museek.InstallerChecks.') -and $id.Contains($token)) {
            $script:currentUser64.DeleteSubKeyTree($script:uninstallPrefix + '\' + $id, $false)
        }
    }
    foreach ($path in @($shortcutPath, $foreignShortcutPath, $legacyShortcutPath, $repairShortcutPath, $updateShortcutPath)) {
        if (([IO.Path]::GetFileName($path)).Contains($token) -and (Test-Path -LiteralPath $path -PathType Leaf)) {
            $shortcutTarget = [MuseekInstallerShellLink]::GetTarget($path)
            if ($shortcutTarget.StartsWith($runDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $path -Force
            }
        }
    }
    $script:currentUser64.Dispose()
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:MUSEEK_INSTALLER_FIXTURE_LOG = $previousFixtureLog
    $env:MUSEEK_INSTALLER_FIXTURE_FAIL_REGISTER = $previousFixtureRegistrationFailure
}
