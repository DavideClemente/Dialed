using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Dialed;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) enforcing a single instance.
/// Launching Dialed while it is already running — e.g. the installer's
/// post-install "Launch" checkbox with the old instance still in the tray, or a
/// second click on the shortcut — redirects the activation to the existing
/// instance (which surfaces its window) and exits without spinning up XAML.
/// Two instances must never run at once: they would fight over the COM port and
/// last-writer-wins each other's settings.json.
/// </summary>
public static class Program
{
    private const string InstanceKey = "Dialed-Main";

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
            return;

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    /// <summary>
    /// Returns true when this process redirected its activation to an already
    /// running instance and should exit.
    /// </summary>
    private static bool DecideRedirection()
    {
        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (mainInstance.IsCurrent)
        {
            mainInstance.Activated += OnRedirectedActivation;
            return false;
        }

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        RedirectActivationTo(activationArgs, mainInstance);
        return true;
    }

    // RedirectActivationToAsync must complete before this process exits, but
    // blocking the STA thread on it directly can deadlock — wait on a
    // semaphore released from a worker thread instead (the pattern from the
    // Windows App SDK AppInstance docs).
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance target)
    {
        using var redirected = new SemaphoreSlim(0, 1);
        Task.Run(async () =>
        {
            try { await target.RedirectActivationToAsync(args); }
            finally { redirected.Release(); }
        });
        redirected.Wait();
    }

    // Raised (on a non-UI thread) in the surviving instance when another
    // launch was redirected here.
    private static void OnRedirectedActivation(object? sender, AppActivationArguments args)
        => (Application.Current as App)?.ShowMainWindow();
}
