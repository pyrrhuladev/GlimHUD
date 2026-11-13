[CmdletBinding()]
param(
    [string] $ExpectedSha256
)

$ErrorActionPreference = 'Stop'
$presentMonVersion = '2.6.0'
$assetName = "PresentMon-$presentMonVersion-x64.exe"
$releaseApi = "https://api.github.com/repos/GameTechDev/PresentMon/releases/tags/v$presentMonVersion"
$releasePage = "https://github.com/GameTechDev/PresentMon/releases/tag/v$presentMonVersion"
$projectRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $projectRoot 'tools\PresentMon'
New-Item -ItemType Directory -Force -Path $destination | Out-Null

Write-Host "Fetching official PresentMon $presentMonVersion release metadata..."
$release = Invoke-RestMethod -Uri $releaseApi -Headers @{ 'User-Agent' = 'GlimHUD-dependency-setup' }
$asset = @($release.assets | Where-Object { $_.name -ceq $assetName })
if ($asset.Count -ne 1) {
    throw "The official release does not contain exactly one $assetName asset. Inspect $releasePage."
}
$downloadUri = [Uri] $asset[0].browser_download_url
if ($downloadUri.Scheme -ne 'https' -or $downloadUri.Host -ne 'github.com' -or
    -not $downloadUri.AbsolutePath.StartsWith('/GameTechDev/PresentMon/releases/download/')) {
    throw 'Unexpected PresentMon asset URL in release metadata.'
}

$publishedDigest = [string] $asset[0].digest
if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
    if ($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'ExpectedSha256 must be 64 hexadecimal characters.' }
    $expectedHash = $ExpectedSha256.ToLowerInvariant()
    if ($publishedDigest -match '^sha256:([0-9a-fA-F]{64})$' -and $Matches[1] -ine $expectedHash) {
        throw 'The supplied checksum does not match the checksum published by GitHub.'
    }
}
elseif ($publishedDigest -match '^sha256:([0-9a-fA-F]{64})$') {
    $expectedHash = $Matches[1].ToLowerInvariant()
}
else {
    throw 'This release has no SHA256 asset digest. Re-run with -ExpectedSha256 after independently verifying the official asset.'
}

$finalPath = Join-Path $destination 'PresentMon.exe'
$temporaryPath = Join-Path $destination "$assetName.download"
$licenseUri = "https://raw.githubusercontent.com/GameTechDev/PresentMon/v$presentMonVersion/LICENSE.txt"
$licensePath = Join-Path $destination 'LICENSE.txt'
$temporaryLicensePath = Join-Path $destination 'LICENSE.txt.download'
try {
    Invoke-WebRequest -Uri $downloadUri.AbsoluteUri -OutFile $temporaryPath
    $actualHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $expectedHash) { throw 'PresentMon SHA256 verification failed. The executable was not installed.' }
    # Fetch the mandatory redistribution notice before making the binary usable.
    Invoke-WebRequest -Uri $licenseUri -OutFile $temporaryLicensePath
    if ((Get-Item -LiteralPath $temporaryLicensePath).Length -eq 0) {
        throw 'The upstream PresentMon license is empty. The executable was not installed.'
    }
    Move-Item -LiteralPath $temporaryLicensePath -Destination $licensePath -Force
    Move-Item -LiteralPath $temporaryPath -Destination $finalPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    if (Test-Path -LiteralPath $temporaryLicensePath) { Remove-Item -LiteralPath $temporaryLicensePath -Force }
}

# Keep the exact release, asset URL and checksum next to the downloaded executable.
[ordered]@{
    project = 'GameTechDev/PresentMon'
    version = $presentMonVersion
    asset = $assetName
    source = $downloadUri.AbsoluteUri
    release = $releasePage
    sha256 = $actualHash
    verifiedUtc = [DateTime]::UtcNow.ToString('O')
    license = 'MIT'
    licenseSource = "https://github.com/GameTechDev/PresentMon/blob/v$presentMonVersion/LICENSE.txt"
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'provenance.json') -Encoding UTF8

Write-Host "Installed $finalPath"
Write-Host "Verified SHA256: $actualHash"
Write-Host 'Rebuild GlimHUD to copy this dependency next to the application.'
