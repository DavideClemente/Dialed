# Dialed

> A hardware-controlled, per-application volume mixer for Windows.

Dialed pairs a small physical controller (an ESP32/Arduino with knobs or rotary
encoders, a push switch, and a round display) with a Windows desktop app so you can
set the volume of individual apps — Spotify, a game, your call — by turning a real
knob, mute with a press, and flip your default output device with a switch. The
controller's round display shows the app icon and volume of whatever you're adjusting.

<!-- Add screenshots to docs/images/ and reference them here, e.g.: -->
<!-- ![Dialed mixer window](docs/images/mixer.png) -->

## Features

- Per-app **and** master volume control from physical knobs / rotary encoders
- Mute an app with a knob press
- Switch the Windows default output device from a hardware switch (or in-app)
- Live app icons + volume mirrored to the controller's round GC9A01 display
- Idle-screen GIF library uploaded to the controller's flash
- Runs from the system tray; optional start-with-Windows
- English and Portuguese (PT-PT) localization

## Hardware

You need the companion controller: an ESP32 (or Arduino Nano variant) with
potentiometers or rotary encoders, a push switch, and a round GC9A01 display,
connected over USB serial. Wiring, the serial protocol, and the firmware sketches
live in **[`Arduino/README.md`](Arduino/README.md)**.

## Installation

1. Go to the [**Releases**](../../releases) page.
2. Download the latest `Dialed-Setup-<version>-x64.exe`.
3. Run it and follow the installer.

> **SmartScreen note:** the installer is not code-signed, so Windows SmartScreen may
> show *"Windows protected your PC"*. Click **More info → Run anyway** to proceed.
> (Code signing requires a paid certificate and isn't set up for this project.)

Requires 64-bit Windows 10 version 1809 (build 17763) or newer.

## Usage

1. Plug in the controller over USB.
2. Launch Dialed; open **Settings** and pick the controller's COM port.
3. On the mixer page, assign an app to each knob.
4. Turn a knob to set that app's volume; press to mute; use the switch to change the
   output device.

## Build from source

Prerequisites: **.NET 8 SDK** and the **Windows App SDK** tooling (Visual Studio 2022
with the "Windows App SDK" / WinUI workload, or the standalone SDK).

```powershell
dotnet build Dialed.csproj -p:Platform=x64 -c Debug
```

`-p:Platform` is **required** — there is no `AnyCPU` platform (valid values: `x64`,
`x86`, `ARM64`). The app is self-contained, so builds bundle the Windows App SDK
runtime and need a matching `RuntimeIdentifier` (derived automatically from the
platform).

To produce a release publish folder:

```powershell
dotnet publish Dialed.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```

## Architecture

Dialed is a single MVVM WinUI 3 app. NAudio's Core Audio API applies per-app volumes
to Windows audio sessions; a symmetric, stringly-typed serial protocol talks to the
controller firmware. For a full tour of the code, see
**[`CLAUDE.md`](CLAUDE.md)**.

## Releasing

Push a version tag and GitHub Actions builds the installer and publishes a Release:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The [`Release`](.github/workflows/release.yml) workflow publishes the x64
self-contained build, compiles the Inno Setup installer, and attaches
`Dialed-Setup-<version>-x64.exe` to the GitHub Release.

## Contributing

Contributions are welcome — issues and pull requests both. By contributing you agree
your changes are licensed under the project's non-commercial license (below).

## License

Licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE)**. In short: you
may use, modify, and share Dialed for any **non-commercial** purpose, but you may not
sell it or use it for commercial advantage. See [`LICENSE`](LICENSE) for the full
terms.
