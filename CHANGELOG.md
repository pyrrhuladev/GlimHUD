# Changelog

## 0.9.1 - 2026-11-14

### Fixed

- Automatic FPS targeting retains the selected game during brief focus changes to known recording-control windows, instead of switching to a recording helper's own low-rate or missing frame stream. Stale game data still expires normally.
- Distinguished nonfatal PresentMon warnings from actual capture-permission failures, avoiding an unnecessary administrator-required status after a successful collection.
- Added CPU-temperature access states for missing PawnIO, denied or unavailable driver access, and a sensor without a reading. Previously unavailable driver access can trigger a bounded sensor reinitialization; missing values are not classified as unsupported CPUs.

### Added

- An on-demand **Copy diagnostics** action for local CPU/sensor and FPS/capture troubleshooting. The bounded clipboard report excludes media, window titles, paths, network identifiers, and serial numbers; nothing is uploaded automatically.
- The unchanged, vendor-signed official PawnIO 2.2.0 installer in both setup and portable releases. The default-selected optional CPU-driver task installs missing or known older versions through Windows UAC; an existing version of 2.2 or newer is retained, and unknown existing versions are left unchanged.
- A Settings action to install the bundled CPU driver without a second download, plus an official website help link. Cancellation or failure leaves GlimHUD usable; restart-required results are reported without automatically rebooting Windows. The shared system driver remains installed when GlimHUD is removed.
- Build-time checks for the bundled installer's pinned size, SHA-256, Authenticode signer, and provenance, plus its separate redistribution notice. GlimHUD does not change Windows security policies.
- License notices and source information for the signed PawnIO modules already embedded in LibreHardwareMonitor.

## 0.9.0 - 2026-11-14

### Added

- Optional compact layout with smaller text and spacing and hidden album artwork, section icons, mute button, and volume slider. Playback controls, volume percentage, selected metrics, optional volume scrolling, and global shortcuts remain available.
- A saved administrator-at-startup preference, enabled by default for unpackaged copies. Windows UAC approval starts an elevated instance; cancellation continues with normal permissions. Packaged and diagnostic launches skip the automatic request.

### Changed

- Kept the regular layout selectable and restored when compact mode is disabled.
- Documented that elevation can help FPS capture and hardware sensor access, while music controls and network readings remain available without it.
- User-requested updates from an elevated HUD use a verified, non-elevated Explorer desktop for the same account. Unsupported desktop or cross-account cases stop safely without an elevated installer fallback, and the installer signature is checked again before handoff.

## 0.8.1 - 2026-11-14

### Fixed

- Reduced glow rendering work and repeated allocations by caching the capsule's pixel map and reusing a bitmap buffer instead of rebuilding vector strips and opacity masks each frame.
- Each glow surface now submits one image, with a bounded raster resolution for large panels and high-DPI displays. Panel text and controls keep their normal WPF rendering.

### Changed

- Preserved the 30 Hz animation cadence, Rainbow Wave and Neon Flow styles, inner/outer glow, music-driven highlights, and Neon Flow's white core.
- Added an optional reproducible glow benchmark and [performance notes](docs/glow-performance.md). Reported offscreen CPU/raster timings are not GPU-utilization measurements.

## 0.8.0 - 2026-11-14

### Added

- Optional network module for live download and upload rates in the HUD, off by default.
- Network settings with automatic or manual adapter selection, reported link speed, per-adapter session received/sent totals, and a 60-second traffic graph.
- English and Russian network labels, statuses, units, and help text.

### Changed

- Network monitoring uses one adapter at a time to avoid adding physical and VPN traffic together. Automatic selection prefers a connected physical Ethernet/Wi-Fi adapter; virtual or tunnel interfaces can be selected explicitly.
- Documented that adapter traffic includes other applications and LAN transfers, while reported link speed is separate from measured throughput. Counters and graph history stay on the device without packet capture or external probes.

## 0.7.0 - 2026-11-14

### Added

- Public product page for GitHub Pages, with English and Russian content, installer and portable downloads, and GitHub support links.
- Self-contained Windows x64 installer for the current Windows account, with upgrade and uninstall support.
- Updates in Settings: optional background checks, manual checks, download progress, and user-initiated installation.
- NetSparkle strict Ed25519 verification for both update metadata and installers. Portable and packaged copies keep their own update paths.
- Release automation for versioned installers, stable download aliases, checksums, and signed update metadata; Pages deploys the feed only after its release assets are public.

### Changed

- Installer downloads are the primary distribution path; portable ZIP downloads remain available.
- Documented the distinction between project update signatures and Windows publisher signing, which is still pending.

## 0.6.1 - 2026-11-14

### Added

- Support and ideas in Settings, with buttons for GitHub Issues and the repository. Links open in the browser only after a click; no report is posted automatically.
- Saved music-response sensitivity, independent of glow intensity and style. Higher values make quieter music trigger the glow more readily.
- A live music signal meter in Settings to check audio input from the selected application.

### Changed

- Expanded English and Russian interface coverage, including shortcut key names and localized link and restart errors.
- Made musical highlights broader and easier to see while retaining each style's resting appearance.

### Fixed

- Preserved short selected-application audio peaks between render frames with faster metering and bounded peak retention.
- Replaced rigid single-frame onset thresholds with adaptive accumulated dynamics for quiet music, compressed tracks, and gradual attacks. Noise, steady levels, and unavailable input do not generate periodic flashes.
- Resampled localized snapshots after a language change so an in-flight read cannot restore stale-language statuses.

## 0.6.0 - 2026-11-14

### Added

- A saved glow style selector with Rainbow Wave and Neon Flow in English and Russian Settings. Existing settings keep Rainbow Wave, and glow remains disabled by default.
- Neon Flow: cyan and pink neon with a bright white core, traveling highlights, and an outer and inner halo.

### Changed

- Music response adds pulses, traveling highlights, and halo expansion from rises in the selected music application's audio level. The baseline style animation continues during silence or unavailable audio without generating an artificial beat.
- Unknown glow style settings safely fall back to Rainbow Wave while preserving the remaining preferences.

## 0.5.1 - 2026-11-14

### Changed

- Replaced the accent-colored halo with a pronounced RGB Rainbow Wave outside and inside the panel, beneath its background and text.
- The rainbow wave moves continuously, including when music is paused or unavailable.
- Music response now adds a brightness boost above the animated baseline. The boost follows only the selected music application's level and fades when that audio is silent or unavailable; no artificial beat is generated.
- Updated English and Russian glow labels and waiting hints to describe the continuous wave and optional music boost.

### Fixed

- Kept the right-click menu above the HUD and glow while it is open, including during periodic overlay refreshes. Applied the same ordering protection to the tray menu.

## 0.5.0 - 2026-11-14

### Added

- Optional panel glow, disabled by default, using the Windows or custom accent color with adjustable intensity.
- Static and music-reactive glow modes. The reactive mode follows the selected music application's audio session level and fades out during silence or when audio is unavailable.

### Changed

- Refined Settings with a darker matte background, quieter borders, consistent spacing, and clearer hover, focus, and disabled states.

### Notes

- FPS capture and data-source behavior are unchanged from 0.4.2.
