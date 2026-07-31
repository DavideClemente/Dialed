using System;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dialed.Core.Models;
using Dialed.Core.Services;

namespace Dialed.Core;

// Surfaced to the UI so an upload failure shows the actual cause, not a generic
// "failed" — the message is meant to be read by the user.
public class IdleGifUploadException : Exception
{
    public IdleGifUploadException(string message) : base(message) { }
}

public class SerialManager
{
    private readonly SerialPort _port;

    // Raw bytes per GIF-upload chunk. Its base64 (×4/3, ~5462 chars) plus the
    // "gif:d:" prefix must stay well under the firmware's serial RX buffer so a
    // chunk can't overrun it even if the device stalls briefly on a redraw.
    private const int GifChunkBytes = 4096;

    public event Action<string, float>? KnobChanged;
    public event Action<string, int>? KnobDelta;
    public event Action<string>? KnobPressed;

    // Position of the two-way output switch: 0 = A, 1 = B. The controller sends
    // "switch:0" / "switch:1" (or "switch:a" / "switch:b") whenever the toggle
    // moves; the app re-routes the Windows default output device in response.
    public event Action<int>? SwitchChanged;

    // The controller reports its firmware as "fw:<board>:<version>" on boot and in
    // reply to "ver?". Metadata only — handlers must not touch the device screen.
    public event Action<string, string>? FirmwareReported;

    // Replies to the GIF-upload protocol ("gif:*") are routed here so the upload
    // coroutine can await them without racing the knob-event handlers.
    private readonly Channel<string> _gifResponses =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public SerialManager(string comPort, int baudRate)
    {
        _port = new SerialPort(comPort, baudRate);
        _port.DataReceived += OnData;
    }

    public bool IsConnected => _port.IsOpen;

    public void Start()
    {
        _port.Open();
    }

    public void Stop()
    {
        try
        {
            _port.DataReceived -= OnData;
            if (_port.IsOpen)
                _port.Close();
            _port.Dispose();
        }
        catch { }
    }

