#pragma once

// Single source of truth for firmware version. Bump FW_VERSION (SemVer) whenever
// the mixer/ firmware changes; the build script (Arduino/tools/build-firmware.ps1)
// reads these to name the merged .bin and fill manifest.json, and the app compares
// this (reported over serial as "fw:<board>:<version>") against the bundled version.
#define FW_BOARD   "esp32-mixer"
#define FW_VERSION "1.1.1"
