using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dialed.Core.Services.Firmware;

/// <summary>
/// The bundled firmware descriptor (firmware/&lt;board&gt;/manifest.json), produced by
/// Arduino/tools/build-firmware.ps1. Version/board/sha256 are derived from
/// Arduino/mixer/version.h + the merged .bin, so they never drift from the device.
/// </summary>
public sealed record FirmwareManifest(
    [property: JsonPropertyName("board")] string Board,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("bin")] string Bin,
    [property: JsonPropertyName("sha256")] string Sha256)
{
    /// <summary>Loads and validates a manifest, or returns null if missing/malformed.</summary>
    public static FirmwareManifest? TryLoad(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            var json = File.ReadAllText(manifestPath);
            var m = JsonSerializer.Deserialize<FirmwareManifest>(json);
            if (m is null || string.IsNullOrWhiteSpace(m.Board) ||
                string.IsNullOrWhiteSpace(m.Version) || string.IsNullOrWhiteSpace(m.Bin))
                return null;
            return m;
        }
        catch { return null; }
    }

    /// <summary>True if the file at <paramref name="binPath"/> matches this manifest's sha256.</summary>
    public bool VerifyBin(string binPath)
    {
        try
        {
            if (!File.Exists(binPath)) return false;
            using var stream = File.OpenRead(binPath);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return string.Equals(hash, Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
