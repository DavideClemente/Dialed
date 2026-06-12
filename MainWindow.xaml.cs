using AudioMixerWin.Core.ViewModels;
using AudioMixerWin.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin
{
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; } = new();

        private readonly MainPage _mainPage;
        private readonly SettingsPage _settingsPage;

        public MainWindow()
        {
            InitializeComponent();

            _mainPage = new MainPage(ViewModel);
            _settingsPage = new SettingsPage(ViewModel);

            ContentFrame.Content = _mainPage;
        }

        private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            ContentFrame.Content = args.IsSettingsSelected ? _settingsPage : _mainPage;
        }
    }
}
