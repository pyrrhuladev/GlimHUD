[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$NoRestore,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$requiredNotices = @(
    'LICENSE', 'THIRD-PARTY-NOTICES.md', 'Notices\LibreHardwareMonitor.txt', 'Notices\NAudio.txt',
    'Notices\HidSharp.txt', 'Notices\BlackSharp.Core.txt', 'Notices\DiskInfoToolkit.txt',
    'Notices\RAMSPDToolkit-NDD.txt', 'Notices\Mono.Posix.NETStandard.txt',
    'Notices\Microsoft.NET.txt', 'Notices\Microsoft.NET-ThirdParty.txt',
    'Notices\NetSparkle.txt', 'Notices\NetSparkle.Chaos.NaCl.txt',
    'Notices\PawnIO.Modules.txt', 'Notices\PawnIO.Modules.LGPL-2.1.txt', 'Notices\PawnIO.Modules.Upstream-README.txt',
    'Notices\PawnIO.Setup.txt'
)
foreach ($relativePath in $requiredNotices) {
    $requiredPath = Join-Path $projectRoot $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or (Get-Item -LiteralPath $requiredPath).Length -eq 0) {
        throw "Required release notice is missing or empty: $relativePath"
    }
}
$pawnIoSource = Join-Path $projectRoot 'tools\PawnIO'
$pawnIoVerifier = Join-Path $PSScriptRoot 'Get-PawnIO.ps1'
if (-not (Test-Path -LiteralPath $pawnIoVerifier -PathType Leaf)) {
    throw 'PawnIO bundle verification script is missing: scripts/Get-PawnIO.ps1.'
}
& $pawnIoVerifier -DestinationDirectory $pawnIoSource -VerifyOnly
$presentMonSource = Join-Path $projectRoot 'tools\PresentMon'
$presentMonExecutables = if (Test-Path -LiteralPath $presentMonSource -PathType Container) {
    @(Get-ChildItem -LiteralPath $presentMonSource -Filter 'PresentMon*.exe' -File | Where-Object { $_.Name -notmatch 'service' })
} else { @() }
if ($presentMonExecutables.Count -gt 0) {
    foreach ($name in @('LICENSE.txt', 'provenance.json')) {
        $requiredPath = Join-Path $presentMonSource $name
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or (Get-Item -LiteralPath $requiredPath).Length -eq 0) {
            throw "PresentMon cannot be redistributed without $name. Run scripts\Get-PresentMon.ps1."
        }
    }
}

$portableDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $portableDotnet) { $portableDotnet } else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $command.Source } else { $null }
}
if (-not $dotnet) { throw 'The .NET 10 SDK is required. https://dotnet.microsoft.com/download/dotnet/10.0' }
$availableSdks = & $dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($availableSdks -match '^10\.')) {
    throw 'Publishing requires the .NET 10 SDK; the Desktop Runtime does not contain build tools.'
}
$outputPath = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'artifacts\GlimHUD-win-x64' }
$arguments = @('publish', (Join-Path $projectRoot 'src\GlimHUD\GlimHUD.csproj'), '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(), '--output', $outputPath, '-p:PublishSingleFile=false')
if ($NoRestore) { $arguments += '--no-restore' }
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Publishing failed with exit code $LASTEXITCODE." }
foreach ($name in @('LICENSE', 'README.md', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', 'CONTRIBUTING.md', 'SECURITY.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $outputPath
}
$docsSource = Join-Path $projectRoot 'docs'
if (Test-Path -LiteralPath $docsSource -PathType Container) {
    Get-ChildItem -LiteralPath $docsSource -File -Recurse | Where-Object { $_.Extension -in '.md', '.txt', '.png', '.svg' } | ForEach-Object {
        $relativePath = $_.FullName.Substring($docsSource.Length).TrimStart('\', '/')
        $destinationPath = Join-Path (Join-Path $outputPath 'docs') $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destinationPath
    }
}
$noticesOutput = Join-Path $outputPath 'Notices'
New-Item -ItemType Directory -Path $noticesOutput -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Notices') -File |
    Where-Object { $_.Extension -in '.txt', '.md' } | Copy-Item -Destination $noticesOutput
$pawnIoOutput = Join-Path $outputPath 'tools\PawnIO'
New-Item -ItemType Directory -Path $pawnIoOutput -Force | Out-Null
foreach ($name in @('PawnIO_setup.exe', 'provenance.json')) {
    Copy-Item -LiteralPath (Join-Path $pawnIoSource $name) -Destination $pawnIoOutput
}
& $pawnIoVerifier -DestinationDirectory $pawnIoOutput -VerifyOnly
if ($presentMonExecutables.Count -gt 0) {
    $presentMonOutput = Join-Path $outputPath 'tools\PresentMon'
    New-Item -ItemType Directory -Path $presentMonOutput -Force | Out-Null
    $presentMonExecutables | Copy-Item -Destination $presentMonOutput
    foreach ($name in @('LICENSE.txt', 'provenance.json', 'README.md')) {
        $sourcePath = Join-Path $presentMonSource $name
        if (Test-Path -LiteralPath $sourcePath -PathType Leaf) { Copy-Item -LiteralPath $sourcePath -Destination $presentMonOutput }
    }
} else {
    Write-Warning 'PresentMon is absent. Music and sensors work, but FPS is unavailable. Run scripts\Get-PresentMon.ps1 and publish again to include it.'
}
Write-Output "Ready: $outputPath\GlimHUD.exe"
