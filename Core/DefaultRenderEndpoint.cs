using System;
using NAudio.CoreAudioApi;

namespace Dialed.Core;

/// <summary>
/// Hands out the endpoint Windows is currently rendering to, re-resolving it on
/// every access and swapping the cached <see cref="MMDevice"/> when the default
/// output changes.
///
/// The default output changes often: the user picks another device, a headset is
/// plugged in — and Dialed itself reassigns it from the hardware output switch
/// (<see cref="OutputManager.SetDefault"/>). An endpoint resolved once at startup
/// keeps driving the device it was resolved from, so the master ("System") channel
/// moves a volume nobody hears and sessions playing on the new device never show up
/// in the mixer.
///
/// Deliberately not thread-safe: <c>MainViewModel</c> marshals every serial event
/// onto the dispatcher, so all access happens on the UI thread.
/// </summary>
public sealed class DefaultRenderEndpoint
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;

    /// <summary>
    /// The current default render endpoint, or null while the machine has no active
    /// output at all (every device disabled or unplugged) and none was ever resolved.
    /// </summary>
    public MMDevice? Current
    {
        get
        {
            MMDevice current;
            try
            {
                current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch
            {
                // No active render endpoint right now. Keep handing out the last-known
                // one rather than tearing the mixer down over a transient gap (a device
                // being reset re-appears within a poll or two).
                return _device;
            }

            if (_device is not null && string.Equals(current.ID, _device.ID, StringComparison.Ordinal))
            {
                current.Dispose();
                return _device;
            }

            _device?.Dispose();
            _device = current;

            // A freshly resolved MMDevice starts with an empty session list, and only
            // GetSessions() refreshes — GetVolume/SetVolume/GetMute/SetMute read
            // Sessions as-is. Prime it so the first call after a switch isn't blind.
            try { _device.AudioSessionManager.RefreshSessions(); }
            catch { /* endpoint vanished again between resolve and refresh */ }

            return _device;
        }
    }
}
