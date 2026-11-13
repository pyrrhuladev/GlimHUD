# PresentMon dependency

GlimHUD reads **Presented FPS** from the official PresentMon console application's
ETW output. It does not inject code into a game. Presented FPS is the rate of
`Present()` calls and can differ from displayed frames, especially with frame
generation.

Run from the repository root:

```powershell
powershell -NoProfile -File .\scripts\Get-PresentMon.ps1
```

The script pins release **2.6.0** from
<https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0>, selects its exact
x64 console executable by name, verifies the SHA256 release-asset digest, and
records provenance next to the executable. It does not install a service or
request elevation. Run it on a machine with network access, then rebuild.

If the release has no published asset digest, supply `-ExpectedSha256` only after
independently verifying that checksum. A checksum is deliberately not invented or
downloaded from an unrelated mirror.

Expected local layout:

- `PresentMon-2.6.0-x64.exe`
- `provenance.json`
- `LICENSE.txt` (the upstream MIT license, required for redistribution)

The application copies this directory to `tools/PresentMon` in its output. If the
executable is absent, FPS stays unavailable and the rest of the HUD keeps working.
If ETW access is denied, the HUD reports the problem instead of fabricating FPS or
automatically requesting administrator privileges.

The automatic target is the foreground application. Use an explicit process in
settings to keep a game selected while switching to another application. Focusing
GlimHUD or the Windows desktop preserves the most recent application target.

Upstream console documentation:
<https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md>.
