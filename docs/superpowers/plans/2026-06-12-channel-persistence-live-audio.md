# Channel Persistence, Live NAudio Feed & Process Filtering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the user's knob→app channel mappings across restarts, keep the "Select App" picker fed by a live, auto-refreshing list of real NAudio sessions, and let the user hide programs they don't care about from that picker.

**Architecture:** Extend `AppSettings`/`SettingsService` (already used for COM port/baud rate) with a `Channels` list and an `ExcludedProcesses` list. `MainViewModel` loads/saves these, runs a `DispatcherTimer` that polls `AudioManager.GetSessions()` into a shared `ObservableCollection<AudioSession>` (`AvailableSessions`), filtered by `ExcludedProcesses`. The picker becomes a dedicated `AppPickerDialog` bound to that live collection, with a per-row "Hide" action. UI gets a bottom-right "+" FAB and a configurable refresh-interval `NumberBox`.

**Tech Stack:** .NET 8, WinUI 3 (Windows App SDK), CommunityToolkit.Mvvm, NAudio, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-06-12-channel-persistence-live-audio-design.md`

---

## Important notes for the implementer

- Build command for every "build" step: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug` (run from the repo root). Expected: `Build succeeded.` / `0 Error(s)`.
- There is no test project. Each task's verification is "build succeeds" plus, where noted, a short manual run of the app (`dotnet build` produces `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\AudioMixerWin.exe`, or run via the `AudioMixerWin (Unpackaged)` launch profile in Visual Studio/Rider).
- Settings file to inspect during manual checks: `%LocalAppData%\AudioMixerWin\settings.json`.
- Each task below shows the **full content** of every file it touches, reflecting the cumulative state after that task (i.e. you do not need to read earlier tasks to know what a file should contain at this point).

---

### Task 1: Settings model — `ChannelConfig` + `AppSettings` additions

**Files:**
- Create: `Core/Models/ChannelConfig.cs`
- Modify: `Core/Services/AppSettings.cs`

- [ ] **Step 1: Create the `ChannelConfig` model**

`Core/Models/ChannelConfig.cs`:

```csharp
namespace AudioMixerWin.Core.Models;

public class ChannelConfig
{
    public int KnobIndex { get; set; }
    public string AppName { get; set; } = "";
}
```

- [ ] **Step 2: Extend `AppSettings` with `RefreshIntervalSeconds`, `Channels`, `ExcludedProcesses`**

`Core/Services/AppSettings.cs`:

```csharp
using System.Collections.Generic;
using AudioMixerWin.Core.Models;

namespace AudioMixerWin.Core.Services;

public class AppSettings
{
    public string ComPort { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public int RefreshIntervalSeconds { get; set; } = 2;
    public List<ChannelConfig> Channels { get; set; } = new();
    public List<string> ExcludedProcesses { get; set; } = new();
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add Core/Models/ChannelConfig.cs Core/Services/AppSettings.cs
git commit -m "Add ChannelConfig model and extend AppSettings for persistence"
```

---

### Task 2: Channel list persistence (load/save) + Reconnect bug fix

