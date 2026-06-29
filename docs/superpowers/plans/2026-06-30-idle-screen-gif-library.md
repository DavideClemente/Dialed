# Idle Screen GIF Library Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an "Idle Screen" side-menu page where the user uploads GIFs into a local cache, previews them, deletes them, and picks one as the selected idle GIF (persisted).

**Architecture:** A focused on-disk asset store (`IdleGifLibraryService`, modeled on `IconStore`) owns the `idle-gifs` cache folder. Library metadata + the selected id live in `AppSettings` and persist through `SettingsService`. A child `IdleScreenViewModel` — created by `MainViewModel`, sharing the one `AppSettings` instance plus a save callback (the pattern `ChannelViewModel` already uses) — backs a new `IdleScreenPage` rendered in the existing `NavigationView`/`ContentFrame`.

**Tech Stack:** .NET 8, WinUI 3 / Windows App SDK, CommunityToolkit.Mvvm, `Windows.Storage.Pickers.FileOpenPicker`, `Windows.Graphics.Imaging.BitmapDecoder`, native WinUI `Image` animated-GIF playback.

## Global Constraints

- Target framework `net8.0-windows10.0.19041.0`; no AnyCPU — every build command passes `-p:Platform=x64`.
- Build command (the per-task gate, since there is no test project): `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug` must succeed.
- Cache root is `%LocalAppData%\AudioMixerWin\` (`Environment.SpecialFolder.LocalApplicationData` + `"AudioMixerWin"`), same as `SettingsService`/`IconStore`. The GIF subfolder is `idle-gifs`.
- File IO is defensive: swallow IO exceptions and return null / skip, matching `IconStore` and `SettingsService`. Never let cache IO throw into the UI.
- `.gif` only. No enforced storage quota — usage is informational.
- Namespaces match folders: services `AudioMixerWin.Core.Services`, models `AudioMixerWin.Core.Models`, view-models `AudioMixerWin.Core.ViewModels`, views `AudioMixerWin.Core.Views`.
- UI label for the chosen GIF is **"Selected"**, never "Active on device" (the GIF does not reach the device in this iteration).
- Dark theme brushes reused from existing pages: `#1C1C1C` surfaces, `#383838` borders (`#505050` hover), `#E0E0E0`/`#F0F0F0` text, `#0A0A0A` canvas, 8px corners, `Segoe UI Variable Display` for headings.

---

## File Structure

- `Core/Models/IdleGifConfig.cs` — **new.** Serializable per-entry metadata.
- `Core/Services/AppSettings.cs` — **modify.** Add `IdleGifs` list + `ActiveIdleGifId`.
- `Core/Services/IdleGifLibraryService.cs` — **new.** Owns the cache folder: import, delete, total size, path resolution, prune-missing.
- `Core/ViewModels/IdleGifViewModel.cs` — **new.** One library entry for the UI.
- `Core/ViewModels/IdleScreenViewModel.cs` — **new.** Page view-model: collection, selection, add/delete/set-active commands.
- `Core/ViewModels/MainViewModel.cs` — **modify.** Build the service + child `IdleScreenViewModel`, expose it.
- `Core/Views/IdleScreenPage.xaml` / `.xaml.cs` — **new.** The Gallery layout.
- `MainWindow.xaml` — **modify.** Add the `Idle Screen` nav item.
- `MainWindow.xaml.cs` — **modify.** Instantiate the page, route nav by `Tag`, supply the window handle for the file picker.

---

### Task 1: `IdleGifConfig` model + `AppSettings` fields

**Files:**
- Create: `Core/Models/IdleGifConfig.cs`
- Modify: `Core/Services/AppSettings.cs`

**Interfaces:**
- Produces: `IdleGifConfig` with `string Id`, `string FileName`, `string OriginalName`, `DateTime AddedUtc`, `long SizeBytes`, `int PixelWidth`, `int PixelHeight` (all public get/set, parameterless — JSON-serializable).
- Produces: `AppSettings.IdleGifs` (`List<IdleGifConfig>`, default `new()`), `AppSettings.ActiveIdleGifId` (`string?`, default `null`).

