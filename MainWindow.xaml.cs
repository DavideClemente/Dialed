using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dialed.Core.Services;
using Dialed.Core.ViewModels;
using Dialed.Core.Views;
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

namespace Dialed
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

        // ---- End-session handling (installer upgrades, logoff/shutdown) ----
        // Inno Setup's Restart Manager pass sends WM_QUERYENDSESSION/WM_ENDSESSION
        // to close the running app before replacing its files. Those must exit the
        // process directly — the AppWindow.Closing dialog would leave the app
        // running and force a "files in use" reboot prompt on the user.

        private const uint WM_QUERYENDSESSION = 0x0011;
        private const uint WM_ENDSESSION = 0x0016;
        private const int GWLP_WNDPROC = -4;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        // SetWindowLongPtrW only exists in 64-bit user32; on x86 it's a macro
        // over SetWindowLongW.
        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
            IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterApplicationRestart(string? pwzCommandline, int dwFlags);

        private const int RESTART_NO_CRASH = 1;
        private const int RESTART_NO_HANG = 2;
        private const int RESTART_NO_REBOOT = 8;

        // Keeps the subclass delegate alive for the window's lifetime — if it were
        // collected, the next message would call a freed thunk and crash.
        private WndProcDelegate? _wndProcHook;
        private IntPtr _prevWndProc;

        public MainViewModel ViewModel { get; } = new();

        private readonly MainPage _mainPage;
        private readonly SettingsPage _settingsPage;
        private readonly OutputPage _outputPage;
        private IdleScreenPage _idleScreenPage = null!;
        private readonly AppWindow _appWindow;
        private readonly TaskbarIcon _trayIcon;
        private readonly string _iconPath;
        private readonly IntPtr _hwnd;

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

            _wndProcHook = WndProc;
            _prevWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_wndProcHook));

            // After a Restart Manager close (installer upgrade), relaunch hidden in
            // the tray — matching how the app was most likely running. Crash/hang
            // restarts are opted out of to avoid restart loops.
            RegisterApplicationRestart(StartupService.MinimizedArg,
                RESTART_NO_CRASH | RESTART_NO_HANG | RESTART_NO_REBOOT);

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
        }

        private void ConfigureTitleBar()
        {
            // The XAML title bar row replaces the system one; caption buttons sit
            // transparently on top of the Mica backdrop.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var titleBar = _appWindow.TitleBar;
            var fg = Color.FromArgb(255, 230, 230, 230);
            var fgMuted = Color.FromArgb(255, 106, 106, 116);
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = fg;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 35, 35, 41);
            titleBar.ButtonHoverForegroundColor = fg;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 27, 27, 32);
            titleBar.ButtonPressedForegroundColor = fgMuted;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveForegroundColor = fgMuted;
        }

        // Status-pill palette: mint when the controller is connected, amber otherwise.
        private static readonly SolidColorBrush PillBgConnected = new(Color.FromArgb(255, 20, 32, 26));
        private static readonly SolidColorBrush PillBgDisconnected = new(Color.FromArgb(255, 26, 20, 0));
        private static readonly SolidColorBrush PillStrokeConnected = new(Color.FromArgb(255, 30, 58, 44));
        private static readonly SolidColorBrush PillStrokeDisconnected = new(Color.FromArgb(255, 58, 46, 16));
        private static readonly SolidColorBrush PillFgConnected = new(Color.FromArgb(255, 127, 214, 172));
        private static readonly SolidColorBrush PillFgDisconnected = new(Color.FromArgb(255, 184, 149, 48));
        private static readonly SolidColorBrush PillDotConnected = new(Color.FromArgb(255, 52, 211, 153));
        private static readonly SolidColorBrush PillDotDisconnected = new(Color.FromArgb(255, 184, 149, 48));

        public SolidColorBrush PillBackground(bool connected) => connected ? PillBgConnected : PillBgDisconnected;
        public SolidColorBrush PillStroke(bool connected) => connected ? PillStrokeConnected : PillStrokeDisconnected;
        public SolidColorBrush PillText(bool connected) => connected ? PillFgConnected : PillFgDisconnected;
        public SolidColorBrush PillDot(bool connected) => connected ? PillDotConnected : PillDotDisconnected;

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_QUERYENDSESSION:
                    // Nothing blocks shutdown: settings persist on every change,
                    // so there is no unsaved state to prompt about.
                    return new IntPtr(1);

                case WM_ENDSESSION:
                    // wParam == 0 means the shutdown was cancelled elsewhere.
                    if (wParam != IntPtr.Zero)
                        ExitApp();
                    return IntPtr.Zero;
            }

            return CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
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

        /// <summary>
        /// Brings the app up hidden in the tray without ever showing the window
        /// (used for the --minimized auto-start launch). The tray icon is already
        /// created in the constructor, so the app remains reachable.
        /// </summary>
        public void HideToTray() => WindowExtensions.Hide(this);

        /// <summary>
        /// Shows and foregrounds the window. Public because a redirected launch of
        /// a second instance surfaces this window via <c>App.ShowMainWindow</c>.
        /// </summary>
        public void RestoreWindow()
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
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

            var files = await picker.PickMultipleFilesAsync();
            return files ?? new List<StorageFile>();
        }

    }
}
