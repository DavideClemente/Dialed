# Channel Display Polish & Resizable Navigation Pane Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Normalize how source/channel names are displayed (rename "System Volume" to "🔊 System" everywhere, capitalize the first letter of every other name), make the `NavigationView` sidebar horizontally resizable (200-400px, persisted), and stop `ChannelViewModel` from losing its icon / failing to resync its volume when the assigned app closes and relaunches.

**Architecture:** Add a single `AudioManager.GetDisplayName(string)` static helper that both the `AudioSession.DisplayName` (set in `GetSessions()`) and a new `ChannelViewModel.DisplayName` computed property delegate to, plus a small `IValueConverter` for the one place (`SettingsPage`'s Hidden list) that binds to a raw `string` instead of an `AudioSession`/`ChannelViewModel`. The nav pane gets a new `AppSettings.NavPaneWidth` (mirroring the existing `RefreshIntervalSeconds` persistence pattern) and a hand-rolled `Border` splitter with pointer-capture drag logic, since no `GridSplitter`/`Sizers` package is referenced by this project. The icon/volume bug is fixed by rewriting `ChannelViewModel.OnAvailableSessionsChanged` to early-return (keep last-known state) when the assigned app isn't running, and to refresh both `IconSource` and `Volume` from the live session when it is.

**Tech Stack:** .NET 8, WinUI 3 (Windows App SDK), CommunityToolkit.Mvvm, NAudio, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-06-13-system-rename-and-resizable-nav-pane-design.md`

---

## Important notes for the implementer

- Build command for every "build" step: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug` (run from the repo root). Expected: `Build succeeded.` / `0 Error(s)`.
- There is no test project. Each task's verification is "build succeeds" plus, where noted, a short manual run of the app (`dotnet build` produces `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\AudioMixerWin.exe`, or run via the `AudioMixerWin (Unpackaged)` launch profile in Visual Studio/Rider).
- Settings file to inspect during manual checks: `%LocalAppData%\AudioMixerWin\settings.json`.
- Each task below shows the **full content** of every file it touches, reflecting the cumulative state after that task (i.e. you do not need to read earlier tasks to know what a file should contain at this point).
- `AudioManager` lives in namespace `AudioMixerWin.Core`. `ChannelViewModel` (namespace `AudioMixerWin.Core.ViewModels`) and the new converter (namespace `AudioMixerWin.Core.Converters`) are both nested inside `AudioMixerWin.Core`, so C#'s enclosing-namespace lookup resolves `AudioManager` unqualified — no new `using` directives are needed for it anywhere.

---

### Task 1: Display name normalization ("🔊 System" + capitalized first letter)

**Files:**
- Modify: `Core/AudioManager.cs`
- Modify: `Core/ViewModels/ChannelViewModel.cs`
- Modify: `Core/Controls/KnobCard.xaml`
- Modify: `Core/Controls/AppPickerDialog.xaml`
- Create: `Core/Converters/ProcessNameDisplayConverter.cs`
- Modify: `Core/Views/SettingsPage.xaml`

- [ ] **Step 1: Add `GetDisplayName` to `AudioManager` and use it in `GetSessions()`**

`Core/AudioManager.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AudioMixerWin.Core.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.CoreAudioApi;

namespace AudioMixerWin.Core;

public class AudioManager
    {
        public const string MasterVolumeProcessName = "System Volume";

        public static string GetDisplayName(string processName)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
                return "🔊 System";

            return processName.Length > 0
                ? char.ToUpperInvariant(processName[0]) + processName[1..]
                : processName;
        }

        private readonly MMDevice _device;
        private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

        public AudioManager()
        {
            var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        public List<AudioSession> GetSessions()
        {
            var result = new List<AudioSession>();

            _device.AudioSessionManager.RefreshSessions();
            var sessions = _device.AudioSessionManager.Sessions;

            Console.WriteLine("Audio sessions detected:\n");

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];

                try
                {
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    result.Add(new AudioSession
                    {
                        ProcessName = process.ProcessName,
                        DisplayName = GetDisplayName(process.ProcessName),
                        Volume = session.SimpleAudioVolume.Volume,
                        IconSource = GetIconForProcess(process),
                    });

                    Console.WriteLine(
                        $"{process.ProcessName} - Volume: {session.SimpleAudioVolume.Volume}"
                    );
                }
                catch
                {
                    Console.WriteLine($"System session - Volume: {session.SimpleAudioVolume.Volume}");
                }
            }

            result.Add(new AudioSession
            {
                ProcessName = MasterVolumeProcessName,
                DisplayName = GetDisplayName(MasterVolumeProcessName),
                Volume = GetMasterVolume(),
            });

            return result
                .GroupBy(x => x!.ProcessName)
                .Select(g => g.First()!)
                .OrderBy(x => x.ProcessName)
                .ToList();;
        }

        private ImageSource? GetIconForProcess(Process process)
        {
            if (_iconCache.TryGetValue(process.ProcessName, out var cached))
                return cached;

            ImageSource? icon = null;
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null)
                {
                    using var extracted = Icon.ExtractAssociatedIcon(path);
                    if (extracted is not null)
                        icon = ConvertIconToBitmapImage(extracted);
                }
            }
            catch
            {
                // Some processes (elevated, different bitness, etc.) deny access to MainModule.
            }

            _iconCache[process.ProcessName] = icon;
            return icon;
        }

        private static WriteableBitmap ConvertIconToBitmapImage(Icon icon)
        {
            using var bitmap = icon.ToBitmap();
            var width = bitmap.Width;
            var height = bitmap.Height;

            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

            try
            {
                var bytes = new byte[bitmapData.Stride * height];
                Marshal.Copy(bitmapData.Scan0, bytes, 0, bytes.Length);

                var writeableBitmap = new WriteableBitmap(width, height);
                using var pixelStream = writeableBitmap.PixelBuffer.AsStream();
                pixelStream.Write(bytes, 0, bytes.Length);

                return writeableBitmap;
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }
        
        public float GetMasterVolume() => _device.AudioEndpointVolume.MasterVolumeLevelScalar;

        public void SetMasterVolume(float volume) =>
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);

        public float GetVolume(string processName)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
                return GetMasterVolume();

            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];

                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(
                            processName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return session.SimpleAudioVolume.Volume;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return 0;
        }
        
        public void SetVolume(string processName, float volume)
        {
            if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
            {
                SetMasterVolume(volume);
                return;
            }

            var sessions = _device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];

                try
                {
                    var pid = (int)session.GetProcessID;
                    var process = Process.GetProcessById(pid);

                    if (process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    {
                        session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Session error: {ex.Message}");
                }
            }
        }
    }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 3: Add `DisplayName` to `ChannelViewModel`**

`Core/ViewModels/ChannelViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace AudioMixerWin.Core.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly ObservableCollection<ChannelViewModel> _channels;
    private readonly Action<ChannelViewModel> _onRemove;
    private readonly Action _onSettingsChanged;
    private readonly Func<AudioSession, string?> _onHideSession;

    public int KnobIndex { get; }

    public string KnobLabel => $"Knob {KnobIndex + 1}";

    public ObservableCollection<AudioSession> AvailableSessions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string appName;

    [ObservableProperty]
    private double volume;

    [ObservableProperty]
    private ImageSource? iconSource;

    public string DisplayName => AudioManager.GetDisplayName(AppName);

    public ChannelViewModel(
        int knobIndex,
        string appName,
        AudioManager audioManager,
        ObservableCollection<AudioSession> availableSessions,
        ObservableCollection<ChannelViewModel> channels,
        Action<ChannelViewModel> onRemove,
        Action onSettingsChanged,
        Func<AudioSession, string?> onHideSession)
    {
        KnobIndex = knobIndex;
        _audioManager = audioManager;
        AvailableSessions = availableSessions;
        _channels = channels;
        _onRemove = onRemove;
        _onSettingsChanged = onSettingsChanged;
        _onHideSession = onHideSession;
        this.appName = appName;
        volume = audioManager.GetVolume(appName) * 100;

        AvailableSessions.CollectionChanged += OnAvailableSessionsChanged;
        UpdateIconSource();
    }

    partial void OnAppNameChanged(string value)
    {
        Volume = _audioManager.GetVolume(value) * 100;
        UpdateIconSource();
        _onSettingsChanged();
    }

    private void OnAvailableSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateIconSource();

    private void UpdateIconSource()
    {
        IconSource = AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(AppName, StringComparison.OrdinalIgnoreCase))?.IconSource;
    }

    partial void OnVolumeChanged(double value) =>
        _audioManager.SetVolume(AppName, (float)(value / 100.0));

    public IEnumerable<AudioSession> GetSelectableSessions()
    {
        var takenByOthers = new HashSet<string>(
            _channels.Where(c => c != this).Select(c => c.AppName),
            StringComparer.OrdinalIgnoreCase);

        return AvailableSessions.Where(s => !takenByOthers.Contains(s.ProcessName));
    }

    public static ChannelViewModel? FindAssignedChannel(IEnumerable<ChannelViewModel> channels, string processName) =>
        channels.FirstOrDefault(c => c.AppName.Equals(processName, StringComparison.OrdinalIgnoreCase));

    public string? HideSession(AudioSession session) => _onHideSession(session);

    public void Remove()
    {
        AvailableSessions.CollectionChanged -= OnAvailableSessionsChanged;
        _onRemove(this);
    }
}
```

(`UpdateIconSource`/`OnAvailableSessionsChanged` are left as-is here — they're rewritten in Task 3. This step only adds `DisplayName` + the `[NotifyPropertyChangedFor]` attribute.)

- [ ] **Step 4: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 5: Bind `KnobCard`'s title to `DisplayName`**

`Core/Controls/KnobCard.xaml`:

```xml
<UserControl
    x:Class="AudioMixerWin.Core.Controls.KnobCard"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Border
        CornerRadius="24"
        BorderThickness="2"
        Padding="20"
        Margin="10">

        <Grid>

            <Button
                HorizontalAlignment="Right"
                VerticalAlignment="Top"
                Content="⚙"
                Click="OnSettingsClick"/>

            <StackPanel Margin="0,20,0,0">

                <StackPanel Orientation="Horizontal" Spacing="12" VerticalAlignment="Center">
                    <Image
                        Source="{x:Bind Channel.IconSource, Mode=OneWay}"
                        Width="32"
                        Height="32"/>

                    <TextBlock
                        Text="{x:Bind Channel.DisplayName, Mode=OneWay}"
                        FontSize="28"
                        FontWeight="SemiBold"
                        VerticalAlignment="Center"/>
                </StackPanel>

                <TextBlock
                    Text="{x:Bind Channel.KnobLabel, Mode=OneWay}"
                    Opacity="0.7"
                    Margin="0,4,0,20"/>

                <Slider
                    Value="{x:Bind Channel.Volume, Mode=TwoWay}"
                    Minimum="0"
                    Maximum="100"
                    Height="32"/>

                <TextBlock
                    Text="{x:Bind FormatPercent(Channel.Volume), Mode=OneWay}"
                    HorizontalAlignment="Right"
                    Margin="0,8,0,0"/>

            </StackPanel>

        </Grid>

    </Border>