- [ ] **Step 1: Create the model**

`Core/Models/IdleGifConfig.cs`:

```csharp
using System;

namespace AudioMixerWin.Core.Models;

/// <summary>
/// Serializable metadata for one GIF in the idle-screen library. The binary
/// lives in the idle-gifs cache folder as {FileName}; this record is persisted
/// in settings.json.
/// </summary>
public class IdleGifConfig
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public long SizeBytes { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
}
```

- [ ] **Step 2: Add fields to `AppSettings`**

In `Core/Services/AppSettings.cs`, add two properties alongside the existing ones (the file already has `using AudioMixerWin.Core.Models;` and `using System.Collections.Generic;`):

```csharp
    public List<IdleGifConfig> IdleGifs { get; set; } = new();
    public string? ActiveIdleGifId { get; set; }
```

- [ ] **Step 3: Build**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Core/Models/IdleGifConfig.cs Core/Services/AppSettings.cs
git commit -m "Add IdleGifConfig model and idle-gif settings fields"
```

---

### Task 2: `IdleGifLibraryService`

**Files:**
- Create: `Core/Services/IdleGifLibraryService.cs`

**Interfaces:**
- Consumes: `IdleGifConfig` (Task 1).
- Produces:
  - `Task<IdleGifConfig?> ImportAsync(Windows.Storage.StorageFile file)` — copies the file into the cache under a new GUID `.gif` name, reads pixel dimensions + size, returns the config (or null on failure).
  - `void Delete(IdleGifConfig config)` — deletes the backing file (swallows errors).
  - `string PathFor(IdleGifConfig config)` — absolute path to the backing file.
  - `bool Exists(IdleGifConfig config)` — backing file present on disk.
  - `long TotalSizeBytes(IEnumerable<IdleGifConfig> configs)` — sum of on-disk sizes.

- [ ] **Step 1: Create the service**

`Core/Services/IdleGifLibraryService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AudioMixerWin.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace AudioMixerWin.Core.Services;

/// <summary>
/// Owns the idle-screen GIF cache folder (one copied .gif file per library
/// entry). Mirrors the defensive, folder-owning style of IconStore: IO errors
/// are swallowed so cache problems never surface as exceptions in the UI.
/// </summary>
public class IdleGifLibraryService
{
    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioMixerWin", "idle-gifs");

    /// <summary>
    /// Copies a picked GIF into the cache under a new GUID filename and reads
    /// its dimensions. Returns null if the file can't be read/decoded.
    /// </summary>
    public async Task<IdleGifConfig?> ImportAsync(StorageFile file)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var id = Guid.NewGuid().ToString("N");
            var fileName = id + ".gif";
            var destPath = Path.Combine(Directory, fileName);

            using (var src = await file.OpenStreamForReadAsync())
            using (var dst = File.Create(destPath))
                await src.CopyToAsync(dst);

            int width = 0, height = 0;
            try
            {
                using var stream = File.OpenRead(destPath);
                var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
                width = (int)decoder.PixelWidth;
                height = (int)decoder.PixelHeight;
            }
            catch
            {
                // Undecodable file: reject the import and clean up the copy.
                try { File.Delete(destPath); } catch { }
                return null;
            }

