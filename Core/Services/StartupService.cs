using System;
using Microsoft.Win32;

namespace Dialed.Core.Services;

/// <summary>
/// Toggles "start with Windows" for this unpackaged app via the per-user Run key
/// (HKCU\...\CurrentVersion\Run). The registry value is the source of truth — not
/// settings.json — so a user who removes it via Task Manager's Startup tab is
/// respected on the next launch. The stored command includes <see cref="MinimizedArg"/>
/// so an auto-start launch comes up hidden in the tray rather than popping a window.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Dialed";

    /// <summary>Command-line flag honoured by <c>App.OnLaunched</c> to start hidden in the tray.</summary>
    public const string MinimizedArg = "--minimized";

    // Environment.ProcessPath is the actual apphost .exe for a self-contained app —
    // exactly what the Run key must invoke. Quoted to survive spaces in the path.
    private static string? Command =>
        Environment.ProcessPath is { Length: > 0 } exe ? $"\"{exe}\" {MinimizedArg}" : null;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            if (enabled && Command is { } command)
                key.SetValue(ValueName, command);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
