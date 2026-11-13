# GlimHUD — English Store listing draft

Status: draft for review. Do not publish until the Store runtime/driver/FPS checks in `docs/microsoft-store.md` are complete. Feature claims must match the final Store build.

## Short description

Music controls and PC telemetry in a transparent desktop overlay.

## Description

Keep music controls and your PC's current status close to your game. GlimHUD is a lightweight desktop overlay with an adjustable transparent background and accent color.

Control compatible Windows media sessions, view track information and artwork, adjust music volume, or hide the player to make room for telemetry. Choose the Windows accent color or your own, configure the overlay, and switch between English and Russian.

GlimHUD is free, open-source software released under the MIT license.

## Candidate feature bullets

- Transparent overlay with adjustable appearance.
- System media controls, track details and artwork from compatible players.
- Optional mouse-wheel volume control over the player.
- Optional player section and automatic display while gaming.
- CPU and memory usage.
- English and Russian interface.

Add GPU load/temperature, CPU temperature, and game FPS to the published description only after confirming the final Store implementation works under standard-user permissions. Available measurements depend on hardware and access. Windowed/borderless operation and compatibility must be checked on supported systems.

## Submission fields still required

- Verified publisher, public support/contact URL and privacy-policy URL.
- Public source repository URL after publication.
- Real screenshots of the released build (not concept renders).
- Version-specific release notes and tested feature limitations.
- Certification notes explaining `runFullTrust`, Windows media access, native telemetry, and PresentMon if retained.
