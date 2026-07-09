# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Dialed is a WinUI 3 desktop app (.NET 8, Windows App SDK) that acts as a hardware-controlled
per-application volume mixer for Windows. The flow:

1. A physical controller (an ESP32/Arduino with potentiometers or rotary encoders, a push switch, and a
   round GC9A01 display — see `Arduino/`) connects over a serial/COM port.
2. Controller → app: it sends lines like `knob1:0.42` (volume), `knob1:up`/`down` (encoder deltas),
   `knob1:press` (mute toggle), and `switch:0`/`switch:1` (output toggle).
3. App → controller: the app pushes assignments (`assign:`), app icons (`icon:`, base64 RGB565), echoed
   volume/mute (`vol:`/`mute:`), config (`cfg:`/`config:`), and streams idle-screen GIFs (`gif:*`).
4. Volumes/mutes are applied to the matching process's Windows audio session via NAudio's Core Audio
   API (`SimpleAudioVolume`); the master channel uses `AudioEndpointVolume`.

The app is a functional, wired-up product — not a prototype. It runs from the tray, persists settings,
localizes to EN/PT, controls per-app + master volume, switches the default output device, and manages an
idle-screen GIF library uploaded to the controller's flash.

## Build & Run

- Target framework: `net8.0-windows10.0.19041.0`. Platforms are `x86`, `x64`, `ARM64` — there is **no
  AnyCPU**, so `-p:Platform` must always be passed.
- Build: `dotnet build Dialed.csproj -p:Platform=x64 -c Debug`
- The app is **self-contained** (`WindowsAppSDKSelfContained` + `SelfContained`), so builds/publishes need
  a `RuntimeIdentifier`. The csproj derives one from `$(Platform)` when none is passed (so VS/Rider F5
  works); without it the app crashes at startup with `REGDB_E_CLASSNOTREG`.
- `BuiltInComInteropSupport=true` is required — NAudio's `MMDeviceEnumerator` uses built-in COM interop,
  which the trimmer would otherwise switch off. Trimming is disabled (`PublishTrimmed=false`) because the
  Core Audio `[ComImport]` interfaces are not trim-safe.
- Run profiles (`Properties/launchSettings.json`): `Dialed (Unpackaged)` and `Dialed
  (Package)` (MSIX, `EnableMsixTooling=true`). The app is unpackaged by default (`WindowsPackageType=None`).
- There is no test project and no `.editorconfig`.

