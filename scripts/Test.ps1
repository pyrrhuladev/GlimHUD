[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [switch] $NoRestore,
    [switch] $NoBuild,
    [switch] $SkipArchiveChecks
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$portableDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $portableDotnet) { $portableDotnet } else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $command.Source } else { $null }
}
if (-not $dotnet) { throw 'The .NET 10 SDK is required. Install it or extract it into .tools\dotnet.' }
$sdks = & $dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^10\.')) { throw 'The .NET 10 SDK is required; a runtime alone cannot build checks.' }
if ($NoBuild) { $NoRestore = $true }

$projects = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.csproj' -Recurse -File | Sort-Object FullName)
if ($projects.Count -eq 0) { throw 'No console check projects were found.' }
foreach ($project in $projects) {
    Write-Host "Running $($project.BaseName)..."
    $arguments = @('run', '--project', $project.FullName, '--configuration', $Configuration)
    if ($NoRestore) { $arguments += '--no-restore' }
    if ($NoBuild) { $arguments += '--no-build' }
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$($project.BaseName) failed with exit code $LASTEXITCODE." }
}

$archiveChecks = Join-Path $projectRoot 'tests\test_package_release.py'
if (-not $SkipArchiveChecks -and (Test-Path -LiteralPath $archiveChecks)) {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { throw 'Python 3 is required for archive checks. Install it or use -SkipArchiveChecks for .NET checks only.' }
    & $python.Source $archiveChecks
    if ($LASTEXITCODE -ne 0) { throw "Archive checks failed with exit code $LASTEXITCODE." }
}
Write-Host "All $($projects.Count) console check projects passed."
