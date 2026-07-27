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
                // Cancel closes the dialog; while flashing, block the close button.
                IsPrimaryButtonEnabled = false;
                await ViewModel.StartFlashAsync();
                // Re-purpose the primary button state after completion.
                IsPrimaryButtonEnabled = ViewModel.CanFlash;
            }
            finally
            {
                // Keep the dialog open so the user sees the result InfoBar; they
                // dismiss with Close. Prevent auto-close on this click.
                args.Cancel = true;
                deferral.Complete();
            }
        };
    }
}