            return new IdleGifConfig
            {
                Id = id,
                FileName = fileName,
                OriginalName = file.Name,
                AddedUtc = DateTime.UtcNow,
                SizeBytes = new FileInfo(destPath).Length,
                PixelWidth = width,
                PixelHeight = height,
            };
        }
        catch
        {
            return null;
        }
    }

    public void Delete(IdleGifConfig config)
    {
        try
        {
            var path = PathFor(config);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    public string PathFor(IdleGifConfig config) => Path.Combine(Directory, config.FileName);

    public bool Exists(IdleGifConfig config)
    {
        try { return File.Exists(PathFor(config)); }
        catch { return false; }
    }

    public long TotalSizeBytes(IEnumerable<IdleGifConfig> configs)
    {
        long total = 0;
        foreach (var c in configs)
        {
            try
            {
                var path = PathFor(c);
                if (File.Exists(path))
                    total += new FileInfo(path).Length;
            }
            catch { }
        }
        return total;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: Build succeeded. (`System.IO.Directory` is fully qualified to avoid clashing with the static `Directory` field.)

- [ ] **Step 3: Commit**

```bash
git add Core/Services/IdleGifLibraryService.cs
git commit -m "Add IdleGifLibraryService for the idle-gif cache folder"
```

---

### Task 3: `IdleGifViewModel`

**Files:**
- Create: `Core/ViewModels/IdleGifViewModel.cs`

**Interfaces:**
- Consumes: `IdleGifConfig` (Task 1).
- Produces: `IdleGifViewModel` with:
  - ctor `IdleGifViewModel(IdleGifConfig config, string filePath)`
  - `string Id` (from config)
  - `IdleGifConfig Config`
  - `string FilePath` (absolute path)
  - `ImageSource Source` — a `BitmapImage` over `FilePath`, type-correct for `Image.Source` and animates the GIF. Built once in the ctor (avoids re-decoding on every binding read).
  - `string OriginalName`
  - `string DimensionsText` → e.g. `"240 × 240"`
  - `string SizeText` → e.g. `"1.2 MB"`
  - `string DetailText` → e.g. `"240 × 240 · 1.2 MB"`
  - `[ObservableProperty] bool isSelected`

- [ ] **Step 1: Create the view-model**

`Core/ViewModels/IdleGifViewModel.cs`:

```csharp
using System;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AudioMixerWin.Core.ViewModels;

public partial class IdleGifViewModel : ObservableObject
{
    public IdleGifViewModel(IdleGifConfig config, string filePath)
    {
        Config = config;
        FilePath = filePath;
        Source = new BitmapImage(new Uri(filePath));
    }

    public IdleGifConfig Config { get; }

    public string Id => Config.Id;

    public string FilePath { get; }

    // BitmapImage over the local file: type-correct for Image.Source and plays
    // the animated GIF (AutoPlay defaults true).
    public ImageSource Source { get; }

    public string OriginalName => Config.OriginalName;

    public string DimensionsText => $"{Config.PixelWidth} × {Config.PixelHeight}";

    public string SizeText => FormatSize(Config.SizeBytes);

    public string DetailText => $"{DimensionsText} · {SizeText}";

    [ObservableProperty]
    private bool isSelected;

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Core/ViewModels/IdleGifViewModel.cs
git commit -m "Add IdleGifViewModel for library entries"
```

---

### Task 4: `IdleScreenViewModel`

**Files:**
- Create: `Core/ViewModels/IdleScreenViewModel.cs`

**Interfaces:**
- Consumes: `AppSettings` (shared instance), `IdleGifLibraryService` (Task 2), `IdleGifViewModel` (Task 3), `IdleGifConfig` (Task 1).
- Produces: `IdleScreenViewModel` with:
  - ctor `IdleScreenViewModel(AppSettings settings, IdleGifLibraryService library, Action save, Func<Task<IReadOnlyList<StorageFile>>> pickGifs, Func<XamlRoot?> getXamlRoot)`
  - `ObservableCollection<IdleGifViewModel> Gifs`
  - `[ObservableProperty] IdleGifViewModel? selectedGif`
  - `[ObservableProperty] string usageText` (e.g. `"3 GIFs · 4.2 MB"`)
  - `bool HasGifs` (for empty-state visibility), raised when the collection changes
  - `IAsyncRelayCommand AddGifsCommand`
  - `IRelayCommand<IdleGifViewModel> SetActiveCommand`
  - `IAsyncRelayCommand<IdleGifViewModel> DeleteGifCommand`

**Notes on the injected delegates:** the picker (`pickGifs`) and `getXamlRoot` are supplied by the page/window so this view-model stays free of WinUI window-handle plumbing. `pickGifs` returns the picked `.gif` files (empty list if cancelled); `getXamlRoot` provides the `XamlRoot` the delete `ContentDialog` needs.

- [ ] **Step 1: Create the view-model**

`Core/ViewModels/IdleScreenViewModel.cs`:

```csharp
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
        UpdateUsage();
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

        var dialog = new ContentDialog
        {
            Title = "Delete GIF",
            Content = $"Remove \"{gif.OriginalName}\" from your idle-screen library?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = _getXamlRoot(),
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
        UpdateUsage();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Core/ViewModels/IdleScreenViewModel.cs
git commit -m "Add IdleScreenViewModel with add/select/delete commands"
```

---

### Task 5: Expose the view-model from `MainViewModel`

**Files:**
- Modify: `Core/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `IdleGifLibraryService` (Task 2), `IdleScreenViewModel` (Task 4).
- Produces: `MainViewModel.IdleScreen` (an `IdleScreenViewModel?`), plus
  `MainViewModel.InitIdleScreen(Func<Task<IReadOnlyList<StorageFile>>> pickGifs, Func<XamlRoot?> getXamlRoot)` which builds it. Two-phase init is used because the picker needs the window handle, which `MainWindow` supplies after the VM is constructed.

- [ ] **Step 1: Add the service field and property**

In `Core/ViewModels/MainViewModel.cs`, add a `using` if not present and a field + property. Near the other private fields (after `private SerialManager _serial;`):

```csharp
    private readonly IdleGifLibraryService _idleGifLibrary = new();

    public IdleScreenViewModel? IdleScreen { get; private set; }
```

(The file already has `using AudioMixerWin.Core.Services;`. Add `using System;`, `using System.Collections.Generic;`, `using System.Threading.Tasks;`, `using Microsoft.UI.Xaml;` only if missing — `System` and `Microsoft.UI.Xaml` are already imported; `System.Collections.Generic` and `System.Threading.Tasks` may need adding. `Windows.Storage` is needed for `StorageFile` — add `using Windows.Storage;`.)

- [ ] **Step 2: Add the two-phase initializer**

Add this method to `MainViewModel` (e.g. just after the constructor):

```csharp
    public void InitIdleScreen(
        Func<Task<IReadOnlyList<Windows.Storage.StorageFile>>> pickGifs,
        Func<XamlRoot?> getXamlRoot)
    {
        IdleScreen = new IdleScreenViewModel(
            _settings, _idleGifLibrary, () => SettingsService.Save(_settings), pickGifs, getXamlRoot);
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Core/ViewModels/MainViewModel.cs
git commit -m "Wire IdleScreenViewModel into MainViewModel"
```

---

### Task 6: `IdleScreenPage` (Gallery layout)

**Files:**
- Create: `Core/Views/IdleScreenPage.xaml`
- Create: `Core/Views/IdleScreenPage.xaml.cs`

**Interfaces:**
- Consumes: `IdleScreenViewModel` (Task 4), `IdleGifViewModel` (Task 3).
- Produces: `AudioMixerWin.Core.Views.IdleScreenPage` with ctor `IdleScreenPage(IdleScreenViewModel viewModel)` and a `ViewModel` property used by `x:Bind`.

**Layout (matches the approved Option A):** header with title + "Upload GIF" button; a hero row (animated preview + name/detail + "Selected" pill) shown when a GIF is selected, with a dashed empty-state prompt otherwise; a usage line; an `ItemsRepeater`+`UniformGridLayout` grid of animated thumbnail cards, each with a hover overlay carrying ✓ (set selected) and 🗑 (delete) and a selected outline.

**Note on the mockup's inline "Add" tile:** the Option-A mockup showed an add tile as the first grid cell. Because composing a non-data tile as the first item of an `ItemsRepeater` bound to `Gifs` is awkward, the add affordance is consolidated into the header **Upload GIF** button (plus the empty-state prompt). Same command, one control instead of two. Flag on plan review if you specifically want the inline tile back.

**Note on the overlay:** the spec describes the ✓/🗑 actions appearing on hover. For a first pass the overlay strip is always visible at the bottom of each card (simpler, no `VisualStateManager` pointer states). Hover-reveal can be a polish follow-up; call it out on review if you want hover-only now.

- [ ] **Step 1: Create the XAML**

`Core/Views/IdleScreenPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="AudioMixerWin.Core.Views.IdleScreenPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:vm="using:AudioMixerWin.Core.ViewModels"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <Page.Resources>
        <SolidColorBrush x:Key="ButtonBackground" Color="#1C1C1C"/>
        <SolidColorBrush x:Key="ButtonBackgroundPointerOver" Color="#282828"/>
        <SolidColorBrush x:Key="ButtonBackgroundPressed" Color="#111111"/>
        <SolidColorBrush x:Key="ButtonBorderBrush" Color="#383838"/>
        <SolidColorBrush x:Key="ButtonBorderBrushPointerOver" Color="#505050"/>
        <SolidColorBrush x:Key="ButtonBorderBrushPressed" Color="#282828"/>
        <SolidColorBrush x:Key="ButtonForeground" Color="#D0D0D0"/>
        <SolidColorBrush x:Key="ButtonForegroundPointerOver" Color="#F0F0F0"/>
        <SolidColorBrush x:Key="ButtonForegroundPressed" Color="#A0A0A0"/>
    </Page.Resources>

    <ScrollViewer>
        <StackPanel Padding="24" Spacing="16">

            <Grid>
                <TextBlock Text="Idle Screen" FontSize="20" FontWeight="SemiBold"
                           FontFamily="Segoe UI Variable Display" Foreground="#E0E0E0"
                           VerticalAlignment="Center" />
                <Button Content="Upload GIF" HorizontalAlignment="Right"
                        Command="{x:Bind ViewModel.AddGifsCommand}"
                        BorderThickness="1" CornerRadius="8" />
            </Grid>

            <TextBlock Text="What your controller shows when nothing's playing"
                       Foreground="#808080" FontSize="12" Margin="0,-8,0,0" />

            <!-- Hero: selected GIF -->
            <Border Background="#141414" BorderBrush="#262626" BorderThickness="1"
                    CornerRadius="10" Padding="16"
                    Visibility="{x:Bind ViewModel.SelectedGif, Mode=OneWay,
                                 Converter={StaticResource NullToCollapsed}}">
                <StackPanel Orientation="Horizontal" Spacing="16">
                    <Border Width="120" Height="120" CornerRadius="9" Background="#0A0A0A">
                        <Image Source="{x:Bind ViewModel.SelectedGif.Source, Mode=OneWay}"
                               Stretch="UniformToFill" />
                    </Border>
                    <StackPanel VerticalAlignment="Center" Spacing="6">
                        <Border Background="#163327" CornerRadius="20" Padding="9,3"
                                HorizontalAlignment="Left">
                            <TextBlock Text="Selected" Foreground="#8FE0BE" FontSize="11" />
                        </Border>
                        <TextBlock Text="{x:Bind ViewModel.SelectedGif.OriginalName, Mode=OneWay}"
                                   Foreground="#F2F2F2" FontSize="15" FontWeight="SemiBold" />
                        <TextBlock Text="{x:Bind ViewModel.SelectedGif.DetailText, Mode=OneWay}"
                                   Foreground="#808080" FontSize="12" />
                    </StackPanel>
                </StackPanel>
            </Border>

            <!-- Hero: empty state -->
            <Border BorderBrush="#3A3A3A" BorderThickness="1" CornerRadius="10" Padding="24"
                    Background="#101010"
                    Visibility="{x:Bind ViewModel.SelectedGif, Mode=OneWay,
                                 Converter={StaticResource NullToVisible}}">
                <StackPanel HorizontalAlignment="Center" Spacing="6">
                    <TextBlock Text="No idle GIF selected" Foreground="#C8C8C8"
                               FontSize="14" HorizontalAlignment="Center" />
                    <TextBlock Text="Upload a GIF and pick it to use as your idle screen."
                               Foreground="#808080" FontSize="12" HorizontalAlignment="Center" />
                </StackPanel>
            </Border>

            <TextBlock Text="{x:Bind ViewModel.UsageText, Mode=OneWay}"
                       Foreground="#6E6E6E" FontSize="11" />

            <!-- Library grid -->
            <ItemsRepeater ItemsSource="{x:Bind ViewModel.Gifs}">
                <ItemsRepeater.Layout>
                    <UniformGridLayout MinItemWidth="150" MinItemHeight="110"
                                       MinColumnSpacing="10" MinRowSpacing="10"
                                       ItemsStretch="Fill" />
                </ItemsRepeater.Layout>
                <ItemsRepeater.ItemTemplate>
                    <DataTemplate x:DataType="vm:IdleGifViewModel">
                        <Grid CornerRadius="9" Background="#141414"
                              BorderBrush="{x:Bind IsSelected, Mode=OneWay,
                                            Converter={StaticResource SelectedToBorderBrush}}"
                              BorderThickness="{x:Bind IsSelected, Mode=OneWay,
                                            Converter={StaticResource SelectedToBorderThickness}}">
                            <Image Source="{x:Bind Source}" Stretch="UniformToFill" />
                            <Grid Background="#99000000" VerticalAlignment="Bottom"
                                  Padding="8" ColumnSpacing="6">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="{x:Bind OriginalName}"
                                           Foreground="#E0E0E0" FontSize="11"
                                           TextTrimming="CharacterEllipsis"
                                           VerticalAlignment="Center" />
                                <Button Grid.Column="1" Padding="6" CornerRadius="6"
                                        Tag="{x:Bind}" Click="OnSetActiveClick"
                                        ToolTipService.ToolTip="Set as idle screen">
                                    <FontIcon Glyph="&#xE73E;" FontSize="12" />
                                </Button>
                                <Button Grid.Column="2" Padding="6" CornerRadius="6"
                                        Tag="{x:Bind}" Click="OnDeleteClick"
                                        ToolTipService.ToolTip="Delete">
                                    <FontIcon Glyph="&#xE74D;" FontSize="12" />
                                </Button>
                            </Grid>
                        </Grid>
                    </DataTemplate>
                </ItemsRepeater.ItemTemplate>
            </ItemsRepeater>

        </StackPanel>
    </ScrollViewer>
