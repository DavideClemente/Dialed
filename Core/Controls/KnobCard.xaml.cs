using System;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AudioMixerWin.Core.Controls;

public sealed partial class KnobCard : UserControl
{
    public static readonly DependencyProperty ChannelProperty =
        DependencyProperty.Register(nameof(Channel), typeof(ChannelViewModel), typeof(KnobCard), new PropertyMetadata(null));

    public ChannelViewModel? Channel
    {
        get => (ChannelViewModel?)GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    public KnobCard()
    {
        InitializeComponent();
    }

    public string FormatPercent(double volume) => $"{volume:0}%";

    public string FormatMuteIcon(bool isMuted) => isMuted ? "🔇" : "🔊";

    public double ConvertMuteToOpacity(bool isMuted) => isMuted ? 0.35 : 1.0;

    public SolidColorBrush GetMuteBrush(bool isMuted) =>
        new SolidColorBrush(isMuted
            ? Color.FromArgb(255, 42, 8, 8)
            : Color.FromArgb(255, 28, 28, 28));

    public SolidColorBrush GetMuteBorderBrush(bool isMuted) =>
        new SolidColorBrush(isMuted
            ? Color.FromArgb(140, 200, 32, 32)
            : Color.FromArgb(255, 48, 48, 48));

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Channel is null)
            return;

        var dialog = new AppPickerDialog(Channel) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && dialog.SelectedSession is AudioSession session)
        {
            Channel.AppName = session.ProcessName;
        }
        else if (result == ContentDialogResult.Secondary)
        {
            Channel.Remove();
        }
    }
}
