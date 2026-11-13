# Shared discovery only: no downloads, installation, certificates, or security changes.
function Find-GlimHudMakeAppx {
    param([string]$ExplicitPath, [string]$ProjectRoot)
    if ($ExplicitPath) {
        $resolved = [IO.Path]::GetFullPath($ExplicitPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "MakeAppx not found: $resolved" }
        return $resolved
    }
    $onPath = Get-Command makeappx.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $roots = @(
        (Join-Path $ProjectRoot '.tools\windows-sdk-buildtools'),
        (Join-Path $ProjectRoot '.tools\nuget-cache\microsoft.windows.sdk.buildtools')
    )
    if (${env:ProgramFiles(x86)}) { $roots += Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin' }
    if ($env:NUGET_PACKAGES) { $roots += Join-Path $env:NUGET_PACKAGES 'microsoft.windows.sdk.buildtools' }
    if ($env:USERPROFILE) { $roots += Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools' }
    foreach ($root in $roots | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        $candidate = Get-ChildItem -LiteralPath $root -Filter makeappx.exe -File -Recurse |
            Where-Object { $_.Directory.Name -eq 'x64' } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    throw 'MakeAppx.exe is required. Install the Windows SDK, or extract Microsoft.Windows.SDK.BuildTools 10.0.26100.1742 to .tools/windows-sdk-buildtools/10.0.26100.1742, or pass -MakeAppxPath. See docs/microsoft-store.md.'
}