</Page>
```

Note: the per-card ✓/🗑 buttons use the codebase's established `Tag="{x:Bind}"` + `Click` pattern (see `SettingsPage.xaml.cs:46` — `x:Bind` to a command inside a `DataTemplate` crashes the XamlCompiler, and `ElementName` bindings are unreliable inside `ItemsRepeater`). The `Click` handlers (Step 2) read the bound `IdleGifViewModel` back off `Tag`. The four converters are registered in Task 7, so this page only builds after Task 7 — build verification for Task 6 happens at the end of Task 7. Glyphs: `&#xE73E;` = Segoe Fluent "Accept/checkmark", `&#xE74D;` = "Delete/trash".

- [ ] **Step 2: Create the code-behind**

`Core/Views/IdleScreenPage.xaml.cs` (the `Click` handlers read the bound item off `Tag`, mirroring `SettingsPage.OnHideClick`):

```csharp
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Views;

public sealed partial class IdleScreenPage : Page
{
    public IdleScreenViewModel ViewModel { get; }

    public IdleScreenPage(IdleScreenViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void OnSetActiveClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is IdleGifViewModel gif)
            ViewModel.SetActiveCommand.Execute(gif);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is IdleGifViewModel gif)
            ViewModel.DeleteGifCommand.Execute(gif);
    }
}
```

