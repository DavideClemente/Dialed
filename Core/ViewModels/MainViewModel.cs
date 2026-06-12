using System;
using System.Collections.ObjectModel;
using System.Linq;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace AudioMixerWin.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly DispatcherQueue _dispatcherQueue;
    private SerialManager _serial;

    [ObservableProperty]
    private string comPort;

    [ObservableProperty]
    private int baudRate;

    [ObservableProperty]
    private string serialStatus = "Not connected";

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _audioManager = new AudioManager();

        AddChannelInternal("Spotify");
        AddChannelInternal("Discord");
        AddChannelInternal("Chrome");
        AddChannelInternal("Game");

        var settings = SettingsService.Load();
        comPort = settings.ComPort;
        baudRate = settings.BaudRate;

        _serial = CreateAndStartSerial();
    }

    private SerialManager CreateAndStartSerial()
    {
        var serial = new SerialManager(ComPort, BaudRate);
        serial.KnobChanged += OnKnobChanged;

        try
        {
            serial.Start();
            SerialStatus = $"Connected to {ComPort} @ {BaudRate}";
        }
        catch (Exception ex)
        {
            SerialStatus = $"Disconnected: {ex.Message}";
        }

        return serial;
    }

    [RelayCommand]
    private void Reconnect()
    {
        _serial.Stop();
        _serial = CreateAndStartSerial();

        SettingsService.Save(new AppSettings { ComPort = ComPort, BaudRate = BaudRate });
    }

    [RelayCommand]
    private void AddChannel() => AddChannelInternal("Select App");

    private void AddChannelInternal(string appName)
    {
        var knobIndex = Channels.Count == 0 ? 0 : Channels.Max(c => c.KnobIndex) + 1;
        Channels.Add(new ChannelViewModel(knobIndex, appName, _audioManager, RemoveChannelInternal));
    }

    private void RemoveChannelInternal(ChannelViewModel channel) => Channels.Remove(channel);

    private void OnKnobChanged(int knobIndex, float normalized)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == knobIndex);
            if (channel != null)
                channel.Volume = normalized * 100;
        });
    }
}
