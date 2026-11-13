[CmdletBinding(DefaultParameterSetName = 'Staging')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Staging')][string]$StagingDirectory,
    [Parameter(Mandatory = $true, ParameterSetName = 'Package')][string]$PackagePath,
    [Parameter(ParameterSetName = 'Package')][string]$MakeAppxPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($PSCmdlet.ParameterSetName -eq 'Package') {
    . (Join-Path $projectRoot 'packaging\WindowsSdkTools.ps1')
    $makeAppx = Find-GlimHudMakeAppx -ExplicitPath $MakeAppxPath -ProjectRoot $projectRoot
    $package = [IO.Path]::GetFullPath($PackagePath)
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Package does not exist: $package" }
    $StagingDirectory = Join-Path ([IO.Path]::GetDirectoryName($package)) ('verification-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $unpackLog = $StagingDirectory + '.makeappx.log'
    & $makeAppx unpack /p $package /d $StagingDirectory *> $unpackLog
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $unpackLog -Tail 40 | Write-Output
        throw "MakeAppx unpack failed with exit code $LASTEXITCODE. Log: $unpackLog"
    }
}
$root = [IO.Path]::GetFullPath($StagingDirectory)
[xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'AppxManifest.xml') -Raw
$ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$ns.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$ns.AddNamespace('uap7', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/7')
$ns.AddNamespace('uap10', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
$ns.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
$application = $manifest.SelectSingleNode('/p:Package/p:Applications/p:Application', $ns)
if ($manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'This packaging pipeline supports x64 only.' }
if (-not $application -or $application.Executable -ne 'GlimHUD.exe') { throw 'Expected a GlimHUD.exe application entry.' }
if ($application.GetAttribute('RuntimeBehavior', $ns.LookupNamespace('uap10')) -ne 'packagedClassicApp' -or $application.GetAttribute('TrustLevel', $ns.LookupNamespace('uap10')) -ne 'mediumIL') { throw 'Expected a packagedClassicApp at mediumIL.' }
if (-not $manifest.SelectSingleNode('/p:Package/p:Capabilities/rescap:Capability[@Name="runFullTrust"]', $ns)) { throw 'Missing rescap:runFullTrust.' }
if (-not $manifest.SelectSingleNode('/p:Package/p:Capabilities/uap7:Capability[@Name="globalMediaControl"]', $ns)) { throw 'Missing uap7:globalMediaControl.' }
if ($manifest.SelectSingleNode('/p:Package/p:Capabilities/*[@Name="allowElevation"]', $ns)) { throw 'Store manifest must not request elevation.' }
$device = $manifest.SelectSingleNode('/p:Package/p:Dependencies/p:TargetDeviceFamily', $ns)
if ($device.Name -ne 'Windows.Desktop' -or [version]$device.MinVersion -lt [version]'10.0.19041.0') { throw 'Expected Windows.Desktop 10.0.19041.0 or later.' }
foreach ($name in @('GlimHUD.exe', 'GlimHUD.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf)) { throw "Missing payload file: $name" }
}
$executableStream = [IO.File]::OpenRead((Join-Path $root 'GlimHUD.exe'))
$executableReader = [IO.BinaryReader]::new($executableStream)
try {
    if ($executableReader.ReadUInt16() -ne 0x5A4D) { throw 'GlimHUD.exe is not a PE executable.' }
    $executableStream.Position = 0x3C
    $peOffset = $executableReader.ReadUInt32()
    $executableStream.Position = $peOffset
    if ($executableReader.ReadUInt32() -ne 0x00004550 -or $executableReader.ReadUInt16() -ne 0x8664) { throw 'GlimHUD.exe must be a valid x64 PE executable.' }
}
finally { $executableReader.Dispose(); $executableStream.Dispose() }
Add-Type -AssemblyName System.Drawing
$visual = $manifest.SelectSingleNode('/p:Package/p:Applications/p:Application/uap:VisualElements', $ns)
foreach ($asset in @(
    @{ Path = [string]$manifest.Package.Properties.Logo; Width = 50; Height = 50 },
    @{ Path = [string]$visual.Square44x44Logo; Width = 44; Height = 44 },
    @{ Path = [string]$visual.Square150x150Logo; Width = 150; Height = 150 }
)) {
    $assetPath = [IO.Path]::GetFullPath((Join-Path $root $asset.Path))
    if (-not $assetPath.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Asset path escapes the package.' }
    $bitmap = [Drawing.Image]::FromFile($assetPath)
    try { if ($bitmap.Width -ne $asset.Width -or $bitmap.Height -ne $asset.Height) { throw "Invalid image dimensions: $($asset.Path)" } }
    finally { $bitmap.Dispose() }
}
$drivers = @(Get-ChildItem -LiteralPath $root -Filter '*.sys' -File -Recurse)
if ($drivers.Count -gt 0) { throw "Kernel driver payload is not supported by this package: $($drivers.Name -join ', ')" }
if (Test-Path -LiteralPath (Join-Path $root 'LibreHardwareMonitorLib.dll')) { Write-Warning 'LibreHardwareMonitor can contain compressed driver resources inside its DLL. Absence of .sys files is not a driver-free audit; Store compatibility remains unresolved.' }
if (Test-Path -LiteralPath (Join-Path $root 'tools\PresentMon\PresentMon.exe')) { Write-Warning 'PresentMon ETW capture must be tested as a standard user from the installed package. runFullTrust does not grant administrator rights.' }
Write-Output "Package structure checks passed: $root"
Write-Output 'These checks do not install, sign, launch, run WACK, or certify the application.'
