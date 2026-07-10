using System;
using System.IO;
using System.Text.Json;

namespace Dialed.Core.Services;

public static class SettingsService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dialed", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                    return settings;
            }
        }
        catch
        {
            // Settings save on every change, so returning defaults means the next
            // change overwrites the file. Keep the unreadable original so the
            // user's config stays recoverable.
            try { File.Copy(FilePath, FilePath + ".corrupt", overwrite: true); }
            catch { }
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write-then-rename so a crash or power loss mid-write can never
            // leave settings.json truncated.
            var tmpPath = FilePath + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(settings));
            File.Move(tmpPath, FilePath, overwrite: true);
        }
        catch { }
    }
}