**Files:**
- Modify: `Core/ViewModels/ChannelViewModel.cs`
- Modify: `Core/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add an `onSettingsChanged` callback to `ChannelViewModel`**

`Core/ViewModels/ChannelViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioMixerWin.Core.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly Action<ChannelViewModel> _onRemove;
    private readonly Action _onSettingsChanged;

    public int KnobIndex { get; }

    public string KnobLabel => $"Knob {KnobIndex + 1}";

    [ObservableProperty]
    private string appName;

    [ObservableProperty]
    private double volume;

    public ChannelViewModel(
        int knobIndex,
        string appName,
        AudioManager audioManager,
        Action<ChannelViewModel> onRemove,
        Action onSettingsChanged)
    {
        KnobIndex = knobIndex;
        _audioManager = audioManager;
        _onRemove = onRemove;
        _onSettingsChanged = onSettingsChanged;
        this.appName = appName;
        volume = audioManager.GetVolume(appName) * 100;
    }

    partial void OnAppNameChanged(string value)
    {
        Volume = _audioManager.GetVolume(value) * 100;
        _onSettingsChanged();
    }

    partial void OnVolumeChanged(double value) =>
        _audioManager.SetVolume(AppName, (float)(value / 100.0));

    public List<AudioSession> GetAvailableSessions() => _audioManager.GetSessions();

    public void Remove() => _onRemove(this);
}
```

(`GetAvailableSessions()` stays for now — `KnobCard` still calls it. It's removed in Task 3.)

- [ ] **Step 2: Load/save the channel list and fix `Reconnect` in `MainViewModel`**

`Core/ViewModels/MainViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace AudioMixerWin.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AppSettings _settings;
    private SerialManager _serial;

    [ObservableProperty]
    private string comPort;

    [ObservableProperty]
    private int baudRate;

    [ObservableProperty]
    private string serialStatus = "Not connected";

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _audioManager = new AudioManager();
        _settings = SettingsService.Load();

        comPort = _settings.ComPort;
        baudRate = _settings.BaudRate;

        foreach (var config in _settings.Channels)
            AddChannelInternal(config.AppName, config.KnobIndex, save: false);

        _serial = CreateAndStartSerial();
    }

    private SerialManager CreateAndStartSerial()
    {
        var serial = new SerialManager(ComPort, BaudRate);
        serial.KnobChanged += OnKnobChanged;

        try
        {
            serial.Start();
            SerialStatus = $"Connected to {ComPort} @ {BaudRate}";
        }
        catch (Exception ex)
        {
            SerialStatus = $"Disconnected: {ex.Message}";
        }

        return serial;
    }

    [RelayCommand]
    private void Reconnect()
    {
        _serial.Stop();
        _serial = CreateAndStartSerial();

        _settings.ComPort = ComPort;
        _settings.BaudRate = BaudRate;
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private void AddChannel() => AddChannelInternal("Select App");

    private void AddChannelInternal(string appName, int? knobIndex = null, bool save = true)
    {
        var index = knobIndex ?? (Channels.Count == 0 ? 0 : Channels.Max(c => c.KnobIndex) + 1);
        Channels.Add(new ChannelViewModel(index, appName, _audioManager, RemoveChannelInternal, SaveChannels));

        if (save)
            SaveChannels();
    }

    private void RemoveChannelInternal(ChannelViewModel channel)
    {
        Channels.Remove(channel);
        SaveChannels();
    }

    private void SaveChannels()
    {
        _settings.Channels = Channels
            .Select(c => new ChannelConfig { KnobIndex = c.KnobIndex, AppName = c.AppName })
            .ToList();

        SettingsService.Save(_settings);
    }

    private void OnKnobChanged(int knobIndex, float normalized)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == knobIndex);
            if (channel != null)
                channel.Volume = normalized * 100;
        });
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 4: Manual check — persistence**

Run the app. Click "Add Channel" twice, assign apps to each via the gear-icon picker (existing behavior). Close the app, check `%LocalAppData%\AudioMixerWin\settings.json` contains a `"channels"` array with your two entries (`knobIndex`/`appName`), then relaunch the app and confirm the same two channels with the same assigned apps reappear. Then click "Reconnect" on the Settings page and re-check `settings.json` — confirm `channels` is still present (this is the regression check for the `Reconnect` fix).

- [ ] **Step 5: Commit**

```bash
git add Core/ViewModels/ChannelViewModel.cs Core/ViewModels/MainViewModel.cs
git commit -m "Persist channel list (knob/app mappings) across restarts"
```

---

### Task 3: Live `AvailableSessions` feed from NAudio

**Files:**
- Modify: `Core/ViewModels/ChannelViewModel.cs`
- Modify: `Core/ViewModels/MainViewModel.cs`
- Modify: `Core/Controls/KnobCard.xaml.cs`

- [ ] **Step 1: `ChannelViewModel` exposes the shared `AvailableSessions` collection**

