# Contributing to Dialed

Thanks for your interest in contributing to Dialed! This document provides guidelines for submitting bug reports, feature requests, and pull requests.

## Getting Started

### Prerequisites
- **.NET 8 SDK** — [download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Windows 10/11** with Windows App SDK
- **Visual Studio 2022** or **JetBrains Rider** (optional, but recommended for WinUI development)
- For firmware changes: **Arduino IDE** or **VS Code + Arduino extension**

### Build & Run

```bash
# Clone the repository
git clone https://github.com/DavideClemente/Dialed.git
cd Dialed

# Build for x64
dotnet build Dialed.csproj -p:Platform=x64 -c Debug

# Run
dotnet run -p:Platform=x64
```

**Note:** Dialed is a WinUI 3 app (.NET 8) with no `AnyCPU` target. Platform must always be specified (`x86`, `x64`, or `ARM64`).

See `CLAUDE.md` for more technical details on architecture, conventions, and project structure.

## How to Contribute

### 1. Fork & Branch

1. Fork the repository on GitHub
2. Create a feature branch: `git checkout -b fix/your-fix-name` or `feature/your-feature-name`
3. Make your changes
4. Commit with a clear message: `git commit -m "fix: describe the change"`
5. Push to your fork: `git push origin your-branch-name`
6. Open a pull request to `master`

### 2. Code Style

- Follow C# naming conventions (PascalCase for classes, camelCase for locals)
- Use `[ObservableProperty]` for MVVM properties (via `CommunityToolkit.Mvvm`)
- Localize user-facing strings: use `Loc.Get(...)` and add entries to `Strings/en-US/Resources.resw` and `Strings/pt-PT/Resources.resw`
- No `.editorconfig` yet — match the existing code style

### 3. Serial Protocol

If you modify the serial protocol between app and hardware:
- Update **both** `SerialManager.cs` (app) and `Arduino/mixer/` (firmware)
- Bump `FW_VERSION` in `Arduino/mixer/version.h`
- Document the protocol change in your PR

### 4. Testing

- Run the app manually to test your changes
- Test on the target platform if possible (`x86`, `x64`, `ARM64`)
- For audio/output changes, test with the hardware controller connected

## Reporting Issues

When reporting a bug, include:
- Windows version and build number
- App version
- Steps to reproduce
- Actual vs. expected behavior
- Screenshots or logs if applicable

## Architecture Overview

Dialed is a single MVVM application. Key components:

- **`MainWindow.xaml`** — root window, navigation, tray icon
- **`Core/Views/`** — four pages (Mixer, Idle Screen, Output, Settings)
- **`Core/ViewModels/`** — view models (MainViewModel, ChannelViewModel, OutputViewModel, IdleScreenViewModel)
- **`Core/Services/`** — audio, serial, settings, localization, GIF encoding
- **`Arduino/`** — ESP32/Nano firmware for the hardware controller

Full architecture docs are in `CLAUDE.md`.

## Pull Request Process

1. Ensure your branch is up-to-date with `master`
2. Add a descriptive title and summary to your PR
3. Link any related issues
4. Wait for review and feedback
5. Address review comments with new commits (no force-push)
6. Your PR must be approved by at least one reviewer before it can be merged
7. Once approved, the maintainer will merge using **squash commits** to keep master's history clean

## Questions?

- Check `CLAUDE.md` for technical details
- Open an issue if something is unclear
- Discussions are welcome in PRs

Thanks for contributing! 🎉