Key NuGet packages: `CommunityToolkit.Mvvm`, `NAudio` (session volume), `AudioSwitcher.AudioApi.CoreAudio`
(setting the default output device — NAudio can't), `H.NotifyIcon.WinUI` (tray), `System.IO.Ports`,
`System.Management` (WMI COM-port descriptions), `System.Drawing.Common` (icon/GIF pixel work).

## Architecture

Single MVVM app. `App.xaml.cs` (namespace `Dialed`) applies the saved language override, then
`OnLaunched` creates `MainWindow` (root `Dialed` namespace, at repo root — **not** under `Core/`).
`MainWindow` hosts a `NavigationView` + `Frame` switching between four pages, plus a tray icon, custom
title bar, resizable nav-pane splitter, and a "minimize to tray vs quit" close dialog.

- **`MainWindow.xaml(.cs)`** — owns the single `MainViewModel`, builds the four pages, wires the tray menu
  (which can navigate to any page), and does Win32 interop for the window/tray icon and foreground
  restore. Closing the window is intercepted (`OnWindowClosing`) to offer minimize-to-tray.

- **Pages** (`Core/Views/`): `MainPage` (mixer grid), `IdleScreenPage`, `OutputPage`, `SettingsPage`. Each
  takes its view model via constructor.

- **View models** (`Core/ViewModels/`):
  - `MainViewModel` — the hub. Owns `AudioManager`, `OutputManager`, `SerialManager`, the refresh
    `DispatcherTimer`, all persisted settings (as `[ObservableProperty]` mirrors that save on change), the
    `Channels`/`AvailableSessions`/`HiddenProcesses` collections, and the serial event handlers
    (`OnKnobChanged`/`OnKnobDelta`/`OnKnobPressed`/`OnSwitchChanged`). It owns the idle-GIF upload flow
    (`PushIdleGifAsync`) and exposes child VMs `Output` and (lazily via `InitIdleScreen`) `IdleScreen`.
  - `ChannelViewModel` — one mixer channel: `AppName`, `Volume`, `IsMuted`, `IconSource`, `IsOffline`
    (assigned app not running), knob assignment. Writing `Volume`/`IsMuted` calls back into `AudioManager`.
  - `OutputViewModel` — two output "positions" (A/B) each bound to a playback device, with mutually
    exclusive picker lists; the hardware switch or a tap on a card sets the Windows default device.
  - `IdleScreenViewModel` — the GIF library: import, select-active (auto-uploads to the controller),
    delete, and a usage/upload-status readout.
  - `IdleGifViewModel` — wraps one `IdleGifConfig` for the library UI.

- **Core services / logic** (`Core/`):
  - `AudioManager.cs` — wraps NAudio against the default **render** endpoint. `GetSessions()` enumerates
    sessions, resolves each to a process, dedupes, and appends a synthetic master channel
    (`MasterVolumeProcessName = "System Volume"`). Also extracts each app's icon to a canonical 64×64 BGRA
    buffer (persisted via `IconStore`), and derives a dominant accent color and an RGB565 buffer for the
    hardware display. `GetVolume`/`SetVolume`/`GetMute`/`SetMute` special-case the master channel to
    `AudioEndpointVolume`.
  - `SerialManager.cs` — owns the `SerialPort`. Parses inbound lines into `KnobChanged`/`KnobDelta`/
    `KnobPressed`/`SwitchChanged` events, and sends outbound `assign:`/`icon:`/`vol:`/`mute:`/`cfg:`/
    `config:` lines. Implements the ACK-paced `gif:*` upload protocol (chunked base64, one chunk in flight)
    with `IdleGifUploadException` carrying user-readable, localized failure reasons.
  - `OutputManager.cs` — uses `AudioSwitcher`'s `CoreAudioController` to list playback devices and set the
    system default (multimedia + communications roles).
  - `Core/Services/`: `SettingsService` (JSON at `%LOCALAPPDATA%\Dialed\settings.json`),
    `AppSettings` (the persisted model), `IconStore` (per-process `.bgra` icon cache),
    `IdleGifLibraryService` (copies imported media into `%LOCALAPPDATA%\Dialed\idle-gifs\`),
    `GifFrameEncoder` (decodes a GIF/image to square RGB565 frames for the GC9A01, resampling to a frame
    cap / max fps), and the localization stack `Loc` + `LocExtension` (`{loc:Loc}` markup) + `LocalizationService`.

- **Models** (`Core/Models/`): `AudioSession`, `ChannelConfig` (persisted knob↔app), `IdleGifConfig`,
  `InputMode` (`Potentiometer`/`RotaryEncoder`), `OutputDevice`, `ComPortInfo` (WMI-enriched port list).

- **Controls** (`Core/Controls/`): `KnobCard` (per-channel card) and `AppPickerDialog` (choose the app for
  a channel). Converters live in `Core/Converters/` and are registered in `App.xaml`.

- **Firmware** (`Arduino/`): the ESP32 `mixer` sketch (and an Arduino Nano variant) implementing the other
  side of the serial protocol, including the idle-GIF flash storage. `Arduino/README.md` has details.

## Conventions & gotchas

- **Serial protocol is symmetric and stringly-typed.** When adding a message, update both
  `SerialManager` and the matching firmware in `Arduino/mixer/`. Knob IDs are 1-based on the wire
  (`knob1`) but 0-based in `ChannelConfig.KnobIndex`/`Channels`.
- **Don't echo `vol:` on connect or assignment.** A `vol:` line makes the device switch to its volume
  screen; it should only appear on real knob interaction. `SyncChannel` deliberately sends only
  `assign:`/`icon:`. See the comment in `MainViewModel.SyncChannel`.
- **All user-facing strings go through `Loc.Get(...)`** and live in `Strings/en-US/Resources.resw` +
  `Strings/pt-PT/Resources.resw`. The language override is applied in the `App` ctor before any UI exists;
  it's unpackaged so `PrimaryLanguageOverride` isn't persisted by the OS — it's stored in settings and
  re-applied each launch, and a language change surfaces a restart prompt rather than re-theming live.
- **Settings persist on every change** — the `[ObservableProperty]` `partial void On…Changed` handlers in
  `MainViewModel` write through `SettingsService.Save`. There's no explicit "save" button.
- `AudioManager` targets only the default **render** endpoint and silently ignores sessions whose process
  can't be resolved (system sounds, elevated/cross-bitness processes).
- Session and output-device lists are **polled** on a `DispatcherTimer` (default 2s), not event-driven.
