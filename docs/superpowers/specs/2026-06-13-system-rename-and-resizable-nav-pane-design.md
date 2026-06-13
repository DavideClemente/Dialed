# Channel Display Polish & Resizable Navigation Pane — Design

**Date:** 2026-06-13
**Status:** Approved by user, pending implementation plan

## Goals

1. Wherever the "System Volume" master-volume channel is shown to the user (channel
   card title, app picker, Settings' "Manage Sources" lists), display it as
   "🔊 System" instead of "System Volume". The internal identifier
   (`AudioManager.MasterVolumeProcessName = "System Volume"`, used for
   `GetVolume`/`SetVolume` matching and persisted in `settings.json` as a channel's
   `AppName`) is unchanged.
2. Make the `NavigationView` pane in `MainWindow.xaml` (the "Mixer"/"Settings"
   sidebar) horizontally resizable by dragging its right edge, within a 200–400px
   range, with the chosen width persisted across restarts.
3. Every displayed source name gets its first letter capitalized (e.g. "spotify" →
   "Spotify"), via the same display-name path as Goal 1.
4. A channel's icon must not disappear when its assigned app closes — keep showing
   the last-known icon until the app (re)appears with a possibly-updated one.
5. When a channel's assigned app is closed, the slider may be moved without effect
   (no live session to apply it to). When the app relaunches, re-read its actual
   volume from the new session and update the slider to match, overwriting whatever
   it showed while the app was closed.

## Current State (recap)

- `AudioManager.GetSessions()` builds `AudioSession` objects with `ProcessName` and
  `DisplayName` set to the same value (`process.ProcessName` for real processes,
  `MasterVolumeProcessName` for the synthetic master-volume entry). `DisplayName`
  is therefore currently redundant.
- `KnobCard.xaml` binds its title to `Channel.AppName` (a `ChannelViewModel`
  `[ObservableProperty]` that holds the raw process name and is used directly for
  `AudioManager.GetVolume`/`SetVolume` and persisted via `ChannelConfig.AppName`).
- `AppPickerDialog.xaml` and `SettingsPage.xaml`'s "Visible" `ListView` both bind
  their row text to `{x:Bind ProcessName}`. `SettingsPage.xaml`'s "Hidden"
  `ListView` binds directly to `{x:Bind}` on `ObservableCollection<string>
  HiddenProcesses` (raw process names).
- `MainWindow.xaml` is a `Window` containing a single `NavigationView` with
  `MenuItems` ("Mixer") and the built-in "Settings" footer item, hosting
  `ContentFrame`. `OpenPaneLength` is left at its default (320px) and has no resize
  affordance.
- `AppSettings` (`Core/Services/AppSettings.cs`) persists `ComPort`, `BaudRate`,
  `RefreshIntervalSeconds`, `Channels`, `ExcludedProcesses` via `SettingsService`.
  `MainViewModel` mirrors several of these as `[ObservableProperty]`s with
  `On...Changed` partial methods that save back to `AppSettings`.
- `ChannelViewModel.UpdateIconSource()` sets `IconSource` to
  `AvailableSessions.FirstOrDefault(...)?.IconSource` — when the assigned app's
  session disappears (app closed), this resolves to `null` and the card's icon
  vanishes. `Volume` is only set from `AudioManager.GetVolume` in the constructor
  and in `OnAppNameChanged`; it is never re-read after the app's session
  reappears, so edits made to the slider while the app was closed are never
  reconciled against the relaunched app's actual volume.

## Section 1 — Display name normalization ("🔊 System" + PascalCase)

### `AudioManager.cs` (global namespace, no `namespace` declaration)

Add a static helper next to `MasterVolumeProcessName`:

```csharp
public const string MasterVolumeProcessName = "System Volume";

public static string GetDisplayName(string processName)
{
    if (processName.Equals(MasterVolumeProcessName, StringComparison.OrdinalIgnoreCase))
        return "🔊 System";

    return processName.Length > 0
        ? char.ToUpperInvariant(processName[0]) + processName[1..]
        : processName;
}
```

The PascalCase rule is intentionally simple — capitalize the first character only,
leave the rest unchanged (`spotify` → `Spotify`, `msedge` → `Msedge`, `Discord` →
`Discord`). No name dictionary.

In `GetSessions()`, change both places that set `DisplayName`:

```csharp
result.Add(new AudioSession
{
    ProcessName = process.ProcessName,
    DisplayName = GetDisplayName(process.ProcessName), // was: process.ProcessName
    Volume = session.SimpleAudioVolume.Volume,
    IconSource = GetIconForProcess(process),
});
...
result.Add(new AudioSession
{
    ProcessName = MasterVolumeProcessName,
    DisplayName = GetDisplayName(MasterVolumeProcessName), // was: MasterVolumeProcessName
    Volume = GetMasterVolume(),
});
```

(`GetDisplayName` is a no-op for every real process — `process.ProcessName` never
equals `"System Volume"` — so this is safe to apply unconditionally.)

### `ChannelViewModel.cs`

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(DisplayName))]
private string appName;

