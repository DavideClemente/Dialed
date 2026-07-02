using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace AudioMixerWin.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly OutputManager _outputManager;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    // Polls for the controller being unplugged/replugged so we can drop the stale
    // handle and re-open automatically. Fixed cadence (independent of the session
    // refresh interval) so reconnection stays responsive even if refresh is slow.
    private readonly DispatcherTimer _connectionTimer;
    private SerialManager _serial;
    // What the UI currently reflects about the connection, so the watchdog can spot a
    // drop even when Windows closes the handle itself on unplug (IsConnected flips
    // false before we detect the port vanishing).
    private bool _wasConnected;
    private readonly IdleGifLibraryService _idleGifLibrary = new();

    public IdleScreenViewModel? IdleScreen { get; private set; }

    public OutputViewModel Output { get; }

    [ObservableProperty]
    private string comPort;

    [ObservableProperty]
    private int baudRate;

    [ObservableProperty]
    private string serialStatus = Loc.Get("Serial_NotConnected");

    /// <summary>Mirrors the serial connection for the shell's always-visible status pill.</summary>
    [ObservableProperty]
    private bool isSerialConnected;

    [ObservableProperty]
    private double refreshIntervalSeconds;

    [ObservableProperty]
    private int idleTimeoutSeconds;

    [ObservableProperty]
    private double navPaneWidth;

    [ObservableProperty]
    private InputMode inputMode;

    [ObservableProperty]
    private double encoderStepPercent;

    [ObservableProperty]
    private int knobCount;

    [ObservableProperty]
    private bool autoReconnect;

    [ObservableProperty]
    private bool debugSerialEvents;

    [ObservableProperty]
    private bool showPercentSign;

    // Backed by the registry Run key (see StartupService), not settings.json.
    [ObservableProperty]
    private bool startWithWindows;

    [ObservableProperty]
    private string language;

    [ObservableProperty]
    private bool languageRestartPending;

    public IReadOnlyList<LanguageOption> LanguageOptions => LocalizationService.Options;

    [ObservableProperty]
    private int draggedChannelIndex = -1;

    [ObservableProperty]
    private int targetDropIndex = -1;

    [ObservableProperty]
    private bool isDragging;

    [ObservableProperty]
    private double dragDropIndicatorY = -1;

    public ObservableCollection<string> SerialLog { get; } = new();

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    public ObservableCollection<AudioSession> AvailableSessions { get; } = new();

    public ObservableCollection<string> HiddenProcesses { get; } = new();

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _audioManager = new AudioManager();
        _outputManager = new OutputManager();
        _settings = SettingsService.Load();

        comPort = _settings.ComPort;
        baudRate = _settings.BaudRate;
        refreshIntervalSeconds = _settings.RefreshIntervalSeconds;
        idleTimeoutSeconds = _settings.IdleTimeoutSeconds;
        navPaneWidth = _settings.NavPaneWidth;
        inputMode = _settings.InputMode;
        encoderStepPercent = _settings.EncoderStepPercent;
        knobCount = _settings.KnobCount;
        autoReconnect = _settings.AutoReconnect;
        debugSerialEvents = _settings.DebugSerialEvents;
        showPercentSign = _settings.ShowPercentSign;
        startWithWindows = StartupService.IsEnabled;
        language = _settings.Language;

        foreach (var process in _settings.ExcludedProcesses)
            HiddenProcesses.Add(process);

        Output = new OutputViewModel(_settings, _outputManager, () => SettingsService.Save(_settings));

        // Serial must be created before channels are added: AddChannelInternal
        // reads _serial.IsConnected to seed each channel's connected state.
        _serial = CreateAndStartSerial();

        foreach (var config in _settings.Channels)
            AddChannelInternal(config.AppName, config.KnobIndex, save: false);

        ScheduleResync();

        RefreshAvailableSessions();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds) };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshAvailableSessions();
            Output.RefreshDevices();
        };
        _refreshTimer.Start();

        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectionTimer.Tick += (_, _) => CheckConnection();
        if (AutoReconnect)
            _connectionTimer.Start();
    }

    // Waits for the controller to finish booting after a (re)connect, then pushes
    // assignments/config. Shared by first launch, manual reconnect, and auto-reconnect.
    private void ScheduleResync() => _ = Task.Run(async () =>
    {
        await Task.Delay(2000);
        _dispatcherQueue.TryEnqueue(SyncAllChannels);
    });

    // Detects unplug (target port vanished from the system) and replug (port is back
    // while we're disconnected), tearing down / re-opening the serial handle to match.
    private void CheckConnection()
    {
        if (!AutoReconnect)
            return;

        var portPresent = PortIsPresent(ComPort);
        var connected = _serial.IsConnected;

        // Connection dropped without us tearing it down — on many boards Windows closes
        // the handle itself on unplug, so IsConnected flips false before we notice the
        // port vanish. Reflect the loss in the UI once.
        if (_wasConnected && !connected)
        {
            _wasConnected = false;
            LogSerial($"watchdog: connection lost ({ComPort})");
            SerialStatus = Loc.Get("Serial_DeviceRemoved");
            UpdateChannelsSerialState(false);
        }

        if (connected)
        {
            // Still reported open but the port is gone → surprise-removed. Close on a
            // background thread (Close() can block/deadlock on a removed device) and swap
            // in an inert placeholder so the next tick can't touch a disposing port.
            if (!portPresent)
            {
                LogSerial($"watchdog: {ComPort} disappeared → dropping connection");
                var dead = _serial;
                UnsubscribeSerial(dead);
                Task.Run(dead.Stop);
                _serial = new SerialManager(ComPort, BaudRate);
                _wasConnected = false;
                SerialStatus = Loc.Get("Serial_DeviceRemoved");
                UpdateChannelsSerialState(false);
            }
            else
            {
                _wasConnected = true;
            }
            return;
        }

        // Disconnected: re-open as soon as the port reappears. DetachSerial first so a
        // never-opened or already-torn-down handle can't leave stale event handlers.
        if (portPresent)
        {
            LogSerial($"watchdog: {ComPort} present → reconnecting");
            DetachSerial();
            _serial = CreateAndStartSerial();
            if (_serial.IsConnected)
            {
                _wasConnected = true;
                LogSerial("watchdog: reconnected");
                ScheduleResync();
            }
            else
            {
                LogSerial("watchdog: reopen failed (port present but Open threw)");
            }
        }
    }

    private static bool PortIsPresent(string comPort) =>
        SerialPort.GetPortNames().Any(p => p.Equals(comPort, StringComparison.OrdinalIgnoreCase));

    public void InitIdleScreen(
        Func<Task<IReadOnlyList<StorageFile>>> pickGifs,
        Func<XamlRoot?> getXamlRoot)
    {
        IdleScreen = new IdleScreenViewModel(
            _settings, _idleGifLibrary, () => SettingsService.Save(_settings), pickGifs, getXamlRoot,
            PushIdleGifAsync, ClearIdleGif, () => _serial.IsConnected);
    }

    // Display target for the GC9A01 (square; the round bezel hides the corners).
    private const int IdleGifTarget = 240;
    // Upper bound on frames regardless of free space — caps upload time and the
    // firmware's in-RAM delay table (must stay <= MAX_GIF_FRAMES in idlegif.cpp).
    private const int IdleGifFrameCap = 60;
    // The board renders 240x240 from flash at ~15-20fps; resampling to this avoids
    // wasting flash on frames it can't show and keeps playback at the right speed.
    private const int IdleGifMaxFps = 20;

    /// <summary>
    /// Encodes the library GIF and uploads it to the controller's flash, adapting
    /// the frame count to the device's free space. Throws
    /// <see cref="IdleGifUploadException"/> with a user-readable reason on failure.
    /// </summary>
    public async Task PushIdleGifAsync(IdleGifConfig config, IProgress<double>? progress, CancellationToken ct)
    {
        if (!_serial.IsConnected)
            throw new IdleGifUploadException(Loc.Get("Gif_NotConnected"));

        var path = _idleGifLibrary.PathFor(config);
        if (!File.Exists(path))
            throw new IdleGifUploadException(Loc.Get("Gif_MissingCache"));

        long free = await _serial.QueryIdleGifSpaceAsync(ct);
        if (free < 0)
            throw new IdleGifUploadException(Loc.Get("Gif_NoResponse_Conn"));

        long frameBytes = (long)IdleGifTarget * IdleGifTarget * 2;
        long headerEstimate = 16 + 2L * IdleGifFrameCap; // magic+dims + delay table
        int byBudget = (int)((free - headerEstimate) / frameBytes);
        if (byBudget < 1)
            throw new IdleGifUploadException(Loc.Get("Gif_NoStorage"));

        int cap = Math.Min(IdleGifFrameCap, byBudget);

        var encoded = await Task.Run(() => GifFrameEncoder.Encode(path, IdleGifTarget, cap, IdleGifMaxFps), ct);
        await _serial.UploadIdleGifAsync(encoded, progress, ct);
    }

    public void ClearIdleGif() => _serial.ClearIdleGif();

    private SerialManager CreateAndStartSerial()
    {
        var serial = new SerialManager(ComPort, BaudRate);
        serial.KnobChanged += OnKnobChanged;
        serial.KnobDelta += OnKnobDelta;
        serial.KnobPressed += OnKnobPressed;
        serial.SwitchChanged += OnSwitchChanged;

        try
        {
            serial.Start();
            SerialStatus = Loc.Get("Serial_Connected", ComPort, BaudRate);
        }
        catch (Exception ex)
        {
            SerialStatus = Loc.Get("Serial_Disconnected", ex.Message);
        }

        UpdateChannelsSerialState(serial.IsConnected);
        return serial;
    }

    [RelayCommand]
    private void Reconnect()
    {
        DetachSerial();
        _serial = CreateAndStartSerial();
        ScheduleResync();

        _settings.ComPort = ComPort;
        _settings.BaudRate = BaudRate;
        SettingsService.Save(_settings);
    }

    // Unsubscribes from the current serial handle and closes it synchronously. Used by
    // the reopen paths (manual Reconnect / watchdog replug) where the device is present,
    // so Close() returns quickly. Safe on an already-stopped handle (Stop swallows).
    private void DetachSerial()
    {
        UnsubscribeSerial(_serial);
        _serial.Stop();
    }

    private void UnsubscribeSerial(SerialManager serial)
    {
        serial.KnobChanged -= OnKnobChanged;
        serial.KnobDelta -= OnKnobDelta;
        serial.KnobPressed -= OnKnobPressed;
        serial.SwitchChanged -= OnSwitchChanged;
    }

    [RelayCommand]
    private void AddChannel() => AddChannelInternal("Select App");

    private void AddChannelInternal(string appName, int? knobIndex = null, bool save = true)
    {
        var index = knobIndex ?? FirstFreeKnobIndex();
        var ch = new ChannelViewModel(index, appName, _audioManager, AvailableSessions, Channels, RemoveChannelInternal, SaveChannels, HideSession, SyncChannel, () => KnobCount);
        ch.IsSerialConnected = _serial.IsConnected;
        Channels.Add(ch);

        if (save)
            SaveChannels();
    }

    private int FirstFreeKnobIndex()
    {
        var used = Channels.Select(c => c.KnobIndex).ToHashSet();
        var i = 0;
        while (used.Contains(i)) i++;
        return i;
    }

    private void RemoveChannelInternal(ChannelViewModel channel)
    {
        Channels.Remove(channel);
        SaveChannels();
    }

    private void SaveChannels()
    {
        _settings.Channels = Channels
            .Select(c => new ChannelConfig { KnobIndex = c.KnobIndex, AppName = c.AppName })
            .ToList();

        SettingsService.Save(_settings);
    }

    private void UpdateChannelsSerialState(bool connected)
    {
        IsSerialConnected = connected;
        foreach (var ch in Channels)
            ch.IsSerialConnected = connected;
    }

    private void SyncChannel(ChannelViewModel ch)
    {
        var color = _audioManager.GetIconColor(ch.AppName);
        var icon = _audioManager.GetIconRgb565(ch.AppName);
        // Only push the label/color/icon — the device stores these silently and
        // stays on its idle screen. Deliberately no SendVolume here: a "vol:" line
        // makes the device switch to the volume screen (displayShowKnob), so echoing
        // one on connect or on an assignment change would pop the volume screen
        // without any physical interaction. The volume screen should appear only
        // when the user actually turns a knob (handled locally on the device and
        // echoed back via OnKnobChanged/OnKnobDelta).
        _serial.SendAssignment(ch.KnobIndex, ch.AppName, color, icon);
    }

    private void SyncAllChannels()
    {
        // The controller resets its idle timeout to a built-in default on boot, so
        // push the configured value whenever we (re)sync after a connect.
        _serial.SendIdleTimeout(IdleTimeoutSeconds * 1000);
        _serial.SendShowPercent(ShowPercentSign);

        foreach (var ch in Channels)
            SyncChannel(ch);
    }

    internal void SwapChannels(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < 0 || fromIndex >= Channels.Count || toIndex >= Channels.Count || fromIndex == toIndex)
            return;

        // Exchange the two grid squares; every other channel stays put.
        var from = Channels[fromIndex];
        Channels[fromIndex] = Channels[toIndex];
        Channels[toIndex] = from;
        SaveChannels();
    }

    public string? HideSession(AudioSession session)
    {
        var assigned = ChannelViewModel.FindAssignedChannel(Channels, session.ProcessName);
        if (assigned is not null)
            return Loc.Get("Hide_Blocked", session.ProcessName, assigned.KnobLabel);

        if (!_settings.ExcludedProcesses.Contains(session.ProcessName, StringComparer.OrdinalIgnoreCase))
            _settings.ExcludedProcesses.Add(session.ProcessName);

        if (!HiddenProcesses.Contains(session.ProcessName, StringComparer.OrdinalIgnoreCase))
            HiddenProcesses.Add(session.ProcessName);

        AvailableSessions.Remove(session);
        SettingsService.Save(_settings);
        return null;
    }

    [RelayCommand]
    private void UnhideProcess(string processName)
    {
        _settings.ExcludedProcesses.RemoveAll(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase));

        for (var i = HiddenProcesses.Count - 1; i >= 0; i--)
        {
            if (HiddenProcesses[i].Equals(processName, StringComparison.OrdinalIgnoreCase))
                HiddenProcesses.RemoveAt(i);
        }

        SettingsService.Save(_settings);
        RefreshAvailableSessions();
    }

    private void RefreshAvailableSessions()
    {
        var current = _audioManager.GetSessions()
            .Where(s => !_settings.ExcludedProcesses.Contains(s.ProcessName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        for (var i = AvailableSessions.Count - 1; i >= 0; i--)
        {
            if (!current.Any(s => s.ProcessName.Equals(AvailableSessions[i].ProcessName, StringComparison.OrdinalIgnoreCase)))
                AvailableSessions.RemoveAt(i);
        }

        foreach (var session in current)
        {
            if (!AvailableSessions.Any(s => s.ProcessName.Equals(session.ProcessName, StringComparison.OrdinalIgnoreCase)))
                AvailableSessions.Add(session);
        }
    }

    partial void OnRefreshIntervalSecondsChanged(double value)
    {
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, value));
        _settings.RefreshIntervalSeconds = (int)value;
        SettingsService.Save(_settings);
    }

    partial void OnIdleTimeoutSecondsChanged(int value)
    {
        _settings.IdleTimeoutSeconds = Math.Max(1, value);
        SettingsService.Save(_settings);
        _serial.SendIdleTimeout(_settings.IdleTimeoutSeconds * 1000);
    }

    partial void OnNavPaneWidthChanged(double value)
    {
        _settings.NavPaneWidth = value;
        SettingsService.Save(_settings);
    }

    public int InputModeIndex
    {
        get => (int)InputMode;
        set => InputMode = (InputMode)value;
    }

    public Microsoft.UI.Xaml.Visibility IsRotaryEncoder =>
        InputMode == InputMode.RotaryEncoder
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    partial void OnInputModeChanged(InputMode value)
    {
        _settings.InputMode = value;
        SettingsService.Save(_settings);
        OnPropertyChanged(nameof(InputModeIndex));
        OnPropertyChanged(nameof(IsRotaryEncoder));
    }

    partial void OnEncoderStepPercentChanged(double value)
    {
        _settings.EncoderStepPercent = (float)value;
        SettingsService.Save(_settings);
    }

    partial void OnKnobCountChanged(int value)
    {
        _settings.KnobCount = value;
        SettingsService.Save(_settings);
    }

    partial void OnDebugSerialEventsChanged(bool value)
    {
        _settings.DebugSerialEvents = value;
        SettingsService.Save(_settings);
        if (!value) SerialLog.Clear();
    }

    partial void OnShowPercentSignChanged(bool value)
    {
        _settings.ShowPercentSign = value;
        SettingsService.Save(_settings);
        _serial?.SendShowPercent(value);
    }

    partial void OnStartWithWindowsChanged(bool value) => StartupService.SetEnabled(value);

    partial void OnAutoReconnectChanged(bool value)
    {
        _settings.AutoReconnect = value;
        SettingsService.Save(_settings);
        if (value)
            _connectionTimer.Start();
        else
            _connectionTimer.Stop();
    }

    partial void OnLanguageChanged(string value)
    {
        _settings.Language = value ?? "";
        SettingsService.Save(_settings);
        // PrimaryLanguageOverride only fully applies to freshly created UI, so
        // surface a restart prompt rather than re-theming live.
        LanguageRestartPending = true;
    }

    private void LogSerial(string message)
    {
        if (!DebugSerialEvents) return;
        _dispatcherQueue.TryEnqueue(() =>
        {
            SerialLog.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            while (SerialLog.Count > 50)
                SerialLog.RemoveAt(SerialLog.Count - 1);
        });
    }

    // Arduino sends 1-based IDs like "knob1", "knob2"; channels are stored 0-based.
    private static int? ParseKnobIndex(string knobId)
    {
        var digits = new string(knobId.SkipWhile(c => !char.IsDigit(c)).ToArray());
        return int.TryParse(digits, out var n) ? n - 1 : null;
    }

    private void OnKnobChanged(string knobId, float normalized)
    {
        if (ParseKnobIndex(knobId) is not int index)
        {
            LogSerial($"{knobId} → {normalized:P0} [no index parsed]");
            return;
        }
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == index);
            LogSerial(channel != null
                ? $"{knobId} → {normalized:P0} (index={index}, app={channel.AppName})"
                : $"{knobId} → {normalized:P0} [no channel at index {index}, channels={string.Join(",", Channels.Select(c => c.KnobIndex))}]");
            if (channel != null)
            {
                channel.Volume = normalized * 100;
                _serial.SendVolume(index, (float)(channel.Volume / 100.0));
            }
        });
    }

    private void OnKnobDelta(string knobId, int delta)
    {
        if (ParseKnobIndex(knobId) is not int index)
        {
            LogSerial($"{knobId} → {(delta > 0 ? "up" : "down")} [no index parsed]");
            return;
        }
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == index);
            if (channel == null)
            {
                LogSerial($"{knobId} → {(delta > 0 ? "up" : "down")} [no channel at index {index}, channels={string.Join(",", Channels.Select(c => c.KnobIndex))}]");
                return;
            }
            var before = channel.Volume;
            var next = Math.Clamp(before + delta * EncoderStepPercent, 0, 100);
            channel.Volume = next;
            // Echo the resulting absolute level back so the device gauge tracks it.
            // (Encoders only send relative up/down; without this the display has
            // no source for the true volume — see knobs.cpp.)
            _serial.SendVolume(index, (float)(next / 100.0));
            var actual = _audioManager.GetVolume(channel.AppName) * 100;
            LogSerial($"{knobId} → {(delta > 0 ? "up" : "down")} | {before:F0}% → {next:F0}% (audio={actual:F0}%)");
        });
    }

    private void OnSwitchChanged(int position)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            LogSerial($"switch → position {(position == 0 ? "A" : "B")}");
            Output.ApplySwitchPosition(position);
        });
    }

    private void OnKnobPressed(string knobId)
    {
        if (ParseKnobIndex(knobId) is not int index)
        {
            LogSerial($"{knobId} → press [no index parsed]");
            return;
        }
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == index);
            if (channel == null)
            {
                LogSerial($"{knobId} → press [no channel at index {index}]");
                return;
            }
            channel.ToggleMuteCommand.Execute(null);
            _serial?.SendMute(index, channel.IsMuted);
            LogSerial($"{knobId} → press | {channel.AppName} muted={channel.IsMuted}");
        });
    }
}
