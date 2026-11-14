# Website, installers, and updates

GlimHUD is distributed from the public [GitHub repository](https://github.com/pyrrhuladev/GlimHUD) under MIT. The [product page](https://pyrrhuladev.github.io/GlimHUD/) is a static GitHub Pages site. Downloads and source remain on GitHub; there is no Microsoft Store or Steam dependency.

## Public endpoints

| Purpose | URL |
| --- | --- |
| Product page | `https://pyrrhuladev.github.io/GlimHUD/` |
| Latest installer | `https://github.com/pyrrhuladev/GlimHUD/releases/latest/download/GlimHUD-Setup-x64.exe` |
| Latest portable copy | `https://github.com/pyrrhuladev/GlimHUD/releases/latest/download/GlimHUD-win-x64.zip` |
| Stable appcast | `https://pyrrhuladev.github.io/GlimHUD/updates/stable/appcast.xml` |
| Appcast signature | `https://pyrrhuladev.github.io/GlimHUD/updates/stable/appcast.xml.signature` |

The updater uses the versioned installer URL in the signed feed, such as `releases/download/v0.7.0/GlimHUD-0.7.0-Setup-x64.exe`. It never installs a mutable latest-download alias. Each release also includes versioned portable files and `SHA256SUMS.txt`.

## What is signed

NetSparkle authenticates the exact appcast bytes and the exact installer bytes using Ed25519. The public key is pinned in `src/GlimHUD/Assets/update-public-key.txt` and shipped with the application. Missing keys, missing signatures, modified metadata, altered installers, unexpected download URLs, and mismatched file lengths fail closed. Only stable Windows x64 installers from this repository can be offered.

This is separate from Authenticode, Windows publisher identity, and SmartScreen reputation. The GlimHUD installer and executable are currently not Authenticode-signed, so Windows can show a warning. The bundled official PawnIO installer retains its own vendor signature; this does not sign GlimHUD. Ed25519 update signing does not remove that warning or imply Microsoft certification. A code-signing certificate can be added later without replacing the update key.

Installed copies offer updates in Settings. Checks never force installation or restart. Portable copies link to GitHub Releases for manual replacement; packaged copies do not use this installer updater. Settings and update downloads stay under `%LOCALAPPDATA%\GlimHUD`.

## Elevation and per-user updates

GlimHUD keeps an `asInvoker` manifest. Its separate administrator-at-startup preference is enabled by default for deployed unpackaged copies and requests elevation through the normal Windows UAC prompt. Approval starts an elevated instance; cancellation or a failed request continues normally. Settings can disable future automatic requests. Diagnostic and packaged launches skip this path. Elevation can improve FPS or hardware-sensor access; it is not required for music controls or network counters and does not guarantee every sensor will work.

The GlimHUD installer remains per-user. Installing the optional system-wide PawnIO driver is a separate vendor installer step that can request UAC approval. An elevated HUD delegates a requested GlimHUD update to the existing Explorer desktop only after checking that it belongs to the same account and session, is the Windows Explorer executable, and has a non-elevated medium-integrity token. It checks the desktop again before dispatch and revalidates the cached installer's signature after stopping HUD services. It does not relax cache permissions, create a shared staging directory, or fall back to starting the GlimHUD installer with its elevated token.

If UAC was approved with a different administrator account, the HUD's settings and update cache belong to that account; the original desktop belongs to the signed-in user. This cross-account update handoff is intentionally blocked. A missing, replaced, or elevated Explorer desktop is also unavailable. For example, disabling UAC can leave an administrator's Explorer elevated, which cannot provide this handoff. Restart GlimHUD normally, or download the release installer and run it as the account that owns the installation.

A failed desktop preflight leaves the HUD running. A later handoff failure can occur after the HUD has stopped; the app reports that manual restart or download is needed. A successful Shell handoff means the launch request was accepted, not that installation completed. No UAC policy or Windows group membership is changed.

Microsoft references: [UAC account and token behavior](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works), [Explorer-based unelevated launches](https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643), and [the elevated-Explorer limitation when UAC is disabled](https://devblogs.microsoft.com/oldnewthing/20150618-00/?p=45351).

## Bundled CPU-temperature driver

Release installers and portable ZIPs include the complete unchanged [official PawnIO 2.2.0 installer](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0). `scripts/Get-PawnIO.ps1` downloads it from the pinned versioned upstream URL and requires the expected 3,410,960-byte length, SHA-256, valid Authenticode signature, and pinned signer certificate. Packaging also requires its provenance and notices. This fetch happens during a release build; application users do not need a separate download. The helper only downloads and verifies files, without installing a driver.

The CPU-driver task is optional and selected by default in GlimHUD setup. It reads the PawnIO uninstall registration's `DisplayVersion` in both Windows registry views. Missing or known older versions can launch the bundled vendor setup with `-install -silent` through UAC. Version 2.2 or newer is retained, and an existing installation whose version cannot be established is left unchanged. No unsigned developer edition, security-policy changes, or driver downgrade is requested. Portable users and existing installations can request the bundled installer from Settings.

Exit code `0` indicates success; `3010` indicates success with a required Windows restart. GlimHUD does not reboot Windows automatically. Cancellation and other failures do not cancel the GlimHUD installation; CPU-temperature access remains dependent on the actual driver and sensor state. An installed driver is not treated as proof of a temperature reading. The system-wide driver can be shared with other applications, so GlimHUD's uninstaller leaves it in place; it can be managed separately in Windows Installed apps.

The official signed setup has separate vendor terms permitting redistribution **unmodified**, confirmed by the [author's redistribution guidance](https://github.com/a1ive/nwinfo/issues/86). GlimHUD redistributes that complete installer, rather than extracting and shipping its driver payload. Do not describe the official signed binary as simply GPL-licensed: the [open-source driver](https://github.com/namazso/PawnIO) and the signed modules embedded in LibreHardwareMonitor have separate licenses. See [third-party notices](../THIRD-PARTY-NOTICES.md). The [official release notes](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0) document DOS exit codes and silent-mode restart reporting.

## One-time repository setup

For the prepared workstation checkout, log in to an account that administers the repository, then run the helper:

```powershell
gh auth login
.\scripts\Enable-Publishing.ps1 -StartRelease
```

The helper checks repository administration, existing local keys, and the public key and version on remote `main` before changing anything. It uploads the private seed through UTF-8 stdin without a byte-order mark, enables Pages with GitHub Actions as its source, and starts the Release workflow. This works in Windows PowerShell 5.1 and PowerShell 7, preserves the caller's output encoding, and handles an expected Pages 404 without treating other CLI failures as success. It never generates a new key or prints the private seed. `-WhatIf` performs the read-only checks and previews changes; omit `-StartRelease` to configure publishing without starting a release. A successful Release deploys the website and feed automatically. The following steps describe the equivalent manual setup.

1. In repository **Settings → Pages → Build and deployment**, choose **GitHub Actions** as the source. This requires repository administration access. The workflow's `GITHUB_TOKEN` cannot perform this initial enablement.
2. Generate an update key pair once with the pinned official NetSparkle tool. Keep the private seed in a password manager or other encrypted offline backup. Do not generate a different key for each release.
3. Commit only the public key to `src/GlimHUD/Assets/update-public-key.txt`.
4. Add the base64 private seed as the repository Actions secret **`NETSPARKLE_PRIVATE_KEY`**. Never commit it, include it in release archives, or print it in logs.

The initial workstation key pair is in the ignored `.tools/update-signing` directory. If it already exists, preserve it rather than running key generation again. For a new project checkout without an existing signing identity:

```powershell
dotnet tool install NetSparkleUpdater.Tools.AppCastGenerator --version 2.9.0 --tool-path .tools/netsparkle-appcast
$env:DOTNET_ROLL_FORWARD = 'Major'
.\.tools\netsparkle-appcast\netsparkle-generate-appcast.exe --generate-keys --key-path .tools/update-signing
Copy-Item -LiteralPath .tools/update-signing/NetSparkle_Ed25519.pub -Destination src/GlimHUD/Assets/update-public-key.txt
```

With the GitHub CLI authenticated to an account that administers this repository, upload the private seed directly from the file without displaying it:

```powershell
Get-Content -LiteralPath .tools/update-signing/NetSparkle_Ed25519.priv -Raw |
    gh secret set NETSPARKLE_PRIVATE_KEY --repo pyrrhuladev/GlimHUD
```

Do not use the tool's `--export` command in a recorded terminal: it prints the private key. The signing script passes private material only through the child process environment. The feed builder checks its generated signatures against the pinned public key before writing a releasable feed.

If Pages is not enabled yet, this authenticated administrative API command is equivalent to selecting GitHub Actions as its source:

```powershell
gh api repos/pyrrhuladev/GlimHUD/pages --method POST -f build_type=workflow
```

## Publishing a release

Update the project version and changelog, run the normal solution checks, and push the reviewed source to `main`. Start **Actions → Release → Run workflow**, leaving the version field empty to use the project version. Alternatively, use an authenticated CLI:

```powershell
gh workflow run release.yml --repo pyrrhuladev/GlimHUD --ref main -f version=0.7.0
```

Pushing a tag that exactly matches the project version, such as `v0.7.0`, also starts the release workflow. Alternatively, push reviewed source to a dedicated branch named `release/v0.7.0`. The branch name must exactly match the project version; an ordinary push to `main` does not publish a release. A branch-triggered or manual run creates the version tag at the exact source commit used by its build. Existing published versions and tags pointing elsewhere cannot be overwritten; publish a higher version instead.

The Release workflow restores/builds/tests the solution, runs real signing and tamper-rejection checks with temporary keys, fetches verified PresentMon and the official PawnIO installer, publishes a self-contained app, and compiles the per-user installer with the verified Inno Setup compiler. It signs both the versioned installer and the feed, verifies both signatures, and creates checksums. Its publish job uploads all assets into a draft release before making that complete release public.

The Pages workflow runs after a successful Release, on website changes to `main`, or manually. It builds the site from `site/` and fetches the signed feed assets from the latest public release. It verifies the feed signature and checks that its versioned installer and stable download aliases are present before deploying. The feed is never advanced before its installer is downloadable. A normal website edit preserves the published release's feed, and `release.json` always reflects that public release rather than an unreleased source version. Incomplete or pre-updater releases cannot deploy the page with broken download links; publish the first complete release before the initial Pages deployment.

GitHub does not start ordinary release-event workflows for releases created with `GITHUB_TOKEN`, so Pages intentionally listens to successful `workflow_run` completion instead. If a deploy fails, the previous Pages deployment remains in place; rerun **Pages** after resolving the error:

```powershell
gh workflow run pages.yml --repo pyrrhuladev/GlimHUD --ref main
```

Once deployed, check the product page, both feed URLs, and a fresh installer download. A newer-to-older downgrade is not offered by the updater. To revert a faulty application change, publish its fix as a higher version rather than replacing an existing installer. Publishing the same bytes under new contents at an old signed URL is prohibited.

## Local packaging and feed verification

```powershell
.\scripts\Get-PawnIO.ps1
.\scripts\Publish.ps1 -SelfContained
.\scripts\Get-InnoSetup.ps1
.\scripts\Build-Installer.ps1 -InputDirectory artifacts/GlimHUD-win-x64 -OutputDirectory artifacts/release
.\scripts\Build-UpdateFeed.ps1 -InstallerPath artifacts/release/GlimHUD-0.7.0-Setup-x64.exe -Version 0.7.0 -OutputDirectory artifacts/release -KeyDirectory .tools/update-signing
python -m unittest discover -s tests -p test_update_feed.py -v
```

These commands compile and sign artifacts without installing the application or publishing anything. `Build-UpdateFeed.ps1` requires a fresh feed output and never replaces existing signed metadata. `KeyDirectory` is for local signing; CI instead supplies `NETSPARKLE_PRIVATE_KEY` only to its signing step. A public-key change affects all future signatures and must be planned as a key migration; clients with the old pinned key will reject the replacement.

Primary references: [NetSparkle usage and signatures](https://github.com/NetSparkleUpdater/NetSparkle), [custom GitHub Pages workflows](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages), and [workflow trigger restrictions](https://docs.github.com/en/actions/how-tos/writing-workflows/choosing-when-your-workflow-runs/triggering-a-workflow).