    public void SendVolume(int knobIndex, float volume)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"vol:knob{knobIndex + 1}:{volume.ToString("F2", CultureInfo.InvariantCulture)}"); }
        catch { }
    }

    public void SendShowPercent(bool show)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"config:pct:{(show ? 1 : 0)}"); }
        catch { }
    }

    public void SendMute(int knobIndex, bool muted)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"mute:knob{knobIndex + 1}:{(muted ? 1 : 0)}"); }
        catch { }
    }

    // Tell the controller how long (ms) to wait with no knob activity before
    // it switches to its idle screen. Parsed by handleConfigLine on the device.
    public void SendIdleTimeout(int milliseconds)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"cfg:idle:{Math.Max(0, milliseconds)}"); }
        catch { }
    }

    // Tell the controller how many knobs are actually wired so it stops sampling the
    // pins mapped to higher (unconnected) knobs — otherwise those floating inputs emit
    // phantom up/down/press events. Parsed by handleConfigLine on the device.
    public void SendKnobCount(int count)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"cfg:knobs:{Math.Max(0, count)}"); }
        catch { }
    }

    /// <summary>Asks the controller to (re)report its firmware version via a "fw:" line.</summary>
    public void RequestFirmwareVersion()
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine("ver?"); }
        catch { }
    }

    public void SendAssignment(int knobIndex, string appName, (byte R, byte G, byte B) color, byte[] iconRgb565)
    {
        if (!_port.IsOpen) return;
        try
        {
            var knobId = $"knob{knobIndex + 1}";
            var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
            var safeName = appName.Replace("\r", "").Replace("\n", "");
            _port.WriteLine($"assign:{knobId}:{hex}:{safeName}");
            if (iconRgb565.Length > 0)
                _port.WriteLine($"icon:{knobId}:{Convert.ToBase64String(iconRgb565)}");
        }
        catch { }
    }

    // Pushes what's assigned to an output position. The analogue of SendAssignment:
    // the device stores it silently and stays on whatever screen it's showing, so
    // this is safe to send on connect. Its counterpart SendOutputSwitch is NOT.
    public void SendOutputAssignment(int position, bool isHeadset, string name)
    {
        if (!_port.IsOpen) return;
        try
        {
            var pos = position == 0 ? "a" : "b";
            var kind = isHeadset ? "h" : "s";
            var safeName = name.Replace("\r", "").Replace("\n", "");
            _port.WriteLine($"outset:{pos}:{kind}:{safeName}");
        }
        catch { }
    }

    // Reports the result of a switch. This DOES wake the device's screen (it shows
    // the output card), so it must only ever follow a real switch — never a connect
    // or a poll-detected default-device change. Same rule as SendVolume.
    // state: "ok" | "none" (nothing assigned to that position) | "fail" (routing failed).
    public void SendOutputSwitch(int position, string state)
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine($"out:{(position == 0 ? "a" : "b")}:{state}"); }
        catch { }
    }

    // Accumulates inbound bytes across DataReceived events so a line split across two
    // reads is still dispatched exactly once.
    private readonly StringBuilder _rxBuffer = new();

    // Upper bound on the partial-line buffer. Inbound lines are short (knob events,
    // "fw:", "gif:" acks); anything larger means we're accumulating garbage with no
    // newline, so drop it rather than grow without limit.
    private const int RxBufferLimit = 64 * 1024;

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            // DataReceived signals *new* arrivals — it is NOT re-raised just because
            // unread bytes remain buffered. Reading a single line per event therefore
            // leaves the rest queued and the app replays a growing backlog of stale
            // events: spin an encoder then reverse, and it keeps applying the old
            // direction for several steps before catching up. Drain everything
            // available and dispatch every complete line.
            var chunk = _port.ReadExisting();
            if (chunk.Length == 0) return;

            if (_rxBuffer.Length + chunk.Length > RxBufferLimit)
                _rxBuffer.Clear();
            _rxBuffer.Append(chunk);

            int start = 0, scan = 0;
            while (scan < _rxBuffer.Length)
            {
                var c = _rxBuffer[scan];
                if (c == '\n' || c == '\r')
                {
                    if (scan > start)
                    {
                        var line = _rxBuffer.ToString(start, scan - start).Trim();
                        if (line.Length > 0)
                            HandleCommand(line);
                    }
                    start = scan + 1;
                }
                scan++;
            }
            _rxBuffer.Remove(0, start);   // keep only the trailing partial line
        }
        catch { }
    }

    private void HandleCommand(string cmd)
    {
        // GIF upload acknowledgements are consumed by UploadIdleGifAsync, not the
        // knob pipeline. Route them and stop.
        if (cmd.StartsWith("gif:", StringComparison.Ordinal))
        {
            _gifResponses.Writer.TryWrite(cmd);
            return;
        }

        // "fw:<board>:<version>" — firmware version report. Three parts, so handle
        // before the generic 2-part knob split below.
        if (cmd.StartsWith("fw:", StringComparison.Ordinal))
        {
            var fw = cmd.Split(':');
            if (fw.Length == 3)
                FirmwareReported?.Invoke(fw[1].Trim(), fw[2].Trim());
            return;
        }

        var parts = cmd.Split(':');
        if (parts.Length != 2)
            return;

        var knobId  = parts[0].Trim();
        var payload = parts[1].Trim();

        if (knobId == "switch")
        {
            if (payload is "0" or "a" or "A")
                SwitchChanged?.Invoke(0);
            else if (payload is "1" or "b" or "B")
                SwitchChanged?.Invoke(1);
            return;
        }

        if (payload == "up")
            KnobDelta?.Invoke(knobId, +1);
        else if (payload == "down")
            KnobDelta?.Invoke(knobId, -1);
        else if (payload == "press")
            KnobPressed?.Invoke(knobId);
        else if (float.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            KnobChanged?.Invoke(knobId, Math.Clamp(value, 0f, 1f));
    }

    // ── Idle-screen GIF upload ──────────────────────────────────────────────────
    // Line protocol, ACK-paced (one chunk in flight) so the ESP32's UART/flash
    // never falls behind. The device replies on the "gif:*" channel; see
    // Arduino/mixer/idlegif.cpp for the matching firmware side.

    private void DrainGifResponses()
    {
        while (_gifResponses.Reader.TryRead(out _)) { }
    }

    private async Task<string?> ReadGifResponseAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { return await _gifResponses.Reader.ReadAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // timeout
    }

    /// <summary>Bytes the controller has free for an idle GIF, or -1 if unknown.</summary>
    public async Task<long> QueryIdleGifSpaceAsync(CancellationToken ct = default)
    {
        if (!_port.IsOpen) return -1;
        DrainGifResponses();
        try { _port.WriteLine("gif:space?"); } catch { return -1; }

        var reply = await ReadGifResponseAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (reply != null && reply.StartsWith("gif:space:", StringComparison.Ordinal)
            && long.TryParse(reply.AsSpan("gif:space:".Length), out var bytes))
            return bytes;
        return -1;
    }

    /// <summary>
    /// Uploads the encoded GIF to the controller's flash, reporting 0..1 progress.
    /// Throws <see cref="IdleGifUploadException"/> with a user-readable reason on
    /// failure. The device keeps no half-written GIF — the firmware writes a temp
    /// file and only swaps it in on success.
    /// </summary>
    public async Task UploadIdleGifAsync(EncodedGif gif, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!_port.IsOpen)
            throw new IdleGifUploadException(Loc.Get("Gif_NotConnected"));
        if (gif.Frames.Count == 0)
            throw new IdleGifUploadException(Loc.Get("Gif_NoFrames"));

        DrainGifResponses();

        var delaysCsv = string.Join(",", System.Linq.Enumerable.Select(gif.Frames, f => f.DelayMs));
        WriteOrThrow($"gif:begin:{gif.Frames.Count}:{gif.Width}:{gif.Height}:{delaysCsv}");

        var rdy = await ReadGifResponseAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (rdy == null)
            throw new IdleGifUploadException(Loc.Get("Gif_NoResponse_Cable"));
        if (rdy == "gif:err")
            throw new IdleGifUploadException(Loc.Get("Gif_Rejected_Partition"));
        if (rdy != "gif:rdy")
            throw new IdleGifUploadException(Loc.Get("Gif_Unexpected", rdy));

        long totalBytes = gif.PixelByteCount;
        long sentBytes = 0;
        var chunk = new byte[GifChunkBytes];
        int chunkFill = 0;

        async Task FlushChunkAsync()
        {
            if (chunkFill == 0) return;
            var b64 = Convert.ToBase64String(chunk, 0, chunkFill);
            WriteOrThrow("gif:d:" + b64);
            var ack = await ReadGifResponseAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            if (ack == null)
                throw new IdleGifUploadException(Loc.Get("Gif_TimedOut"));
            if (ack == "gif:err")
                throw new IdleGifUploadException(Loc.Get("Gif_TransferError"));
            if (ack != "gif:ack")
                throw new IdleGifUploadException(Loc.Get("Gif_Unexpected", ack));
            sentBytes += chunkFill;
            chunkFill = 0;
            progress?.Report(totalBytes > 0 ? (double)sentBytes / totalBytes : 1);
        }

        foreach (var frame in gif.Frames)
        {
            var px = frame.Rgb565;
            int offset = 0;
            while (offset < px.Length)
            {
                int take = Math.Min(chunk.Length - chunkFill, px.Length - offset);
                Array.Copy(px, offset, chunk, chunkFill, take);
                chunkFill += take;
                offset += take;
                if (chunkFill == chunk.Length)
                    await FlushChunkAsync().ConfigureAwait(false);
            }
        }
        await FlushChunkAsync().ConfigureAwait(false);

        WriteOrThrow("gif:end");
        var done = await ReadGifResponseAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (done == null)
            throw new IdleGifUploadException(Loc.Get("Gif_BeforeConfirm"));
        if (done != "gif:done")
            throw new IdleGifUploadException(Loc.Get("Gif_Verify"));
    }

    private void WriteOrThrow(string line)
    {
        try { _port.WriteLine(line); }
        catch (Exception ex) { throw new IdleGifUploadException(Loc.Get("Gif_WriteFailed", ex.Message)); }
    }

    /// <summary>Removes the stored idle GIF so the controller reverts to its built-in animation.</summary>
    public void ClearIdleGif()
    {
        if (!_port.IsOpen) return;
        try { _port.WriteLine("gif:clear"); } catch { }
    }
}
