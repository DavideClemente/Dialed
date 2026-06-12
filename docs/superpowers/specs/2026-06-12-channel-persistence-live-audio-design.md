# Channel Persistence, Live NAudio Feed & Process Filtering — Design

**Date:** 2026-06-12
**Status:** Approved by user, pending implementation plan

## Goals

1. Persist the user's channel list (knob → app mappings) across app restarts.
2. Keep the "available apps" list in the channel picker fed by live NAudio session
   data, refreshing automatically as apps start/stop playing audio.
3. Let the user hide programs they don't care about from the picker, with that
   exclusion persisted.

## Current State (recap)

- `MainViewModel.Channels` (`ObservableCollection<ChannelViewModel>`) is seeded with
  hardcoded names ("Spotify", "Discord", "Chrome", "Game") on every startup — never
  persisted.
- `AudioManager.GetSessions()` already returns real, deduped NAudio sessions, but it's
  only called once per picker-open (a static snapshot) via
  `ChannelViewModel.GetAvailableSessions()`.
- `AppSettings` / `SettingsService` already persist `ComPort` / `BaudRate` to
  `%LocalAppData%\AudioMixerWin\settings.json`.
- `MainViewModel.Reconnect()` has a latent bug: it saves `new AppSettings { ComPort =
  ..., BaudRate = ... }`, which would silently discard any other persisted settings
  fields (this matters once `Channels`/`ExcludedProcesses` are added).
- The "Select App" picker is built ad-hoc in `KnobCard.xaml.cs` using a plain
  `ListView` + `DisplayMemberPath`, with no per-row actions.

## Section 1 — Settings Model

Extend `Core/Services/AppSettings.cs`:

```csharp
public class AppSettings
{
    public string ComPort { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public int RefreshIntervalSeconds { get; set; } = 2;
    public List<ChannelConfig> Channels { get; set; } = new();
    public List<string> ExcludedProcesses { get; set; } = new();
}
```

New `Core/Models/ChannelConfig.cs`:

```csharp
public class ChannelConfig
{
    public int KnobIndex { get; set; }
    public string AppName { get; set; } = "";
}
```

Only `KnobIndex` + `AppName` are persisted per channel. `Volume` is never stored — it's
always read live from NAudio via `AudioManager.GetVolume`.

**Reconnect bug fix:** `MainViewModel` holds the loaded `AppSettings` as a field
(`_settings`), mutates its properties in place, and always calls
`SettingsService.Save(_settings)` on the same instance — never constructs a fresh
`AppSettings`. This applies to `Reconnect`, channel save, and exclude-list save.

## Section 2 — Channel List Persistence (`MainViewModel`)

- **Startup:** load `_settings = SettingsService.Load()`. For each `ChannelConfig` in
  `_settings.Channels`, create a `ChannelViewModel` with that `KnobIndex`/`AppName`
  (via `AddChannelInternal(appName, knobIndex, save: false)`). If `_settings.Channels`
  is empty (first run, or user removed everything), `Channels` stays empty — no
  hardcoded seed channels.
