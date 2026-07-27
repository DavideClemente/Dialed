using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Dialed.Core.Services;
using Dialed.Core.Services.Firmware;
using Dialed.Core.ViewModels;

namespace Dialed.Core.ViewModels;

public partial class FlashFirmwareViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public FlashFirmwareViewModel(MainViewModel main)
    {
        _main = main;
    }

    public string BoardName => _main.Esp32Flasher?.DisplayName ?? Loc.Get("Flash_Board_Esp32");
    public string BundledVersionText => Loc.Get("Flash_Dialog_BundledVersion", _main.BundledFirmwareVersion ?? "—");
    public string PortText => Loc.Get("Flash_Dialog_Port", string.IsNullOrWhiteSpace(_main.ComPort) ? "—" : _main.ComPort);

    /// <summary>True when a port is selected and firmware assets exist.</summary>
    public bool CanFlash => !IsFlashing && _main.Esp32Flasher is not null && !string.IsNullOrWhiteSpace(_main.ComPort);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFlash))]
    private bool isFlashing;

    [ObservableProperty]
    private int percent;

    [ObservableProperty]
    private string statusText = "";

    [ObservableProperty]
    private bool hasResult;

    [ObservableProperty]
    private string resultMessage = "";

    [ObservableProperty]
    private bool isError;

    public async Task StartFlashAsync()
    {
        if (!CanFlash) return;

        HasResult = false;
        IsError = false;
        Percent = 0;
        StatusText = "";
        IsFlashing = true;
        _cts = new CancellationTokenSource();

        var progress = new Progress<FlashProgress>(p =>
        {
            Percent = p.Percent;
            StatusText = p.StatusText;
        });

        try
        {
            await _main.FlashControllerAsync(progress, _cts.Token);
            IsError = false;
            ResultMessage = Loc.Get("Flash_Success");
        }
        catch (FlashException ex)
        {
            IsError = true;
            ResultMessage = ex.Message;
        }
        catch (Exception ex)
        {
            IsError = true;
            ResultMessage = Loc.Get("Flash_Err_WriteFailed", ex.Message);
        }
        finally
        {
            IsFlashing = false;
            HasResult = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel() => _cts?.Cancel();
}
