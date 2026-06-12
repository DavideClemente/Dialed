# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AudioMixerWin is a WinUI 3 desktop app (.NET 8, Windows App SDK) that acts as a hardware-controlled
per-application volume mixer for Windows. The intended flow:

1. A physical controller (e.g. an Arduino with potentiometers/knobs) is connected over a serial/COM port.
2. It sends lines like `Spotify:0.42` (app name + float volume 0–1, invariant culture).
3. The app parses these lines and sets the volume of the matching process's Windows audio session
   via NAudio's Core Audio API (`ISimpleAudioVolume`).

## Build & Run

- Target framework: `net8.0-windows10.0.19041.0`. Platforms are `x86`, `x64`, `ARM64` — there is **no
  AnyCPU**, so `-p:Platform` must always be passed.
- Build: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
- Run profiles (`Properties/launchSettings.json`, used from Visual Studio/Rider):
  - `AudioMixerWin (Unpackaged)` — runs the app directly as a project.
  - `AudioMixerWin (Package)` — runs via MSIX packaging (`EnableMsixTooling=true`).
- There is no test project and no `.editorconfig` in the repo.

## Architecture

The codebase currently contains **two parallel UIs** at different stages of completion:

- **Active entry point**: `App.xaml.cs` (namespace `AudioMixerWin`) creates the root
  `MainWindow.xaml`/`.xaml.cs` (also namespace `AudioMixerWin`). This is currently just a static
  2x2 `Grid` of bordered cards with hardcoded `TextBlock`s ("Spotify", "Discord", "Game A", "Game B")
  — no audio or serial logic is wired into it yet.

- **In-progress MVVM rewrite** under `Core/`, not yet referenced from `App.xaml.cs`:
  - `Core/Views/MainWindow.xaml(.cs)` — meant to host `Core/Controls/KnobCard` instances bound to
    `Core/ViewModels/MainViewModel`.
  - `Core/ViewModels/MainViewModel.cs` — `ObservableObject` (CommunityToolkit.Mvvm) holding an
    `ObservableCollection<ChannelViewModel>`, currently seeded with hardcoded channel names
    (Spotify, Discord, Chrome, Game).
  - `Core/ViewModels/ChannelViewModel.cs` — `ObservableObject` with `AppName` + `[ObservableProperty] Volume`.
  - `Core/Controls/KnobCard.xaml` — per-channel card control (name, knob label, progress bar, % text).

- **Core logic classes** (`Core/`), written but not yet connected to either UI:
  - `AudioManager.cs` — wraps `NAudio.CoreAudioApi.MMDeviceEnumerator` against the default render
    device. `GetSessions()` enumerates active audio sessions, resolves each to a process name via
    `Process.GetProcessById`, dedupes by process name, and returns `AudioSession` models.
    `GetVolume`/`SetVolume` look up a session by process name (case-insensitive) and read/write
    `SimpleAudioVolume.Volume` (clamped to 0–1 on set).
  - `SerialManager.cs` — opens a `SerialPort`, and on each received line splits on `:` into
    `app:volume`, parsing volume as `float` with `CultureInfo.InvariantCulture`, then calls
    `AudioManager.SetVolume(app, volume)`.
  - `Core/Models/AudioSession.cs` — `INotifyPropertyChanged` model (`ProcessName`, `DisplayName`, `Volume`)
    returned by `AudioManager`.
  - `Core/Models/MixerChannel.cs` — older hand-rolled `INotifyPropertyChanged` model (`AppName`, `Volume`),
    overlaps conceptually with the newer `ChannelViewModel`/`AudioSession`.
  - `Core/Models/KnobMapping.cs` — intended to map a physical `KnobId` to an `AudioSession`/app name
    and target volume; not yet used by any other class.

## Known Issues / State to Be Aware Of

- **The project does not currently build** (`dotnet build -p:Platform=x64` fails with CS1002) because
  `Core/Views/MainWindow.xaml.cs` has an incomplete statement (`DataContext` on its own line with no
  assignment/terminator).
- `Core/Views/MainWindow.xaml` declares `x:Class="AudioMixerWin.MainWindowWindow"`, but the code-behind
  class is `AudioMixerWin.Core.Views.MainWindow` — class name/namespace mismatch with the XAML root.
- `Core/Controls/KnobCard.xaml` declares `x:Class="AudioMixer.WinUI.Controls.KnobCard"` (wrong root
  namespace for this project — should be under `AudioMixerWin.Core.Controls`) and has no code-behind
  `.cs` file.
- There are two overlapping "channel" abstractions (`Core/Models/MixerChannel.cs` vs
  `Core/ViewModels/ChannelViewModel.cs` + `Core/Models/AudioSession.cs`). When continuing the MVVM
  rewrite, consolidate on one rather than extending both.
- `AudioManager` only targets the default render (playback) audio endpoint and silently ignores
  sessions whose process can't be resolved (e.g. system sounds).