public string DisplayName => AudioManager.GetDisplayName(AppName);
```

(`AudioManager` is already referenced unqualified elsewhere in this file, e.g. the
`_audioManager` field — it lives in the global namespace, so no new `using` is
needed.) `[NotifyPropertyChangedFor]` comes from `CommunityToolkit.Mvvm.ComponentModel`,
already imported.

### `KnobCard.xaml`

```xml
<TextBlock
    Text="{x:Bind Channel.DisplayName, Mode=OneWay}"
    ...
```
(was `Channel.AppName`)

### `AppPickerDialog.xaml`

```xml
<TextBlock Text="{x:Bind DisplayName}" VerticalAlignment="Center" />
```
(was `{x:Bind ProcessName}`, inside the `SelectableSessions` `DataTemplate
x:DataType="models:AudioSession"`)

### `SettingsPage.xaml`

"Visible" list (`AvailableSessions`, same `AudioSession` template):

```xml
<TextBlock Text="{x:Bind DisplayName}" VerticalAlignment="Center" />
```

"Hidden" list (`HiddenProcesses`, `ObservableCollection<string>`) — add a new
converter since there's no `AudioSession.DisplayName` to bind to:

`Core/Converters/ProcessNameDisplayConverter.cs` (new file, new
`AudioMixerWin.Core.Converters` namespace):

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

`SettingsPage.xaml`: add the namespace and resource, apply to the Hidden list's
`TextBlock`:

```xml
<Page ...
    xmlns:converters="using:AudioMixerWin.Core.Converters">

    <Page.Resources>
        <converters:ProcessNameDisplayConverter x:Key="ProcessNameDisplay" />
    </Page.Resources>

    ...
    <TextBlock Text="{x:Bind Path=., Converter={StaticResource ProcessNameDisplay}}" VerticalAlignment="Center" />
```

## Section 2 — Resizable navigation pane

### `AppSettings.cs`

```csharp
public double NavPaneWidth { get; set; } = 320;
```

### `MainViewModel.cs`

```csharp
[ObservableProperty]
private double navPaneWidth;
```

Initialize in the constructor alongside the other settings-backed fields:

```csharp
navPaneWidth = _settings.NavPaneWidth;
```

Add a partial method that persists, mirroring `OnRefreshIntervalSecondsChanged`:

```csharp
partial void OnNavPaneWidthChanged(double value)
{
    _settings.NavPaneWidth = value;
    SettingsService.Save(_settings);
}
```

### `MainWindow.xaml`

Wrap the existing `NavigationView` and a new splitter `Border` in a `Grid`:

```xml
<Grid>
    <NavigationView
        x:Name="NavView"
        IsBackButtonVisible="Collapsed"
        SelectionChanged="OnNavSelectionChanged">
        <!-- existing MenuItems / Frame, unchanged -->
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
```

The `Border`'s `Background` toggles between `Transparent` and a subtle highlight
brush on `PointerEntered`/`PointerExited` (while not dragging) to indicate it's
draggable.

### `MainWindow.xaml.cs`

```csharp
private const double MinPaneWidth = 200;
private const double MaxPaneWidth = 400;
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
```

No custom resize cursor — `Border` doesn't expose `ProtectedCursor` to
`MainWindow`'s code-behind without a custom control subclass, and the hover
highlight is sufficient affordance for this small change.

## Section 3 — Icon persistence & volume re-sync on session changes

### `ChannelViewModel.cs`

Replace `UpdateIconSource()` with a helper used both at construction time and on
reassignment, and rewrite the `AvailableSessions.CollectionChanged` handler to
also re-sync `Volume`:

```csharp
private ImageSource? GetSessionIcon(string appName) =>
    AvailableSessions.FirstOrDefault(s => s.ProcessName.Equals(appName, StringComparison.OrdinalIgnoreCase))?.IconSource;
