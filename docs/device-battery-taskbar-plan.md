# Device batteries and Windows taskbar: implementation plan

Status: researched 2026-11-18; planned after themes, not implemented in this change.

## Product behavior

Add an optional **Devices** module to the HUD and a separate compact taskbar view. Show device name, charge, charging state when known, and measurement age. Users can select, hide, rename and order devices independently of their theme. A theme controls appearance; it must not start device I/O or change the user's device selection.

Never present an unavailable reading as 0%, a cached reading as live, or a coarse battery category as a measured percentage. Keep ordinary telemetry and music controls usable when a device provider or taskbar view is unavailable.

## What the reference project actually does

[Wireless Device Battery Monitor](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor) is native C++/Win32, not a general Windows battery API or a reusable .NET library. Its provider registry combines Bluetooth properties, standard HID reports, controller APIs and many device-specific protocols. Its UI offers taskbar text and notification-area icons.

The taskbar renderer creates a layered child HWND in Explorer's taskbar. Windows 10 and Windows 11 need different placement logic; secondary displays get additional windows. Windows 11 clock/task-button bounds require UI Automation. The implementation explicitly moves those calls to a windowless worker because cross-process calls from the child-window UI thread had caused Explorer deadlocks. See [widget.cpp](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/blob/666b7def119066d098c105805d68f9314af2fc64/src/widget.cpp).

The repository is [GPL-3.0](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/blob/666b7def119066d098c105805d68f9314af2fc64/LICENSE); GlimHUD is MIT. The proposed implementation uses independent code against Windows APIs and documented protocols. Copying this project's implementation into GlimHUD would require a separate licensing decision. No source or binary from it has been added.

## Battery providers

| Stage | Provider | Coverage and limitations |
| --- | --- | --- |
| 1 | Windows Bluetooth device properties | Read the charge Windows already knows; group service nodes using device container identity. Some devices expose no value; the cached value may lag. Confirm connection using the physical device, not an audio-profile node alone. |
| 1 | `Windows.Gaming.Input.Gamepad.TryGetBatteryReport()` | Controllers supported by Windows. Use remaining/full capacity only when both values are valid; capacity fields and report availability are optional. |
| 1 | `XInputGetBatteryInformation` fallback | Xbox-compatible wireless controllers/headsets. Four categories: Empty, Low, Medium, Full; show those labels rather than invented percentages. Wired/disconnected/unknown types are distinct states. |
| 2 | BLE Battery Service | Read/subscribe to the standard Battery Level characteristic when exposed and accessible. Avoid forcing connections or pairing merely to refresh the HUD. |
| 2 | Standard HID battery usages | Inspect descriptors and only read declared battery fields; exclude UPS/power devices and unsupported collections. Do not blindly probe every HID device. |
| 3 | Vendor adapters | Add tested VID/PID/interface/protocol allowlists by actual demand: Logitech HID++, Sony controllers, then the user's headsets/keyboards/mice. A 2.4 GHz dongle does not imply standard battery support. |

The Bluetooth cached-property technique in the reference implementation reads `{104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2` through SetupAPI. Treat this as an implementation-dependent Windows fallback, not a universally documented contract. [generic.cpp](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/blob/666b7def119066d098c105805d68f9314af2fc64/src/drivers/generic.cpp) also demonstrates why generic device probing needs care: some devices misbehave even on nominal reads.

Proposed seams: `IDeviceBatteryProvider`, immutable `DeviceBatterySnapshot`, `DeviceBatteryService`, `BluetoothBatteryProvider`, `GamepadBatteryProvider`, then isolated vendor providers. Each reading carries stable physical identity, provider, optional percent/category, connection state, optional charge state and timestamp. Deduplicate by container/controller identity, not display name or VID/PID alone.

Poll on a background worker roughly once per minute, with debounced device-change/resume refresh and explicit Refresh. Bound enumeration/concurrency; isolate provider failures; reuse the snapshot between settings, HUD and taskbar. No new kernel driver is planned for this feature.

## Taskbar implementation

Start with a separate `TaskbarTelemetryWindow` and `TaskbarPlacementService`; do not reparent the main WPF overlay. Reuse formatted snapshots and safe theme tokens while enforcing taskbar height, legibility and a bounded width. Keep glow/continuous animation disabled in the taskbar view.

Prototype an independent nonactivating window positioned alongside the notification area, without modifying Explorer's windows. This avoids making the application's WPF window a cross-process child. It still needs explicit hide/reposition handling and does not reserve a native taskbar slot. Evaluate a native child host only if the prototype proves insufficient; cross-process parenting and mismatched DPI awareness require separate validation.

Windows taskbar internals and automation identifiers are compatibility heuristics, not a supported arbitrary-widget extension contract. If placement is uncertain, crowded, hidden or unsupported, fall back to the existing HUD/notification area. Do not cover task buttons or the clock. `ITaskbarList3` overlay icons decorate an app's taskbar button; they are not a replacement for free-standing telemetry text.

Observe Explorer recreation (`TaskbarCreated`), display/work-area changes, per-monitor DPI, theme changes and suspend/resume. Run all UI Automation work on a dedicated MTA thread with no windows. Cache results so Explorer responsiveness never blocks GlimHUD's UI. Detect auto-hide/fullscreen and conceal the view with its taskbar. Preserve placement choice by monitor identity rather than transient display numbering.

Acceptance cases: Windows 10/11; centered/left-aligned taskbar; auto-hide; mixed 100/150/200% DPI; primary/secondary displays; monitor unplug/replug; Explorer restart; sleep/wake; maximized and fullscreen games; crowded taskbar; no device; charging; device sleep/disconnect; two identical devices; stale/invalid reports; cancelled/failed provider operations. Check that the feature produces no permanent render loop and does not steal game focus.

## Primary references

- [Microsoft: Bluetooth battery profiles and services](https://learn.microsoft.com/en-us/windows-hardware/design/accessory-guidelines/bluetooth-accessory-guidelines/bluetooth-accessory-guidelines-battery-profiles-services)
- [Microsoft: Gamepad.TryGetBatteryReport](https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.gamepad.trygetbatteryreport)
- [Microsoft: XINPUT_BATTERY_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_battery_information)
- [Microsoft: Bluetooth GATT client](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/gatt-client)
- [Microsoft: taskbar notifications and TaskbarCreated](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar)
- [Microsoft: SHAppBarMessage](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shappbarmessage)
- [Microsoft: SetParent and DPI behavior](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent)
- [Microsoft: UI Automation threading](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)
