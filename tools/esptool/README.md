# esptool.exe (not committed as a binary)

This folder holds the standalone Windows build of Espressif's `esptool`, used by
`Arduino/tools/build-firmware.ps1` to merge the compiled bootloader/partitions/app
binaries into a single flashable image, and (in later tasks) by the app itself to
flash a connected ESP32.

The `.exe` is not checked into source control. To set it up:

1. Download `esptool-vX.Y.Z-windows-amd64.zip` from
   https://github.com/espressif/esptool/releases, where `X.Y.Z` is the version
   currently recorded in `tools/esptool/VERSION.txt` (the build script and app
   use esptool **v5** hyphenated command syntax, e.g. `write-flash` / `merge-bin`).
2. Extract it and copy `esptool.exe` to `tools/esptool/esptool.exe` (this folder).
3. Record the version you downloaded in `tools/esptool/VERSION.txt` as a bare
   SemVer on a single line, e.g. `5.3.1` — no `v` prefix. The release workflow
   parses this file to download the matching `esptool.exe`, so the format is
   load-bearing.

Verify with:

```
tools\esptool\esptool.exe version
```

Until `esptool.exe` is placed here, `dotnet build`/`publish` still succeed (the
csproj's `Content Include` for it is conditional on the file existing), and the
app's firmware-flashing UI degrades gracefully rather than failing to build.

## CI

`.github/workflows/release.yml` downloads
`esptool-v<VERSION.txt>-windows-amd64.zip` from Espressif's GitHub releases on
every release build, so the exe does not need to be committed. Bumping the pin
is a one-line edit to `VERSION.txt` — do it here and locally at the same time so
CI and your machine stay on the same version.
