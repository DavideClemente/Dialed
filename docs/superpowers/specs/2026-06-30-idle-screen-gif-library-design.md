# Idle Screen GIF Library — Design Spec
_2026-06-30_

## Goal

Add a new **Idle Screen** entry to the side menu where the user manages a personal library
of GIFs and picks one to use as the controller's idle screen. The user can upload GIFs over
time (stored in a local cache), preview them, remove ones they no longer want, and mark a
single GIF as the selected idle screen.

This iteration is **Windows-app only**. It builds the library + selection UI and persists the
choice. Actually rendering the chosen GIF on the ESP32 GC9A01 display (downscale, quantize,
flash upload, firmware playback) is **out of scope** here and deferred to a follow-up spec —
see [Out of scope](#out-of-scope). Because the GIF does not yet reach the device, the UI labels
the chosen GIF **"Selected"**, never "Active on device".

The chosen visual direction is the "Gallery" layout (hero preview + library grid) approved
during `/frontend-design`, with a single active GIF.

---

## User-facing behavior

- A new **Idle Screen** item appears in the navigation pane between **Mixer** and the built-in
  **Settings** item.
- The page shows:
  - A **hero** at the top: the currently selected GIF playing (animated), with its name,
    dimensions, and size, plus a "Selected" badge. If nothing is selected yet, the hero shows
    an empty-state prompt to upload or pick a GIF.
  - A **library grid** of every uploaded GIF as an animated thumbnail card. Hovering a card
    reveals two actions: **set as selected** (✓) and **delete** (🗑). The currently selected
    card is outlined.
  - A leading **Add** tile in the grid (and an **Upload GIF** button in the header) that opens
    a file picker.
  - A footer line summarizing usage: "*N GIFs · X MB*" (informational only).
- **Upload:** picking one or more media files (`.gif` plus static images — `.png`, `.jpg`,
  `.jpeg`, `.bmp`) copies them into the cache and adds them to the library. Other file types are
  not selectable in the picker.
- **Select:** clicking a card's ✓ (or the card) makes it the selected idle GIF; the hero and the
  outline update; the choice persists across restarts.
- **Delete:** clicking a card's 🗑 asks for confirmation via a `ContentDialog`. On confirm, the
  file is removed from the cache and the library. Deleting the selected GIF clears the selection
  (hero returns to empty state).

### Decisions locked for this iteration

- **GIFs and static images.** Animated `.gif` plus static `.png`/`.jpg`/`.jpeg`/`.bmp` are
  accepted. A still is encoded as a single-frame upload — the same pipeline handles both because
  the decoder and encoder are format-agnostic (`GifFrameEncoder` falls back to one frame when the
  source isn't animated).
- **No enforced storage cap.** The footer shows total usage as information; there is no quota
  and no automatic eviction.

---

## Architecture

Follows the existing patterns: a focused on-disk asset store (like `IconStore`), state persisted
through `AppSettings`/`SettingsService`, and a child view-model created by `MainViewModel` that
shares the single `AppSettings` instance via a save callback (like `ChannelViewModel`).

### New / changed components

| Component | Type | Responsibility |
|-----------|------|----------------|
| `Core/Services/IdleGifLibraryService.cs` | new, instance class | Owns the `idle-gifs` cache folder. Imports a picked file (copy to `{guid}.gif`, read dimensions via `BitmapDecoder`), deletes a file by id, computes total size, resolves a config's on-disk path. Returns/accepts `IdleGifConfig`. |
| `Core/Models/IdleGifConfig.cs` | new model | Serializable metadata for one library entry: `Id`, `FileName`, `OriginalName`, `AddedUtc`, `SizeBytes`, `PixelWidth`, `PixelHeight`. |
| `Core/Services/AppSettings.cs` | changed | Add `List<IdleGifConfig> IdleGifs { get; set; } = new();` and `string? ActiveIdleGifId { get; set; }`. |
| `Core/ViewModels/IdleScreenViewModel.cs` | new `ObservableObject` | Backs the page. Holds `ObservableCollection<IdleGifViewModel>`, `SelectedGif`, total-size text. Commands: `AddGifs`, `DeleteGif`, `SetActive`. Persists via the shared settings + save callback. |
| `Core/ViewModels/IdleGifViewModel.cs` | new `ObservableObject` | One library entry for the UI: exposes `OriginalName`, size/dimension display strings, the on-disk file path (for `Image.Source`), and `IsSelected`. |
| `Core/Views/IdleScreenPage.xaml(.cs)` | new `Page` | The Option-A layout. Constructed with the `IdleScreenViewModel`. |
| `Core/ViewModels/MainViewModel.cs` | changed | Construct `IdleGifLibraryService` and expose a child `IdleScreenViewModel` (built with the shared `_settings`, the service, and a `SettingsService.Save(_settings)` callback). |
| `MainWindow.xaml` | changed | Add the `Idle Screen` `NavigationViewItem` (`Tag="idle"`) with an icon. |
| `MainWindow.xaml.cs` | changed | Add `_idleScreenPage`; in `OnNavSelectionChanged`, switch on the selected item's `Tag`/`IsSettingsSelected` to choose among mixer / idle / settings pages. |

### Data flow

- **Import:** picker → `IdleGifLibraryService.ImportAsync(storageFile)` copies into
  `%LocalAppData%\AudioMixerWin\idle-gifs\{guid}.gif`, reads pixel dimensions and size, returns an
  `IdleGifConfig` → `IdleScreenViewModel` adds it to `_settings.IdleGifs`, creates an
  `IdleGifViewModel`, and saves.
- **Select:** `IdleScreenViewModel.SetActive(vm)` sets `_settings.ActiveIdleGifId = vm.Id`,
  updates `IsSelected` flags + `SelectedGif`, saves.
- **Delete:** confirm dialog → `IdleGifLibraryService.Delete(config)` removes the file →
  remove from `_settings.IdleGifs`; if it was the active id, clear `ActiveIdleGifId`/`SelectedGif`;
  save.
- **Load (startup):** `IdleScreenViewModel` is seeded from `_settings.IdleGifs`; `SelectedGif` is
  the entry whose `Id == ActiveIdleGifId` (or none). Entries whose backing file is missing on disk
  are skipped (and pruned from settings) so a manually-cleared cache can't strand metadata.

### Cache location

`%LocalAppData%\AudioMixerWin\idle-gifs\` — sibling of the existing `icons\` folder and
`settings.json`, created on first import (same `Directory.CreateDirectory` guard as `IconStore`).

---

## UI details

- **Page shell:** `ScrollViewer` → `StackPanel`/`Grid`, `Padding="24"`, matching `MainPage`/
  `SettingsPage`. Dark brushes reused from existing pages (`#1C1C1C` surfaces, `#383838` borders,
  `#E0E0E0`/`#F0F0F0` text, 8px corners, `Segoe UI Variable Display` for headings).
- **Hero:** horizontal layout — animated `Image` preview (fixed box, `Stretch="UniformToFill"`,
  rounded clip) on the left; name (`FontSize="15"`), `240×240 · X MB` sub-line, and a "Selected"
  pill on the right. Empty state replaces it with a dashed prompt.
- **Grid:** `ItemsRepeater` with `UniformGridLayout` (`MinItemWidth` ~140, `ItemsStretch="Fill"`),
  same as `MainPage`. Each item is a card `UserControl`/`DataTemplate`: animated `Image`
  thumbnail, a hover overlay (`PointerEntered`/`Exited` or `VisualState`) with the ✓ and 🗑
  buttons, selected outline bound to `IsSelected`.
- **Add tile + Upload button:** both invoke `AddGifsCommand` → `FileOpenPicker` with
  `FileTypeFilter = { ".gif" }`. The unpackaged app must call
  `WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd)` before `PickMultipleFilesAsync`;
  the page receives the window handle (or a picker-factory) from `MainWindow`.
- **Animated GIF playback:** WinUI 3's `Image` control plays animated GIFs natively when
  `Source` is a `BitmapImage` pointed at the file `Uri`; no third-party decoder needed.

---

## Error handling

- File operations in `IdleGifLibraryService` use the same defensive `try/catch` style as
  `IconStore`/`SettingsService` (swallow IO errors, return null/skip rather than throw).
- Import of a corrupt/undecodable GIF: if `BitmapDecoder` fails, the entry is rejected (file not
  added, optional brief `InfoBar`/no-op) rather than added with bogus metadata.
- Missing backing file at load time: entry is pruned from `_settings.IdleGifs` and skipped.
- Picker cancelled / returns no files: no-op.

---

## Testing

There is no test project in the repo and these changes are UI/IO-bound (file picker, WinUI
animated `Image`, navigation). Verification is manual:

1. Build `dotnet build AudioMixerWin.csproj -p:Platform=x64 -c Debug` (must pass).
2. Launch; confirm the **Idle Screen** nav item appears and selects its page.
3. Upload one or more media files (`.gif` and static `.png`/`.jpg`) → they appear as thumbnails
   (GIFs animate, stills render); cache folder populated.
4. Select a GIF → hero + outline update; restart → selection persists.
5. Delete a GIF (incl. the selected one) → confirm dialog, file removed, selection cleared when
   appropriate.
6. Confirm the footer usage count/size updates across add/delete.

Where logic is non-trivial and decoupled from WinUI (e.g. pruning missing files, size totals),
keep it in `IdleGifLibraryService` so it *could* be unit-tested later, even though no test
project exists today.

---

## Out of scope

- **Pushing the GIF to the ESP32** — downscaling/cropping to the 240×240 round display, color
  quantization, an upload-over-serial protocol, flash (SPIFFS/LittleFS) storage, and firmware
  playback on idle. This is the hard part and gets its own spec. The hardware constraints make it
  non-trivial: classic ESP32 (no PSRAM, ~290 KB heap) and a 115200-baud link (~11 KB/s) cannot
  live-stream ~113 KB/frame, so delivery must be a one-time converted upload to flash.
- Per-item cropping/editing, reordering the library, rotation/playlist (cycling multiple items),
  and any storage quota/eviction.
