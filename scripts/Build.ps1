[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$portableDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $portableDotnet) { $portableDotnet } else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $command.Source } else { $null }
}
if (-not $dotnet) {
    throw 'The .NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 or extract it into .tools\dotnet.'
}
$availableSdks = & $dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($availableSdks -match '^10\.')) {
    throw 'The .NET 10 SDK is required; a Desktop Runtime alone is insufficient. https://dotnet.microsoft.com/download/dotnet/10.0'
}
$arguments = @('build', (Join-Path $projectRoot 'src\GlimHUD\GlimHUD.csproj'), '--configuration', $Configuration)
if ($NoRestore) { $arguments += '--no-restore' }
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
