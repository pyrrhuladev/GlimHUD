[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InputDirectory,
    [string]$IdentityName = 'GlimHUD.Development',
    [string]$Publisher = 'CN=GlimHUDDevelopment',
    [string]$PublisherDisplayName = 'GlimHUD Development',
    [string]$Version,
    [string]$OutputDirectory,
    [string]$MakeAppxPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $projectRoot 'packaging\WindowsSdkTools.ps1')
$makeAppx = Find-GlimHudMakeAppx -ExplicitPath $MakeAppxPath -ProjectRoot $projectRoot
$inputPath = [IO.Path]::GetFullPath($InputDirectory)
foreach ($requiredFile in @('GlimHUD.exe', 'GlimHUD.dll', 'GlimHUD.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $inputPath $requiredFile) -PathType Leaf)) {
        throw "A complete self-contained win-x64 publish is required; missing $requiredFile in $inputPath. Run scripts/Publish.ps1 -SelfContained first."
    }
}
if ($IdentityName -notmatch '^[A-Za-z0-9][A-Za-z0-9.\-]{2,49}$') { throw 'IdentityName must contain 3-50 ASCII letters, digits, periods, or hyphens.' }
if (-not $Publisher.StartsWith('CN=', [StringComparison]::OrdinalIgnoreCase)) { throw 'Publisher must be the complete Partner Center X.500 publisher value beginning CN=.' }
if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\GlimHUD\GlimHUD.csproj') -Raw
    $Version = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
    if (($Version -split '\.').Count -eq 3) { $Version += '.0' }
}
$packageVersion = $null
if (-not [version]::TryParse($Version, [ref]$packageVersion) -or $packageVersion.Revision -lt 0 -or
    $packageVersion.Major -eq 0 -and $packageVersion.Minor -eq 0 -and $packageVersion.Build -eq 0 -and $packageVersion.Revision -eq 0 -or
    @($packageVersion.Major, $packageVersion.Minor, $packageVersion.Build, $packageVersion.Revision | Where-Object { $_ -gt 65535 }).Count -gt 0) {
    throw 'Version must have four numeric components from 0 to 65535 and cannot be 0.0.0.0.'
}
if ($packageVersion.Revision -ne 0) { throw 'Store package revision (fourth version component) must be 0.' }
$outputPath = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'artifacts\msix' }
if ($outputPath.TrimEnd('\') -eq $inputPath.TrimEnd('\') -or $outputPath.StartsWith($inputPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside InputDirectory.'
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$stagingPath = Join-Path $outputPath ("staging-{0}-{1}" -f $Version, [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stagingPath | Out-Null
Get-ChildItem -LiteralPath $inputPath | Copy-Item -Destination $stagingPath -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\Assets') -Destination $stagingPath -Recurse -Force
foreach ($notice in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $notice)) { Copy-Item -LiteralPath (Join-Path $projectRoot $notice) -Destination $stagingPath -Force }
}
[xml]$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging\AppxManifest.xml') -Raw
$manifest.Package.Identity.SetAttribute('Name', $IdentityName)
$manifest.Package.Identity.SetAttribute('Publisher', $Publisher)
$manifest.Package.Identity.SetAttribute('Version', $Version)
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$manifest.Save((Join-Path $stagingPath 'AppxManifest.xml'))
& (Join-Path $PSScriptRoot 'Test-StorePackage.ps1') -StagingDirectory $stagingPath
$packagePath = Join-Path $outputPath ("GlimHUD-{0}-x64-unsigned.msix" -f $Version)
if (Test-Path -LiteralPath $packagePath) { throw "Output already exists: $packagePath. Choose another output directory or explicitly remove the old package." }
$packLog = $packagePath + '.makeappx.log'
Write-Output "Running MakeAppx semantic validation and packaging; log: $packLog"
& $makeAppx pack /d $stagingPath /p $packagePath *> $packLog
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $packLog -Tail 40 | Write-Output
    throw "MakeAppx pack failed with exit code $LASTEXITCODE. Staging retained: $stagingPath"
}
& (Join-Path $PSScriptRoot 'Test-StorePackage.ps1') -PackagePath $packagePath -MakeAppxPath $makeAppx
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($packagePath + '.sha256', "$hash  $([IO.Path]::GetFileName($packagePath))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Unsigned package: $packagePath"
Write-Output "SHA256: $hash"
if ($IdentityName -eq 'GlimHUD.Development' -or $Publisher -eq 'CN=GlimHUDDevelopment') {
    Write-Warning 'Development identity: this package is a packaging test artifact. Rebuild with the exact Partner Center identity before submission.'
}
Write-Warning 'Packaging validation is not Store certification. Review the hardware-driver and ETW blockers in docs/microsoft-store.md before submission. No certificate was created or installed.'
