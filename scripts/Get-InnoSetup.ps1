[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsRoot = Join-Path $projectRoot '.tools'
$compilerDirectory = Join-Path $toolsRoot 'inno-setup'
$compilerPath = Join-Path $compilerDirectory 'ISCC.exe'
$version = '6.7.4'
$downloadUri = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe'
$downloadPath = Join-Path $toolsRoot "innosetup-$version.exe"
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
if (Test-Path -LiteralPath $compilerPath -PathType Leaf) {
    $existingSignature = Get-AuthenticodeSignature -LiteralPath $compilerPath
    if ($existingSignature.Status -eq 'Valid' -and $existingSignature.SignerCertificate.Subject -match 'Pyrsys B\.V\.|Jordan Russell') {
        Write-Output "Portable Inno Setup compiler already available: $compilerPath"
        return
    }
    throw 'An existing unverified portable compiler was found. Inspect it before preparing another compiler.'
}
if (-not (Test-Path -LiteralPath $downloadPath -PathType Leaf)) {
    Write-Output "Downloading official Inno Setup $version to the workspace."
    Invoke-WebRequest -Uri $downloadUri -OutFile $downloadPath -UseBasicParsing
}
$signature = Get-AuthenticodeSignature -LiteralPath $downloadPath
if ($signature.Status -ne 'Valid') { throw "Downloaded Inno Setup signature is not valid: $($signature.Status). Nothing was executed." }
if ($signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') { throw 'Unexpected Inno Setup download publisher. Nothing was executed.' }
$logPath = Join-Path $toolsRoot "inno-portable-$([guid]::NewGuid().ToString('N').Substring(0,8)).log"
# Official /PORTABLE=1 mode creates no uninstaller or Apps entry. /NOICONS and an
# empty task list keep shortcuts/file associations untouched; all files stay in .tools.
$arguments = @('/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/TASKS=""',"/DIR=`"$compilerDirectory`"","/LOG=`"$logPath`"")
$process = Start-Process -FilePath $downloadPath -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "Portable compiler preparation failed: $($process.ExitCode). See $logPath" }
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) { throw "Portable compiler missing after preparation. See $logPath" }
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $compilerPath
if ($compilerSignature.Status -ne 'Valid' -or $compilerSignature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') { throw 'Prepared compiler signature did not verify.' }
$provenance = [ordered]@{ version=$version; source=$downloadUri; sha256=(Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant(); publisher=$signature.SignerCertificate.Subject; mode='workspace-portable' }
[IO.File]::WriteAllText((Join-Path $compilerDirectory 'provenance.json'), ($provenance | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Output "Portable compiler ready: $compilerPath"
