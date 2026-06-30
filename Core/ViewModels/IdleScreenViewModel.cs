using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace AudioMixerWin.Core.ViewModels;

public partial class IdleScreenViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IdleGifLibraryService _library;
    private readonly Action _save;
    private readonly Func<Task<IReadOnlyList<StorageFile>>> _pickGifs;
    private readonly Func<XamlRoot?> _getXamlRoot;

    public ObservableCollection<IdleGifViewModel> Gifs { get; } = new();

    [ObservableProperty]
    private IdleGifViewModel? selectedGif;

    [ObservableProperty]
    private string usageText = "";

    public bool HasGifs => Gifs.Count > 0;

    public IdleScreenViewModel(
        AppSettings settings,
        IdleGifLibraryService library,
        Action save,
        Func<Task<IReadOnlyList<StorageFile>>> pickGifs,
        Func<XamlRoot?> getXamlRoot)
    {
        _settings = settings;
        _library = library;
        _save = save;
        _pickGifs = pickGifs;
        _getXamlRoot = getXamlRoot;

        Gifs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasGifs));
            UpdateUsage();
        };

        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        // Prune entries whose backing file is gone (e.g. cache cleared by hand),
        // so stale metadata can't strand the library.
        var pruned = false;
        for (var i = _settings.IdleGifs.Count - 1; i >= 0; i--)
        {
            if (!_library.Exists(_settings.IdleGifs[i]))
            {
                if (_settings.IdleGifs[i].Id == _settings.ActiveIdleGifId)
                    _settings.ActiveIdleGifId = null;
                _settings.IdleGifs.RemoveAt(i);
                pruned = true;
            }
        }
        if (pruned)
            _save();

        foreach (var config in _settings.IdleGifs)
            Gifs.Add(new IdleGifViewModel(config, _library.PathFor(config)));

        var active = Gifs.FirstOrDefault(g => g.Id == _settings.ActiveIdleGifId);
        if (active != null)
        {
            active.IsSelected = true;
            SelectedGif = active;
        }

        UpdateUsage();
    }

    private void UpdateUsage()
    {
        var count = Gifs.Count;
        var bytes = _library.TotalSizeBytes(Gifs.Select(g => g.Config));
        var size = bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
            : $"{bytes / 1024.0:0} KB";
        UsageText = count == 1 ? $"1 GIF · {size}" : $"{count} GIFs · {size}";
    }

    [RelayCommand]
    private async Task AddGifsAsync()
    {
        var files = await _pickGifs();
        if (files == null || files.Count == 0)
            return;

        foreach (var file in files)
        {
            var config = await _library.ImportAsync(file);
            if (config == null)
                continue;

            _settings.IdleGifs.Add(config);
            Gifs.Add(new IdleGifViewModel(config, _library.PathFor(config)));
        }

        _save();
    }

    [RelayCommand]
    private void SetActive(IdleGifViewModel? gif)
    {
        if (gif == null)
            return;

        foreach (var g in Gifs)
            g.IsSelected = ReferenceEquals(g, gif);

        SelectedGif = gif;
        _settings.ActiveIdleGifId = gif.Id;
        _save();
    }

    [RelayCommand]
    private async Task DeleteGifAsync(IdleGifViewModel? gif)
    {
        if (gif == null)
            return;

        var xamlRoot = _getXamlRoot();
        if (xamlRoot == null)
            return;

        var dialog = new ContentDialog
        {
            Title = "Delete GIF",
            Content = $"Remove \"{gif.OriginalName}\" from your idle-screen library?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _library.Delete(gif.Config);
        _settings.IdleGifs.RemoveAll(c => c.Id == gif.Id);
        Gifs.Remove(gif);

        if (_settings.ActiveIdleGifId == gif.Id)
        {
            _settings.ActiveIdleGifId = null;
            SelectedGif = null;
        }

        _save();
    }
}
