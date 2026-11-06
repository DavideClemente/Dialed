using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Dialed.Core.Models;
using Dialed.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;

namespace Dialed.Core.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly ObservableCollection<ChannelViewModel> _channels;
    private readonly Action<ChannelViewModel> _onRemove;
    private readonly Action _onSettingsChanged;
    private readonly Func<AudioSession, string?> _onHideSession;
    private readonly Action<ChannelViewModel> _onSyncNeeded;
    private readonly Func<int> _knobCount;
    private bool _readingAudioState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KnobLabel))]
    private int knobIndex;

    public string KnobLabel => Loc.Get("Knob_Label", KnobIndex + 1);

    public ObservableCollection<AudioSession> AvailableSessions { get; }

    /// <summary>Placeholder name for a channel that hasn't been assigned an app yet.</summary>
    public const string UnassignedAppName = "Select App";

    /// <summary>Controls running applications not assigned to another channel.</summary>
    public const string OtherAppsName = "Other Apps";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    private string appName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    private bool isAppRunning;

    /// <summary>
    /// True when the channel is assigned to a real app that isn't currently running.
    /// Such a channel can't actually be controlled, so the UI dims it and flags it.
    /// An unassigned ("Select App") placeholder is never considered offline.
    /// </summary>
    public bool IsOffline =>
        !AppName.Equals(UnassignedAppName, StringComparison.OrdinalIgnoreCase) && !IsAppRunning;

    [ObservableProperty]
    private double volume;

    [ObservableProperty]
    private ImageSource? iconSource;

    /// <summary>
    /// The app's dominant icon color (same one pushed to the hardware display),
    /// used to tint the card's icon container and slider fill.
    /// </summary>
    [ObservableProperty]
    private Windows.UI.Color accentColor = NeutralAccent;

    // Unassigned channels get a quiet gray; the master channel gets the brand mint.
    private static readonly Windows.UI.Color NeutralAccent = Windows.UI.Color.FromArgb(255, 0x8A, 0x8A, 0x92);
    private static readonly Windows.UI.Color MasterAccent = Windows.UI.Color.FromArgb(255, 0x34, 0xD3, 0x99);

    [ObservableProperty]
    private bool isMuted;

    [ObservableProperty]
    private bool isSerialConnected;

    /// <summary>
    /// Returns the appropriate fallback icon glyph when no app icon is available.
    /// Microphone channels show a microphone icon, "Other Apps" shows a grid, others show a speaker.
    /// </summary>
    public string FallbackIconGlyph
    {
        get
        {
            if (AppName.Equals(AudioManager.MicrophoneVolumeProcessName, StringComparison.OrdinalIgnoreCase))
                return "\uE720"; // Microphone icon

            if (AppName.Equals(OtherAppsName, StringComparison.OrdinalIgnoreCase))
                return "\uE8A0"; // Grid/Apps icon

            return "\uE767"; // Default speaker icon
        }
    }

    public string DisplayName
    {
        get
        {
            if (AppName.Equals(UnassignedAppName, StringComparison.OrdinalIgnoreCase))
                return Loc.Get("AppPicker_Title");

            if (AppName.Equals(OtherAppsName, StringComparison.OrdinalIgnoreCase))
            {
                var unassignedCount = GetUnassignedSessions().Count();
                return unassignedCount > 0
                    ? Loc.Get("OtherApps_DisplayName_Count", unassignedCount)
                    : Loc.Get("OtherApps_DisplayName");
            }

            return AudioManager.GetDisplayName(AppName);
        }
    }

    public ChannelViewModel(
        int knobIndex,
        string appName,
        AudioManager audioManager,
        ObservableCollection<AudioSession> availableSessions,
        ObservableCollection<ChannelViewModel> channels,
        Action<ChannelViewModel> onRemove,
        Action onSettingsChanged,
        Func<AudioSession, string?> onHideSession,
        Action<ChannelViewModel> onSyncNeeded,
        Func<int> knobCount)
    {
        this.knobIndex = knobIndex;
        _knobCount = knobCount;
        _audioManager = audioManager;
        AvailableSessions = availableSessions;
        _channels = channels;
        _onRemove = onRemove;
        _onSettingsChanged = onSettingsChanged;
        _onHideSession = onHideSession;
        _onSyncNeeded = onSyncNeeded;
        this.appName = appName;
        volume = audioManager.GetVolume(appName) * 100;
        isMuted = audioManager.GetMute(appName);

        AvailableSessions.CollectionChanged += OnAvailableSessionsChanged;
        IconSource = GetSessionIcon(appName);
        AccentColor = ComputeAccent(appName);
        UpdateRunningState();
    }

    private Windows.UI.Color ComputeAccent(string appName)
    {
        if (appName.Equals(UnassignedAppName, StringComparison.OrdinalIgnoreCase))
            return NeutralAccent;
        if (appName.Equals(OtherAppsName, StringComparison.OrdinalIgnoreCase))
            return Windows.UI.Color.FromArgb(255, 0x6D, 0x9E, 0xEB);
        if (appName.Equals(AudioManager.MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
            return MasterAccent;

        var (r, g, b) = _audioManager.GetIconColor(appName);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private bool IsCatchAll => AppName.Equals(OtherAppsName, StringComparison.OrdinalIgnoreCase);

    private void UpdateRunningState() =>
        IsAppRunning = IsCatchAll
            ? GetUnassignedSessions().Any()
            : AvailableSessions.Any(s => s.ProcessName.Equals(AppName, StringComparison.OrdinalIgnoreCase));

    public (double Volume, bool IsMuted) GetCurrentAudioState()
    {
        var processName = IsCatchAll ? GetUnassignedSessions().FirstOrDefault()?.ProcessName : AppName;
        return processName is null
            ? (Volume, IsMuted)
            : (_audioManager.GetVolume(processName) * 100, _audioManager.GetMute(processName));
    }

    partial void OnKnobIndexChanged(int value)
    {
        _onSettingsChanged();
        _onSyncNeeded(this);
    }

    partial void OnAppNameChanged(string value)
    {
        ReadAudioState();
        IconSource = GetSessionIcon(value);
        AccentColor = ComputeAccent(value);
        UpdateRunningState();
        OnPropertyChanged(nameof(FallbackIconGlyph));
        _onSettingsChanged();
        _onSyncNeeded(this);
    }

    private void OnAvailableSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(DisplayName));
        var session = IsCatchAll
            ? GetUnassignedSessions().FirstOrDefault()
            : AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(AppName, StringComparison.OrdinalIgnoreCase));
        IsAppRunning = session is not null;
        if (session is null)
            return; // app not running — keep last-known icon and volume

        if (!IsCatchAll)
            IconSource = session.IconSource;
        // The icon (and so its dominant color) may only become extractable once
        // the app is actually running — refresh the tint alongside it.
        AccentColor = ComputeAccent(AppName);
        ReadAudioState();
        _onSyncNeeded(this);
    }

    private void ReadAudioState()
    {
        // Reading the representative app must not change the other catch-all apps.
        var state = GetCurrentAudioState();
        _readingAudioState = true;
        try
        {
            Volume = state.Volume;
            IsMuted = state.IsMuted;
        }
        finally { _readingAudioState = false; }
    }

    private ImageSource? GetSessionIcon(string appName) =>
        AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(appName, StringComparison.OrdinalIgnoreCase))?.IconSource
        // App not currently running: recover the last-known icon persisted to disk.
        ?? _audioManager.GetIcon(appName);

    partial void OnVolumeChanged(double value)
    {
        if (_readingAudioState)
            return;
        if (IsCatchAll)
        {
            foreach (var session in GetUnassignedSessions())
                _audioManager.SetVolume(session.ProcessName, (float)(value / 100.0));
        }
        else
            _audioManager.SetVolume(AppName, (float)(value / 100.0));
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_readingAudioState)
            return;
        if (IsCatchAll)
        {
            foreach (var session in GetUnassignedSessions())
                _audioManager.SetMute(session.ProcessName, value);
        }
        else
            _audioManager.SetMute(AppName, value);
    }

    private IEnumerable<AudioSession> GetUnassignedSessions()
    {
        var assignedApps = new HashSet<string>(
            _channels.Where(c => !c.IsCatchAll).Select(c => c.AppName),
            StringComparer.OrdinalIgnoreCase);
        return AvailableSessions.Where(s =>
            !s.ProcessName.Equals(AudioManager.MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase) &&
            !s.ProcessName.Equals(AudioManager.MicrophoneVolumeProcessName, StringComparison.OrdinalIgnoreCase) &&
            !assignedApps.Contains(s.ProcessName));
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    public IEnumerable<AudioSession> GetSelectableSessions()
    {
        var taken = new HashSet<string>(
            _channels.Where(c => c != this).Select(c => c.AppName),
            StringComparer.OrdinalIgnoreCase);

        return AvailableSessions.Where(s => !taken.Contains(s.ProcessName));
    }

    public IEnumerable<int> GetSelectableKnobIndices()
    {
        var taken = _channels.Where(c => c != this).Select(c => c.KnobIndex).ToHashSet();
        // 0..count-1, plus this channel's own current index even if it sits above the count
        return Enumerable.Range(0, Math.Max(_knobCount(), KnobIndex + 1))
            .Where(i => !taken.Contains(i));
    }

    public static ChannelViewModel? FindAssignedChannel(IEnumerable<ChannelViewModel> channels, string processName) =>
        channels.FirstOrDefault(c => c.AppName.Equals(processName, StringComparison.OrdinalIgnoreCase));

    public string? HideSession(AudioSession session) => _onHideSession(session);

    public void Remove()
    {
        AvailableSessions.CollectionChanged -= OnAvailableSessionsChanged;
        _onRemove(this);
    }
}