- **`AddChannelInternal(string appName, int? knobIndex = null, bool save = true)`** —
  computes `knobIndex` via the existing `Max(KnobIndex) + 1` logic when not supplied
  (i.e. for new channels added via the "+" button), uses the supplied value when
  restoring from settings, and only triggers `SaveChannels()` when `save` is true (so
  bulk-loading at startup doesn't cause redundant writes).
- **`RemoveChannelInternal`** — removes the channel, then calls `SaveChannels()`.
- **AppName changes** — `ChannelViewModel.OnAppNameChanged` (already refreshes
  `Volume` from `AudioManager.GetVolume`) additionally invokes an `onSettingsChanged`
  callback that triggers `SaveChannels()`.
- **`SaveChannels()`** — rebuilds `_settings.Channels` from the current `Channels`
  collection (`KnobIndex` + `AppName` per entry) and calls
  `SettingsService.Save(_settings)`.
- **`Reconnect`** — sets `_settings.ComPort = ComPort; _settings.BaudRate = BaudRate;`
  and saves `_settings` (per Section 1 fix).

## Section 3 — Live Available-Sessions Feed

- `MainViewModel` gets `public ObservableCollection<AudioSession> AvailableSessions {
  get; } = new();` — one shared, live collection used by all channel pickers.
- A `DispatcherTimer`, interval = `_settings.RefreshIntervalSeconds` seconds (default
  2), started in the constructor (after an initial synchronous
  `RefreshAvailableSessions()` call so the list isn't empty before the first tick),
  calls `RefreshAvailableSessions()` on each tick.
- **`RefreshAvailableSessions()`**:
  1. Calls `_audioManager.GetSessions()`.
  2. Filters out any session whose `ProcessName` is in `_settings.ExcludedProcesses`
     (case-insensitive).
  3. **Diffs** the result into `AvailableSessions` — removes entries no longer
     present, adds new ones — rather than clearing and rebuilding, so an open
     picker's `ListView` doesn't flicker/reset selection on refresh.
- `ChannelViewModel` no longer calls `GetAvailableSessions()` on demand. Instead it
  holds a reference to the shared `AvailableSessions` collection (passed in via
  constructor) and exposes it as a property for the picker to bind to.
- **Configurable interval:** `MainViewModel` gets an `[ObservableProperty]
  RefreshIntervalSeconds`. Its `OnRefreshIntervalSecondsChanged` partial method
  updates the running `DispatcherTimer.Interval` and immediately persists
  `_settings.RefreshIntervalSeconds` via `SettingsService.Save(_settings)` (no
  "Reconnect" required — this isn't tied to the serial connection).
- **Out of scope:** assigned-channel volumes are *not* polled/synced from external
  changes (e.g. Windows Volume Mixer) — they remain read/written directly via
  `GetVolume`/`SetVolume` as today. Only the *available apps list* is live.

## Section 4 — UI Changes

### New `Core/Controls/AppPickerDialog.xaml` (+ `.xaml.cs`)

Replaces the inline `ListView`/`ContentDialog` currently built in code in
`KnobCard.xaml.cs.OnSettingsClick`.

- A `ContentDialog` (`Title="Select App"`, `PrimaryButtonText="Select"`,
  `SecondaryButtonText="Remove Channel"`, `CloseButtonText="Cancel"` — same as today).
- Contains a `ListView` bound to `Channel.AvailableSessions` (the live, shared
  collection), `SelectionMode="Single"`.
- Custom `DataTemplate` per row (`x:DataType="models:AudioSession"`): a `Grid` with
  the process name (`TextBlock`, bound to `ProcessName`) on the left and a small
  "Hide" `Button` on the right.
- `OnHideClick` (button click handler): reads the `AudioSession` from the button's
  `Tag` (bound via `{x:Bind}`), calls `Channel.HideSession(session)`. This is a plain
  in-dialog button click — it does **not** close the `ContentDialog`. Removing the
  item from `AvailableSessions` updates the `ListView` immediately.
- Exposes `SelectedSession` (the `ListView.SelectedItem` as `AudioSession?`) for the
  caller to read after `ShowAsync()`.

`KnobCard.xaml.cs.OnSettingsClick` shrinks to:

```csharp
private async void OnSettingsClick(object sender, RoutedEventArgs e)
{
    if (Channel is null) return;

    var dialog = new AppPickerDialog(Channel) { XamlRoot = XamlRoot };
    var result = await dialog.ShowAsync();

    if (result == ContentDialogResult.Primary && dialog.SelectedSession is AudioSession session)
        Channel.AppName = session.ProcessName;
    else if (result == ContentDialogResult.Secondary)
        Channel.Remove();
}
```

### `ChannelViewModel` changes

- Constructor gains `ObservableCollection<AudioSession> availableSessions`,
  `Action onSettingsChanged`, and `Action<AudioSession> onHideSession` parameters.
- Exposes `AvailableSessions` (passthrough property to the shared collection).
- `HideSession(AudioSession session) => onHideSession(session)`.
- `OnAppNameChanged` additionally calls `onSettingsChanged()`.
- `GetAvailableSessions()` method is removed (superseded by the live collection).

### `MainViewModel.HideSession`

```csharp
private void HideSession(AudioSession session)
{
    if (!_settings.ExcludedProcesses.Contains(session.ProcessName, StringComparer.OrdinalIgnoreCase))
        _settings.ExcludedProcesses.Add(session.ProcessName);

    AvailableSessions.Remove(session);
    SettingsService.Save(_settings);
}
```

Note: hiding a process only affects what's offered in the picker going forward. A
channel already assigned to a hidden process keeps working normally (volume
read/write is independent of `AvailableSessions`). There is no "unhide" UI in this
iteration — `ExcludedProcesses` can be edited directly in `settings.json` if needed.

### `MainPage.xaml`

Replace the current top-left "Add Channel" `Button` with a circular "+" FAB anchored
bottom-right, overlaid on the `ItemsRepeater`/`ScrollViewer` via a `Grid`, bound to the
same `AddChannelCommand`.

### `SettingsPage.xaml`

Add a "Refresh Interval (seconds)" `NumberBox` (e.g. `Minimum="1"`, `Maximum="30"`)
bound two-way to `ViewModel.RefreshIntervalSeconds`. Takes effect immediately (timer
interval updated + saved on change), no "Reconnect" needed.

## Section 5 — Verification (manual)

No test project exists, and this feature is tightly coupled to live NAudio sessions,
COM ports, and file I/O — not worth adding test infrastructure for this change. Verify
manually after implementation:

1. Build (`dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug`) and run.
2. Add channels, assign apps via the picker, adjust volumes; close and relaunch —
   confirm the same channels/apps/knob mappings reappear.
3. Start/stop an audio-playing app (e.g. a browser tab) while the picker is open —
   confirm it appears/disappears in `AvailableSessions` within the configured refresh
   interval.
4. Click "Hide" on a process — confirm it disappears immediately, and stays hidden
   after restart (check `%LocalAppData%\AudioMixerWin\settings.json`).
5. Change "Refresh Interval" in Settings — confirm the live list's update cadence
   changes without restarting the app.
6. Click "Reconnect" — confirm `Channels`/`ExcludedProcesses` in `settings.json` are
   preserved (regression check for the Section 1 fix).
