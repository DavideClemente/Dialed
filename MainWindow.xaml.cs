using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AudioMixerWin.Core.Services;
using AudioMixerWin.Core.ViewModels;
using AudioMixerWin.Core.Views;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace AudioMixerWin
{
    public sealed partial class MainWindow : Window
    {
        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint IMAGE_ICON = 1;

        private const int GCLP_HICON = -14;
        private const int GCLP_HICONSM = -34;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        private const double MinPaneWidth = 200;
        private const double MaxPaneWidth = 400;

        public MainViewModel ViewModel { get; } = new();

        private readonly MainPage _mainPage;
        private readonly SettingsPage _settingsPage;
        private readonly OutputPage _outputPage;
        private IdleScreenPage _idleScreenPage = null!;
        private readonly AppWindow _appWindow;
        private readonly TaskbarIcon _trayIcon;
        private readonly string _iconPath;
        private readonly IntPtr _hwnd;

        private bool _isDraggingSplitter;
        private double _dragStartX;
        private double _dragStartWidth;

        public MainWindow()
        {
            InitializeComponent();

            var hwnd = WindowNative.GetWindowHandle(this);
            _iconPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()!.Location)!,
                "Assets", "AudioMixer.ico");
            _hwnd = hwnd;
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.SetIcon(_iconPath);
            _appWindow.Closing += OnWindowClosing;
            _appWindow.Changed += OnAppWindowChanged;
            ConfigureTitleBar();

            this.Activated += OnFirstActivated;

            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "Audio Mixer",
                Icon = new System.Drawing.Icon(_iconPath),
                LeftClickCommand = new RelayCommand(RestoreWindow),
                DoubleClickCommand = new RelayCommand(RestoreWindow),
                ContextMenuMode = ContextMenuMode.PopupMenu,
                ContextFlyout = BuildTrayMenu(),
            };
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);

            _mainPage = new MainPage(ViewModel);
            _settingsPage = new SettingsPage(ViewModel);
            _outputPage = new OutputPage(ViewModel.Output);
            ViewModel.InitIdleScreen(PickGifFilesAsync, () => Content?.XamlRoot);
            _idleScreenPage = new IdleScreenPage(ViewModel.IdleScreen!);

            ContentFrame.Content = _mainPage;

            // The built-in settings item's label follows the OS language even with a
            // language override, so give it explicit English content. SettingsItem is
            // only realized once the control template is applied (on Loaded).
            NavView.Loaded += OnNavViewLoaded;

            NavView.OpenPaneLength = ViewModel.NavPaneWidth;
            PositionSplitter(ViewModel.NavPaneWidth);
        }

        private void ConfigureTitleBar()
        {
            var titleBar = _appWindow.TitleBar;
            var bg = Color.FromArgb(255, 10, 10, 10);
            var fg = Color.FromArgb(255, 230, 230, 230);
            var fgMuted = Color.FromArgb(255, 80, 80, 80);
            var hover = Color.FromArgb(255, 28, 28, 28);
            var pressed = Color.FromArgb(255, 18, 18, 18);
            titleBar.BackgroundColor = bg;
            titleBar.ForegroundColor = fg;
            titleBar.InactiveBackgroundColor = bg;
            titleBar.InactiveForegroundColor = fgMuted;
            titleBar.ButtonBackgroundColor = bg;
            titleBar.ButtonForegroundColor = fg;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = fg;
            titleBar.ButtonPressedBackgroundColor = pressed;
            titleBar.ButtonPressedForegroundColor = fgMuted;
            titleBar.ButtonInactiveBackgroundColor = bg;
            titleBar.ButtonInactiveForegroundColor = fgMuted;
        }

        private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            args.Cancel = true;

            var dialog = new ContentDialog
            {
                Title = Loc.Get("Close_Title"),
                Content = Loc.Get("Close_Content"),
                PrimaryButtonText = Loc.Get("Close_Primary"),
                SecondaryButtonText = Loc.Get("Close_Secondary"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                XamlRoot = Content.XamlRoot,
                DefaultButton = ContentDialogButton.Secondary,
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
                ExitApp();
            else if (result == ContentDialogResult.Secondary)
                MinimizeToTray();
        }

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidPresenterChange || !args.DidSizeChange && !args.DidPositionChange)
            {
                if (_appWindow.Presenter is OverlappedPresenter p &&
                    p.State == OverlappedPresenterState.Minimized)
                {
                    MinimizeToTray();
                }
            }
        }

        private MenuFlyout BuildTrayMenu()
        {
            var menu = new MenuFlyout();

            void AddItem(string text, Action action)
            {
                // PopupMenu (native Win32) mode invokes Command, not the XAML
                // Click event, so bind the action as a RelayCommand.
                menu.Items.Add(new MenuFlyoutItem
                {
                    Text = text,
                    Command = new RelayCommand(action),
                });
            }

            AddItem(Loc.Get("Tray_Open"), RestoreWindow);
            menu.Items.Add(new MenuFlyoutSeparator());
            AddItem(Loc.Get("Nav_Mixer"), () => ShowPage("mixer"));
            AddItem(Loc.Get("Nav_IdleScreen"), () => ShowPage("idle"));
            AddItem(Loc.Get("Nav_Output"), () => ShowPage("output"));
            AddItem(Loc.Get("Nav_Settings"), () => ShowPage("settings"));
            menu.Items.Add(new MenuFlyoutSeparator());
            AddItem(Loc.Get("Tray_Quit"), ExitApp);

            return menu;
        }

        // Navigates the window to a page by NavigationViewItem tag ("settings"
        // targets the built-in settings item) and brings the window forward.
        private void ShowPage(string tag)
        {
            RestoreWindow();

            if (tag == "settings")
            {
                NavView.SelectedItem = NavView.SettingsItem;
                return;
            }

            foreach (var menuItem in NavView.MenuItems)
            {
                if (menuItem is NavigationViewItem nvi && nvi.Tag as string == tag)
                {
                    NavView.SelectedItem = nvi;
                    break;
                }
            }
        }

        private void MinimizeToTray() => WindowExtensions.Hide(this);

        private void RestoreWindow()
        {
            WindowExtensions.Show(this);

            // Showing a hidden window leaves it minimized/behind other windows.
            // Restore the presenter, then force it to the foreground. The click
            // on the tray icon gives our process foreground rights, so
            // SetForegroundWindow succeeds here.
            if (_appWindow.Presenter is OverlappedPresenter p &&
                p.State == OverlappedPresenterState.Minimized)
            {
                p.Restore();
            }

            ShowWindow(_hwnd, SW_RESTORE);
            this.Activate();
            SetForegroundWindow(_hwnd);
        }

        private void ExitApp()
        {
            _trayIcon.Dispose();
            Application.Current.Exit();
        }

        private void OnNavViewLoaded(object sender, RoutedEventArgs e)
        {
            NavView.Loaded -= OnNavViewLoaded;
            if (NavView.SettingsItem is NavigationViewItem settingsItem)
                settingsItem.Content = Loc.Get("Nav_Settings");
        }

        private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            this.Activated -= OnFirstActivated;
            var hIconBig = LoadImage(IntPtr.Zero, _iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
            var hIconSmall = LoadImage(IntPtr.Zero, _iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            SendMessage(_hwnd, WM_SETICON, ICON_BIG, hIconBig);
            SendMessage(_hwnd, WM_SETICON, ICON_SMALL, hIconSmall);
            SetClassLongPtr(_hwnd, GCLP_HICON, hIconBig);
            SetClassLongPtr(_hwnd, GCLP_HICONSM, hIconSmall);
        }

        private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                ContentFrame.Content = _settingsPage;
                return;
            }

            var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
            ContentFrame.Content = tag switch
            {
                "idle" => _idleScreenPage,
                "output" => _outputPage,
                _ => _mainPage,
            };
        }

        private async Task<IReadOnlyList<StorageFile>> PickGifFilesAsync()
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeFilter.Add(".gif");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

            var files = await picker.PickMultipleFilesAsync();
            return files ?? new List<StorageFile>();
        }

        private void PositionSplitter(double paneWidth) =>
            PaneSplitter.Margin = new Thickness(paneWidth - PaneSplitter.Width / 2, 0, 0, 0);

        private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e) =>
            PaneSplitter.Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.3 };

        private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingSplitter)
                PaneSplitter.Background = new SolidColorBrush(Colors.Transparent);
        }

        private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingSplitter = true;
            _dragStartX = e.GetCurrentPoint(Content).Position.X;
            _dragStartWidth = NavView.OpenPaneLength;
            PaneSplitter.CapturePointer(e.Pointer);
        }

        private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingSplitter)
                return;

            var currentX = e.GetCurrentPoint(Content).Position.X;
            var newWidth = Math.Clamp(_dragStartWidth + (currentX - _dragStartX), MinPaneWidth, MaxPaneWidth);
            NavView.OpenPaneLength = newWidth;
            PositionSplitter(newWidth);
        }

        private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            PaneSplitter.ReleasePointerCapture(e.Pointer);
            EndSplitterDrag();
        }

        private void OnSplitterPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            // PointerCaptureLost means capture is already gone; do not call
            // ReleasePointerCapture here as it would be redundant/could throw.
            EndSplitterDrag();
        }

        private void EndSplitterDrag()
        {
            if (!_isDraggingSplitter)
                return;

            _isDraggingSplitter = false;
            PaneSplitter.Background = new SolidColorBrush(Colors.Transparent);
            ViewModel.NavPaneWidth = NavView.OpenPaneLength;
        }
    }
}
