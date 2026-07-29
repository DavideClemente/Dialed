using System;
using System.Threading.Tasks;
using Dialed.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Dialed.Core.Controls;

public sealed partial class FlashFirmwareDialog : ContentDialog
{
    public FlashFirmwareViewModel ViewModel { get; }

    public FlashFirmwareDialog(MainViewModel main)
    {
        ViewModel = new FlashFirmwareViewModel(main);
        InitializeComponent();

        // Keep the dialog open across the flash: intercept Primary, run the flash,
        // and only surface the result. Close is disabled while flashing.
        PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                // IsPrimaryButtonEnabled is bound to ViewModel.CanFlash (OneWay), which
                // flips false the instant IsFlashing becomes true and re-enables once
                // the flash finishes and CanFlash is re-evaluated.
                await ViewModel.StartFlashAsync();
            }
            finally
            {
                // Keep the dialog open on this click so the result InfoBar shows.
                args.Cancel = true;
                deferral.Complete();
            }

            // On success, auto-dismiss after a short pause so the user sees the
            // confirmation without having to click Close. Errors stay open to read
            // (and to allow a retry). Runs on the UI thread (no ConfigureAwait), so
            // Hide() is safe.
            if (ViewModel.HasResult && !ViewModel.IsError)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                Hide();
            }
        };

        // While a flash is running, block dismissal and request cancellation. The CTS
        // cancel propagates into Esp32Flasher.FlashAsync, which kills the esptool
        // process tree and surfaces Flash_Err_Cancelled. Once IsFlashing is false the
        // Close button dismisses the dialog normally.
        CloseButtonClick += (sender, args) =>
        {
            if (ViewModel.IsFlashing)
            {
                args.Cancel = true;    // don't dismiss the dialog mid-flash
                ViewModel.Cancel();    // cancel the CTS -> Esp32Flasher kills the process
            }
            // not flashing: allow normal close
        };
    }
}
