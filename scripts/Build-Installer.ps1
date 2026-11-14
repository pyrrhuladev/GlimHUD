[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InputDirectory,
    [string]$OutputDirectory,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$inputPath = [IO.Path]::GetFullPath($InputDirectory)
$outputPath = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'artifacts\installer' }
if ($outputPath.TrimEnd('\') -eq $inputPath.TrimEnd('\') -or $outputPath.StartsWith($inputPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside InputDirectory.'
}

[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\GlimHUD\GlimHUD.csproj') -Raw
$versionText = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
if ($versionText -notmatch '^\d+\.\d+\.\d+$') { throw 'The installer requires a stable three-component project version.' }
$version = [version]$versionText
if (@($version.Major,$version.Minor,$version.Build | Where-Object { $_ -gt 65535 }).Count -gt 0) { throw 'Installer version components must not exceed 65535.' }

if (Test-Path -LiteralPath $inputPath -PathType Container) {
    $privateFiles = @(Get-ChildItem -LiteralPath $inputPath -File -Recurse | Where-Object {
        $_.Extension.ToLowerInvariant() -in '.priv','.key','.pfx','.p12','.pem' -or
        $_.Name.ToLowerInvariant() -in '.env','id_rsa','id_dsa','id_ecdsa','id_ed25519' -or
        ($_.Name.StartsWith('.env.', [StringComparison]::OrdinalIgnoreCase) -and $_.Name -ne '.env.example')
    })
    if ($privateFiles.Count -gt 0) { throw 'Publish input contains private key or environment material. Remove it before building an installer.' }
}
foreach ($requiredFile in @('GlimHUD.exe','GlimHUD.dll','GlimHUD.runtimeconfig.json','coreclr.dll','hostfxr.dll','PresentationFramework.dll','LICENSE','THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $inputPath $requiredFile) -PathType Leaf)) {
        throw "A complete self-contained win-x64 publish is required; missing $requiredFile in $inputPath. Run scripts/Publish.ps1 -SelfContained first."
    }
}
$pawnIoVerifier = Join-Path $PSScriptRoot 'Get-PawnIO.ps1'
if (-not (Test-Path -LiteralPath $pawnIoVerifier -PathType Leaf)) { throw 'PawnIO bundle verification script is missing.' }
& $pawnIoVerifier -DestinationDirectory (Join-Path $inputPath 'tools\PawnIO') -VerifyOnly
foreach ($forbiddenDirectory in @('src','.git','.tools','.packages','obj')) {
    if (Test-Path -LiteralPath (Join-Path $inputPath $forbiddenDirectory)) { throw "Publish input contains source/build directory: $forbiddenDirectory" }
}
$executable = Join-Path $inputPath 'GlimHUD.exe'
$assemblyVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
if ($assemblyVersion.ProductVersion -notmatch ('^' + [regex]::Escape($versionText) + '(?:$|[.+-])')) {
    throw "Publish version $($assemblyVersion.ProductVersion) does not match project version $versionText. Publish the current project first."
}
$stream = [IO.File]::OpenRead($executable)
$reader = [IO.BinaryReader]::new($stream)
try {
    if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'GlimHUD.exe is not a PE executable.' }
    $stream.Position = 0x3C
    $offset = $reader.ReadUInt32()
    $stream.Position = $offset
    if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'GlimHUD.exe must be a valid native x64 PE executable.' }
}
finally { $reader.Dispose(); $stream.Dispose() }
$notices = Get-Content -LiteralPath (Join-Path $inputPath 'THIRD-PARTY-NOTICES.md') -Raw
foreach ($noticeLink in [regex]::Matches($notices, '\]\((Notices/[^)]+)\)')) {
    $noticePath = [IO.Path]::GetFullPath((Join-Path $inputPath $noticeLink.Groups[1].Value))
    if (-not $noticePath.StartsWith($inputPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
        throw "Missing bundled third-party notice: $($noticeLink.Groups[1].Value)"
    }
}
$markerPath = Join-Path $projectRoot 'packaging\installer\installed.marker'
if ((Get-Content -LiteralPath $markerPath -Raw).Trim() -ne 'GlimHUD installed v1') { throw 'Installer marker is missing or has an unexpected format.' }

$compilerCandidates = if ($IsccPath) { @([IO.Path]::GetFullPath($IsccPath)) } else {
    @(
        (Join-Path $projectRoot '.tools\inno-setup\ISCC.exe'),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
    )
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $command.Source }
}
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $compiler) { throw 'Inno Setup compiler not found. Run scripts/Get-InnoSetup.ps1 for a workspace-only portable compiler, or supply -IsccPath.' }
$signature = Get-AuthenticodeSignature -LiteralPath $compiler
if ($signature.Status -ne 'Valid') { throw "Inno Setup compiler signature is not valid: $($signature.Status)." }
if ($signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.|Jordan Russell') { throw 'Unexpected Inno Setup compiler publisher.' }

$versionedName = "GlimHUD-$versionText-Setup-x64.exe"
$aliasName = 'GlimHUD-Setup-x64.exe'
foreach ($outputName in @($versionedName,$aliasName)) {
    if (Test-Path -LiteralPath (Join-Path $outputPath $outputName)) { throw "Output already exists: $outputName. Choose a fresh output directory." }
}
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$arguments = @(
    '/Qp',
    "/DAppVersion=$versionText",
    "/DPublishDir=$inputPath",
    "/DInstallerOutputDir=$outputPath",
    "/DProjectDir=$projectRoot",
    (Join-Path $projectRoot 'packaging\installer\GlimHUD.iss')
)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
$installer = Join-Path $outputPath $versionedName
if (-not (Test-Path -LiteralPath $installer -PathType Leaf) -or (Get-Item -LiteralPath $installer).Length -lt 1MB) { throw 'Compiled installer is missing or unexpectedly small.' }
Copy-Item -LiteralPath $installer -Destination (Join-Path $outputPath $aliasName)
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
foreach ($name in @($versionedName,$aliasName)) {
    [IO.File]::WriteAllText((Join-Path $outputPath ($name + '.sha256')), "$hash  $name`n", [Text.UTF8Encoding]::new($false))
}
Write-Output "Versioned installer: $installer"
Write-Output "Update/download alias: $(Join-Path $outputPath $aliasName)"
Write-Output "SHA256: $hash"
Write-Warning 'This installer is unsigned. Compilation does not install, uninstall, launch, or certify GlimHUD.'