(`DeleteGifCommand` is an `IAsyncRelayCommand<IdleGifViewModel>`; calling `.Execute(gif)` fires it and the generated command manages the async run — no `await` needed in the handler.)

- [ ] **Step 3: Defer build to Task 7**

This page references converters created in Task 7. Do not build standalone here. Commit the files; the build gate runs at the end of Task 7.

- [ ] **Step 4: Commit**

```bash
git add Core/Views/IdleScreenPage.xaml Core/Views/IdleScreenPage.xaml.cs
git commit -m "Add IdleScreenPage gallery layout (converters pending)"
```

---

### Task 7: Visibility/selection converters

**Files:**
- Create: `Core/Converters/NullToVisibilityConverter.cs`
- Create: `Core/Converters/SelectedToBorderConverters.cs`
- Modify: `App.xaml`

**Interfaces:**
- Consumes: nothing.
- Produces, in namespace `AudioMixerWin.Core.Converters`:
  - `NullToCollapsedConverter` (`IValueConverter`) — non-null → `Visible`, null → `Collapsed`.
  - `NullToVisibleConverter` (`IValueConverter`) — null → `Visible`, non-null → `Collapsed`.
  - `SelectedToBorderBrushConverter` (`IValueConverter`) — `bool true` → `#46C28E` brush, else transparent.
  - `SelectedToBorderThicknessConverter` (`IValueConverter`) — `bool true` → `Thickness(2)`, else `Thickness(0)`.
