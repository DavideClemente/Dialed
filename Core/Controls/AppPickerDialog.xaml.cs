using System;
using System.Collections.ObjectModel;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Controls;

public sealed partial class AppPickerDialog : ContentDialog
{
    private readonly ChannelViewModel _channel;
    private readonly DispatcherTimer _hideInfoBarTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public ObservableCollection<AudioSession> SelectableSessions { get; }

    public AudioSession? SelectedSession => SessionsList.SelectedItem as AudioSession;

    public AppPickerDialog(ChannelViewModel channel)
    {
        _channel = channel;
        SelectableSessions = new ObservableCollection<AudioSession>(_channel.GetSelectableSessions());
        InitializeComponent();

        _hideInfoBarTimer.Tick += (_, _) =>
        {
            _hideInfoBarTimer.Stop();
            HideInfoBar.IsOpen = false;
        };
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not AudioSession session)
            return;

        var blocked = _channel.HideSession(session);
        if (blocked is not null)
        {
            HideInfoBar.Message = blocked;
            HideInfoBar.IsOpen = true;
            _hideInfoBarTimer.Stop();
            _hideInfoBarTimer.Start();
        }
        else
        {
            SelectableSessions.Remove(session);
        }
    }
}