`Core/ViewModels/ChannelViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioMixerWin.Core.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly Action<ChannelViewModel> _onRemove;
    private readonly Action _onSettingsChanged;

    public int KnobIndex { get; }

    public string KnobLabel => $"Knob {KnobIndex + 1}";

    public ObservableCollection<AudioSession> AvailableSessions { get; }

    [ObservableProperty]
    private string appName;

    [ObservableProperty]
    private double volume;

    public ChannelViewModel(
        int knobIndex,
        string appName,
        AudioManager audioManager,
        ObservableCollection<AudioSession> availableSessions,
        Action<ChannelViewModel> onRemove,
        Action onSettingsChanged)
    {
        KnobIndex = knobIndex;
        _audioManager = audioManager;
        AvailableSessions = availableSessions;
        _onRemove = onRemove;
        _onSettingsChanged = onSettingsChanged;
        this.appName = appName;
        volume = audioManager.GetVolume(appName) * 100;
    }

    partial void OnAppNameChanged(string value)
    {
        Volume = _audioManager.GetVolume(value) * 100;
        _onSettingsChanged();
    }

    partial void OnVolumeChanged(double value) =>
        _audioManager.SetVolume(AppName, (float)(value / 100.0));

    public void Remove() => _onRemove(this);
}
```

- [ ] **Step 2: Add the polling timer and `AvailableSessions` to `MainViewModel`**

`Core/ViewModels/MainViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AudioMixerWin.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private SerialManager _serial;

    [ObservableProperty]
    private string comPort;

    [ObservableProperty]
    private int baudRate;

    [ObservableProperty]
    private string serialStatus = "Not connected";

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    public ObservableCollection<AudioSession> AvailableSessions { get; } = new();

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _audioManager = new AudioManager();
        _settings = SettingsService.Load();

        comPort = _settings.ComPort;
        baudRate = _settings.BaudRate;

        foreach (var config in _settings.Channels)
            AddChannelInternal(config.AppName, config.KnobIndex, save: false);

        _serial = CreateAndStartSerial();

        RefreshAvailableSessions();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshAvailableSessions();
        _refreshTimer.Start();
    }

    private SerialManager CreateAndStartSerial()
    {
        var serial = new SerialManager(ComPort, BaudRate);
        serial.KnobChanged += OnKnobChanged;

        try
        {
            serial.Start();
            SerialStatus = $"Connected to {ComPort} @ {BaudRate}";
        }
        catch (Exception ex)
        {
            SerialStatus = $"Disconnected: {ex.Message}";
        }

        return serial;
    }

    [RelayCommand]
    private void Reconnect()
    {
        _serial.Stop();
        _serial = CreateAndStartSerial();

        _settings.ComPort = ComPort;
        _settings.BaudRate = BaudRate;
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private void AddChannel() => AddChannelInternal("Select App");

    private void AddChannelInternal(string appName, int? knobIndex = null, bool save = true)
    {
        var index = knobIndex ?? (Channels.Count == 0 ? 0 : Channels.Max(c => c.KnobIndex) + 1);
        Channels.Add(new ChannelViewModel(index, appName, _audioManager, AvailableSessions, RemoveChannelInternal, SaveChannels));

        if (save)
            SaveChannels();
    }

    private void RemoveChannelInternal(ChannelViewModel channel)
    {
        Channels.Remove(channel);
        SaveChannels();
    }

    private void SaveChannels()
    {
        _settings.Channels = Channels
            .Select(c => new ChannelConfig { KnobIndex = c.KnobIndex, AppName = c.AppName })
            .ToList();

        SettingsService.Save(_settings);
    }

    private void RefreshAvailableSessions()
    {
        var current = _audioManager.GetSessions();

        for (var i = AvailableSessions.Count - 1; i >= 0; i--)
        {
            if (!current.Any(s => s.ProcessName.Equals(AvailableSessions[i].ProcessName, StringComparison.OrdinalIgnoreCase)))
                AvailableSessions.RemoveAt(i);
        }

        foreach (var session in current)
        {
            if (!AvailableSessions.Any(s => s.ProcessName.Equals(session.ProcessName, StringComparison.OrdinalIgnoreCase)))
                AvailableSessions.Add(session);
        }
    }

    private void OnKnobChanged(int knobIndex, float normalized)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == knobIndex);
            if (channel != null)
                channel.Volume = normalized * 100;
        });
    }
}
```

- [ ] **Step 3: Point `KnobCard`'s picker at the live collection**

`Core/Controls/KnobCard.xaml.cs`:

```csharp
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Controls;

public sealed partial class KnobCard : UserControl
{
    public static readonly DependencyProperty ChannelProperty =
        DependencyProperty.Register(nameof(Channel), typeof(ChannelViewModel), typeof(KnobCard), new PropertyMetadata(null));

    public ChannelViewModel? Channel
    {
        get => (ChannelViewModel?)GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    public KnobCard()
    {
        InitializeComponent();
    }

    public string FormatPercent(double volume) => $"{volume:0}%";

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Channel is null)
            return;

        var listView = new ListView
        {
            ItemsSource = Channel.AvailableSessions,
            DisplayMemberPath = nameof(AudioSession.ProcessName),
            SelectionMode = ListViewSelectionMode.Single
        };

        var dialog = new ContentDialog
        {
            Title = "Select App",
            Content = listView,
            PrimaryButtonText = "Select",
            SecondaryButtonText = "Remove Channel",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && listView.SelectedItem is AudioSession session)
        {
            Channel.AppName = session.ProcessName;
        }
        else if (result == ContentDialogResult.Secondary)
        {
            Channel.Remove();
        }
    }
}
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 5: Manual check — live refresh**

Run the app, open the gear-icon picker on any channel, and leave it open. In another window, start playing audio in an app that wasn't previously listed (e.g. open a YouTube video in a browser that wasn't running before). Within ~2 seconds, confirm the new process appears in the picker list without closing/reopening the dialog. Stop the audio/close that app and confirm it disappears from the list within ~2 seconds.

- [ ] **Step 6: Commit**

```bash
git add Core/ViewModels/ChannelViewModel.cs Core/ViewModels/MainViewModel.cs Core/Controls/KnobCard.xaml.cs
git commit -m "Feed channel picker from a live, auto-refreshing NAudio session list"
```

---

### Task 4: Per-row "Hide" action via a new `AppPickerDialog`

**Files:**
- Modify: `Core/ViewModels/ChannelViewModel.cs`
- Modify: `Core/ViewModels/MainViewModel.cs`
- Create: `Core/Controls/AppPickerDialog.xaml`
- Create: `Core/Controls/AppPickerDialog.xaml.cs`
- Modify: `Core/Controls/KnobCard.xaml.cs`

- [ ] **Step 1: Add `HideSession` to `ChannelViewModel`**

`Core/ViewModels/ChannelViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioMixerWin.Core.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly Action<ChannelViewModel> _onRemove;
    private readonly Action _onSettingsChanged;
    private readonly Action<AudioSession> _onHideSession;

    public int KnobIndex { get; }

    public string KnobLabel => $"Knob {KnobIndex + 1}";

    public ObservableCollection<AudioSession> AvailableSessions { get; }

    [ObservableProperty]
    private string appName;

    [ObservableProperty]
    private double volume;

    public ChannelViewModel(
        int knobIndex,
        string appName,
        AudioManager audioManager,
        ObservableCollection<AudioSession> availableSessions,
        Action<ChannelViewModel> onRemove,
        Action onSettingsChanged,
        Action<AudioSession> onHideSession)
    {
        KnobIndex = knobIndex;
        _audioManager = audioManager;
        AvailableSessions = availableSessions;
        _onRemove = onRemove;
        _onSettingsChanged = onSettingsChanged;
        _onHideSession = onHideSession;
        this.appName = appName;
        volume = audioManager.GetVolume(appName) * 100;
    }

    partial void OnAppNameChanged(string value)
    {
        Volume = _audioManager.GetVolume(value) * 100;
        _onSettingsChanged();
    }

    partial void OnVolumeChanged(double value) =>
        _audioManager.SetVolume(AppName, (float)(value / 100.0));

    public void HideSession(AudioSession session) => _onHideSession(session);

    public void Remove() => _onRemove(this);
}
```

- [ ] **Step 2: Add `HideSession` + exclusion filter to `MainViewModel`**

`Core/ViewModels/MainViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AudioMixerWin.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private SerialManager _serial;

    [ObservableProperty]
    private string comPort;

    [ObservableProperty]
    private int baudRate;

    [ObservableProperty]
    private string serialStatus = "Not connected";

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    public ObservableCollection<AudioSession> AvailableSessions { get; } = new();

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _audioManager = new AudioManager();
        _settings = SettingsService.Load();

        comPort = _settings.ComPort;
        baudRate = _settings.BaudRate;

        foreach (var config in _settings.Channels)
            AddChannelInternal(config.AppName, config.KnobIndex, save: false);

        _serial = CreateAndStartSerial();

        RefreshAvailableSessions();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshAvailableSessions();
        _refreshTimer.Start();
    }

    private SerialManager CreateAndStartSerial()
    {
        var serial = new SerialManager(ComPort, BaudRate);
        serial.KnobChanged += OnKnobChanged;

        try
        {
            serial.Start();
            SerialStatus = $"Connected to {ComPort} @ {BaudRate}";
        }
        catch (Exception ex)
        {
            SerialStatus = $"Disconnected: {ex.Message}";
        }

        return serial;
    }

    [RelayCommand]
    private void Reconnect()
    {
        _serial.Stop();
        _serial = CreateAndStartSerial();

        _settings.ComPort = ComPort;
        _settings.BaudRate = BaudRate;
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private void AddChannel() => AddChannelInternal("Select App");

    private void AddChannelInternal(string appName, int? knobIndex = null, bool save = true)
    {
        var index = knobIndex ?? (Channels.Count == 0 ? 0 : Channels.Max(c => c.KnobIndex) + 1);
        Channels.Add(new ChannelViewModel(index, appName, _audioManager, AvailableSessions, RemoveChannelInternal, SaveChannels, HideSession));

        if (save)
            SaveChannels();
    }

    private void RemoveChannelInternal(ChannelViewModel channel)
    {
        Channels.Remove(channel);
        SaveChannels();
    }

    private void SaveChannels()
    {
        _settings.Channels = Channels
            .Select(c => new ChannelConfig { KnobIndex = c.KnobIndex, AppName = c.AppName })
            .ToList();

        SettingsService.Save(_settings);
    }

    private void HideSession(AudioSession session)
    {
        if (!_settings.ExcludedProcesses.Contains(session.ProcessName, StringComparer.OrdinalIgnoreCase))
            _settings.ExcludedProcesses.Add(session.ProcessName);

        AvailableSessions.Remove(session);
        SettingsService.Save(_settings);
    }

    private void RefreshAvailableSessions()
    {
        var current = _audioManager.GetSessions()
            .Where(s => !_settings.ExcludedProcesses.Contains(s.ProcessName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        for (var i = AvailableSessions.Count - 1; i >= 0; i--)
        {
            if (!current.Any(s => s.ProcessName.Equals(AvailableSessions[i].ProcessName, StringComparison.OrdinalIgnoreCase)))
                AvailableSessions.RemoveAt(i);
        }

        foreach (var session in current)
        {
            if (!AvailableSessions.Any(s => s.ProcessName.Equals(session.ProcessName, StringComparison.OrdinalIgnoreCase)))
                AvailableSessions.Add(session);
        }
    }

    private void OnKnobChanged(int knobIndex, float normalized)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == knobIndex);
            if (channel != null)
                channel.Volume = normalized * 100;
        });
    }
}
```

- [ ] **Step 3: Create `AppPickerDialog.xaml`**

`Core/Controls/AppPickerDialog.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<ContentDialog
    x:Class="AudioMixerWin.Core.Controls.AppPickerDialog"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:models="using:AudioMixerWin.Core.Models"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d"
    Title="Select App"
    PrimaryButtonText="Select"
    SecondaryButtonText="Remove Channel"
    CloseButtonText="Cancel">

    <ListView x:Name="SessionsList" ItemsSource="{x:Bind AvailableSessions}" SelectionMode="Single">
        <ListView.ItemTemplate>
            <DataTemplate x:DataType="models:AudioSession">
                <Grid ColumnSpacing="8">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Text="{x:Bind ProcessName}" VerticalAlignment="Center" />
                    <Button Grid.Column="1" Content="Hide" Tag="{x:Bind}" Click="OnHideClick" />
                </Grid>
            </DataTemplate>
        </ListView.ItemTemplate>
    </ListView>