- App-level resource keys: `NullToCollapsed`, `NullToVisible`, `SelectedToBorderBrush`, `SelectedToBorderThickness`.

Check `Core/Converters/ProcessNameDisplayConverter.cs` first to match the existing converter style.

- [ ] **Step 1: Create the null→visibility converters**

`Core/Converters/NullToVisibilityConverter.cs`:

```csharp
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AudioMixerWin.Core.Converters;

public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public class NullToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value == null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 2: Create the selection converters**

`Core/Converters/SelectedToBorderConverters.cs`:

```csharp
using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AudioMixerWin.Core.Converters;

public class SelectedToBorderBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Selected =
        new(Color.FromArgb(0xFF, 0x46, 0xC2, 0x8E));
    private static readonly SolidColorBrush None =
        new(Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Selected : None;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public class SelectedToBorderThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? new Thickness(2) : new Thickness(0);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 3: Register the converters in `App.xaml`**

`App.xaml` already has an `Application.Resources` → `ResourceDictionary` containing the `XamlControlsResources` merged dictionary and the theme color keys. Two edits:

First, add the converters namespace to the `<Application>` element (alongside the existing `xmlns:local`):

```xml
    xmlns:converters="using:AudioMixerWin.Core.Converters"
```

Second, add the four converter instances inside the existing `<ResourceDictionary>`, immediately after the `</ResourceDictionary.MergedDictionaries>` closing tag and before the `<!-- Carbon theme ... -->` color keys:

```xml
            <converters:NullToCollapsedConverter x:Key="NullToCollapsed" />
            <converters:NullToVisibleConverter x:Key="NullToVisible" />
            <converters:SelectedToBorderBrushConverter x:Key="SelectedToBorderBrush" />
            <converters:SelectedToBorderThicknessConverter x:Key="SelectedToBorderThickness" />
```

Do not add a second merged dictionary or a second `Application.Resources` — reuse the existing one.

- [ ] **Step 4: Build (covers Task 6 + Task 7)**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: Build succeeded. (`IdleScreenPage` now resolves all four converter keys.)

- [ ] **Step 5: Commit**

```bash
git add Core/Converters/NullToVisibilityConverter.cs Core/Converters/SelectedToBorderConverters.cs App.xaml
git commit -m "Add idle-screen converters and register them in App.xaml"
```

---

### Task 8: Navigation + page wiring + file picker

**Files:**
- Modify: `MainWindow.xaml`
- Modify: `MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MainViewModel.InitIdleScreen(...)` + `MainViewModel.IdleScreen` (Task 5), `IdleScreenPage` (Task 6).
- Produces: a working **Idle Screen** nav item that shows `IdleScreenPage`, with the file picker initialized against the window handle.

- [ ] **Step 1: Add the nav item**

In `MainWindow.xaml`, inside `<NavigationView.MenuItems>`, after the existing Mixer item, add:

```xml
                <NavigationViewItem Content="Idle Screen" Tag="idle">
                    <NavigationViewItem.Icon>
                        <SymbolIcon Symbol="Pictures" />
                    </NavigationViewItem.Icon>
                </NavigationViewItem>
```

(Keep the existing Mixer item with `Tag="mixer"` and `IsSelected="True"`.)

- [ ] **Step 2: Add the page field**

In `MainWindow.xaml.cs`, next to `private readonly SettingsPage _settingsPage;`, add:

```csharp
        private IdleScreenPage _idleScreenPage = null!;
```

- [ ] **Step 3: Initialize the idle-screen VM + page in the constructor**

In the `MainWindow` constructor, after `_settingsPage = new SettingsPage(ViewModel);` and before `ContentFrame.Content = _mainPage;`, add:

```csharp
            ViewModel.InitIdleScreen(PickGifFilesAsync, () => Content?.XamlRoot);
            _idleScreenPage = new IdleScreenPage(ViewModel.IdleScreen!);
```

- [ ] **Step 4: Add the picker helper**

Add this method to `MainWindow` (it uses the already-imported `WinRT.Interop` and `_hwnd`; add `using System.Collections.Generic;`, `using System.Threading.Tasks;`, `using Windows.Storage;`, `using Windows.Storage.Pickers;` at the top if missing):

```csharp
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
```

- [ ] **Step 5: Route navigation by tag**

Replace the body of `OnNavSelectionChanged` with a three-way switch:

```csharp
        private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                ContentFrame.Content = _settingsPage;
                return;
            }

            var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
            ContentFrame.Content = tag == "idle" ? _idleScreenPage : _mainPage;
        }
```

- [ ] **Step 6: Build**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add MainWindow.xaml MainWindow.xaml.cs
git commit -m "Add Idle Screen nav item, page wiring, and GIF file picker"
```

---

### Task 9: Manual verification

**Files:** none (verification only).

No automated tests exist for this WinUI/IO feature; verify by running the app.

- [ ] **Step 1: Run the app**

Run from Visual Studio/Rider with the `AudioMixerWin (Unpackaged)` profile, or:
`dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug` then launch the built exe.

- [ ] **Step 2: Walk the checklist**

Confirm each, matching the spec's Testing section:
1. The **Idle Screen** item appears in the nav pane between Mixer and Settings; selecting it shows the page; switching back to Mixer and to Settings still works.
2. **Upload GIF** (and the empty-state has you upload) opens a picker filtered to `.gif`; picking one or more adds animated thumbnails; `%LocalAppData%\AudioMixerWin\idle-gifs\` now holds the copied files.
3. Clicking a card's ✓ selects it: the hero shows it animating with name + `W × H · size` and the "Selected" pill, and the card gets the green outline.
4. Close and relaunch the app — the same GIF is still selected (hero populated, outline present).
5. Click a card's 🗑 → confirm dialog → on Delete the thumbnail and the cached file are gone. Delete the selected GIF → hero returns to the empty state.
6. The usage line ("N GIFs · X MB") updates after each add and delete.

- [ ] **Step 3: Note any failures**

If any step fails, capture the exact behavior and fix in the relevant task's files before marking the plan complete. Do not claim completion until steps 1–6 pass.
