using System;
using System.Reflection;
using Dialed.Core.Models;
using Dialed.Core.Services;
using Dialed.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dialed.Core.Views;

public sealed partial class SettingsPage : Page
{
    private readonly DispatcherTimer _hideInfoBarTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainViewModel ViewModel { get; }

    public ComPortInfo[] PortInfos { get; } = ComPortInfo.GetPorts();

    public int[] BaudRates { get; } = { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    public string VersionText { get; } = Loc.Get(
        "Settings_About_Version",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public SettingsPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // Assign the port items and selection in code, not via x:Bind: a compiled
        // ItemsSource binding is only applied during the page's Loading phase, so a
        // SelectedValue set here (or via a TwoWay binding) would run before the items
        // exist — leaving the box blank. Setting ItemsSource first, synchronously,
        // guarantees the saved port resolves.
        ComPortCombo.ItemsSource = PortInfos;
        ComPortCombo.SelectedValue = ViewModel.ComPort;

        _hideInfoBarTimer.Tick += (_, _) =>
        {
            _hideInfoBarTimer.Stop();
            HideInfoBar.IsOpen = false;
        };
    }

    private void OnComPortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComPortCombo.SelectedValue is string port)
            ViewModel.ComPort = port;
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not AudioSession session)
            return;

        var blocked = ViewModel.HideSession(session);
        if (blocked is not null)
        {
            HideInfoBar.Message = blocked;
            HideInfoBar.IsOpen = true;
            _hideInfoBarTimer.Stop();
            _hideInfoBarTimer.Start();
        }
    }

    // x:Bind to ViewModel.UnhideProcessCommand from inside this DataTemplate crashes XamlCompiler, so invoke it via Tag/Click instead.
    private void OnShowClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string processName)
            return;

        ViewModel.UnhideProcessCommand.Execute(processName);
    }

    private async void OnBuyMeABeerClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.buymeacoffee.com/davideclemente"));

}
