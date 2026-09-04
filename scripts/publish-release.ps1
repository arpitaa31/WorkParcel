[CmdletBinding()]
param(
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$solutionPath = Join-Path $repoRoot 'WorkParcel.slnx'
$appProjectPath = Join-Path $repoRoot 'src\WorkParcel.App\WorkParcel.App.csproj'
$hostProjectPath = Join-Path $repoRoot 'src\WorkParcel.BrowserHost\WorkParcel.BrowserHost.csproj'
$extensionSource = Join-Path $repoRoot 'browser-extension'
$browserGuideSource = Join-Path $repoRoot 'docs\BROWSER-EXTENSION-SETUP.md'
$troubleshootingSource = Join-Path $repoRoot 'docs\INSTALLATION-TROUBLESHOOTING.md'
$portableReadmeSource = Join-Path $repoRoot 'docs\PORTABLE-README.md'

function Invoke-RequiredCommand([string]$FilePath, [string[]]$Arguments) {
    Write-Host (">> {0} {1}" -f $FilePath, ($Arguments -join ' '))
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Required command failed with exit code ${LASTEXITCODE}: $FilePath"
    }
}

function Assert-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file was not produced: $Path" }
}

function Assert-Directory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Required directory was not produced: $Path" }
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

function Get-InnoSetupCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }

    $candidateRoots = @()
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles})) { $candidateRoots += ${env:ProgramFiles} }
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) { $candidateRoots += ${env:ProgramFiles(x86)} }
    if (-not [string]::IsNullOrWhiteSpace(${env:LOCALAPPDATA})) { $candidateRoots += (Join-Path ${env:LOCALAPPDATA} 'Programs') }
    foreach ($root in ($candidateRoots | Sort-Object -Unique)) {
        $candidate = Get-ChildItem -LiteralPath $root -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'ISCC.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($null -ne $candidate) { return $candidate }
    }
    return $null
}

