using System;

namespace Dialed.Core.Services.Firmware;

/// <summary>
/// Carries a user-readable, localized reason a flash failed (mirrors the
/// IdleGifUploadException pattern). The message is meant to be shown verbatim.
/// </summary>
public sealed class FlashException : Exception
{
    public FlashException(string message) : base(message) { }
}