</ContentDialog>
```

- [ ] **Step 4: Create `AppPickerDialog.xaml.cs`**

`Core/Controls/AppPickerDialog.xaml.cs`:

```csharp
using System.Collections.ObjectModel;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Controls;

public sealed partial class AppPickerDialog : ContentDialog
{
    private readonly ChannelViewModel _channel;

    public ObservableCollection<AudioSession> AvailableSessions => _channel.AvailableSessions;

    public AudioSession? SelectedSession => SessionsList.SelectedItem as AudioSession;

    public AppPickerDialog(ChannelViewModel channel)
    {
        _channel = channel;
        InitializeComponent();
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is AudioSession session)
            _channel.HideSession(session);
    }
}
```

- [ ] **Step 5: Simplify `KnobCard.OnSettingsClick` to use `AppPickerDialog`**

`Core/Controls/KnobCard.xaml.cs`:

```csharp
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AudioMixerWin.Core.Controls;

public sealed partial class KnobCard : UserControl
{
    public static readonly DependencyProperty ChannelProperty =
        DependencyProperty.Register(nameof(Channel), typeof(ChannelViewModel), typeof(KnobCard), new PropertyMetadata(null));

    public ChannelViewModel? Channel
    {
        get => (ChannelViewModel?)GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    public KnobCard()
    {
        InitializeComponent();
    }

    public string FormatPercent(double volume) => $"{volume:0}%";

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Channel is null)
            return;

