using System;
using System.IO;
using System.Linq;
using Dialed.Core.Services;
using Microsoft.UI.Xaml;

namespace Dialed
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            // Apply the saved language override before any UI is created so
            // framework strings and exception messages honour the choice.
            try { LocalizationService.Apply(SettingsService.Load().Language); }
            catch (Exception ex) { Log("Localization", ex); }

            InitializeComponent();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log("AppDomain", e.ExceptionObject as Exception);
            this.UnhandledException += (_, e) =>
            {
                Log("XamlUnhandled", e.Exception);
                e.Handled = true;
            };
        }

        /// <summary>
        /// Surfaces the main window. Called (from any thread) when a second
        /// launch of the app was redirected to this instance — see Program.cs.
        /// </summary>
        public void ShowMainWindow()
        {
            if (_window is MainWindow window)
                window.DispatcherQueue.TryEnqueue(() => window.RestoreWindow());
        }

        private static void Log(string source, Exception? ex)
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "dialed_crash.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:O}] {source}\n{ex}\n\n");
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                var window = new MainWindow();
                _window = window;

                // An auto-start launch (Run key adds --minimized) comes up hidden in
                // the tray. Skipping Activate() avoids a window flash on boot; the tray
                // icon is created in the MainWindow ctor, so it's available regardless.
                var startMinimized = Environment.GetCommandLineArgs()
                    .Any(a => string.Equals(a, StartupService.MinimizedArg, StringComparison.OrdinalIgnoreCase));

                if (startMinimized)
                    window.HideToTray();
                else
                    window.Activate();
            }
            catch (Exception ex)
            {
                Log("OnLaunched", ex);
                throw;
            }
        }
    }
}
