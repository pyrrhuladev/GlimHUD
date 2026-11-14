[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$StartRelease,
    [string]$KeyDirectory,
    [string]$GitHubCliPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repository = 'pyrrhuladev/GlimHUD'
if (-not $KeyDirectory) { $KeyDirectory = Join-Path $projectRoot '.tools\update-signing' }
if (-not $GitHubCliPath) {
    $command = Get-Command gh -ErrorAction SilentlyContinue
    if ($command) { $GitHubCliPath = $command.Source }
}
if (-not $GitHubCliPath -or -not (Test-Path -LiteralPath $GitHubCliPath -PathType Leaf)) {
    throw 'Install the official GitHub CLI, run gh auth login, then run this script again.'
}
$gh = [IO.Path]::GetFullPath($GitHubCliPath)
$privateKey = (Get-Content -LiteralPath (Join-Path $KeyDirectory 'NetSparkle_Ed25519.priv') -Raw).Trim()
$publicKey = (Get-Content -LiteralPath (Join-Path $KeyDirectory 'NetSparkle_Ed25519.pub') -Raw).Trim()
$pinnedKey = (Get-Content -LiteralPath (Join-Path $projectRoot 'src\GlimHUD\Assets\update-public-key.txt') -Raw).Trim()
try {
    $privateBytes = [Convert]::FromBase64String($privateKey)
    $publicBytes = [Convert]::FromBase64String($publicKey)
} catch { throw 'The existing update key files are not valid base64. No new signing key was generated.' }
if ($privateBytes.Length -ne 32 -or $publicBytes.Length -ne 32 -or $publicKey -cne $pinnedKey) {
    throw 'The existing 32-byte signing keys must match the pinned application public key.'
}
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\GlimHUD\GlimHUD.csproj') -Raw
$version = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The local project must have a stable three-component version.' }

function Invoke-GitHubCommand {
    param([string[]]$Arguments, [AllowNull()][string]$InputText)
    # Windows PowerShell 5.1 turns redirected native stderr into ErrorRecords.
    # Capture it without terminating before the CLI exit code can be inspected.
    $previousErrorAction = $ErrorActionPreference
    $previousOutputEncoding = $OutputEncoding
    try {
        $ErrorActionPreference = 'Continue'
        # Some Windows PowerShell hosts use UTF-8 with a BOM for native stdin.
        # The signing seed is plain base64; do not prefix it with encoding bytes.
        $OutputEncoding = [Text.UTF8Encoding]::new($false)
        if ($PSBoundParameters.ContainsKey('InputText')) {
            $response = @($InputText | & $gh @Arguments 2>&1)
        } else {
            $response = @(& $gh @Arguments 2>&1)
        }
        $exitCode = $LASTEXITCODE
        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = @($response | ForEach-Object { $_.ToString() })
        }
    } finally {
        $OutputEncoding = $previousOutputEncoding
        $ErrorActionPreference = $previousErrorAction
    }
}

function Invoke-GitHubJson([string[]]$Arguments) {
    $response = Invoke-GitHubCommand -Arguments $Arguments
    if ($response.ExitCode -ne 0) { throw "GitHub request failed ($($Arguments -join ' ')). Check authentication and repository permissions." }
    try { return ($response.Output -join "`n") | ConvertFrom-Json } catch { throw 'GitHub returned an unexpected response.' }
}

function Get-MainFile([string]$Path) {
    $file = Invoke-GitHubJson @('api', "repos/$repository/contents/${Path}?ref=main")
    if ($file.type -cne 'file' -or $file.encoding -cne 'base64') { throw "Remote main file is unavailable: $Path" }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($file.content))
}

$previousPrompt = $env:GH_PROMPT_DISABLED
try {
    $env:GH_PROMPT_DISABLED = '1'
    $account = Invoke-GitHubJson @('api', 'user')
    $repo = Invoke-GitHubJson @('api', "repos/$repository")
    if ($repo.full_name -cne $repository -or $repo.permissions.admin -ne $true) {
        throw 'The authenticated GitHub account must administer pyrrhuladev/GlimHUD.'
    }
    if ((Get-MainFile 'src/GlimHUD/Assets/update-public-key.txt').Trim() -cne $pinnedKey) {
        throw 'Remote main has a different public key. Push and review the current source before enabling publication.'
    }
    [xml]$remoteProject = Get-MainFile 'src/GlimHUD/GlimHUD.csproj'
    $remoteVersion = [string]($remoteProject.Project.PropertyGroup.Version | Select-Object -First 1)
    if ($remoteVersion -cne $version) { throw 'Remote main and the local project version differ. Push the current source first.' }
    # A 403/401/transport error must not be mistaken for a disabled Pages site.
    $pagesResponse = Invoke-GitHubCommand -Arguments @('api', "repos/$repository/pages", '--include')
    $pagesExists = $pagesResponse.ExitCode -eq 0
    $pageText = $pagesResponse.Output -join "`n"
    if (-not $pagesExists -and $pageText -notmatch '(?m)^HTTP/\S+\s+404\b') {
        throw 'Could not inspect GitHub Pages. Check repository administration and Pages permissions.'
    }
    Write-Output "Validated $($account.login), repository administration, signing public key, and main version $version."

    if ($PSCmdlet.ShouldProcess($repository, 'Store NETSPARKLE_PRIVATE_KEY as an Actions secret')) {
        # gh reads stdin by default; the seed is never placed in arguments or printed.
        $secretResponse = Invoke-GitHubCommand -Arguments @('secret', 'set', 'NETSPARKLE_PRIVATE_KEY', '--repo', $repository) -InputText $privateKey
        if ($secretResponse.ExitCode -ne 0) { throw 'Could not store the update signing secret. Check Actions secret permissions.' }
        Write-Output 'Actions update-signing secret configured.'
    }
    $method = if ($pagesExists) { 'PUT' } else { 'POST' }
    if ($PSCmdlet.ShouldProcess($repository, 'Configure GitHub Pages with GitHub Actions as its source')) {
        $configureResponse = Invoke-GitHubCommand -Arguments @('api', "repos/$repository/pages", '--method', $method, '-f', 'build_type=workflow')
        if ($configureResponse.ExitCode -ne 0) { throw 'Could not enable GitHub Pages Actions deployment. The signing secret is already configured; rerun after fixing Pages permissions.' }
        Write-Output 'GitHub Pages configured to deploy through Actions.'
    }
    if ($StartRelease -and $PSCmdlet.ShouldProcess($repository, "Start Release for version $version from main")) {
        $dispatchResponse = Invoke-GitHubCommand -Arguments @('workflow', 'run', 'release.yml', '--repo', $repository, '--ref', 'main', '-f', "version=$version")
        if ($dispatchResponse.ExitCode -ne 0) { throw 'Could not start Release. Open repository Actions and run the Release workflow on main.' }
        Write-Output "Release $version requested: https://github.com/$repository/actions/workflows/release.yml"
        Write-Output 'A successful release automatically deploys the product page and signed update feed.'
    } elseif (-not $StartRelease) {
        Write-Output "Ready to publish. Run this script with -StartRelease, or start Release in https://github.com/$repository/actions."
    }
} finally {
    $env:GH_PROMPT_DISABLED = $previousPrompt
    $privateKey = $null
    if ($privateBytes) { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
}
