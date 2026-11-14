# GlimHUD

A transparent Windows overlay for music controls, FPS, CPU, GPU, and memory. Built for windowed and borderless games, with no DLL injection into games.

![GlimHUD overlay preview on dark and light backgrounds](docs/images/overlay-preview.png)

*Design preview with sample measurements.*

**Windows 10 version 2004 or later / Windows 11 · x64 · .NET 10 · MIT**

[Product page](https://pyrrhuladev.github.io/GlimHUD/) · [Download installer](https://github.com/pyrrhuladev/GlimHUD/releases/latest/download/GlimHUD-Setup-x64.exe) · [Release notes](https://github.com/pyrrhuladev/GlimHUD/releases)

## Features

- Compact, movable overlay with a translucent background and readable controls.
- Current track, artwork, previous/play/pause/next controls through Windows media sessions. Supports compatible players, including Yandex Music.
- Per-application music volume through WASAPI, with optional scrolling over the player area. It does not fall back to changing the system master volume.
- Presented FPS through PresentMon, CPU/GPU load and available temperatures, and physical memory usage.
- Optional player hiding, automatic display while a recognized game is active, and manual game executable selection.
- Windows accent color or a custom color, adjustable scale, and click-through mode.
- Independent text/background colors with white, black and custom choices.
- Optional Rainbow Wave and Neon Flow styles with outer and inner glow, adjustable intensity, and music response, off by default.
- Dark matte Settings with consistent spacing and clear control states.
- Alignment guides during free dragging. A small release-only magnet aligns nearby grid lines, edges or centers; hold Shift to skip snapping. Disable guides in Settings if preferred.
- English by default, with Russian selectable in Settings.
- Per-user Windows installer and optional update checks, with installer and feed verification through NetSparkle's Ed25519 signatures.

## Download and use

Download the [Windows x64 installer](https://github.com/pyrrhuladev/GlimHUD/releases/latest/download/GlimHUD-Setup-x64.exe) from the [product page](https://pyrrhuladev.github.io/GlimHUD/). It installs for your Windows account without administrator access. The installer includes the .NET runtime.

For a portable copy, download [GlimHUD-win-x64.zip](https://github.com/pyrrhuladev/GlimHUD/releases/latest/download/GlimHUD-win-x64.zip), extract the complete ZIP, and start `GlimHUD.exe`. Keep all extracted files together. Additional builds and checksums are available in [GitHub Releases](https://github.com/pyrrhuladev/GlimHUD/releases). Framework-dependent source builds require the [.NET 10 Desktop Runtime for x64](https://dotnet.microsoft.com/download/dotnet/10.0).

On the first launch, position the unlocked overlay, then use the tray menu or Settings to adjust its appearance and data sources.

| Shortcut | Action |
| --- | --- |
| `Ctrl + Alt + G` | Toggle locked / interactive mode |
| `Ctrl + Alt + H` | Show / hide the overlay |
| `Ctrl + Alt + S` | Open Settings |
| `Ctrl + Alt + Space` | Play / pause music |
| `Ctrl + Alt + Left` | Previous track |
| `Ctrl + Alt + Right` | Next track |
| `Ctrl + Alt + Up` | Music volume +3% |
| `Ctrl + Alt + Down` | Music volume -3% |
| `Ctrl + Alt + M` | Mute / unmute music |

Music shortcuts work globally, including when the overlay is locked or hidden and when its player section is hidden. Volume shortcuts control the selected music application's audio sessions, not Windows master volume. Hold a volume shortcut to change it repeatedly. Playback, track and mute shortcuts trigger once per key press. Settings lists any shortcuts that Windows could not register because another application uses them or Windows blocks them.

Settings are saved locally at `%LOCALAPPDATA%\GlimHUD\settings.json`. Language changes apply immediately.

**Panel glow** in Settings offers two styles: **Rainbow Wave** keeps the flowing RGB rainbow, while **Neon Flow** adds cyan and pink neon with a bright white core. Both animate outside and inside the panel, beneath its background and text. The intensity slider controls the effect, and the selected style is saved across restarts. Existing settings keep Rainbow Wave.

Optional **React to music** follows the audio level of the application selected for music volume. Rises in that level add light pulses, traveling highlights, and halo expansion. **Music response sensitivity** makes quieter music trigger more readily as you raise it; this preference is saved independently of glow intensity and style. The live **Music signal** meter helps check whether the selected application's audio reaches the effect. The response fades during silence or when that session is unavailable, while the baseline animation continues. No artificial beat is generated when music is absent. It does not follow system-wide or game audio unless that application is selected. Glow is disabled by default.

**Support and ideas** at the bottom of Settings links to [GitHub Issues](https://github.com/pyrrhuladev/GlimHUD/issues) for bug reports and feature requests, and to the [repository](https://github.com/pyrrhuladev/GlimHUD). Each button opens your browser only when clicked; it does not send a report.

Optional **Start with Windows** is available in Settings. It registers the current extracted executable for your Windows sign-in without administrator access. Keep that folder in place; after moving to another copy, toggle the option off and on to update its location. Windows Startup apps settings can independently disable registered applications. The option is off until explicitly enabled and does not alter Windows permissions or request UAC during sign-in.

**Updates** in Settings can check for a newer installed version, download it, and install it when you choose. Automatic checks can be disabled. Checks never install an update or restart the app by themselves. Both update metadata and downloaded installers must pass NetSparkle Ed25519 verification against the public key shipped with GlimHUD. Portable copies use manual updates through GitHub Releases.

Windows publisher signing (Authenticode) is not configured yet, so Windows may show an unknown-publisher or SmartScreen warning. Update signatures authenticate project updates; they do not provide a verified Windows publisher identity. There is no published Microsoft Store listing. See [distribution and release setup](docs/distribution.md) for the website, installer, and update pipeline, or [Microsoft Store preparation](docs/microsoft-store.md) for the separate MSIX work.

## Data sources and limits

Music comes from Windows Global System Media Transport Controls. Metadata and artwork depend on what each player exposes. Playing music sessions are preferred over stale paused sessions, and a source can be selected manually in Settings. Volume targets the matching application's Windows audio sessions; select an application manually if automatic matching is ambiguous.

CPU usage uses Windows Processor Utility, which accounts for clock speed and boost and can exceed 100%. If that counter is unavailable, GlimHUD falls back to CPU busy time. GPU usage takes the busiest graphics engine or vendor core reading, whichever is higher, for the selected adapter. CPU/GPU/RAM sampling runs once per second independently of media and artwork loading. This follows the measurement definitions described by [Valve](https://help.steampowered.com/en/faqs/view/3462-CD4C-36BD-5767); different sampling times and adapter selections can still produce different readings.

RAM and performance counters have native Windows data sources. Optional temperature sensors use LibreHardwareMonitor; available readings depend on hardware, drivers, and permissions. An unavailable measurement is shown as a dash. GlimHUD does not automatically elevate or change Windows security settings.

FPS uses [PresentMon](https://github.com/GameTechDev/PresentMon) to count presented frames. This is not the display refresh rate or the overlay's own frame rate. ETW capture can require additional permissions. Settings shows an explicit administrator restart option when Windows denies ETW access; cancelling the Windows prompt keeps the current app running. No group membership is changed. The other overlay features work when FPS capture is unavailable. Choose a game manually when automatic process selection is unsuitable.

Game-only display is optional. Recognition uses known game installation paths and exact executable paths selected by the user; browsers and launchers are excluded where identifiable. Windowed and borderless modes are the intended targets. Exclusive fullscreen and individual anti-cheat systems need separate compatibility testing.

See the [privacy policy](docs/privacy-policy.md) for local data handling.

## Build from source

Install the [.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0). A Desktop Runtime alone cannot build the project. Scripts also recognize an SDK extracted into `.tools\dotnet`. The project targets Windows SDK contracts for Windows 10 19041; the .NET restore supplies the targeting references. A separate Windows SDK installation is needed for [MSIX packaging](docs/microsoft-store.md), not a normal portable build.

```powershell
.\scripts\Get-PresentMon.ps1
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\Publish.ps1 -SelfContained
```

Published files are written to `artifacts\GlimHUD-win-x64`. Keep all files in that directory together. PresentMon 2.6.0 is downloaded from its official upstream release and checked against its SHA-256 digest. It can be omitted for a build without FPS capture.

To build the optional installer, run `scripts/Get-InnoSetup.ps1`, then `scripts/Build-Installer.ps1 -InputDirectory artifacts/GlimHUD-win-x64`. This compiles the installer without installing or starting it. Release automation and signing-key setup are documented in [distribution.md](docs/distribution.md).

To restore and build the complete solution, including check projects:

```powershell
dotnet restore .\GlimHUD.slnx
dotnet build .\GlimHUD.slnx --configuration Release --no-restore
.\scripts\Test.ps1 -NoRestore -NoBuild
```

The console check projects cover media selection, artwork caching, audio targeting, FPS aggregation, telemetry, settings, input gating, accents, and localization. Normal checks do not change another application's playback or volume. Python 3 runs the release archive fixture checks. Hardware availability is handled by the relevant checks; real game/player compatibility still needs a manual check on Windows.

## Contributing and license

Issues, pull requests, and repository documentation use English. See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

GlimHUD's original code and project assets use the [MIT license](LICENSE). Bundled dependencies retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the accompanying license files.