```

Constructor — replace the trailing `UpdateIconSource();` call:

```csharp
AvailableSessions.CollectionChanged += OnAvailableSessionsChanged;
IconSource = GetSessionIcon(appName);
```

```csharp
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
```

The old `UpdateIconSource` method is removed entirely.

- **App closes**: `AvailableSessions` no longer contains it →
  `OnAvailableSessionsChanged` finds no match → early return → `IconSource` and
  `Volume` retain their last values (fixes the disappearing icon; the slider stays
  wherever the user left it, "saved" per the channel's persisted `AppName`).
- **App relaunches**: a new session appears → `OnAvailableSessionsChanged` finds it
  → `IconSource` refreshed (in case the icon changed) and `Volume` re-read from
  `AudioManager.GetVolume`, overwriting any value the slider showed while the app
  was closed — keeping the UI in sync with the relaunched app's actual volume.
  Setting `Volume` triggers `OnVolumeChanged` → `SetVolume(AppName, ...)`, which
  re-applies the same value back to the session (idempotent no-op).
- **Reassigned to a different app** (`OnAppNameChanged`, via the App Picker):
  `IconSource` and `Volume` are recomputed fresh for the new `AppName` — no stale
  icon/volume carried over from the previous assignment.

## Edge Cases

- **Pane in compact/collapsed mode** (`NavigationView` toggled to icon-only via the
  hamburger button): `OpenPaneLength` still applies once the pane is reopened; the
  splitter remains at its last position and is harmless to drag while collapsed
  (it just changes the width the pane will reopen to).
- **Window narrower than `MinPaneWidth` + content minimum**: not specifically
  guarded — `NavigationView`'s own `PaneDisplayMode="Auto"` responsive collapse for
  narrow windows is unaffected.
- **A real process literally named "System Volume"**: not a valid Windows process
  name, not guarded.
- **`HiddenProcesses` containing `"System Volume"`**: if a user ever hides the
  master-volume entry, the Hidden list shows "🔊 System" via the new converter,
  consistent with everywhere else it's displayed.
- **`GetDisplayName` on an empty string**: returns it unchanged (guarded by the
  `processName.Length > 0` check) — not expected in practice, but avoids an
  `IndexOutOfRangeException`.
- **Channel assigned to an app that's never been running**: `GetSessionIcon`
  returns `null` (no match) and `GetVolume` returns `0` — same as today's
  first-run behavior, unchanged.
- **System-volume channel and `OnAvailableSessionsChanged`**: the synthetic
  "🔊 System" entry is present in `AvailableSessions` whenever the periodic
  refresh runs (it's unconditionally appended in `GetSessions()`), so a channel
  assigned to it always finds a match and its `Volume`/`IconSource` (`null` icon)
  are continuously re-synced from `GetMasterVolume()` — consistent with the
  "snap to actual volume" rule for ordinary apps.

## Verification (manual)

No test project exists (per `CLAUDE.md`).

1. `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug` succeeds.
2. Assign a channel to the system volume entry — its card title reads "🔊 System"
   (not "System Volume"), and its slider still controls the OS master volume.
3. Open that channel's app picker — the system entry is listed as "🔊 System", and
   ordinary apps are shown with a capitalized first letter (e.g. "Spotify").
4. Settings → "Manage Sources" — the system entry shows "🔊 System" in the Visible
   list (or Hidden list, if hidden); other entries show capitalized names.
5. Drag the nav-pane splitter left/right — the "Mixer"/"Settings" pane resizes
   live, clamped to 200–400px.
6. Restart the app — the pane reopens at the last dragged width.
7. Assign a channel to a running app — icon appears. Close that app — icon
   remains, slider stays where it was. Reopen the app — icon refreshes and the
   slider snaps to the relaunched app's actual volume.
8. While the app from step 7 is closed, move its channel's slider — no crash, no
   effect on any live session. Reopen the app — slider snaps to the app's actual
   volume (overwriting the value set while closed).
9. Reassign a channel (App Picker) from an app with an icon to a different,
   not-currently-running app — the old icon disappears (no stale carry-over) and
   the slider resets to `0%` until the new app is detected running.