</UserControl>
```

- [ ] **Step 6: Bind the app picker's row text to `DisplayName`**

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

    <StackPanel Spacing="8">
        <InfoBar x:Name="HideInfoBar" Severity="Warning" IsOpen="False" IsClosable="True" />

        <ListView x:Name="SessionsList" ItemsSource="{x:Bind SelectableSessions}" SelectionMode="Single">
            <ListView.ItemTemplate>
                <DataTemplate x:DataType="models:AudioSession">
                    <Grid ColumnSpacing="8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Text="{x:Bind DisplayName}" VerticalAlignment="Center" />
                        <Button Grid.Column="1" Content="Hide" Tag="{x:Bind}" Click="OnHideClick" />
                    </Grid>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </StackPanel>
</ContentDialog>
```

- [ ] **Step 7: Create `ProcessNameDisplayConverter` for the Hidden-processes list**

`Core/Converters/ProcessNameDisplayConverter.cs`:

```csharp
using System;
using Microsoft.UI.Xaml.Data;

namespace AudioMixerWin.Core.Converters;

public class ProcessNameDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string processName ? AudioManager.GetDisplayName(processName) : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 8: Apply `DisplayName`/converter to `SettingsPage`'s Visible and Hidden lists**

`Core/Views/SettingsPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="AudioMixerWin.Core.Views.SettingsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:models="using:AudioMixerWin.Core.Models"
    xmlns:converters="using:AudioMixerWin.Core.Converters"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <Page.Resources>
        <converters:ProcessNameDisplayConverter x:Key="ProcessNameDisplay" />
    </Page.Resources>

    <ScrollViewer>
        <StackPanel Padding="24" Spacing="8" MaxWidth="400" HorizontalAlignment="Left">

            <TextBlock Text="Serial Connection" FontSize="20" FontWeight="SemiBold" Margin="0,0,0,8" />

            <TextBlock Text="COM Port" />
            <ComboBox
                ItemsSource="{x:Bind PortNames}"
                SelectedItem="{x:Bind ViewModel.ComPort, Mode=TwoWay}"
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

            <TextBlock Text="Visible (tap Hide to exclude from app pickers)" Opacity="0.7" Margin="0,16,0,0" />
            <InfoBar x:Name="HideInfoBar" Severity="Warning" IsOpen="False" IsClosable="True" />
            <ListView ItemsSource="{x:Bind ViewModel.AvailableSessions}" MaxHeight="200">
                <ListView.ItemTemplate>
                    <DataTemplate x:DataType="models:AudioSession">
                        <Grid ColumnSpacing="8">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock Text="{x:Bind DisplayName}" VerticalAlignment="Center" />
                            <Button Grid.Column="1" Content="Hide" Tag="{x:Bind}" Click="OnHideClick" />
                        </Grid>
                    </DataTemplate>
                </ListView.ItemTemplate>
            </ListView>

            <TextBlock Text="Hidden (tap Show to restore)" Opacity="0.7" Margin="0,12,0,0" />
            <ListView ItemsSource="{x:Bind ViewModel.HiddenProcesses}" MaxHeight="200">
                <ListView.ItemTemplate>
                    <DataTemplate x:DataType="x:String">
                        <Grid ColumnSpacing="8">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock Text="{x:Bind Path=., Converter={StaticResource ProcessNameDisplay}}" VerticalAlignment="Center" />
                            <Button Grid.Column="1" Content="Show" Tag="{x:Bind}" Click="OnShowClick" />
                        </Grid>
                    </DataTemplate>
                </ListView.ItemTemplate>
            </ListView>

        </StackPanel>
    </ScrollViewer>
