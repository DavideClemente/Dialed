using System;
using NAudio.CoreAudioApi;

namespace Dialed.Core;

/// <summary>
/// Hands out the machine's default capture endpoint, re-resolving it on each access
/// so the microphone channel follows the active input device instead of pinning to
/// the one Windows had when the app started.
/// </summary>
public sealed class DefaultCaptureEndpoint
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;

    public MMDevice? Current
    {
        get
        {
            MMDevice current;
            try
            {
                current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            }
            catch
            {
                return _device;
            }

            if (_device is not null && string.Equals(current.ID, _device.ID, StringComparison.Ordinal))
            {
                current.Dispose();
                return _device;
            }

            _device?.Dispose();
            _device = current;

            try
            {
                _device.AudioSessionManager.RefreshSessions();
            }
            catch { }

            return _device;
        }
    }
}
