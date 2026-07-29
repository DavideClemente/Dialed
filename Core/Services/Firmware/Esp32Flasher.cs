using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dialed.Core.Services;

namespace Dialed.Core.Services.Firmware;

/// <summary>
/// Flashes a classic ESP32 (WROOM) by shelling out to the bundled esptool.exe.
/// The merged image is written at offset 0x0. --chip esp32 makes esptool refuse a
/// non-classic ESP32 (S3/C3/…) with a "Wrong chip" style error we surface clearly.
/// </summary>
public sealed partial class Esp32Flasher : IBoardFlasher
{
    private readonly string _esptoolPath;
    private readonly string _binPath;
    private const int FlashBaud = 921600;

    public string BoardId { get; }
    public string DisplayName { get; }
    public string FirmwareVersion { get; }

    public Esp32Flasher(FirmwareManifest manifest, string esptoolPath, string binPath)
    {
        BoardId = manifest.Board;
        FirmwareVersion = manifest.Version;
        DisplayName = Loc.Get("Flash_Board_Esp32");
        _esptoolPath = esptoolPath;
        _binPath = binPath;
    }

    [GeneratedRegex(@"\((\d+)\s*%\)")]
    private static partial Regex ProgressRegex();

    /// <summary>Extracts a 0..100 percent from an esptool "Writing at 0x… (NN %)" line, or -1.</summary>
    internal static int ParsePercent(string line)
    {
        var m = ProgressRegex().Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) ? Math.Clamp(p, 0, 100) : -1;
    }

    /// <summary>Maps an esptool exit/output to a localized FlashException, or null on success.</summary>
    internal static string? ClassifyFailure(int exitCode, string output)
    {
        if (exitCode == 0) return null;
        var o = output.ToLowerInvariant();
        if (o.Contains("wrong chip") || o.Contains("this chip is") || o.Contains("chip is not"))
            return Loc.Get("Flash_Err_WrongChip");
        if (o.Contains("failed to connect") || o.Contains("wrong boot mode") ||
            o.Contains("no serial data received") || o.Contains("invalid head of packet"))
            return Loc.Get("Flash_Err_NotBootloader");
        if (o.Contains("access is denied") || o.Contains("could not open") || o.Contains("permission denied"))
            return Loc.Get("Flash_Err_PortBusy");
        return Loc.Get("Flash_Err_WriteFailed", output.Trim());
    }

    public async Task FlashAsync(string comPort, IProgress<FlashProgress> progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(comPort))
            throw new FlashException(Loc.Get("Flash_Err_NoPort"));
        if (!File.Exists(_esptoolPath))
            throw new FlashException(Loc.Get("Flash_Err_EsptoolMissing"));
        if (!File.Exists(_binPath))
            throw new FlashException(Loc.Get("Flash_Err_BinMissing"));

        progress.Report(new FlashProgress(0, Loc.Get("Flash_Progress_Detecting")));

        // esptool v5 command/flag syntax (hyphens). --before/--after are omitted:
        // their defaults are exactly default-reset / hard-reset.
        var args = $"--chip esp32 --port {comPort} --baud {FlashBaud} " +
                   $"write-flash 0x0 \"{_binPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = _esptoolPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();

        void OnLine(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            var pct = ParsePercent(line);
            if (pct >= 0)
                progress.Report(new FlashProgress(pct, Loc.Get("Flash_Progress_Writing", pct)));
        }

        proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data);

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new FlashException(Loc.Get("Flash_Err_WriteFailed", ex.Message));
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw new FlashException(Loc.Get("Flash_Err_Cancelled"));
        }

        string outText;
        lock (output) outText = output.ToString();

        var failure = ClassifyFailure(proc.ExitCode, outText);
        if (failure is not null)
            throw new FlashException(failure);

        progress.Report(new FlashProgress(100, Loc.Get("Flash_Progress_Rebooting")));
    }
}