        var dialog = new AppPickerDialog(Channel) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && dialog.SelectedSession is AudioSession session)
        {
            Channel.AppName = session.ProcessName;
        }
        else if (result == ContentDialogResult.Secondary)
        {
            Channel.Remove();
        }
    }
}
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 7: Manual check — hide/exclude**

Run the app, open the picker on any channel. Click "Hide" next to a process you don't want to see (e.g. a background process). Confirm it disappears from the list immediately without closing the dialog. Close the dialog, reopen it — confirm the hidden process stays gone. Check `%LocalAppData%\AudioMixerWin\settings.json` — confirm `excludedProcesses` contains that process name. Confirm a channel already assigned to a hidden process still shows/controls volume normally.

- [ ] **Step 8: Commit**

```bash
git add Core/ViewModels/ChannelViewModel.cs Core/ViewModels/MainViewModel.cs Core/Controls/AppPickerDialog.xaml Core/Controls/AppPickerDialog.xaml.cs Core/Controls/KnobCard.xaml.cs
git commit -m "Add per-row Hide action to exclude processes from the app picker"
```

---

### Task 5: Bottom-right "+" FAB on `MainPage`

**Files:**
- Modify: `Core/Views/MainPage.xaml`

- [ ] **Step 1: Replace the top "Add Channel" button with a bottom-right FAB**

