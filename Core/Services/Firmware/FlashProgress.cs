namespace Dialed.Core.Services.Firmware;

/// <summary>A flashing progress update: 0..100 percent plus a localized status line.</summary>
public readonly record struct FlashProgress(int Percent, string StatusText);
