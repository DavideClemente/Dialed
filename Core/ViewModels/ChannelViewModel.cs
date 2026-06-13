using System;
using System.Collections.ObjectModel;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioMixerWin.Core.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly Action<ChannelViewModel> _onRemove;
    private readonly Action _onSettingsChanged;
    private readonly Action<AudioSession> _onHideSession;

    public int KnobIndex { get; }

    public string KnobLabel => $"Knob {KnobIndex + 1}";

    public ObservableCollection<AudioSession> AvailableSessions { get; }

    [ObservableProperty]
    private string appName;

    [ObservableProperty]
    private double volume;

    public ChannelViewModel(
        int knobIndex,
        string appName,
        AudioManager audioManager,
        ObservableCollection<AudioSession> availableSessions,
        Action<ChannelViewModel> onRemove,
        Action onSettingsChanged,
        Action<AudioSession> onHideSession)
    {
        KnobIndex = knobIndex;
        _audioManager = audioManager;
        AvailableSessions = availableSessions;
        _onRemove = onRemove;
        _onSettingsChanged = onSettingsChanged;
        _onHideSession = onHideSession;
        this.appName = appName;
        volume = audioManager.GetVolume(appName) * 100;
    }

    partial void OnAppNameChanged(string value)
    {
        Volume = _audioManager.GetVolume(value) * 100;
        _onSettingsChanged();
    }

    partial void OnVolumeChanged(double value) =>
        _audioManager.SetVolume(AppName, (float)(value / 100.0));

    public void HideSession(AudioSession session) => _onHideSession(session);

    public void Remove() => _onRemove(this);
}
