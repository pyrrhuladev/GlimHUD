[CmdletBinding()]
param(
    [switch] $VerifyOnly,
    [string] $DestinationDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = if ($DestinationDirectory) { [IO.Path]::GetFullPath($DestinationDirectory) } else { Join-Path $projectRoot 'tools\PawnIO' }
$version = '2.2.0'
$expectedSize = 3410960
$expectedHash = '1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032'
$expectedSigner = 'F380DCC9F706E2756A5047B832FFE719E1BC35F5'
$source = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
$finalPath = Join-Path $destination 'PawnIO_setup.exe'
$provenancePath = Join-Path $destination 'provenance.json'

function Assert-OfficialInstaller([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -ne $expectedSize) {
        throw 'The complete official PawnIO 2.2.0 installer is required. Run scripts/Get-PawnIO.ps1.'
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $expectedHash) {
        throw 'PawnIO SHA256 verification failed. The installer was not executed.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ine $expectedSigner) {
        throw 'PawnIO Authenticode signature or pinned publisher verification failed.'
    }
}

if ($VerifyOnly) {
    Assert-OfficialInstaller $finalPath
    if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) { throw 'PawnIO provenance is missing.' }
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    if ($provenance.version -cne $version -or $provenance.sha256 -cne $expectedHash -or [long]$provenance.size -ne $expectedSize -or
        $provenance.source -cne $source -or $provenance.signerThumbprint -cne $expectedSigner) {
        throw 'PawnIO provenance does not match the pinned official release.'
    }
    Write-Host 'Verified bundled official PawnIO 2.2.0 installer; no download or execution.'
    return
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
$temporaryPath = Join-Path $destination 'PawnIO_setup.exe.download'
try {
    if (-not (Test-Path -LiteralPath $finalPath -PathType Leaf)) {
        Invoke-WebRequest -Uri $source -OutFile $temporaryPath -TimeoutSec 120
        Assert-OfficialInstaller $temporaryPath
        Move-Item -LiteralPath $temporaryPath -Destination $finalPath -Force
    }
    else { Assert-OfficialInstaller $finalPath }
    [ordered]@{
        project = 'namazso/PawnIO.Setup'
        version = $version
        source = $source
        sha256 = $expectedHash
        size = $expectedSize
        signerThumbprint = $expectedSigner
        signer = 'namazso.eu'
        license = 'Official vendor installer; redistribution unmodified'
        redistributionPermission = 'https://github.com/a1ive/nwinfo/issues/86'
    } | ConvertTo-Json | Set-Content -LiteralPath $provenancePath -Encoding UTF8
}
finally {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) { Remove-Item -LiteralPath $temporaryPath -Force }
}
Write-Host 'Verified official PawnIO 2.2.0 installer and provenance; installer not executed.'
