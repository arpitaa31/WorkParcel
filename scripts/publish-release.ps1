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
$releaseVersion = '0.2.0-beta.3'
$releaseNotesSource = Join-Path $repoRoot 'docs\RELEASE-NOTES-0.2.0-beta.3.md'
$troubleshootingSource = Join-Path $repoRoot 'docs\INSTALLATION-TROUBLESHOOTING.md'
$portableReadmeSource = Join-Path $repoRoot 'docs\PORTABLE-README.md'

function Invoke-RequiredCommand([string]$FilePath, [string[]]$Arguments) {
    Write-Host (">> {0} {1}" -f $FilePath, ($Arguments -join ' '))
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Required command failed with exit code $($LASTEXITCODE): $FilePath" }
}

function Assert-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file was not produced: $Path" }
}

function Assert-Directory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Required directory was not produced: $Path" }
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    Assert-Directory $Source
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

function Get-InnoSetupCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }

    $candidateRoots = @()
    $programFiles = [Environment]::GetEnvironmentVariable('ProgramFiles')
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $localAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
    if (-not [string]::IsNullOrWhiteSpace($programFiles)) { $candidateRoots += $programFiles }
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) { $candidateRoots += $programFilesX86 }
    if (-not [string]::IsNullOrWhiteSpace($localAppData)) { $candidateRoots += (Join-Path $localAppData 'Programs') }
    foreach ($root in ($candidateRoots | Sort-Object -Unique)) {
        $candidate = Get-ChildItem -LiteralPath $root -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'ISCC.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($null -ne $candidate) { return $candidate }
    }
    return $null
}

function Assert-CleanPath([string]$Path) {
    $forbidden = '(?i)(^|[\\/])(BrowserHost|browser-extension|Setup-BrowserHost\.ps1|BrowserTab|favicon)([\\/]|$)|(^|[\\/])(\.git|obj|bin|TestResults)([\\/]|$)|(^|[\\/])\.env([.\\/]|$)|\.(db|log|pdb)$'
    foreach ($entry in (Get-ChildItem -LiteralPath $Path -Recurse -Force)) {
        if ($entry.FullName -match $forbidden) { throw "Forbidden release content found: $($entry.FullName)" }
    }
}

function Assert-CleanArchive([string]$Path) {
    Assert-File $Path
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($archive.Entries.Count -eq 0) { throw "Archive is empty: $Path" }
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -match '(?i)(BrowserHost|browser-extension|Setup-BrowserHost\.ps1|BrowserTab|favicon)|(^|[\\/])(\.git|obj|bin|TestResults)([\\/]|$)|(^|[\\/])\.env([.\\/]|$)|\.(db|log|pdb)$') {
                throw "Forbidden release entry '$($entry.FullName)' found in $Path"
            }
        }
    } finally { $archive.Dispose() }
}

