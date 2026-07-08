using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.CoreAudioApi;

namespace AudioMixerWin.Core;

public class AudioManager
    {
        public const string MasterVolumeProcessName = "System Volume";

        public static string GetDisplayName(string processName)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
                return Loc.Get("System_DisplayName");

            return processName.Length > 0
                ? char.ToUpperInvariant(processName[0]) + processName[1..]
                : processName;
        }

        private readonly MMDevice _device;
        private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _iconRgb565Cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (byte R, byte G, byte B)> _iconColorCache = new(StringComparer.OrdinalIgnoreCase);
        // Canonical 64x64 straight-alpha BGRA per process; persisted to disk so icons
        // survive restarts even when the source process is no longer running.
        private readonly Dictionary<string, byte[]> _iconBgraCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly (byte R, byte G, byte B) DefaultAccent = (0, 200, 255);

        public AudioManager()
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        public List<AudioSession> GetSessions()
        {
            var result = new List<AudioSession>();

            _device.AudioSessionManager.RefreshSessions();
            var sessions = _device.AudioSessionManager.Sessions;

            Console.WriteLine("Audio sessions detected:\n");

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];

                try
                {
                    var pid = (int)session.GetProcessID;

                    // Never offer the mixer itself as a controllable channel.
                    if (pid == Environment.ProcessId)
                        continue;

                    var process = Process.GetProcessById(pid);

                    result.Add(new AudioSession
                    {
                        ProcessName = process.ProcessName,
                        DisplayName = GetDisplayName(process.ProcessName),
                        Volume = session.SimpleAudioVolume.Volume,
                        IconSource = GetIconForProcess(process),
                    });

                    Console.WriteLine(
                        $"{process.ProcessName} - Volume: {session.SimpleAudioVolume.Volume}"
                    );
                }
                catch
                {
                    Console.WriteLine($"System session - Volume: {session.SimpleAudioVolume.Volume}");
                }
            }

            result.Add(new AudioSession
            {
                ProcessName = MasterVolumeProcessName,
                DisplayName = GetDisplayName(MasterVolumeProcessName),
                Volume = GetMasterVolume(),
            });

            return result
                .GroupBy(x => x!.ProcessName)
                .Select(g => g.First()!)
                .OrderBy(x => x.ProcessName)
                .ToList();;
        }

        // Public so the UI can recover an icon for an assigned-but-not-running app on restart.
        public ImageSource? GetIcon(string processName) => BuildIcon(processName, null);

        private ImageSource? GetIconForProcess(Process process) =>
            BuildIcon(process.ProcessName, process);

        private ImageSource? BuildIcon(string processName, Process? live)
        {
            if (_iconCache.TryGetValue(processName, out var cached))
                return cached;

            var bgra = GetIconBgra(processName, live);
            if (bgra is null)
                return null; // don't cache the miss — a later live launch should still populate

            var icon = BgraToWriteableBitmap(bgra);
            _iconCache[processName] = icon;
            return icon;
        }

        public byte[] GetIconRgb565(string processName)
        {
            if (_iconRgb565Cache.TryGetValue(processName, out var cached))
                return cached;

            var bgra = GetIconBgra(processName, null);
            if (bgra is null)
                return Array.Empty<byte>(); // don't cache the miss

            var result = Bgra64ToRgb565(bgra);
            _iconRgb565Cache[processName] = result;
            return result;
        }

        public (byte R, byte G, byte B) GetIconColor(string processName)
        {
            if (_iconColorCache.TryGetValue(processName, out var cached))
                return cached;

            var bgra = GetIconBgra(processName, null);
            if (bgra is null)
                return DefaultAccent; // don't cache the miss

            var result = ComputeDominantColor(bgra);
            _iconColorCache[processName] = result;
            return result;
        }

        // Resolves the canonical 64x64 BGRA buffer for a process. A fresh extraction from a
        // running process is persisted to disk; if the process isn't running, the last-known
        // icon is restored from disk so it survives restarts and app disassociation.
        private byte[]? GetIconBgra(string processName, Process? live)
        {
            if (_iconBgraCache.TryGetValue(processName, out var cached))
                return cached;

            byte[]? bgra = null;
            try
            {
                string? path = null;
                if (live is not null)
                {
                    path = live.MainModule?.FileName;
                }
                else
                {
                    var procs = Process.GetProcessesByName(processName);
                    if (procs.Length > 0)
                        path = procs[0].MainModule?.FileName;
                    foreach (var p in procs) p.Dispose();
                }

                if (path is not null)
                    bgra = ExtractBgraFromPath(path);
            }
            catch
            {
                // Some processes (elevated, different bitness, etc.) deny access to MainModule.
            }

            if (bgra is not null)
                IconStore.Save(processName, bgra);   // fresh extraction → persist
            else
                bgra = IconStore.Load(processName);   // not running → restore last-known

            // Only cache hits; a total miss stays uncached so a later live launch can populate it.
            if (bgra is not null)
                _iconBgraCache[processName] = bgra;
            return bgra;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int PrivateExtractIcons(
            string szFileName, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[] phicon, int[]? piconid, int nIcons, int flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // Requests the file's icon at exactly `size` px. Unlike ExtractAssociatedIcon
        // (which only ever hands back the small 32x32 shell icon), PrivateExtractIcons
        // picks the best-matching image from the icon group — modern apps ship a
        // 256x256 entry — and scales it to `size`. Returns null if none is extractable.
        private static System.Drawing.Bitmap? ExtractIconBitmap(string path, int size)
        {
            var handles = new IntPtr[1];
            int count = PrivateExtractIcons(path, 0, size, size, handles, null, 1, 0);
            if (count <= 0 || handles[0] == IntPtr.Zero) return null;
            try
            {
                // FromHandle does not own the handle; ToBitmap copies the pixels out,
                // so the icon (and handle) can be released immediately afterwards.
                using var icon = System.Drawing.Icon.FromHandle(handles[0]);
                return icon.ToBitmap();
            }
            finally { DestroyIcon(handles[0]); }
        }

        // Renders the file's associated icon to a 64x64 straight-alpha BGRA buffer
        // (Format32bppArgb memory order: B,G,R,A). Stride is exactly width*4 (no padding).
        private static byte[]? ExtractBgraFromPath(string path)
        {
            const int size = IconStore.IconSize;

            // Prefer a native size-64 extraction (downscaled from a large source =
            // crisp); fall back to the associated icon (a 32->64 upscale = softer) only
            // when the file exposes no icon group PrivateExtractIcons can read.
            System.Drawing.Bitmap? srcBmp = ExtractIconBitmap(path, size);
            if (srcBmp is null)
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                srcBmp = icon?.ToBitmap();
            }
            if (srcBmp is null) return null;

            using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode    = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode      = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode        = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality   = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(srcBmp, 0, 0, size, size);
            }
            srcBmp.Dispose();

            var data = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, size, size),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var bgra = new byte[data.Stride * size];
                Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
                return bgra;
            }
            finally { bmp.UnlockBits(data); }
        }

        // Coarse-histogram dominant color: skip transparent/near-gray/near-dark pixels,
        // bucket the rest (3 bits/channel), return the average of the most populated bucket.
        private static (byte R, byte G, byte B) ComputeDominantColor(byte[] argb)
        {
            var counts = new Dictionary<int, int>();
            var sums = new Dictionary<int, (long R, long G, long B, int N)>();
            int pixels = argb.Length / 4;

            for (int i = 0; i < pixels; i++)
            {
                int o = i * 4;
                byte b = argb[o], g = argb[o + 1], r = argb[o + 2], a = argb[o + 3];
                if (a < 128) continue;

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int sat = max == 0 ? 0 : (max - min) * 255 / max;
                if (sat < 40 || max < 40) continue; // skip gray and very dark pixels

                int key = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
                counts.TryGetValue(key, out int c);
                counts[key] = c + 1;
                sums.TryGetValue(key, out var s);
                sums[key] = (s.R + r, s.G + g, s.B + b, s.N + 1);
            }

            if (counts.Count == 0) return DefaultAccent;

            int best = counts.OrderByDescending(kv => kv.Value).First().Key;
            var bs = sums[best];
            return ((byte)(bs.R / bs.N), (byte)(bs.G / bs.N), (byte)(bs.B / bs.N));
        }

        // Converts the 64x64 straight-alpha BGRA buffer to RGB565 (LSB first), blended onto black.
        private static byte[] Bgra64ToRgb565(byte[] bgra)
        {
            const int size = IconStore.IconSize;
            var rgb565 = new byte[size * size * 2];
            for (int i = 0; i < size * size; i++)
            {
                int o = i * 4;
                byte b = bgra[o];
                byte grn = bgra[o + 1];
                byte r = bgra[o + 2];
                byte a = bgra[o + 3];
                // Alpha-blend onto black
                r = (byte)(r * a / 255);
                grn = (byte)(grn * a / 255);
                b = (byte)(b * a / 255);

                ushort px = (ushort)(((r >> 3) << 11) | ((grn >> 2) << 5) | (b >> 3));
                rgb565[i * 2]     = (byte)(px & 0xFF);   // LSB first
                rgb565[i * 2 + 1] = (byte)(px >> 8);
            }
            return rgb565;
        }

        // Builds a WinUI WriteableBitmap from the straight-alpha BGRA buffer. WinUI expects
        // premultiplied BGRA8, so multiply each channel by alpha.
        private static WriteableBitmap BgraToWriteableBitmap(byte[] bgra)
        {
            const int size = IconStore.IconSize;
            var premultiplied = new byte[bgra.Length];
            for (int o = 0; o < bgra.Length; o += 4)
            {
                byte a = bgra[o + 3];
                premultiplied[o]     = (byte)(bgra[o] * a / 255);
                premultiplied[o + 1] = (byte)(bgra[o + 1] * a / 255);
                premultiplied[o + 2] = (byte)(bgra[o + 2] * a / 255);
                premultiplied[o + 3] = a;
            }

            var writeableBitmap = new WriteableBitmap(size, size);
            using var pixelStream = writeableBitmap.PixelBuffer.AsStream();
            pixelStream.Write(premultiplied, 0, premultiplied.Length);
            return writeableBitmap;
        }
        
        public float GetMasterVolume() => _device.AudioEndpointVolume.MasterVolumeLevelScalar;

        public void SetMasterVolume(float volume) =>
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);

        public float GetVolume(string processName)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
                return GetMasterVolume();

            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];

                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(
                            processName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return session.SimpleAudioVolume.Volume;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return 0;
        }
        
        public void SetVolume(string processName, float volume)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
            {
                SetMasterVolume(volume);
                return;
            }

            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];

                try
                {
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    {
                        session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Session error: {ex.Message}");
                }
            }
        }

        public bool GetMute(string processName)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
                return _device.AudioEndpointVolume.Mute;

            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        return session.SimpleAudioVolume.Mute;
                }
                catch { }
            }

            return false;
        }

        public void SetMute(string processName, bool muted)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
            {
                _device.AudioEndpointVolume.Mute = muted;
                return;
            }

            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        session.SimpleAudioVolume.Mute = muted;
                }
                catch { }
            }
        }
    }