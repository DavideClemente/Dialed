using System.IO.Ports;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Views;

public sealed partial class SettingsPage : Page
{
    public MainViewModel ViewModel { get; }

    public string[] PortNames { get; } = SerialPort.GetPortNames();

    public int[] BaudRates { get; } = { 9600, 19200, 38400, 57600, 115200 };

    public SettingsPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