`Core/Views/MainPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="AudioMixerWin.Core.Views.MainPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:AudioMixerWin.Core.Controls"
    xmlns:vm="using:AudioMixerWin.Core.ViewModels"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <Grid Padding="24">

        <ScrollViewer>
            <ItemsRepeater ItemsSource="{x:Bind ViewModel.Channels}">
                <ItemsRepeater.Layout>
                    <UniformGridLayout MinItemWidth="260" MinItemHeight="200" ItemsStretch="Fill" />
                </ItemsRepeater.Layout>
                <ItemsRepeater.ItemTemplate>
                    <DataTemplate x:DataType="vm:ChannelViewModel">
                        <controls:KnobCard Channel="{x:Bind}" />
                    </DataTemplate>
                </ItemsRepeater.ItemTemplate>
            </ItemsRepeater>
        </ScrollViewer>

        <Button
            Command="{x:Bind ViewModel.AddChannelCommand}"
            Content="+"
            FontSize="24"
            Width="56"
            Height="56"
            CornerRadius="28"
            HorizontalAlignment="Right"
            VerticalAlignment="Bottom"
            Margin="0,0,24,24" />

    </Grid>
</Page>
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 3: Manual check**

Run the app. Confirm a circular "+" button appears anchored to the bottom-right of the Mixer page, and clicking it adds a new channel card (same as the old "Add Channel" button did).

- [ ] **Step 4: Commit**

```bash
git add Core/Views/MainPage.xaml
git commit -m "Move Add Channel control to a floating action button"
```

---

### Task 6: Configurable refresh interval

**Files:**
- Modify: `Core/ViewModels/MainViewModel.cs`
- Modify: `Core/Views/SettingsPage.xaml`

- [ ] **Step 1: Add `RefreshIntervalSeconds` property and wire it to the timer**

`Core/ViewModels/MainViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AudioMixerWin.Core.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private SerialManager _serial;

    [ObservableProperty]
    private string comPort;

    [ObservableProperty]
    private int baudRate;

    [ObservableProperty]
    private string serialStatus = "Not connected";

    [ObservableProperty]
    private double refreshIntervalSeconds;

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    public ObservableCollection<AudioSession> AvailableSessions { get; } = new();

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _audioManager = new AudioManager();
        _settings = SettingsService.Load();

        comPort = _settings.ComPort;
        baudRate = _settings.BaudRate;
        refreshIntervalSeconds = _settings.RefreshIntervalSeconds;

        foreach (var config in _settings.Channels)
            AddChannelInternal(config.AppName, config.KnobIndex, save: false);

        _serial = CreateAndStartSerial();

        RefreshAvailableSessions();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds) };
        _refreshTimer.Tick += (_, _) => RefreshAvailableSessions();
        _refreshTimer.Start();
    }

    private SerialManager CreateAndStartSerial()
    {
        var serial = new SerialManager(ComPort, BaudRate);
        serial.KnobChanged += OnKnobChanged;

        try
        {
            serial.Start();
            SerialStatus = $"Connected to {ComPort} @ {BaudRate}";
        }
        catch (Exception ex)
        {
            SerialStatus = $"Disconnected: {ex.Message}";
        }

        return serial;
    }

    [RelayCommand]
    private void Reconnect()
    {
        _serial.Stop();
        _serial = CreateAndStartSerial();

        _settings.ComPort = ComPort;
        _settings.BaudRate = BaudRate;
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private void AddChannel() => AddChannelInternal("Select App");

    private void AddChannelInternal(string appName, int? knobIndex = null, bool save = true)
    {
        var index = knobIndex ?? (Channels.Count == 0 ? 0 : Channels.Max(c => c.KnobIndex) + 1);
        Channels.Add(new ChannelViewModel(index, appName, _audioManager, AvailableSessions, RemoveChannelInternal, SaveChannels, HideSession));

        if (save)
            SaveChannels();
    }

    private void RemoveChannelInternal(ChannelViewModel channel)
    {
        Channels.Remove(channel);
        SaveChannels();
    }

    private void SaveChannels()
    {
        _settings.Channels = Channels
            .Select(c => new ChannelConfig { KnobIndex = c.KnobIndex, AppName = c.AppName })
            .ToList();

        SettingsService.Save(_settings);
    }

    private void HideSession(AudioSession session)
    {
        if (!_settings.ExcludedProcesses.Contains(session.ProcessName, StringComparer.OrdinalIgnoreCase))
            _settings.ExcludedProcesses.Add(session.ProcessName);

        AvailableSessions.Remove(session);
        SettingsService.Save(_settings);
    }

    private void RefreshAvailableSessions()
    {
        var current = _audioManager.GetSessions()
            .Where(s => !_settings.ExcludedProcesses.Contains(s.ProcessName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        for (var i = AvailableSessions.Count - 1; i >= 0; i--)
        {
            if (!current.Any(s => s.ProcessName.Equals(AvailableSessions[i].ProcessName, StringComparison.OrdinalIgnoreCase)))
                AvailableSessions.RemoveAt(i);
        }

        foreach (var session in current)
        {
            if (!AvailableSessions.Any(s => s.ProcessName.Equals(session.ProcessName, StringComparison.OrdinalIgnoreCase)))
                AvailableSessions.Add(session);
        }
    }

    partial void OnRefreshIntervalSecondsChanged(double value)
    {
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, value));
        _settings.RefreshIntervalSeconds = (int)value;
        SettingsService.Save(_settings);
    }

    private void OnKnobChanged(int knobIndex, float normalized)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var channel = Channels.FirstOrDefault(c => c.KnobIndex == knobIndex);
            if (channel != null)
                channel.Volume = normalized * 100;
        });
    }
}
```

- [ ] **Step 2: Add the refresh-interval `NumberBox` to `SettingsPage`**

`Core/Views/SettingsPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="AudioMixerWin.Core.Views.SettingsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <StackPanel Padding="24" Spacing="8" MaxWidth="400" HorizontalAlignment="Left">

        <TextBlock Text="Serial Connection" FontSize="20" FontWeight="SemiBold" Margin="0,0,0,8" />

        <TextBlock Text="COM Port" />
        <ComboBox
            IsEditable="True"
            ItemsSource="{x:Bind PortNames}"
            Text="{x:Bind ViewModel.ComPort, Mode=TwoWay}"
            HorizontalAlignment="Stretch" />

        <TextBlock Text="Baud Rate" Margin="0,12,0,0" />
        <ComboBox
            ItemsSource="{x:Bind BaudRates}"
            SelectedItem="{x:Bind ViewModel.BaudRate, Mode=TwoWay}"
            HorizontalAlignment="Stretch" />

        <Button Content="Reconnect" Command="{x:Bind ViewModel.ReconnectCommand}" Margin="0,16,0,0" />

        <TextBlock Text="{x:Bind ViewModel.SerialStatus, Mode=OneWay}" Opacity="0.7" Margin="0,8,0,0" />

        <TextBlock Text="Audio Sessions" FontSize="20" FontWeight="SemiBold" Margin="0,24,0,8" />

        <TextBlock Text="Refresh Interval (seconds)" />
        <NumberBox
            Value="{x:Bind ViewModel.RefreshIntervalSeconds, Mode=TwoWay}"
            Minimum="1"
            Maximum="30"
            SpinButtonPlacementMode="Inline"
            HorizontalAlignment="Stretch" />

    </StackPanel>
