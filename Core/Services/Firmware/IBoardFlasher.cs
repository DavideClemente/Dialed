using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dialed.Core.Services.Firmware;

/// <summary>
/// Flashes one board type. The extensibility seam for adding Nano / other ESP32
/// variants later; only Esp32Flasher ships today.
/// </summary>
public interface IBoardFlasher
{
    string BoardId { get; }          // e.g. "esp32-mixer"
    string DisplayName { get; }      // e.g. "ESP32 (round display)"
    string FirmwareVersion { get; }  // bundled version, from the manifest

    /// <summary>
    /// Flashes the bundled firmware to the board on <paramref name="comPort"/>.
    /// Throws <see cref="FlashException"/> with a localized reason on failure.
    /// The caller must have released the serial port first.
    /// </summary>
    Task FlashAsync(string comPort, IProgress<FlashProgress> progress, CancellationToken ct);
}