if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) { throw "Version source not found: $propsPath" }
[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$version = [string]$props.Project.PropertyGroup.VersionPrefix
$releaseVersion = [string]$props.Project.PropertyGroup.ReleaseVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props must contain a three-part VersionPrefix; found '$version'." }
if ($releaseVersion -ne '0.2.0-beta.1') { throw "This release script is for 0.2.0-beta.1; found '$releaseVersion'." }
$releaseNotesSource = Join-Path $repoRoot ("docs\RELEASE-NOTES-{0}.md" -f $version)

foreach ($requiredPath in @($solutionPath, $appProjectPath, $hostProjectPath, $extensionSource, $releaseNotesSource, $browserGuideSource, $troubleshootingSource, $portableReadmeSource)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Required release input is missing: $requiredPath" }
}

$releaseRoot = Join-Path $repoRoot ("artifacts\release\{0}" -f $releaseVersion)
$stagingRoot = Join-Path $releaseRoot 'staging'
$installerStage = Join-Path $stagingRoot 'installer'
$portableStage = Join-Path $stagingRoot ("WorkParcel-Portable-{0}-win-x64" -f $releaseVersion)
$extensionStage = Join-Path $stagingRoot 'browser-extension'
$appPublish = Join-Path $stagingRoot 'app-publish'
$hostPublish = Join-Path $stagingRoot 'host-publish'
$portableZip = Join-Path $releaseRoot ("WorkParcel-Portable-{0}-win-x64.zip" -f $releaseVersion)
$extensionZip = Join-Path $releaseRoot ("WorkParcel-Browser-Extension-{0}.zip" -f $releaseVersion)
$installerArtifact = Join-Path $releaseRoot ("WorkParcel-Setup-{0}.exe" -f $releaseVersion)
$checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$releaseNotesArtifact = Join-Path $releaseRoot ("RELEASE_NOTES-{0}.md" -f $releaseVersion)
$browserGuideArtifact = Join-Path $releaseRoot 'BROWSER-EXTENSION-SETUP.md'
$troubleshootingArtifact = Join-Path $releaseRoot 'INSTALLATION-TROUBLESHOOTING.md'

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
Invoke-RequiredCommand $dotnet @('restore', $solutionPath, '-r', 'win-x64')
Invoke-RequiredCommand $dotnet @('build', $solutionPath, '-c', 'Release', '--no-restore')
Invoke-RequiredCommand $dotnet @('test', $solutionPath, '-c', 'Release', '--no-restore')

New-Item -ItemType Directory -Path $appPublish, $hostPublish -Force | Out-Null
$publishCommon = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:Platform=x64', '-p:WindowsPackageType=None', '-p:EnableMsixTooling=false', '-p:EnableWinAppRunSupport=false', '-p:WindowsAppSDKSelfContained=true', '-p:PublishSingleFile=false', '-p:PublishReadyToRun=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
Invoke-RequiredCommand $dotnet (@('publish', $appProjectPath) + $publishCommon + @('-p:PublishDir=' + $appPublish + '\'))
Invoke-RequiredCommand $dotnet (@('publish', $hostProjectPath) + $publishCommon + @('-p:PublishDir=' + $hostPublish + '\'))

Assert-File (Join-Path $appPublish 'WorkParcel.exe')
Assert-File (Join-Path $appPublish 'Assets\AppIcon.ico')
Assert-File (Join-Path $hostPublish 'WorkParcel.BrowserHost.exe')

# The installer stage mirrors the installed package contents. It contains binaries and
# user-facing support files only; no source, database, logs, or build cache.
New-Item -ItemType Directory -Path $installerStage, (Join-Path $installerStage 'App'), (Join-Path $installerStage 'BrowserHost'), (Join-Path $installerStage 'Documentation') -Force | Out-Null
Copy-DirectoryContents $appPublish (Join-Path $installerStage 'App')
Copy-DirectoryContents $hostPublish (Join-Path $installerStage 'BrowserHost')
Copy-Item -LiteralPath (Join-Path $repoRoot 'tools\Setup-BrowserHost.ps1') -Destination (Join-Path $installerStage 'BrowserHost\Setup-BrowserHost.ps1') -Force

New-Item -ItemType Directory -Path $extensionStage -Force | Out-Null
foreach ($fileName in @('manifest.json', 'background.js', 'popup.html', 'popup.js', 'popup.css')) {
    Copy-Item -LiteralPath (Join-Path $extensionSource $fileName) -Destination $extensionStage -Force
}
Copy-DirectoryContents (Join-Path $extensionSource 'icons') (Join-Path $extensionStage 'icons')
Copy-DirectoryContents $extensionStage (Join-Path $installerStage 'browser-extension')
foreach ($doc in @(@{Source=$browserGuideSource; Name='Browser-Extension-Setup.md'}, @{Source=$troubleshootingSource; Name='Installation-Troubleshooting.md'})) {
    Copy-Item -LiteralPath $doc.Source -Destination (Join-Path $installerStage ("Documentation\{0}" -f $doc.Name)) -Force
}

# Portable package: WorkParcel.exe is at the extraction root and supporting
# files remain alongside it. User data still goes to Local AppData.
New-Item -ItemType Directory -Path $portableStage, (Join-Path $portableStage 'BrowserHost'), (Join-Path $portableStage 'browser-extension'), (Join-Path $portableStage 'Documentation') -Force | Out-Null
Copy-DirectoryContents $appPublish $portableStage
Copy-DirectoryContents $hostPublish (Join-Path $portableStage 'BrowserHost')
Copy-Item -LiteralPath (Join-Path $repoRoot 'tools\Setup-BrowserHost.ps1') -Destination (Join-Path $portableStage 'BrowserHost\Setup-BrowserHost.ps1') -Force
Copy-DirectoryContents $extensionStage (Join-Path $portableStage 'browser-extension')
Copy-Item -LiteralPath $portableReadmeSource -Destination (Join-Path $portableStage 'README.md') -Force
Copy-Item -LiteralPath $browserGuideSource -Destination (Join-Path $portableStage 'Documentation\Browser-Extension-Setup.md') -Force
Copy-Item -LiteralPath $troubleshootingSource -Destination (Join-Path $portableStage 'Documentation\Installation-Troubleshooting.md') -Force

foreach ($pdb in (Get-ChildItem -LiteralPath $stagingRoot -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $pdb.FullName -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $portableZip) { Remove-Item -LiteralPath $portableZip -Force }
if (Test-Path -LiteralPath $extensionZip) { Remove-Item -LiteralPath $extensionZip -Force }
Compress-Archive -LiteralPath $portableStage -DestinationPath $portableZip -CompressionLevel Optimal
Compress-Archive -LiteralPath $extensionStage -DestinationPath $extensionZip -CompressionLevel Optimal

Copy-Item -LiteralPath $releaseNotesSource -Destination $releaseNotesArtifact -Force
Copy-Item -LiteralPath $browserGuideSource -Destination $browserGuideArtifact -Force
Copy-Item -LiteralPath $troubleshootingSource -Destination $troubleshootingArtifact -Force

$inno = if ($SkipInstaller) { $null } else { Get-InnoSetupCompiler }
if ($null -ne $inno) {
    $issPath = Join-Path $repoRoot 'installer\WorkParcel.iss'
    Invoke-RequiredCommand $inno @($issPath, "/DAppVersion=$version", "/DReleaseLabel=$releaseVersion", "/DStageDir=$installerStage", "/DReleaseDir=$releaseRoot")
    Assert-File $installerArtifact
} else {
    Write-Warning ("Inno Setup compiler was not found. Portable and browser-extension artifacts were created; install Inno Setup and rerun this script to create {0}." -f $installerArtifact)
}

foreach ($archivePath in @($portableZip, $extensionZip)) {
    Assert-File $archivePath
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($archive.Entries.Count -eq 0) { throw "Archive is empty: $archivePath" }
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -match '(^|/)(\.git|obj|bin|TestResults|\.vscode)(/|$)|(^|/)\.env($|\.)|\.db($|[-.])|\.log$|\.pdb$') { throw "Forbidden release entry '$($entry.FullName)' found in $archivePath" }
        }
    } finally { $archive.Dispose() }
}

$requiredArtifacts = @($portableZip, $extensionZip, $releaseNotesArtifact, $browserGuideArtifact, $troubleshootingArtifact)
if ($null -ne $inno) { $requiredArtifacts += $installerArtifact }
foreach ($artifact in $requiredArtifacts) { Assert-File $artifact }

$hashLines = foreach ($artifact in ($requiredArtifacts | Sort-Object)) {
    $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    "{0}  {1}" -f $hash, (Split-Path -Leaf $artifact)
}
Set-Content -LiteralPath $checksumPath -Value $hashLines -Encoding ascii
foreach ($line in $hashLines) {
    $parts = $line -split '\s+', 2
    $checkPath = Join-Path $releaseRoot $parts[1]
    if ((Get-FileHash -LiteralPath $checkPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $parts[0]) { throw "Checksum verification failed: $checkPath" }
}

$forbiddenNames = @('.git', '.env', 'obj', 'TestResults', 'browser-profile', 'cookies', 'workparcel.db', 'host.log', 'workparcel.log')
foreach ($entry in (Get-ChildItem -LiteralPath $stagingRoot -Recurse -Force)) {
    foreach ($forbidden in $forbiddenNames) {
        if ($entry.Name -ieq $forbidden -or $entry.FullName -match "[\\/]$([regex]::Escape($forbidden))([\\/]|$)") { throw "Forbidden release staging entry found: $($entry.FullName)" }
    }
}

Write-Host ''
Write-Host 'WorkParcel release artifacts:'
foreach ($artifact in ($requiredArtifacts | Sort-Object)) { Write-Host (" - {0}" -f $artifact) }
Write-Host (" - {0}" -f $checksumPath)
if ($null -eq $inno) { Write-Host 'Installer: not compiled (Inno Setup unavailable).' } else { Write-Host ("Installer compiler: {0}" -f $inno) }