</Page>
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 4: Manual check — configurable interval**

Run the app, go to Settings, change "Refresh Interval (seconds)" to a different value (e.g. 5). Without restarting, open the picker and verify new/closed apps now take ~5 seconds to appear/disappear instead of ~2. Close and relaunch the app, return to Settings, confirm the value you set is still shown (i.e. it was persisted to `settings.json`).

- [ ] **Step 5: Commit**

```bash
git add Core/ViewModels/MainViewModel.cs Core/Views/SettingsPage.xaml
git commit -m "Make NAudio session refresh interval configurable"
```

---

### Task 7: Full manual verification pass

**Files:** none (verification only)

- [ ] **Step 1: Build and launch**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`, then launch `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\AudioMixerWin.exe` (or use the `AudioMixerWin (Unpackaged)` launch profile).

- [ ] **Step 2: Persistence round-trip**

Add 2-3 channels via the "+" FAB, assign different apps to each via the picker, and adjust their volume sliders. Close the app entirely, then relaunch it. Confirm the same channels appear, in the same knob order, with the same assigned app names (volumes will reflect each app's *current* live volume, which is expected since volume is never persisted — only `KnobIndex`/`AppName` are).

- [ ] **Step 3: Live session feed**

With the picker open on a channel, start an app that plays audio (e.g. a browser tab playing video) and confirm it appears in the list within the configured refresh interval. Stop the audio/close the app and confirm it disappears within the same interval.

- [ ] **Step 4: Hide/exclude**

In the picker, click "Hide" on a process. Confirm it vanishes immediately. Close and reopen the picker (and restart the app) — confirm it stays hidden. Open `%LocalAppData%\AudioMixerWin\settings.json` and confirm the process name is listed under `excludedProcesses`.

- [ ] **Step 5: Configurable refresh interval**

In Settings, change "Refresh Interval (seconds)" to a noticeably different value and confirm the live list's update cadence changes accordingly (per Task 6, Step 4).

- [ ] **Step 6: Reconnect regression check**

On the Settings page, click "Reconnect". Open `%LocalAppData%\AudioMixerWin\settings.json` and confirm `channels` and `excludedProcesses` are still present and unchanged (i.e. `Reconnect` did not wipe them).

- [ ] **Step 7: Final commit (if any docs/notes were updated during verification)**

If verification required any fixes, ensure they were committed in their respective tasks above. No additional commit is needed if all checks pass as-is.
