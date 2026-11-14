# GlimHUD privacy policy

Last updated: 2026-11-14

GlimHUD is a local Windows application. It has no GlimHUD account, advertising service, analytics service, telemetry endpoint, or developer-operated backend. It does not automatically upload usage data or diagnostics to the developer.

## Information used on the device

To show the overlay and provide its controls, GlimHUD reads information exposed by Windows, compatible media players, and hardware monitoring components:

- Media source identity, track title, artist, playback state, and artwork from Windows media sessions.
- Windows audio session identities and volume levels, to control the application selected for music volume.
- Peak audio levels from the selected music application when the optional panel glow's music response is enabled. Both glow styles use the same local level meter, and their baseline animations do not require audio input. The glow meter does not record audio or use microphone input.
- Running process identities, executable paths, and the foreground window, to identify games and select an FPS target.
- Presented-frame events through PresentMon and local CPU, GPU, memory, and available temperature readings.
- The Windows accent color when the system accent option is enabled.

These values are used locally for the overlay and its settings. They are not sent to a GlimHUD service. GlimHUD does not inject a DLL into a game.

## Local storage and controls

Preferences are stored at `%LOCALAPPDATA%\GlimHUD\settings.json`. They can include appearance, language, source selections, selected application identities, and game executable paths. Artwork may be held temporarily in memory for display; it is not a browsing or listening history database.

You can hide the music player, disable game-only visibility, select sources manually, or close the application from its tray menu. To remove saved preferences, close GlimHUD and delete its local settings file. Replacing or removing portable program files does not necessarily remove those preferences.

PresentMon and Windows may create local capture or diagnostic data as part of operation or troubleshooting. GlimHUD does not upload these files automatically. Review logs and screenshots before deliberately attaching them to a GitHub issue; they can reveal paths, process names, or media information.

## Network activity and third parties

Installed copies can check for updates through GitHub Pages and download an installer from this project's GitHub Releases. Automatic checks are enabled by default for installed copies and can be disabled in Settings. They run shortly after startup and about every six hours while the application is running. Manual checks and downloads happen only when requested; installation and restart always require the install button. Portable copies do not make these background update requests, and packaged copies use their distribution channel instead.

Update requests retrieve public version metadata, its signature, and, when requested, the installer. They do not include track titles, audio levels, hardware readings, game paths, settings, or diagnostic logs. GitHub and its download infrastructure can receive ordinary connection information such as your IP address and request headers. Downloads are cached under `%LOCALAPPDATA%\GlimHUD\Updates`; a matching Ed25519 signature is required before an installer can run. GitHub's own privacy policy applies to its services.

The development setup scripts download dependencies such as PresentMon, Inno Setup, and NetSparkle tools from their official sources when explicitly run. Opening support, source, or release links launches your browser and is governed by GitHub's privacy policy.

Media players, Windows media integration, artwork providers, hardware components, and Windows itself can use network services independently. Artwork supplied through a media session may originate from a remote service. The absence of a GlimHUD telemetry service does not mean that the player, operating system, or distribution service never uses the network.

No data is sold by the GlimHUD project. Third-party components retain their own licenses; see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## Questions and changes

For ordinary questions, use the [GitHub repository](https://github.com/pyrrhuladev/GlimHUD). Use the [security reporting guidance](../SECURITY.md) for sensitive matters and do not post private data publicly. Changes to this policy will be recorded in the repository with an updated date.
