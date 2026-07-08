using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;

namespace AudioMixerWin.Core.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly ObservableCollection<ChannelViewModel> _channels;
    private readonly Action<ChannelViewModel> _onRemove;
    private readonly Action _onSettingsChanged;
    private readonly Func<AudioSession, string?> _onHideSession;
    private readonly Action<ChannelViewModel> _onSyncNeeded;
    private readonly Func<int> _knobCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KnobLabel))]
    private int knobIndex;

    public string KnobLabel => Loc.Get("Knob_Label", KnobIndex + 1);

    public ObservableCollection<AudioSession> AvailableSessions { get; }

    /// <summary>Placeholder name for a channel that hasn't been assigned an app yet.</summary>
    public const string UnassignedAppName = "Select App";

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

    public string DisplayName =>
        AppName.Equals(UnassignedAppName, StringComparison.OrdinalIgnoreCase)
            ? Loc.Get("AppPicker_Title")
            : AudioManager.GetDisplayName(AppName);

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
        if (appName.Equals(AudioManager.MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
            return MasterAccent;

        var (r, g, b) = _audioManager.GetIconColor(appName);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private void UpdateRunningState() =>
        IsAppRunning = AvailableSessions.Any(s => s.ProcessName.Equals(AppName, StringComparison.OrdinalIgnoreCase));

    partial void OnKnobIndexChanged(int value)
    {
        _onSettingsChanged();
        _onSyncNeeded(this);
    }

    partial void OnAppNameChanged(string value)
    {
        Volume = _audioManager.GetVolume(value) * 100;
        IsMuted = _audioManager.GetMute(value);
        IconSource = GetSessionIcon(value);
        AccentColor = ComputeAccent(value);
        UpdateRunningState();
        _onSettingsChanged();
        _onSyncNeeded(this);
    }

    private void OnAvailableSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var session = AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(AppName, StringComparison.OrdinalIgnoreCase));
        IsAppRunning = session is not null;
        if (session is null)
            return; // app not running — keep last-known icon and volume

        IconSource = session.IconSource;
        // The icon (and so its dominant color) may only become extractable once
        // the app is actually running — refresh the tint alongside it.
        AccentColor = ComputeAccent(AppName);
        Volume = _audioManager.GetVolume(AppName) * 100;
        IsMuted = _audioManager.GetMute(AppName);
        _onSyncNeeded(this);
    }

    private ImageSource? GetSessionIcon(string appName) =>
        AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(appName, StringComparison.OrdinalIgnoreCase))?.IconSource
        // App not currently running: recover the last-known icon persisted to disk.
        ?? _audioManager.GetIcon(appName);

    partial void OnVolumeChanged(double value) =>
        _audioManager.SetVolume(AppName, (float)(value / 100.0));

    partial void OnIsMutedChanged(bool value) =>
        _audioManager.SetMute(AppName, value);

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    public IEnumerable<AudioSession> GetSelectableSessions()
    {
        var taken = new HashSet<string>(
            _channels.Select(c => c.AppName),
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
