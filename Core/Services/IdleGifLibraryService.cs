using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AudioMixerWin.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace AudioMixerWin.Core.Services;

/// <summary>
/// Owns the idle-screen GIF cache folder (one copied .gif file per library
/// entry). Mirrors the defensive, folder-owning style of IconStore: IO errors
/// are swallowed so cache problems never surface as exceptions in the UI.
/// </summary>
public class IdleGifLibraryService
{
    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioMixerWin", "idle-gifs");

    /// <summary>
    /// Copies a picked GIF into the cache under a new GUID filename and reads
    /// its dimensions. Returns null if the file can't be read/decoded.
    /// </summary>
    public async Task<IdleGifConfig?> ImportAsync(StorageFile file)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var id = Guid.NewGuid().ToString("N");
            var fileName = id + ".gif";
            var destPath = Path.Combine(Directory, fileName);

            using (var src = await file.OpenStreamForReadAsync())
            using (var dst = File.Create(destPath))
                await src.CopyToAsync(dst);

            int width = 0, height = 0;
            try
            {
                using var stream = File.OpenRead(destPath);
                using var ras = stream.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(ras);
                width = (int)decoder.PixelWidth;
                height = (int)decoder.PixelHeight;
            }
            catch
            {
                // Undecodable file: reject the import and clean up the copy.
                try { File.Delete(destPath); } catch { }
                return null;
            }

            return new IdleGifConfig
            {
                Id = id,
                FileName = fileName,
                OriginalName = file.Name,
                AddedUtc = DateTime.UtcNow,
                SizeBytes = new FileInfo(destPath).Length,
                PixelWidth = width,
                PixelHeight = height,
            };
        }
        catch
        {
            return null;
        }
    }

    public void Delete(IdleGifConfig config)
    {
        try
        {
            var path = PathFor(config);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    public string PathFor(IdleGifConfig config) => Path.Combine(Directory, config.FileName);

    public bool Exists(IdleGifConfig config)
    {
        try { return File.Exists(PathFor(config)); }
        catch { return false; }
    }

    public long TotalSizeBytes(IEnumerable<IdleGifConfig> configs)
    {
        long total = 0;
        foreach (var c in configs)
        {
            try
            {
                var path = PathFor(c);
                if (File.Exists(path))
                    total += new FileInfo(path).Length;
            }
            catch { }
        }
        return total;
    }
}