Assert-File $propsPath
[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$version = [string]$props.Project.PropertyGroup.VersionPrefix
$propsReleaseVersion = [string]$props.Project.PropertyGroup.ReleaseVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props must contain a three-part VersionPrefix; found '$version'." }
if ($propsReleaseVersion -ne $releaseVersion) { throw "Directory.Build.props must target $releaseVersion; found '$propsReleaseVersion'." }

foreach ($requiredPath in @($solutionPath, $appProjectPath, $releaseNotesSource, $troubleshootingSource, $portableReadmeSource)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Required release input is missing: $requiredPath" }
}

$releaseRoot = Join-Path $repoRoot ("artifacts\release\{0}" -f $releaseVersion)
$stagingRoot = Join-Path $releaseRoot 'staging'
$installerStage = Join-Path $stagingRoot 'installer'
$portableStage = Join-Path $stagingRoot ("WorkParcel-Portable-{0}-win-x64" -f $releaseVersion)
$appPublish = Join-Path $stagingRoot 'app-publish'
$portableZip = Join-Path $releaseRoot ("WorkParcel-Portable-{0}-win-x64.zip" -f $releaseVersion)
$installerArtifact = Join-Path $releaseRoot ("WorkParcel-Setup-{0}.exe" -f $releaseVersion)
$checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$releaseNotesArtifact = Join-Path $releaseRoot ("RELEASE_NOTES-{0}.md" -f $releaseVersion)
$troubleshootingArtifact = Join-Path $releaseRoot 'INSTALLATION-TROUBLESHOOTING.md'

# Beta 3 is intentionally built into a fresh, exact output directory. This
# prevents stale earlier staging files from entering the release package.
if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
Invoke-RequiredCommand $dotnet @('restore', $solutionPath, '-r', 'win-x64')
Invoke-RequiredCommand $dotnet @('build', $solutionPath, '-c', 'Release', '--no-restore')
Invoke-RequiredCommand $dotnet @('test', $solutionPath, '-c', 'Release', '--no-restore')

New-Item -ItemType Directory -Path $appPublish | Out-Null
$publishCommon = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore', '-p:Platform=x64', '-p:WindowsPackageType=None', '-p:EnableMsixTooling=false', '-p:EnableWinAppRunSupport=false', '-p:WindowsAppSDKSelfContained=true', '-p:PublishSingleFile=false', '-p:PublishReadyToRun=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
Invoke-RequiredCommand $dotnet (@('publish', $appProjectPath) + $publishCommon + @('-p:PublishDir=' + $appPublish + '\'))
Assert-File (Join-Path $appPublish 'WorkParcel.exe')
Assert-File (Join-Path $appPublish 'Assets\AppIcon.ico')

# The installer stage contains the published app and user-facing support docs.
New-Item -ItemType Directory -Path $installerStage, (Join-Path $installerStage 'App'), (Join-Path $installerStage 'Documentation') | Out-Null
Copy-DirectoryContents $appPublish (Join-Path $installerStage 'App')
Copy-Item -LiteralPath $troubleshootingSource -Destination (Join-Path $installerStage 'Documentation\INSTALLATION-TROUBLESHOOTING.md') -Force

# Portable package: WorkParcel.exe is at the extraction root and supporting
# files remain beside it. User data still goes to Local AppData.
New-Item -ItemType Directory -Path $portableStage, (Join-Path $portableStage 'Documentation') | Out-Null
Copy-DirectoryContents $appPublish $portableStage
Copy-Item -LiteralPath $portableReadmeSource -Destination (Join-Path $portableStage 'README.md') -Force
Copy-Item -LiteralPath $troubleshootingSource -Destination (Join-Path $portableStage 'Documentation\INSTALLATION-TROUBLESHOOTING.md') -Force

foreach ($pdb in (Get-ChildItem -LiteralPath $stagingRoot -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $pdb.FullName -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
Compress-Archive -LiteralPath $portableStage -DestinationPath $portableZip -CompressionLevel Optimal
Copy-Item -LiteralPath $releaseNotesSource -Destination $releaseNotesArtifact -Force
Copy-Item -LiteralPath $troubleshootingSource -Destination $troubleshootingArtifact -Force

$inno = if ($SkipInstaller) { $null } else { Get-InnoSetupCompiler }
if ($null -eq $inno -and -not $SkipInstaller) { throw 'Inno Setup compiler was not found. Install Inno Setup or rerun with -SkipInstaller for a portable-only development check.' }
if ($null -ne $inno) {
    $issPath = Join-Path $repoRoot 'installer\WorkParcel.iss'
    Invoke-RequiredCommand $inno @($issPath, "/DAppVersion=$version", "/DReleaseLabel=$releaseVersion", "/DStageDir=$installerStage", "/DReleaseDir=$releaseRoot")
    Assert-File $installerArtifact
}

Assert-CleanPath $stagingRoot
Assert-CleanArchive $portableZip

$requiredArtifacts = @($portableZip, $releaseNotesArtifact, $troubleshootingArtifact)
if ($null -ne $inno) { $requiredArtifacts += $installerArtifact }
foreach ($artifact in $requiredArtifacts) { Assert-File $artifact }

$allowedTopLevel = @((Split-Path -Leaf $portableZip), (Split-Path -Leaf $releaseNotesArtifact), (Split-Path -Leaf $troubleshootingArtifact), 'SHA256SUMS.txt', 'staging')
if ($null -ne $inno) { $allowedTopLevel += Split-Path -Leaf $installerArtifact }
foreach ($entry in (Get-ChildItem -LiteralPath $releaseRoot -Force)) {
    if ($allowedTopLevel -notcontains $entry.Name) { throw "Unexpected beta 3 release output: $($entry.Name)" }
}

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

Write-Host ''
Write-Host 'WorkParcel 0.2.0 Beta 3 release artifacts:'
foreach ($artifact in ($requiredArtifacts | Sort-Object)) { Write-Host (" - {0}" -f $artifact) }
Write-Host (" - {0}" -f $checksumPath)