</Page>
```

- [ ] **Step 9: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 10: Manual check — display names**

Run the app.
1. Assign a channel to the system-volume entry (via the gear-icon picker) — its card title should read "🔊 System" (not "System Volume"), and its slider should still control the OS master volume.
2. Open that channel's app picker — the system entry is listed as "🔊 System", and ordinary apps are shown with a capitalized first letter (e.g. a process named `spotify` shows as "Spotify").
3. Go to Settings → "Manage Sources" — the Visible list shows "🔊 System" (or capitalized app names) and, if you hide the system entry, the Hidden list also shows "🔊 System" via the new converter.

- [ ] **Step 11: Commit**

```bash
git add Core/AudioManager.cs Core/ViewModels/ChannelViewModel.cs Core/Controls/KnobCard.xaml Core/Controls/AppPickerDialog.xaml Core/Converters/ProcessNameDisplayConverter.cs Core/Views/SettingsPage.xaml
git commit -m "Display '🔊 System' for the master volume channel and capitalize source names"
```

---

### Task 2: Resizable navigation pane

**Files:**
- Modify: `Core/Services/AppSettings.cs`
- Modify: `Core/ViewModels/MainViewModel.cs`
- Modify: `MainWindow.xaml`
- Modify: `MainWindow.xaml.cs`

- [ ] **Step 1: Add `NavPaneWidth` to `AppSettings`**

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
    public double NavPaneWidth { get; set; } = 320;
    public List<ChannelConfig> Channels { get; set; } = new();
    public List<string> ExcludedProcesses { get; set; } = new();
}
```

