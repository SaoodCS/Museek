[CmdletBinding()]
param([string]$DotNetPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$script:checkCount = 0

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Release check failed: $Message" }
    $script:checkCount++
}

foreach ($name in @('Get-ReleaseInfo.ps1', 'Prepare-Release.ps1')) {
    Assert-Check (Test-Path -LiteralPath (Join-Path $projectRoot "scripts\$name") -PathType Leaf) "$name exists"
}

$getInfo = Join-Path $projectRoot 'scripts\Get-ReleaseInfo.ps1'
$prepare = Join-Path $projectRoot 'scripts\Prepare-Release.ps1'
[xml]$actualProject = Get-Content -LiteralPath (Join-Path $projectRoot 'src\Museek\Museek.csproj') -Raw
$version = [string]$actualProject.Project.PropertyGroup.Version
$tag = 'v' + $version
$runDirectory = Join-Path $projectRoot ('artifacts\release-checks-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $runDirectory 'Fixture 音楽 Project'
$payload = Join-Path $fixture 'dist\Museek'
$output = Join-Path $fixture 'dist\release'
$projectFile = Join-Path $fixture 'src\Museek\Museek.csproj'
$notesFile = Join-Path $fixture "releases\$version.md"
$installer = Join-Path $fixture 'dist\setup.exe'
$fixtureExe = Join-Path $payload 'Museek.exe'

function Assert-WorkspacePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($runDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Test path is outside this run's workspace: $absolute"
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

function Write-Fixture([string]$Path, [string]$Text) {
    [void](Assert-WorkspacePath $Path)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Assert-Throws([scriptblock]$Action, [string]$Message, [string]$ErrorPattern = '') {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert-Check ($null -ne $caught) $Message
    if ($ErrorPattern) {
        Assert-Check ($caught.Exception.Message -match $ErrorPattern) "$Message reports the relevant problem"
    }
}

function Invoke-Prepare {
    & $prepare -Tag $tag -ProjectRoot $fixture -PayloadDirectory $payload `
        -InstallerPath $installer -OutputDirectory $output
}

function Read-Checksums([string]$Path) {
    $hashes = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if (-not $line.Trim()) { continue }
        Assert-Check ($line -match '^([a-fA-F0-9]{64})\s+\*?([^\r\n]+)$') 'checksum entries use SHA256 and a filename'
        $name = $Matches[2]
        Assert-Check (-not $hashes.ContainsKey($name)) "checksum has no duplicate entry for $name"
        $hashes[$name] = $Matches[1]
    }
    return $hashes
}

function Convert-JsonMap([object]$Value) {
    # Windows PowerShell 5.1 does not support ConvertFrom-Json -AsHashtable.
    if ($null -eq $Value) { return $null }
    if ($Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [array]) {
        $items = @($Value | ForEach-Object { Convert-JsonMap $_ })
        return ,$items
    }
    if ($Value -is [pscustomobject]) {
        $map = @{}
        foreach ($property in $Value.PSObject.Properties) {
            $map[$property.Name] = Convert-JsonMap $property.Value
        }
        return $map
    }
    return $Value
}

New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
[void](Assert-WorkspacePath $fixture)
$validProject = "<Project><PropertyGroup><Version>$version</Version></PropertyGroup></Project>"
$validNotes = "# Museek $version`n`n## Changes`n`n- Publish the Windows installer after an explicit version tag.`n- Preserve música metadata and album artwork.`n"
Write-Fixture $projectFile $validProject
Write-Fixture $notesFile $validNotes
Write-Fixture (Join-Path $fixture 'README.md') "# Museek`nRun the installer to install or update.`n"
Write-Fixture (Join-Path $fixture 'THIRD-PARTY-NOTICES.md') "# Notices`nFixture copyright notice.`n"
Write-Fixture (Join-Path $fixture 'CHANGELOG.md') "# Changelog`n`n## $version`n- Add tagged releases.`n"
Write-Fixture (Join-Path $payload 'licenses\LICENSE.txt') "Release fixture license text.`n"
Write-Fixture (Join-Path $payload 'licenses\FFmpeg-build.txt') "ffmpeg fixture build`nconfiguration: --fixture`n"
Write-Fixture (Join-Path $payload 'licenses\subfolder\Unicode-音楽.txt') "Nested license fixture.`n"
Write-Fixture (Join-Path $payload 'NOT-A-RELEASE-ASSET.txt') 'Never publish the raw payload folder.'
foreach ($pair in @(@('dist\setup.exe', $installer), @('dist\Museek\Museek.exe', $fixtureExe))) {
    $source = Join-Path $projectRoot $pair[0]
    Assert-Check (Test-Path -LiteralPath $source -PathType Leaf) "built $($pair[0]) is available; run Build.ps1 before ReleaseChecks"
    [void](Assert-WorkspacePath $pair[1])
    Copy-Item -LiteralPath $source -Destination $pair[1]
}

$release = @(& $getInfo -Tag $tag -ProjectRoot $fixture)
Assert-Check ($release.Count -eq 1) 'metadata validation returns exactly one result'
Assert-Check ($release[0].Version -ceq $version) 'version comes from the validated tag and project'
Assert-Check ($release[0].Tag -ceq $tag) 'metadata retains the exact tag'
Assert-Check ($release[0].Title -ceq ('Museek ' + $version)) 'release title contains the app and version'
Assert-Check ([IO.Path]::GetFullPath($release[0].NotesPath) -ceq [IO.Path]::GetFullPath($notesFile)) 'release notes resolve to this version inside the project'

foreach ($invalidTag in @('', $version, 'V' + $version, 'v01.1.0', 'v1.01.0', 'v1.1.00',
    'v1.1.0.0', 'v1.1', 'v1.1.0-rc.1', 'v1.1.0+build', 'v../1.1.0', 'v1/1/0',
    (' ' + $tag), ($tag + ' '), ($tag + "`n"), 'v-1.1.0')) {
    $capturedTag = $invalidTag
    Assert-Throws { & $getInfo -Tag $capturedTag -ProjectRoot $fixture } "invalid tag is refused: [$capturedTag]"
}
Assert-Throws { & $getInfo -Tag 'v42.43.44' -ProjectRoot $fixture } 'tag and project version mismatch is refused' 'version|match'
Write-Fixture $projectFile '<Project><PropertyGroup><Version>01.1.0</Version></PropertyGroup></Project>'
Assert-Throws { & $getInfo -Tag $tag -ProjectRoot $fixture } 'a noncanonical project version is refused' 'version|match'
Write-Fixture $projectFile "<Project><PropertyGroup><Version>$version</Version><Version>$version</Version></PropertyGroup></Project>"
Assert-Throws { & $getInfo -Tag $tag -ProjectRoot $fixture } 'ambiguous duplicate project versions are refused' 'version|single'
Write-Fixture $projectFile '<Project><PropertyGroup><Version>65535.65535.65535</Version></PropertyGroup></Project>'
Write-Fixture (Join-Path $fixture 'releases\65535.65535.65535.md') "# Highest Windows version resource values`n`n- Keep all three version components in the valid resource range.`n"
$maximum = & $getInfo -Tag 'v65535.65535.65535' -ProjectRoot $fixture
Assert-Check ($maximum.Version -ceq '65535.65535.65535') 'the largest Windows version resource components are accepted'
foreach ($overflow in @('65536.1.1', '1.65536.1', '1.1.65536')) {
    Write-Fixture $projectFile "<Project><PropertyGroup><Version>$overflow</Version></PropertyGroup></Project>"
    $overflowTag = 'v' + $overflow
    Assert-Throws { & $getInfo -Tag $overflowTag -ProjectRoot $fixture } 'version components exceeding Windows resource limits are refused' 'version|65535|Windows'
}
Write-Fixture $projectFile $validProject

$savedNotes = $notesFile + '.saved'
[void](Assert-WorkspacePath $savedNotes)
Move-Item -LiteralPath $notesFile -Destination $savedNotes
Assert-Throws { & $getInfo -Tag $tag -ProjectRoot $fixture } 'missing notes are refused' 'note|missing|exist'
Move-Item -LiteralPath $savedNotes -Destination $notesFile
foreach ($invalidNotes in @('', " `r`n`t", "# Museek $version`n`n## Changes`n", "# Museek $version`n`nTODO`n",
    "# Museek $version`n`nTBD`n", "# Museek $version`n`nCHANGEME`n")) {
    Write-Fixture $notesFile $invalidNotes
    Assert-Throws { & $getInfo -Tag $tag -ProjectRoot $fixture } 'empty or placeholder release notes are refused' 'note|empty|placeholder|TODO|TBD'
}
Write-Fixture $notesFile $validNotes

# A pre-existing unrelated file must cause refusal before any setup or documentation is overwritten.
Write-Fixture (Join-Path $output 'my-recording.flac') 'User-owned sentinel.'
Assert-Throws { Invoke-Prepare } 'unknown files in the output folder are refused' 'output|unexpected|unknown|recogniz'
Assert-Check ([IO.File]::ReadAllText((Join-Path $output 'my-recording.flac')) -ceq 'User-owned sentinel.') 'refusal preserves unrelated output content'
Assert-Check (-not (Test-Path -LiteralPath (Join-Path $output "Museek-$version-setup.exe"))) 'refusal creates no partial release installer'
$output = Join-Path $fixture 'dist\clean-release'

Assert-Throws {
    & $prepare -Tag $tag -ProjectRoot $fixture -PayloadDirectory $payload -InstallerPath $installer `
        -OutputDirectory (Join-Path $runDirectory 'outside-project')
} 'release output outside the project dist folder is refused' 'output|dist|inside|within'
foreach ($alias in @($payload, (Join-Path $payload 'release'), (Join-Path $fixture 'dist'))) {
    $capturedAlias = $alias
    Assert-Throws {
        & $prepare -Tag $tag -ProjectRoot $fixture -PayloadDirectory $payload -InstallerPath $installer `
            -OutputDirectory $capturedAlias
    } 'output cannot overwrite or overlap the installer or app payload' 'output|separate|dist'
}
$junctionTarget = Join-Path $fixture 'junction-target'
$junctionOutput = Join-Path $fixture 'dist\linked-release'
[void](Assert-WorkspacePath $junctionTarget)
[void](Assert-WorkspacePath $junctionOutput)
New-Item -ItemType Directory -Path $junctionTarget | Out-Null
New-Item -ItemType Junction -Path $junctionOutput -Value $junctionTarget | Out-Null
Assert-Throws {
    & $prepare -Tag $tag -ProjectRoot $fixture -PayloadDirectory $payload -InstallerPath $installer `
        -OutputDirectory $junctionOutput
} 'release output junctions are refused' 'symbolic|junction|link'
Assert-Check (@(Get-ChildItem -LiteralPath $junctionTarget).Count -eq 0) 'junction refusal leaves the target untouched'

$staleExe = (Get-Process -Id $PID).Path
$savedExe = $fixtureExe + '.saved'
Move-Item -LiteralPath $fixtureExe -Destination $savedExe
Copy-Item -LiteralPath $staleExe -Destination $fixtureExe
Assert-Throws { Invoke-Prepare } 'a stale published app is refused' 'version|match'
Assert-Check (-not (Test-Path -LiteralPath (Join-Path $output "Museek-$version-setup.exe"))) 'stale app refusal creates no release installer'
Move-Item -LiteralPath $savedExe -Destination $fixtureExe -Force
$savedSetup = $installer + '.saved'
Move-Item -LiteralPath $installer -Destination $savedSetup
Copy-Item -LiteralPath $staleExe -Destination $installer
Assert-Throws { Invoke-Prepare } 'a stale installer is refused' 'version|match'
Assert-Check (-not (Test-Path -LiteralPath (Join-Path $output "Museek-$version-setup.exe"))) 'stale installer refusal creates no release installer'
Move-Item -LiteralPath $savedSetup -Destination $installer -Force

foreach ($requiredDoc in @('README.md', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md')) {
    $docPath = Join-Path $fixture $requiredDoc
    $savedDoc = $docPath + '.saved'
    Move-Item -LiteralPath $docPath -Destination $savedDoc
    Assert-Throws { Invoke-Prepare } "$requiredDoc must exist before release staging" 'document|missing|empty'
    Move-Item -LiteralPath $savedDoc -Destination $docPath
    $originalDoc = [IO.File]::ReadAllText($docPath)
    Write-Fixture $docPath " `r`n"
    Assert-Throws { Invoke-Prepare } "$requiredDoc must contain text before release staging" 'document|missing|empty'
    Write-Fixture $docPath $originalDoc
}
$licenseDirectory = Join-Path $payload 'licenses'
$savedLicenses = Join-Path $payload 'licenses.saved'
Move-Item -LiteralPath $licenseDirectory -Destination $savedLicenses
Assert-Throws { Invoke-Prepare } 'missing published licenses are refused' 'license|missing'
New-Item -ItemType Directory -Path $licenseDirectory | Out-Null
Assert-Throws { Invoke-Prepare } 'empty published licenses are refused' 'license|empty'
Remove-Item -LiteralPath $licenseDirectory
Move-Item -LiteralPath $savedLicenses -Destination $licenseDirectory

Invoke-Prepare | Out-Null
$expectedAssets = @("Museek-$version-setup.exe", 'release-notes.md', 'README.md',
    'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md', 'licenses.zip', 'SHA256SUMS.txt') | Sort-Object
$actualAssets = @(Get-ChildItem -LiteralPath $output -File | Select-Object -ExpandProperty Name | Sort-Object)
Assert-Check (@(Compare-Object $expectedAssets $actualAssets).Count -eq 0) 'staging contains exactly the installer, notes, documents, licenses, and checksums'
Assert-Check (@(Get-ChildItem -LiteralPath $output -Directory).Count -eq 0) 'staging contains no raw app subfolders'
Assert-Check ([IO.File]::ReadAllText((Join-Path $output 'release-notes.md')) -ceq $validNotes) 'release notes retain exact text and Unicode characters'
foreach ($doc in @('README.md', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md')) {
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $fixture $doc)).Hash -ceq
        (Get-FileHash -LiteralPath (Join-Path $output $doc)).Hash) "$doc comes from the tagged source"
}
Assert-Check ((Get-FileHash -LiteralPath $installer).Hash -ceq
    (Get-FileHash -LiteralPath (Join-Path $output "Museek-$version-setup.exe")).Hash) 'the versioned setup is byte-identical to the validated installer'
$hashes = Read-Checksums (Join-Path $output 'SHA256SUMS.txt')
$checksummedAssets = @($expectedAssets | Where-Object { $_ -ne 'SHA256SUMS.txt' })
Assert-Check (@(Compare-Object $checksummedAssets @($hashes.Keys | Sort-Object)).Count -eq 0) 'checksums cover every staged asset except the checksum file itself'
foreach ($name in $checksummedAssets) {
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash -ieq
        $hashes[$name]) "$name matches its published SHA256 checksum"
}
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $output 'licenses.zip'))
try {
    $entries = @($archive.Entries | Where-Object Name | ForEach-Object { $_.FullName.Replace('\', '/') -replace '^licenses/', '' })
    Assert-Check ($entries -contains 'LICENSE.txt') 'licenses archive contains the published app license'
    Assert-Check ($entries -contains 'FFmpeg-build.txt') 'licenses archive contains exact FFmpeg build information'
    Assert-Check ($entries -contains 'subfolder/Unicode-音楽.txt') 'licenses archive preserves nested files and Unicode names'
    Assert-Check ($entries.Count -eq 3) 'licenses archive contains all and only the payload licenses'
    $buildEntry = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -match '(^|/)FFmpeg-build\.txt$' })[0]
    $reader = [IO.StreamReader]::new($buildEntry.Open())
    try {
        Assert-Check ($reader.ReadToEnd() -ceq [IO.File]::ReadAllText((Join-Path $payload 'licenses\FFmpeg-build.txt'))) 'archived FFmpeg information retains the exact published bytes'
    } finally { $reader.Dispose() }
} finally { $archive.Dispose() }

$previousPackageHash = (Get-FileHash -LiteralPath (Join-Path $output "Museek-$version-setup.exe")).Hash
Move-Item -LiteralPath $fixtureExe -Destination $savedExe
Assert-Throws { Invoke-Prepare } 'missing published app refuses restaging before mutation' 'binary|missing|Build'
Assert-Check ((Get-FileHash -LiteralPath (Join-Path $output "Museek-$version-setup.exe")).Hash -ceq
    $previousPackageHash) 'failed restaging preserves the previous installer'
Assert-Check ([IO.File]::ReadAllText((Join-Path $output 'release-notes.md')) -ceq $validNotes) 'failed restaging preserves previous release notes'
Move-Item -LiteralPath $savedExe -Destination $fixtureExe

# Re-staging is allowed, but stale assets from another version cannot survive in the package.
Write-Fixture (Join-Path $output 'Museek-0.0.1-setup.exe') 'Obsolete test setup.'
Write-Fixture (Join-Path $output 'README.md') 'Stale test documentation.'
Invoke-Prepare | Out-Null
Assert-Check (-not (Test-Path -LiteralPath (Join-Path $output 'Museek-0.0.1-setup.exe'))) 're-staging removes stale versioned installers'
Assert-Check ([IO.File]::ReadAllText((Join-Path $output 'README.md')) -ceq
    [IO.File]::ReadAllText((Join-Path $fixture 'README.md'))) 're-staging refreshes current documentation'

# Parse YAML using a test-only library, not indentation or text assumptions.
if (-not $DotNetPath) {
    $portableSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $portableSdk) { $DotNetPath = $portableSdk }
    else { $DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$parserRoot = Join-Path $runDirectory 'YamlParser'
Write-Fixture (Join-Path $parserRoot 'YamlParser.csproj') @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><PackageReference Include="YamlDotNet" Version="16.3.0" /></ItemGroup>
</Project>
'@
Write-Fixture (Join-Path $parserRoot 'Program.cs') @'
using System.Text.Json;
using YamlDotNet.Serialization;
try {
    var deserializer = new DeserializerBuilder().WithDuplicateKeyChecking().Build();
    var workflow = deserializer.Deserialize<object>(File.ReadAllText(args[0]));
    Console.WriteLine(JsonSerializer.Serialize(workflow));
    return 0;
} catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
'@
& $DotNetPath build (Join-Path $parserRoot 'YamlParser.csproj') -c Release --nologo -v quiet
Assert-Check ($LASTEXITCODE -eq 0) 'the isolated YAML parser builds'
$workflowPath = Join-Path $projectRoot '.github\workflows\release.yml'
Assert-Check (Test-Path -LiteralPath $workflowPath -PathType Leaf) 'the GitHub release workflow exists'
$workflowJson = & $DotNetPath (Join-Path $parserRoot 'bin\Release\net10.0\YamlParser.dll') $workflowPath
Assert-Check ($LASTEXITCODE -eq 0) 'release workflow is valid YAML without duplicate keys'
$workflow = Convert-JsonMap ($workflowJson | ConvertFrom-Json)
Assert-Check ($workflow.on.Count -eq 1 -and $workflow.on.ContainsKey('push')) 'only an explicit push event can trigger release publishing'
Assert-Check ($workflow.on.push.ContainsKey('tags') -and -not $workflow.on.push.ContainsKey('branches')) 'release triggers have tags and no main-branch push trigger'
Assert-Check (@($workflow.on.push.tags).Count -gt 0 -and @($workflow.on.push.tags | Where-Object { $_ -notlike 'v*' }).Count -eq 0) 'release tag filters are version-prefixed'
Assert-Check ($workflow.permissions.Count -eq 1 -and $workflow.permissions.contents -eq 'read') 'tagged source builds with a read-only repository token'
Assert-Check ($workflow.jobs.ContainsKey('build') -and $workflow.jobs.ContainsKey('publish')) 'building and publication have separate jobs'
Assert-Check ($workflow.jobs.publish.needs -eq 'build') 'publication requires successful checks and packaging'
Assert-Check ($workflow.jobs.publish.permissions.Count -eq 1 -and $workflow.jobs.publish.permissions.contents -eq 'write') 'only publication requests release write permission'
Assert-Check (-not $workflow.jobs.build.ContainsKey('permissions') -or
    $workflow.jobs.build.permissions.contents -eq 'read') 'the build job does not gain release write permission'
$buildSteps = @($workflow.jobs.build.steps)
$uses = @($workflow.jobs.Values | ForEach-Object { $_.steps } | Where-Object { $_.ContainsKey('uses') })
foreach ($step in $uses) {
    Assert-Check ($step.uses -match '@[a-fA-F0-9]{40}$') "workflow action $($step.uses) is pinned to its source revision"
}
$checkout = @($buildSteps | Where-Object { $_.ContainsKey('uses') -and $_.uses -like 'actions/checkout@*' })
Assert-Check ($checkout.Count -eq 1) 'the build checks out tagged source exactly once'
Assert-Check (-not $checkout[0].with.ContainsKey('ref') -or $checkout[0].with.ref -eq '${{ github.sha }}' -or
    $checkout[0].with.ref -eq '${{ github.ref }}') 'checkout follows the triggering commit instead of main'
Assert-Check ($checkout[0].with['persist-credentials'].ToString() -eq 'false') 'checkout does not preserve a Git write credential in tagged source'
$metadataIndex = -1; $buildIndex = -1; $prepareIndex = -1; $uploadIndex = -1
for ($i = 0; $i -lt $buildSteps.Count; $i++) {
    $step = $buildSteps[$i]
    if ($step.ContainsKey('run')) {
        if ($step.run -match 'Get-ReleaseInfo\.ps1') { $metadataIndex = $i }
        if ($step.run -match '[./\\]Build\.ps1') { $buildIndex = $i; Assert-Check ($step.run -notmatch '-SkipChecks') 'release builds run all app and installer checks' }
        if ($step.run -match 'Prepare-Release\.ps1') { $prepareIndex = $i }
    }
    if ($step.ContainsKey('uses') -and $step.uses -like 'actions/upload-artifact@*') {
        $uploadIndex = $i
        Assert-Check ($step.with.path -eq 'dist/release/**') 'only prepared release assets are uploaded'
        Assert-Check ($step.with['if-no-files-found'] -eq 'error') 'missing release assets fail the build'
    }
}
Assert-Check ($metadataIndex -ge 0 -and $metadataIndex -lt $buildIndex -and $buildIndex -lt $prepareIndex -and
    $prepareIndex -lt $uploadIndex) 'metadata validation, full checks, asset staging, and upload run in that order'
$publishSteps = @($workflow.jobs.publish.steps)
Assert-Check (@($publishSteps | Where-Object { $_.ContainsKey('uses') -and $_.uses -like 'actions/checkout@*' }).Count -eq 0) 'publication executes its reviewed workflow without checking out tagged source with a write token'
$download = @($publishSteps | Where-Object { $_.ContainsKey('uses') -and $_.uses -like 'actions/download-artifact@*' })
Assert-Check ($download.Count -eq 1 -and $download[0].with['artifact-ids'] -eq '${{ needs.build.outputs.artifact-id }}') 'publication downloads the exact tested artifact from this build'
$publishRun = @($publishSteps | Where-Object { $_.ContainsKey('run') } | ForEach-Object { $_.run }) -join "`n"
Assert-Check ($publishRun -match 'gh' -and $publishRun -match "'release',\s*'create'" -and
    $publishRun -match '--verify-tag' -and $publishRun -match '--notes-file') 'publication creates a tag-verified release with explicit release notes'
Assert-Check ($publishRun -notmatch '--clobber|release\s+delete|release\s+edit') 'reruns cannot overwrite a published release'

# Execute the reviewed publication step against local fixtures with an in-scope gh stub.
# Every gh invocation resolves to this function; this never contacts GitHub or publishes.
function Invoke-PublishPreview {
    $script:previewCalls = 0
    $script:previewArguments = @()
    function gh {
        $script:previewCalls++
        $script:previewArguments = @($args)
        Set-Variable -Name LASTEXITCODE -Scope 1 -Value 0
    }
    Assert-Check ((Get-Command gh).CommandType -eq 'Function') 'publication preview uses its local gh stub'
    & ([scriptblock]::Create($publishRun))
}

$releaseAssets = Join-Path $fixture 'release-assets'
[void](Assert-WorkspacePath $output)
[void](Assert-WorkspacePath $releaseAssets)
Move-Item -LiteralPath $output -Destination $releaseAssets
$savedEnvironment = @{}
$savedGlobalExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$hadGlobalExitCode = $null -ne $savedGlobalExitCode
$globalExitCodeValue = if ($hadGlobalExitCode) { $savedGlobalExitCode.Value } else { $null }
foreach ($name in @('GITHUB_WORKSPACE', 'RELEASE_TAG', 'RELEASE_VERSION', 'RELEASE_TITLE')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $env:GITHUB_WORKSPACE = $fixture
    $env:RELEASE_TAG = $tag
    $env:RELEASE_VERSION = $version
    $env:RELEASE_TITLE = 'Museek ' + $version
    Invoke-PublishPreview
    Assert-Check ($script:previewCalls -eq 1) 'a complete verified package invokes release creation exactly once'
    Assert-Check ($script:previewArguments[0] -ceq 'release' -and $script:previewArguments[1] -ceq 'create' -and
        $script:previewArguments[2] -ceq $tag) 'release creation receives the exact version tag'
    Assert-Check ($script:previewArguments -contains '--verify-tag') 'release creation verifies that the tag exists'
    $notesFlag = [Array]::IndexOf($script:previewArguments, '--notes-file')
    Assert-Check ($notesFlag -ge 0 -and $script:previewArguments[$notesFlag + 1] -ceq
        (Join-Path $releaseAssets 'release-notes.md')) 'the tagged release notes become the release body'
    $attached = @($script:previewArguments | Where-Object {
        $_ -is [string] -and $_.StartsWith($releaseAssets + '\', [StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object { [IO.Path]::GetFileName($_) })
    # The notes path occurs once as the body argument and once as a downloadable attachment.
    Assert-Check (@($attached | Where-Object { $_ -ceq 'release-notes.md' }).Count -eq 2) 'release notes are attached as well as used for the release body'
    Assert-Check (@(Compare-Object $expectedAssets @($attached | Sort-Object -Unique)).Count -eq 0) 'all seven checksummed release files are attached'

    Write-Fixture (Join-Path $releaseAssets 'README.md') 'Damaged after artifact download.'
    Assert-Throws { Invoke-PublishPreview } 'modified downloaded assets refuse publication' 'checksum'
    Assert-Check ($script:previewCalls -eq 0) 'checksum refusal makes no release command call'
    Copy-Item -LiteralPath (Join-Path $fixture 'README.md') -Destination (Join-Path $releaseAssets 'README.md') -Force

    Write-Fixture (Join-Path $releaseAssets 'unrelated-recording.flac') 'Never attach user audio.'
    Assert-Throws { Invoke-PublishPreview } 'unexpected downloaded assets refuse publication' 'seven|expected|package'
    Assert-Check ($script:previewCalls -eq 0) 'unexpected asset refusal makes no release command call'
    Remove-Item -LiteralPath (Join-Path $releaseAssets 'unrelated-recording.flac')

    $downloadedSetup = Join-Path $releaseAssets "Museek-$version-setup.exe"
    $savedDownloadedSetup = Join-Path $fixture 'saved-downloaded-setup.exe'
    [void](Assert-WorkspacePath $downloadedSetup)
    [void](Assert-WorkspacePath $savedDownloadedSetup)
    Move-Item -LiteralPath $downloadedSetup -Destination $savedDownloadedSetup
    Assert-Throws { Invoke-PublishPreview } 'missing downloaded installer refuses publication' 'seven|expected|package'
    Assert-Check ($script:previewCalls -eq 0) 'missing installer refusal makes no release command call'
    Move-Item -LiteralPath $savedDownloadedSetup -Destination $downloadedSetup
    Invoke-PublishPreview
    Assert-Check ($script:previewCalls -eq 1) 'the intact restored package can be published'
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    if ($hadGlobalExitCode) {
        Set-Variable -Name LASTEXITCODE -Scope Global -Value $globalExitCodeValue
    } else {
        Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
}

Write-Host "Release check artifacts: $runDirectory"
Write-Host "Release checks passed: $script:checkCount"
