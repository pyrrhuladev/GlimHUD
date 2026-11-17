[CmdletBinding()]
param(
    [string] $AppPath,
    [string] $OutputDirectory,
    [ValidateRange(5, 300)][int] $TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $AppPath) {
    $AppPath = Join-Path $projectRoot 'src\GlimHUD\bin\Release\net10.0-windows10.0.19041.0\GlimHUD.exe'
}
$app = [System.IO.Path]::GetFullPath($AppPath)
if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
    throw "GlimHUD executable not found: $app. Build the Release configuration first or pass -AppPath."
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts\theme-checks' }
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
# A separate run directory keeps the diagnostic settings and success report isolated.
$runDirectory = Join-Path $output ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$reportPath = Join-Path $runDirectory 'theme-smoke.json'
$process = Start-Process -FilePath $app -ArgumentList @('--theme-smoke', ('"' + $reportPath + '"')) -WindowStyle Hidden -PassThru
try {
    $null = $process.Handle
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "Theme presentation checks exceeded $TimeoutSeconds seconds."
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        $errorPath = $reportPath + '.error.txt'
        $details = if (Test-Path -LiteralPath $errorPath -PathType Leaf) { Get-Content -LiteralPath $errorPath -Raw } else { 'No diagnostic error report was produced.' }
        throw "Theme presentation checks failed (exit $($process.ExitCode)). $details"
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.Success -ne $true -or $report.Assertions -le 0 -or @($report.Cases).Count -lt 20) {
        throw 'Theme presentation report did not contain all expected cases and assertions.'
    }
    Write-Host "Theme presentation checks passed: $($report.Assertions) assertions across $(@($report.Cases).Count) EN/RU cases."
    Write-Host "Report: $reportPath"
    Write-Host "Preview: $(Join-Path $runDirectory 'themes-contact-sheet.png')"
}
finally { $process.Dispose() }