- [ ] **Step 2: Add `NavPaneWidth` to `MainViewModel` with persistence**

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

    [ObservableProperty]
    private double navPaneWidth;

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();

    public ObservableCollection<AudioSession> AvailableSessions { get; } = new();

    public ObservableCollection<string> HiddenProcesses { get; } = new();

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _audioManager = new AudioManager();
        _settings = SettingsService.Load();

        comPort = _settings.ComPort;
        baudRate = _settings.BaudRate;
        refreshIntervalSeconds = _settings.RefreshIntervalSeconds;
        navPaneWidth = _settings.NavPaneWidth;

        foreach (var process in _settings.ExcludedProcesses)
            HiddenProcesses.Add(process);

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
        Channels.Add(new ChannelViewModel(index, appName, _audioManager, AvailableSessions, Channels, RemoveChannelInternal, SaveChannels, HideSession));

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

    public string? HideSession(AudioSession session)
    {
        var assigned = ChannelViewModel.FindAssignedChannel(Channels, session.ProcessName);
        if (assigned is not null)
            return $"Can't hide '{session.ProcessName}' — it's assigned to {assigned.KnobLabel}. Unassign it first.";

        if (!_settings.ExcludedProcesses.Contains(session.ProcessName, StringComparer.OrdinalIgnoreCase))
            _settings.ExcludedProcesses.Add(session.ProcessName);

        if (!HiddenProcesses.Contains(session.ProcessName, StringComparer.OrdinalIgnoreCase))
            HiddenProcesses.Add(session.ProcessName);

        AvailableSessions.Remove(session);
        SettingsService.Save(_settings);
        return null;
    }

    [RelayCommand]
    private void UnhideProcess(string processName)
    {
        _settings.ExcludedProcesses.RemoveAll(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase));

        for (var i = HiddenProcesses.Count - 1; i >= 0; i--)
        {
            if (HiddenProcesses[i].Equals(processName, StringComparison.OrdinalIgnoreCase))
                HiddenProcesses.RemoveAt(i);
        }

        SettingsService.Save(_settings);
        RefreshAvailableSessions();
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

    partial void OnNavPaneWidthChanged(double value)
    {
        _settings.NavPaneWidth = value;
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

- [ ] **Step 4: Add the splitter `Border` to `MainWindow.xaml`**

`MainWindow.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Window
    x:Class="AudioMixerWin.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:AudioMixerWin"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d"
    Title="AudioMixerWin">

    <Window.SystemBackdrop>
        <MicaBackdrop />
    </Window.SystemBackdrop>

    <Grid>
        <NavigationView
            x:Name="NavView"
            IsBackButtonVisible="Collapsed"
            SelectionChanged="OnNavSelectionChanged">

            <NavigationView.MenuItems>
                <NavigationViewItem Content="Mixer" Tag="mixer" IsSelected="True">
                    <NavigationViewItem.Icon>
                        <SymbolIcon Symbol="Volume" />
                    </NavigationViewItem.Icon>
                </NavigationViewItem>
            </NavigationView.MenuItems>

            <Frame x:Name="ContentFrame" />

        </NavigationView>

        <Border
            x:Name="PaneSplitter"
            Width="6"
            HorizontalAlignment="Left"
            Background="Transparent"
            PointerEntered="OnSplitterPointerEntered"
            PointerExited="OnSplitterPointerExited"
            PointerPressed="OnSplitterPointerPressed"
            PointerMoved="OnSplitterPointerMoved"
            PointerReleased="OnSplitterPointerReleased" />
    </Grid>
</Window>
```

- [ ] **Step 5: Wire up the drag logic in `MainWindow.xaml.cs`**

`MainWindow.xaml.cs`:

```csharp
using System;
using AudioMixerWin.Core.ViewModels;
using AudioMixerWin.Core.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AudioMixerWin
{
    public sealed partial class MainWindow : Window
    {
        private const double MinPaneWidth = 200;
        private const double MaxPaneWidth = 400;

        public MainViewModel ViewModel { get; } = new();

        private readonly MainPage _mainPage;
        private readonly SettingsPage _settingsPage;

        private bool _isDraggingSplitter;
        private double _dragStartX;
        private double _dragStartWidth;

        public MainWindow()
        {
            InitializeComponent();

            _mainPage = new MainPage(ViewModel);
            _settingsPage = new SettingsPage(ViewModel);

            ContentFrame.Content = _mainPage;

            NavView.OpenPaneLength = ViewModel.NavPaneWidth;
            PositionSplitter(ViewModel.NavPaneWidth);
        }

        private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            ContentFrame.Content = args.IsSettingsSelected ? _settingsPage : _mainPage;
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
            _isDraggingSplitter = false;
            PaneSplitter.ReleasePointerCapture(e.Pointer);
            PaneSplitter.Background = new SolidColorBrush(Colors.Transparent);
            ViewModel.NavPaneWidth = NavView.OpenPaneLength;
        }
    }
}
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 7: Manual check — resizable pane**

Run the app.
1. Hover the right edge of the "Mixer"/"Settings" sidebar — it highlights gray.
2. Drag it left/right — the pane resizes live, clamped between 200px and 400px.
3. Close and relaunch the app — the pane reopens at the width you left it (check `%LocalAppData%\AudioMixerWin\settings.json` for a `navPaneWidth` value matching what you set).

- [ ] **Step 8: Commit**

```bash
git add Core/Services/AppSettings.cs Core/ViewModels/MainViewModel.cs MainWindow.xaml MainWindow.xaml.cs
git commit -m "Make the navigation pane resizable (200-400px, persisted)"
```

---

### Task 3: Icon persistence & volume re-sync on session changes

**Files:**
- Modify: `Core/ViewModels/ChannelViewModel.cs`

- [ ] **Step 1: Rewrite icon/volume sync logic**

`Core/ViewModels/ChannelViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace AudioMixerWin.Core.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly AudioManager _audioManager;
    private readonly ObservableCollection<ChannelViewModel> _channels;
    private readonly Action<ChannelViewModel> _onRemove;
    private readonly Action _onSettingsChanged;
    private readonly Func<AudioSession, string?> _onHideSession;

    public int KnobIndex { get; }

    public string KnobLabel => $"Knob {KnobIndex + 1}";

    public ObservableCollection<AudioSession> AvailableSessions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string appName;

    [ObservableProperty]
    private double volume;

    [ObservableProperty]
    private ImageSource? iconSource;

    public string DisplayName => AudioManager.GetDisplayName(AppName);

    public ChannelViewModel(
        int knobIndex,
        string appName,
        AudioManager audioManager,
        ObservableCollection<AudioSession> availableSessions,
        ObservableCollection<ChannelViewModel> channels,
        Action<ChannelViewModel> onRemove,
        Action onSettingsChanged,
        Func<AudioSession, string?> onHideSession)
    {
        KnobIndex = knobIndex;
        _audioManager = audioManager;
        AvailableSessions = availableSessions;
        _channels = channels;
        _onRemove = onRemove;
        _onSettingsChanged = onSettingsChanged;
        _onHideSession = onHideSession;
        this.appName = appName;
        volume = audioManager.GetVolume(appName) * 100;

        AvailableSessions.CollectionChanged += OnAvailableSessionsChanged;
        IconSource = GetSessionIcon(appName);
    }

    partial void OnAppNameChanged(string value)
    {
        Volume = _audioManager.GetVolume(value) * 100;
        IconSource = GetSessionIcon(value);
        _onSettingsChanged();
    }

    private void OnAvailableSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var session = AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(AppName, StringComparison.OrdinalIgnoreCase));
        if (session is null)
            return; // app not running — keep last-known icon and volume

        IconSource = session.IconSource;
        Volume = _audioManager.GetVolume(AppName) * 100;
    }

    private ImageSource? GetSessionIcon(string appName) =>
        AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(appName, StringComparison.OrdinalIgnoreCase))?.IconSource;

    partial void OnVolumeChanged(double value) =>
        _audioManager.SetVolume(AppName, (float)(value / 100.0));

    public IEnumerable<AudioSession> GetSelectableSessions()
    {
        var takenByOthers = new HashSet<string>(
            _channels.Where(c => c != this).Select(c => c.AppName),
            StringComparer.OrdinalIgnoreCase);

        return AvailableSessions.Where(s => !takenByOthers.Contains(s.ProcessName));
    }

    public static ChannelViewModel? FindAssignedChannel(IEnumerable<ChannelViewModel> channels, string processName) =>
        channels.FirstOrDefault(c => c.AppName.Equals(processName, StringComparison.OrdinalIgnoreCase));

    public string? HideSession(AudioSession session) => _onHideSession(session);

    public void Remove()
    {
        AvailableSessions.CollectionChanged -= OnAvailableSessionsChanged;
        _onRemove(this);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 3: Manual check — icon persistence and volume re-sync**

Run the app.
1. Assign a channel to a running app that has an icon (e.g. a browser) — the icon appears on the card.
2. Close that app — the icon remains on the card, and the slider stays wherever you left it.
3. While the app is closed, move that channel's slider to a different value — no crash, no effect on any live session (nothing audible changes).
4. Reopen the app — within one refresh interval, the icon refreshes (in case it changed) and the slider snaps to the relaunched app's actual current volume, overwriting the value you set while it was closed.
5. Reassign that channel (via the gear-icon picker) to a different app that is **not** currently running — the old icon disappears immediately (no stale carry-over) and the slider resets to `0%` until the new app is detected running.

- [ ] **Step 4: Commit**

```bash
git add Core/ViewModels/ChannelViewModel.cs
git commit -m "Keep last-known channel icon/volume when an app closes and resync on relaunch"
```

---

## Final manual verification pass

After all three tasks are complete, do one end-to-end run covering everything in this plan:

1. Build: `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug` → `Build succeeded.` / `0 Error(s)`.
2. System-volume channel shows "🔊 System" on its card, in the app picker, and in Settings' Visible/Hidden lists; other sources show with a capitalized first letter everywhere.
3. Drag the nav-pane splitter to a new width, restart the app, confirm it reopens at that width.
4. Assign a channel to a running app with an icon, close the app (icon stays, slider stays), move the slider while closed (no effect), reopen the app (icon refreshes, slider snaps to actual volume).
5. Reassign a channel to a different, not-currently-running app — old icon clears, slider resets to 0% until the new app appears.
