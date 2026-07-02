using System;
using System.ComponentModel;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;


namespace AudioMixerWin.Core.Controls;

public sealed partial class KnobCard : UserControl
{
    public static readonly DependencyProperty ChannelProperty =
        DependencyProperty.Register(nameof(Channel), typeof(ChannelViewModel), typeof(KnobCard),
            new PropertyMetadata(null, OnChannelPropertyChanged));

    public ChannelViewModel? Channel
    {
        get => (ChannelViewModel?)GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    public KnobCard()
    {
        InitializeComponent();
    }

    private static void OnChannelPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (KnobCard)d;
        if (e.OldValue is ChannelViewModel oldVm)
            oldVm.PropertyChanged -= card.OnChannelVmPropertyChanged;
        if (e.NewValue is ChannelViewModel newVm)
            newVm.PropertyChanged += card.OnChannelVmPropertyChanged;
        card.ApplySliderAccent();
    }

    private void OnChannelVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelViewModel.AccentColor))
            ApplySliderAccent();
    }

    // The slider's value-fill brushes are lightweight-styling resources resolved once
    // by the control template, so they can't be data-bound. The brush instances are
    // declared in XAML and mutated in place — color changes propagate to the template.
    private void ApplySliderAccent()
    {
        if (Channel is null || VolumeSlider is null)
            return;

        var accent = Channel.AccentColor;
        SetSliderBrush("SliderTrackValueFill", accent);
        SetSliderBrush("SliderTrackValueFillPointerOver", Lighten(accent, 0.18));
        SetSliderBrush("SliderTrackValueFillPressed", Darken(accent, 0.18));
    }

    private void SetSliderBrush(string key, Color color)
    {
        if (VolumeSlider.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            brush.Color = color;
    }

    private static Color Lighten(Color c, double amount) => Color.FromArgb(255,
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));

    private static Color Darken(Color c, double amount) => Color.FromArgb(255,
        (byte)(c.R * (1 - amount)),
        (byte)(c.G * (1 - amount)),
        (byte)(c.B * (1 - amount)));

    // Low-alpha washes of the app's dominant color for the icon container.
    public SolidColorBrush GetAccentTintBrush(Color accent) =>
        new(Color.FromArgb(26, accent.R, accent.G, accent.B));

    public SolidColorBrush GetAccentBorderBrush(Color accent) =>
        new(Color.FromArgb(64, accent.R, accent.G, accent.B));

    public Visibility ConvertIconToVisibility(ImageSource? icon) =>
        icon is not null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ConvertIconFallbackToVisibility(ImageSource? icon) =>
        icon is null ? Visibility.Visible : Visibility.Collapsed;

    public string FormatPercent(double volume) => $"{volume:0}%";

    // Segoe Fluent icon glyphs: Mute / Volume.
    public string FormatMuteIcon(bool isMuted) => isMuted ? "" : "";

    public double ConvertMuteToOpacity(bool isMuted) => isMuted ? 0.35 : 1.0;

    public Visibility ConvertDisconnectedToVisibility(bool isSerialConnected) =>
        isSerialConnected ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ConvertOfflineToVisibility(bool isOffline) =>
        isOffline ? Visibility.Visible : Visibility.Collapsed;

    // Dim the whole card when its app isn't running so it reads as inactive.
    public double ConvertOfflineToOpacity(bool isOffline) => isOffline ? 0.45 : 1.0;

    // The volume control does nothing while the app is closed, so disable it.
    public bool ConvertOfflineToEnabled(bool isOffline) => !isOffline;

    public SolidColorBrush GetMuteBrush(bool isMuted) =>
        new SolidColorBrush(isMuted
            ? Color.FromArgb(255, 42, 12, 12)
            : Color.FromArgb(255, 35, 35, 41));

    public SolidColorBrush GetMuteBorderBrush(bool isMuted) =>
        new SolidColorBrush(isMuted
            ? Color.FromArgb(140, 200, 32, 32)
            : Color.FromArgb(255, 46, 46, 53));

    public SolidColorBrush GetMuteIconBrush(bool isMuted) =>
        new SolidColorBrush(isMuted
            ? Color.FromArgb(255, 240, 149, 149)
            : Color.FromArgb(255, 201, 201, 209));

    public SolidColorBrush GetPercentBrush(bool isMuted) =>
        new SolidColorBrush(isMuted
            ? Color.FromArgb(255, 85, 85, 92)
            : Color.FromArgb(255, 201, 201, 209));

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Channel is null)
            return;

        var dialog = new AppPickerDialog(Channel) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            Channel.KnobIndex = dialog.SelectedKnobIndex;
            if (dialog.SelectedSession is AudioSession session)
                Channel.AppName = session.ProcessName;
        }
        else if (result == ContentDialogResult.Secondary)
        {
            Channel.Remove();
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var mainPage = FindParentMainPage();
        mainPage?.OnKnobCardPointerPressed(this, e);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var mainPage = FindParentMainPage();
        mainPage?.OnKnobCardPointerMoved(this, e);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var mainPage = FindParentMainPage();
        mainPage?.OnKnobCardPointerReleased(this, e);
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var mainPage = FindParentMainPage();
        mainPage?.OnKnobCardPointerCaptureLost(this, e);
    }

    private Core.Views.MainPage? FindParentMainPage()
    {
        var parent = VisualTreeHelper.GetParent(this);
        while (parent != null && parent is not Core.Views.MainPage)
            parent = VisualTreeHelper.GetParent(parent);
        return parent as Core.Views.MainPage;
    }
}
