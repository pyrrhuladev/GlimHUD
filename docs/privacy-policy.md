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
- Local network adapter names and identifiers, connection state, reported link speed, and cumulative received/sent byte counters, to calculate adapter traffic rates and session totals. These readings can include traffic from all applications and the local network. GlimHUD does not capture packets, inspect their destinations or payloads, or send network probes for this feature.
- During an update from an elevated HUD, local Explorer process identity, Windows account identifiers, session, and elevation state, to check whether a same-account ordinary-user installer handoff is available. These checks do not read passwords or create an account history.
- The Windows accent color when the system accent option is enabled.

These values are used locally for the overlay and its settings. They are not sent to a GlimHUD service. GlimHUD does not inject a DLL into a game.

## Local storage and controls

Preferences are stored at `%LOCALAPPDATA%\GlimHUD\settings.json`. They can include appearance, language, source selections, selected application identities, game executable paths, and the selected network adapter identifier. Artwork may be held temporarily in memory for display; it is not a browsing or listening history database.

Compact layout and the administrator-at-startup preference are also stored locally. For unpackaged copies, the administrator request is enabled by default and uses the normal Windows UAC prompt. Cancelling leaves GlimHUD running with normal permissions, and the option can be disabled in Settings. Windows handles UAC consent or administrator credentials; GlimHUD does not receive or store those credentials. Elevation does not add an upload or analytics service. If Windows starts the app under a different administrator account, that account has its own per-user settings and data locations.

Network byte counters are sampled locally about once per second during normal app operation. The optional HUD network module is hidden by default. Per-adapter session totals and the recent 60-second graph stay in memory and are cleared when GlimHUD closes; traffic history is not saved to disk. A saved adapter choice identifies a local interface, not a list of websites or connections.

You can hide the music player, disable game-only visibility, select sources manually, or close the application from its tray menu. To remove saved preferences, close GlimHUD and delete its local settings file. Replacing or removing portable program files does not necessarily remove those preferences.

PresentMon and Windows may create local capture or diagnostic data as part of operation or troubleshooting. GlimHUD does not upload these files automatically. Review logs and screenshots before deliberately attaching them to a GitHub issue; they can reveal paths, process names, or media information.

The **Copy diagnostics** button generates a bounded JSON report and copies it to the clipboard only after a click. It includes app/OS version, architecture, administrator state, CPU model and temperature/access status, selected temperature sensor, PawnIO presence/version/device-open error and the last requested driver-install outcome/error codes, up to 64 temperature sensor names/identifiers/values, and the FPS target's process ID/name, selection mode, frame counts, swapchain, age, and typed running/availability/elevation states. Free-form FPS status and collector error text are not copied. This report excludes media information, window titles, file paths, network identifiers, and serial numbers. It is not automatically uploaded or saved as a diagnostic history. You choose whether to paste it elsewhere and should review it before sharing.

## Network activity and third parties

Installed copies can check for updates through GitHub Pages and download an installer from this project's GitHub Releases. Automatic checks are enabled by default for installed copies and can be disabled in Settings. They run shortly after startup and about every six hours while the application is running. Manual checks and downloads happen only when requested; installation and restart always require the install button. Portable copies do not make these background update requests, and packaged copies use their distribution channel instead.

Update requests retrieve public version metadata, its signature, and, when requested, the installer. They do not include track titles, audio levels, hardware readings, network adapter identities or traffic counters, game paths, settings, or diagnostic logs. The network monitor itself does not make external requests. GitHub and its download infrastructure can receive ordinary connection information such as your IP address and request headers. Downloads are cached under `%LOCALAPPDATA%\GlimHUD\Updates`; a matching Ed25519 signature is required before an installer can run. GitHub's own privacy policy applies to its services.

The development setup scripts download dependencies such as PresentMon, PawnIO's official installer, Inno Setup, and NetSparkle tools from their official sources when explicitly run. Opening support, source, or release links launches your browser and is governed by GitHub's privacy policy.

Setup and portable releases include the unchanged official PawnIO 2.2.0 installer as a local file. The optional CPU-driver task is selected by default during GlimHUD setup; when needed, it starts that bundled installer through Windows UAC. Portable users and existing installations can request installation from Settings. GlimHUD does not make a second driver download, receive UAC credentials, or automatically restart Windows. CPU access checks read local driver registration and device availability and do not contact a server. PawnIO's help link opens its official website only when clicked. The driver and its vendor installer are separate third-party components; their own terms and Windows behavior apply. The shared system driver remains installed when GlimHUD is uninstalled.

Media players, Windows media integration, artwork providers, hardware components, and Windows itself can use network services independently. Artwork supplied through a media session may originate from a remote service. The absence of a GlimHUD telemetry service does not mean that the player, operating system, or distribution service never uses the network.

No data is sold by the GlimHUD project. Third-party components retain their own licenses; see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## Questions and changes

For ordinary questions, use the [GitHub repository](https://github.com/pyrrhuladev/GlimHUD). Use the [security reporting guidance](../SECURITY.md) for sensitive matters and do not post private data publicly. Changes to this policy will be recorded in the repository with an updated date.
