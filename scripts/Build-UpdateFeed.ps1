[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$PublicKeyPath,
    [string]$KeyDirectory,
    [string]$AppCastToolPath,
    [datetimeoffset]$PublishedAt = [datetimeoffset]::UtcNow
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer.PSIsContainer -or $installer.Length -eq 0 -or $installer.Name -cne "GlimHUD-$Version-Setup-x64.exe") {
    throw 'Supply the nonempty, versioned GlimHUD x64 installer matching Version.'
}
if (-not $PublicKeyPath) { $PublicKeyPath = Join-Path $projectRoot 'src\GlimHUD\Assets\update-public-key.txt' }
$publicKey = (Get-Content -LiteralPath $PublicKeyPath -Raw).Trim()
try { $keyBytes = [Convert]::FromBase64String($publicKey) } catch { throw 'The pinned public key must be base64 Ed25519.' }
if ($keyBytes.Length -ne 32) { throw 'The pinned Ed25519 public key must contain 32 bytes.' }
$privateKey = if ($KeyDirectory) {
    (Get-Content -LiteralPath (Join-Path $KeyDirectory 'NetSparkle_Ed25519.priv') -Raw).Trim()
} else { $env:NETSPARKLE_PRIVATE_KEY }
if ([string]::IsNullOrWhiteSpace($privateKey)) { throw 'NETSPARKLE_PRIVATE_KEY or KeyDirectory is required. Unsigned feeds are not published.' }
try { $privateBytes = [Convert]::FromBase64String($privateKey) } catch { throw 'The update signing key must be a base64 Ed25519 seed.' }
if ($privateBytes.Length -ne 32) { throw 'The update signing key must contain a 32-byte Ed25519 seed.' }
if (-not $AppCastToolPath) {
    $localTool = Join-Path $projectRoot '.tools\netsparkle-appcast\netsparkle-generate-appcast.exe'
    $command = Get-Command netsparkle-generate-appcast -ErrorAction SilentlyContinue
    $AppCastToolPath = if (Test-Path -LiteralPath $localTool) { $localTool } elseif ($command) { $command.Source } else { $null }
}
if (-not $AppCastToolPath -or -not (Test-Path -LiteralPath $AppCastToolPath -PathType Leaf)) {
    throw 'Install NetSparkleUpdater.Tools.AppCastGenerator 2.9.0 and supply AppCastToolPath.'
}
$tool = [IO.Path]::GetFullPath($AppCastToolPath)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$appcastPath = Join-Path $outputPath 'appcast.xml'
$signaturePath = "$appcastPath.signature"
if ((Test-Path -LiteralPath $appcastPath) -or (Test-Path -LiteralPath $signaturePath)) { throw 'Use a fresh output directory; signed metadata is immutable.' }

function Get-VerifiedSignature([string]$Path) {
    # Private material is passed through the child process environment, never arguments or logs.
    $signOutput = @(& $tool --generate-signature $Path 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'NetSparkle signing failed.' }
    $matches = [regex]::Matches(($signOutput -join "`n"), '(?m)^Signature: ([A-Za-z0-9+/]{86}==)\s*$')
    if ($matches.Count -ne 1) { throw 'NetSparkle did not return one valid Ed25519 signature.' }
    $signature = $matches[0].Groups[1].Value
    $verifyOutput = @(& $tool --verify $Path --signature $signature 2>&1)
    # Tool 2.9.0 also exits zero for an invalid signature, so require its affirmative result.
    if ($LASTEXITCODE -ne 0 -or ($verifyOutput -join "`n").Trim() -cne 'Signature valid') {
        throw 'Update signature verification failed against the pinned public key.'
    }
    return $signature
}

$previousPrivate = $env:SPARKLE_PRIVATE_KEY
$previousPublic = $env:SPARKLE_PUBLIC_KEY
try {
    $env:SPARKLE_PRIVATE_KEY = $privateKey
    $env:SPARKLE_PUBLIC_KEY = $publicKey
    $installerSignature = Get-VerifiedSignature $installer.FullName
    $sparkle = 'http://www.andymatuschak.org/xml-namespaces/sparkle'
    $downloadUrl = "https://github.com/pyrrhuladev/GlimHUD/releases/download/v$Version/$($installer.Name)"
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.NewLineChars = "`n"
    $writer = [Xml.XmlWriter]::Create($appcastPath, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('rss')
        $writer.WriteAttributeString('version', '2.0')
        $writer.WriteAttributeString('xmlns', 'sparkle', $null, $sparkle)
        $writer.WriteStartElement('channel')
        $writer.WriteElementString('title', 'GlimHUD updates')
        $writer.WriteElementString('link', 'https://pyrrhuladev.github.io/GlimHUD/')
        $writer.WriteElementString('description', 'Stable GlimHUD releases for Windows x64.')
        $writer.WriteElementString('language', 'en')
        $writer.WriteStartElement('item')
        $writer.WriteElementString('title', "GlimHUD $Version")
        $writer.WriteElementString('pubDate', $PublishedAt.UtcDateTime.ToString('r', [Globalization.CultureInfo]::InvariantCulture))
        $writer.WriteElementString('description', "GlimHUD $Version. Release notes: https://github.com/pyrrhuladev/GlimHUD/releases/tag/v$Version")
        $writer.WriteStartElement('enclosure')
        $writer.WriteAttributeString('url', $downloadUrl)
        $writer.WriteAttributeString('version', $sparkle, $Version)
        $writer.WriteAttributeString('shortVersionString', $sparkle, $Version)
        $writer.WriteAttributeString('signature', $sparkle, $installerSignature)
        $writer.WriteAttributeString('os', $sparkle, 'windows')
        $writer.WriteAttributeString('length', $installer.Length.ToString([Globalization.CultureInfo]::InvariantCulture))
        $writer.WriteAttributeString('type', 'application/octet-stream')
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    } finally { $writer.Dispose() }
    $feedSignature = Get-VerifiedSignature $appcastPath
    [IO.File]::WriteAllText($signaturePath, $feedSignature, [Text.UTF8Encoding]::new($false))
} catch {
    foreach ($path in @($appcastPath, $signaturePath)) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
    throw
} finally {
    $env:SPARKLE_PRIVATE_KEY = $previousPrivate
    $env:SPARKLE_PUBLIC_KEY = $previousPublic
    $privateKey = $null
    [Array]::Clear($privateBytes, 0, $privateBytes.Length)
}
Write-Output "Verified update metadata: $appcastPath"
Write-Output "Detached signature: $signaturePath"
