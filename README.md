# BT Device Battery

Battery levels for your Bluetooth devices, right in the Windows tray. Lightweight, portable, no installer: one exe, one icon, live percentages with notifications and a Fluent settings window.

Single tray icon showing the most critical connected device (green ≥ 50%, amber 20–49%, red < 20%); per-device menu, rich tooltip, connect/disconnect detection within a second, and configurable low-battery notifications.

## Supported devices

| Device type | Battery | Notes |
|---|---|---|
| BLE devices with the standard Battery Service (keyboards, mice, sensors) | ✅ | Read from the same PnP property Windows Settings uses — no extra connection is opened |
| Classic headsets / earbuds (HFP, incl. dual-mode) | ✅ | Battery appears while connected |
| DualSense / DualShock 4 controllers | ✅ | Via the controller's HID report — the byte Windows never surfaces |
| Devices exposing nothing to Windows (vendor protocols) | ⚠️ | Listed in the menu, but without a percentage — there is nothing to read |
| Devices connected via proprietary USB dongles (Logitech Unifying, etc.) | ❌ | Not Bluetooth: Windows exposes no battery for them |

Tested with a Logitech MX Keys S (BLE keyboard), Philips TAT3509 earbuds (dual-mode HFP/A2DP) and a DualSense controller, on Windows 11 with a TP-Link UB500 Plus adapter.

### The controller caveat

The DualSense battery lives in its "enhanced reports". Switching the controller to that mode is a firmware-level protocol change that **breaks DirectInput apps until the controller is power-cycled** — the same trade-off documented in SDL. BT Device Battery therefore never forces it: the battery shows as soon as something else (Steam, SDL) has switched the pad, and an opt-in setting exists for forcing it when you accept the trade-off.

## Features

- Single tray icon, threshold-colored, drawn at the tray's actual pixel size
- Per-device menu with live percentages, glyph and custom name; connected devices can be disconnected straight from the tray (pairing kept)
- Instant on/off detection: device-interface notifications and radio state, not just polling
- Toast notifications: low battery (with re-arm logic), connect/disconnect — per-device mute
- Fluent settings window: thresholds, poll intervals, per-device overrides, run at startup

## Build & run

Requires the .NET 10 SDK, Windows 10 1809+ (developed on 11).

```
dotnet run --project src/BTDeviceBattery/BTDeviceBattery.csproj
```

Publish the portable single-file exe (~70 MB, self-contained, no installer):

```
dotnet publish src/BTDeviceBattery/BTDeviceBattery.csproj -c Release
```

Output: `src/BTDeviceBattery/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/BTDeviceBattery.exe`

## How battery reading works

There is no single Windows API for Bluetooth batteries, so the app merges three sources:

1. **PnP `BatteryLevel` property** — the undocumented device property Settings uses, read via cfgmgr32 from BTHENUM/BTHHFENUM/BTHLE/HID devnodes. Covers classic headsets and BLE devices alike.
2. **GATT Battery Service** (`0x180F` / `0x2A19`) — cached-first reads via WinRT, with notification subscriptions where supported.
3. **Controller HID reports** — DualSense and DualShock 4 never surface a battery to Windows, so the app reads their input report with a one-shot `GetInputReport` that doesn't steal packets from the input stream games are consuming.

Devices are merged and deduplicated by MAC across all sources; dual-mode devices appear once. Connection state comes from three signals: devnode presence, the radio state (the quick-settings toggle powers the radio off *without removing any devnode*, so presence alone lies), and the Bluetooth stack's own `fConnected`. Paired-but-off devices are listed from the stack's registry, so they stay in the menu as "off".

## Settings

Stored in `%APPDATA%\BTDeviceBattery\settings.json`, written atomically. Per-device customizations are keyed by device address, so they survive reconnections and reboots.

## Troubleshooting

`BTDeviceBattery.exe --dump` prints the raw PnP scan, connect states, BLE interfaces and HID report data — to the console (when launched from one) and to `%TEMP%\btdevicebattery-dump.txt`. A rolling state log lives at `%APPDATA%\BTDeviceBattery\log.txt`.