using System;
using System.IO;

namespace Dialed.Core.Services.Firmware;

/// <summary>
/// Resolves the bundled flashing assets (esptool + per-board firmware) relative to
/// the running exe. The Esp32 flasher property is added in the Esp32Flasher task.
/// </summary>
public sealed class FirmwareCatalog
{
    private readonly string _baseDir;

    public FirmwareCatalog(string baseDir) => _baseDir = baseDir;

    public static string DefaultBaseDir => AppContext.BaseDirectory;

    public string EsptoolPath => Path.Combine(_baseDir, "tools", "esptool", "esptool.exe");

    public string Esp32ManifestPath => Path.Combine(_baseDir, "firmware", "esp32", "manifest.json");

    public FirmwareManifest? Esp32Manifest => FirmwareManifest.TryLoad(Esp32ManifestPath);

    public string Esp32BinPath(FirmwareManifest m) => Path.Combine(_baseDir, "firmware", "esp32", m.Bin);
}
