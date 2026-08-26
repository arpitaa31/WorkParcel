param(
    [Parameter(Mandatory=$true)][string]$HostExecutablePath,
    [ValidatePattern('^[a-p]{32}$')][string]$ChromeExtensionId,
    [ValidatePattern('^[a-p]{32}$')][string]$EdgeExtensionId,
    [ValidateSet('Chrome','Edge','Both')][string]$Browser = 'Both',
    [switch]$Remove
)

$hostName = 'com.workparcel.browser'
$hostPath = [System.IO.Path]::GetFullPath($HostExecutablePath)
$hostDirectory = [System.IO.Path]::GetDirectoryName($hostPath)
$manifestPath = Join-Path $hostDirectory "$hostName.json"
$originsPath = Join-Path $hostDirectory 'allowed-origins.json'
$targets = switch ($Browser) {
    'Chrome' { @(@{ Name='Chrome'; Key="HKCU\Software\Google\Chrome\NativeMessagingHosts\$hostName"; Id=$ChromeExtensionId }) }
    'Edge'   { @(@{ Name='Edge'; Key="HKCU\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"; Id=$EdgeExtensionId }) }
    default  { @(@{ Name='Chrome'; Key="HKCU\Software\Google\Chrome\NativeMessagingHosts\$hostName"; Id=$ChromeExtensionId }, @{ Name='Edge'; Key="HKCU\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"; Id=$EdgeExtensionId }) }
}

function Read-ExistingOrigins {
    $values = @()
    if (Test-Path -LiteralPath $originsPath -PathType Leaf) {
        try {
            $parsed = Get-Content -LiteralPath $originsPath -Raw | ConvertFrom-Json
            $values = @($parsed | ForEach-Object { $_ })
        }
        catch { throw "The existing allowed-origins.json is invalid: $originsPath" }
    } elseif (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $existingManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $values = @($existingManifest.allowed_origins | ForEach-Object { $_ })
        } catch { throw "The existing native-host manifest is invalid: $manifestPath" }
    }

    foreach ($value in $values) {
        if (-not [string]::IsNullOrWhiteSpace([string]$value) -and [string]$value -match '^chrome-extension://[a-p]{32}/$') { [string]$value }
    }
}

function Write-JsonFile([string]$Path, [object]$Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 4
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

if (-not $Remove) {
    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw "Host executable was not found: $hostPath" }
    foreach ($target in $targets) { if ([string]::IsNullOrWhiteSpace($target.Id)) { throw "Provide the exact $($target.Name) extension ID for -Browser $Browser." } }
    $newOrigins = @($targets | ForEach-Object { "chrome-extension://$($_.Id)/" })
    $origins = @(@(Read-ExistingOrigins) + $newOrigins | Sort-Object -Unique)
    $manifest = [ordered]@{ name=$hostName; description='WorkParcel browser bridge'; path=$hostPath; type='stdio'; allowed_origins=$origins }
    Write-JsonFile $manifestPath $manifest
    Write-JsonFile $originsPath $origins
    foreach ($target in $targets) { New-Item -Path "Registry::$($target.Key)" -Force | Out-Null; Set-ItemProperty -LiteralPath "Registry::$($target.Key)" -Name '(default)' -Value $manifestPath }
    Write-Host "Registered $hostName for $($targets.Name -join ' and ') extension ID(s)."
    exit 0
}

if ($Browser -ne 'Both') {
    foreach ($target in $targets) { if ([string]::IsNullOrWhiteSpace($target.Id)) { throw "Provide the exact $($target.Name) extension ID when removing a single-browser registration." } }
}

foreach ($target in $targets) { Remove-Item -LiteralPath "Registry::$($target.Key)" -Force -ErrorAction SilentlyContinue }
if ($Browser -eq 'Both') {
    Remove-Item -LiteralPath $manifestPath,$originsPath -Force -ErrorAction SilentlyContinue
} elseif (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $removeOrigins = @($targets | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Id) } | ForEach-Object { "chrome-extension://$($_.Id)/" })
    $remaining = @(Read-ExistingOrigins | Where-Object { $removeOrigins -notcontains $_ })
    if ($remaining.Count -eq 0) {
        Remove-Item -LiteralPath $manifestPath,$originsPath -Force -ErrorAction SilentlyContinue
    } else {
        try { $existingManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
        catch { throw "The existing native-host manifest is invalid: $manifestPath" }
        $updatedManifest = [ordered]@{ name=$hostName; description=([string]$existingManifest.description); path=([string]$existingManifest.path); type='stdio'; allowed_origins=$remaining }
        Write-JsonFile $manifestPath $updatedManifest
        Write-JsonFile $originsPath $remaining
    }
}
Write-Host "Removed $hostName registration for $($targets.Name -join ' and ')."
