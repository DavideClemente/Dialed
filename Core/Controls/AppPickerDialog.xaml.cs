using System.Collections.ObjectModel;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Controls;

public sealed partial class AppPickerDialog : ContentDialog
{
    private readonly ChannelViewModel _channel;

    public ObservableCollection<AudioSession> AvailableSessions => _channel.AvailableSessions;

    public AudioSession? SelectedSession => SessionsList.SelectedItem as AudioSession;

    public AppPickerDialog(ChannelViewModel channel)
    {
        _channel = channel;
        InitializeComponent();
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is AudioSession session)
            _channel.HideSession(session);
    }
}
