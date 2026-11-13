# GlimHUD

A transparent Windows overlay for music controls, FPS, CPU, GPU, and memory. Built for windowed and borderless games, with no DLL injection into games.

![GlimHUD overlay preview on dark and light backgrounds](docs/images/overlay-preview.png)

*Design preview with sample measurements.*

**Windows 10 version 2004 or later / Windows 11 · x64 · .NET 10 · MIT**

## Features

- Compact, movable overlay with a translucent background and readable controls.
- Current track, artwork, previous/play/pause/next controls through Windows media sessions. Supports compatible players, including Yandex Music.
- Per-application music volume through WASAPI, with optional scrolling over the player area. It does not fall back to changing the system master volume.
- Presented FPS through PresentMon, CPU/GPU load and available temperatures, and physical memory usage.
- Optional player hiding, automatic display while a recognized game is active, and manual game executable selection.
- Windows accent color or a custom color, adjustable scale, and click-through mode.
- English by default, with Russian selectable in Settings.

## Download and use

Get builds from [GitHub Releases](https://github.com/pyrrhuladev/GlimHUD/releases). Extract the complete ZIP before starting `GlimHUD.exe`. Self-contained builds include the .NET runtime; framework-dependent builds require the [.NET 10 Desktop Runtime for x64](https://dotnet.microsoft.com/download/dotnet/10.0).

On the first launch, position the unlocked overlay, then use the tray menu or Settings to adjust its appearance and data sources.

| Shortcut | Action |
| --- | --- |
| `Ctrl + Alt + G` | Toggle locked / interactive mode |
| `Ctrl + Alt + H` | Show / hide the overlay |
| `Ctrl + Alt + S` | Open Settings |

Settings are saved locally at `%LOCALAPPDATA%\GlimHUD\settings.json`. Language changes apply immediately.

Unsigned MSIX packaging is available for development; there is currently no published Microsoft Store listing. See [Microsoft Store preparation](docs/microsoft-store.md) for packaging, signing status, and unresolved certification requirements. Portable GitHub builds are not currently Authenticode-signed. There is no built-in updater; replace portable files manually when updating.

## Data sources and limits

Music comes from Windows Global System Media Transport Controls. Metadata and artwork depend on what each player exposes. Playing music sessions are preferred over stale paused sessions, and a source can be selected manually in Settings. Volume targets the matching application's Windows audio sessions; select an application manually if automatic matching is ambiguous.

CPU and RAM have native Windows data sources. Optional hardware sensors use LibreHardwareMonitor; available readings depend on hardware, drivers, and permissions. An unavailable measurement is shown as a dash. GlimHUD does not automatically elevate or change Windows security settings.

FPS uses [PresentMon](https://github.com/GameTechDev/PresentMon) to count presented frames. This is not the display refresh rate or the overlay's own frame rate. ETW capture can require additional permissions, such as membership in Performance Log Users or an elevated launch. The other overlay features work when FPS capture is unavailable. Choose a game manually when automatic process selection is unsuitable.

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
