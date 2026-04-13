# CLAUDE.md — KoEnVue Project Guide

## What is this?

Windows Korean/English IME state indicator. Shows current input mode (한/En/EN) as a draggable floating overlay.
C# 14 / .NET 10 + NativeAOT single exe (~4.7MB). Zero external NuGet packages.

## Tech Stack

- **Target**: `net10.0-windows`, PublishAot, AllowUnsafeBlocks
- **Source Generators**: `[LibraryImport]`, `[JsonSerializable]`
- **P/Invoke**: User32, Imm32, Shell32, Gdi32, Kernel32, Shcore, Ole32, OleAut32, Dwmapi
- **`[DllImport]` is banned** — always use `[LibraryImport]`

## Hard Constraints (P1-P5)

| Rule | Description |
|------|-------------|
| **P1** | Zero external NuGet packages. .NET 10 BCL + Windows API only |
| **P2** | UI text defaults to Korean. Log messages and config keys in English |
| **P3** | No magic numbers → const/enum/config. No string comparisons → enum |
| **P4** | Shared modules enforced: DPI→DpiHelper, Color→ColorHelper, GDI handles→SafeGdiHandles, P/Invoke→Native/, structs/constants→Win32Types.cs, Win32 dialog metrics→Win32DialogHelper |
| **P5** | app.manifest UAC requireAdministrator |

## Architecture

2-thread model:
```
Main thread (UI):       Message loop + rendering + tray + WM_TIMER animation
Detection thread (BG):  80ms polling → PostMessage to main
```

### Indicator Positioning

**Draggable floating window** with two-tier position memory:
1. Indicator is a separate TOPMOST window, not tied to any foreground window's geometry
2. User drags the indicator to preferred position → saved to both runtime hwnd dict and `config.json` (`indicator_positions`)
3. On foreground change → lookup order: runtime hwnd position → config process name position → default position
4. Default position (no saved position): config `default_indicator_position` (Corner anchor + delta) if set, else hardcoded fallback `workArea.Right + DefaultIndicatorOffsetX (-200), workArea.Top + DefaultIndicatorOffsetY (10)`. Both compute from the foreground window's monitor work area (multi-monitor aware)
5. Runtime hwnd positions enable per-window distinction (e.g., multiple Notepad/Chrome windows), lost on restart
6. Config process name positions persist across sessions as fallback

`WM_NCHITTEST → HTCAPTION` enables native drag. `WM_ENTERSIZEMOVE`/`WM_EXITSIZEMOVE` tracks drag lifecycle. `WM_MOVING` handles Shift-key axis locking, window-edge magnetic snapping, and cross-monitor DPI change during drag — hold Shift to constrain movement to the dominant axis (horizontal or vertical) from the drag start point. When `config.SnapToWindows` is enabled (default on), the indicator's edges snap to nearby top-level window edges and the current monitor's work area edges within `SnapThresholdPx`; toggle via the tray menu "창에 자석처럼 붙이기".

**System input processes exception** (StartMenuExperienceHost, SearchHost, SearchApp): TOPMOST z-band cannot rise above these shell UI surfaces, so any saved position that ends up under them becomes unreachable. For these processes, drag is ignored (position never saved) and `GetDefaultPosition` places the indicator just above the window's visual top-left corner (`frame.Left`, `frame.Top - labelH - gap`), clamped to `workArea.Top`. The "visual" frame is obtained via `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` to exclude the invisible resize border that `GetWindowRect` includes.

### Indicator Rendering

Hardcoded to: **Text label** ("한"/"En"/"EN") + **RoundedRect** shape. No style/shape selection.
GDI-based: DIB section + RoundRect + DrawTextW + premultiplied alpha + UpdateLayeredWindow.

## Project Structure

```
KoEnVue/
├── Native/      P/Invoke (one file per DLL: User32, Imm32, Shell32, Gdi32, Kernel32, Shcore, Ole32, OleAut32, Dwmapi) + Win32Types.cs + SafeGdiHandles.cs + AppMessages.cs + VirtualDesktop.cs
├── Models/      AppConfig (record) + enums (DisplayMode, DetectionMethod, ImeState, FontWeight, Theme, NonKoreanImeMode, AppProfileMatch, AppFilterMode, TrayIconStyle, TrayClickAction, LogLevel, Corner)
├── Detector/    ImeStatus (IME state detection + WinEvent hook), SystemFilter (7-condition hide logic)
├── UI/          Overlay (GDI rendering + floating positioning), Animation (WM_TIMER state machine), Tray (system tray + schtasks + cleanup dialog + scale input dialog), TrayIcon (GDI icon), SettingsDialog (scrollable 59-field config editor)
├── Config/      DefaultConfig, Settings (load/save/validate/migrate/hot-reload/app-profiles), ThemePresets (6 themes)
├── Utils/       DpiHelper, ColorHelper (Hex↔ColorRef↔RGB), Logger, I18n (Ko/En UI text), Win32DialogHelper (dialog metric helpers)
└── Program.cs   Main loop (2-thread management + lifecycle)
```

## Build & Run

```bash
dotnet build                          # debug build
dotnet publish -r win-x64 -c Release  # NativeAOT release publish
```

**Always run both debug and release builds.** A debug-only build leaves the release EXE outdated.

csproj has `NoWarn: SYSLIB1051` (.NET 10 LibraryImport IntPtr diagnostic suppression).

## .NET 10 Compatibility Notes

| Issue | Resolution |
|-------|------------|
| `ImplicitUsings` | Not enabled by default in .NET 10 → explicit in csproj |
| `Nullable` | Explicit `enable` in csproj for nullable reference warnings |
| `SYSLIB1051` | `IntPtr` promoted to error in `[LibraryImport]` source gen → `NoWarn` |
| `uint → nint` cast | Explicit `(nint)` cast required for `uint` constants passed to `IntPtr` params |
| `int & uint` mixed ops | `GetWindowLongW` (int) + `WS_CAPTION` (uint) → CS0034. Use `unchecked((int)...)` |
| STJ record init defaults | Source gen loses `init` defaults for properties absent from JSON. Workaround: `MergeWithDefaults()` in Settings.cs |

## Key Implementation Decisions

- **Floating indicator**: Draggable TOPMOST window. Two-tier position memory: runtime `Dictionary<IntPtr, (int,int)>` for per-window positions + persistent `config.json` `indicator_positions` for per-process positions. `WM_NCHITTEST → HTCAPTION` for drag. `WM_ENTERSIZEMOVE`/`WM_EXITSIZEMOVE` for drag lifecycle
- **Delegate GC prevention**: Static field retention for P/Invoke callbacks (e.g. `_imeChangeCallback` in ImeStatus.cs)
- **COM init ordering**: Main thread pre-initializes COM STA + forces `SystemFilter` static constructor before detection thread starts
- **Overlay window class**: Separately registered (shared WndProc with main window)
- **WM_DESTROY guard**: `hwnd == _hwndMain` check prevents app exit when overlay is destroyed
- **GDI NULL_PEN**: Required for RoundRect to avoid 1px black border. GetStockObject handles are system-owned — never DeleteObject
- **Premultiplied alpha**: Post-processing needed for GDI output (non-premultiplied) with DrawTextW antialiasing edges
- **DIB top-down**: Negative biHeight so (0,0) is top-left
- **Tray callback routing**: Handled in Program.cs (not Tray.cs) because it needs `_indicatorVisible` access
- **Settings.cs**: Static class, record `with` expressions. LoadFromFile pipeline: MergeWithDefaults → Deserialize → EnsureSubObjects → Migrate → Validate → ThemePresets.Apply
- **I18n.cs**: Bool flag + ternary pattern (NativeAOT-friendly, zero allocation). Uses `GetUserDefaultUILanguage()` P/Invoke
- **Config hot-reload**: `_lastConfigMtime` updated after `Settings.Save()` to prevent self-triggered reload
- **Volatile field workaround**: `Action<AppConfig>` callback pattern since `ref` cannot be used with volatile `_config`
- **File logging**: Async queue (`ConcurrentQueue` + `ManualResetEventSlim` + dedicated `LogDrain` thread). Single rotation (.log → .log.old)
- **Label border**: GDI `CreatePen(PS_SOLID)` + `NULL_BRUSH` overlay stroke. Pen-width inset (`borderW/2`) to keep border inside DIB
- **Slide animation**: Ease-out cubic `1-(1-t)^3` interpolation via `TIMER_ID_SLIDE`. All animation timers use `DefaultConfig.AnimationFrameMs` (16ms ≈ 60fps)
- **NonKoreanImeMode Dim**: `GetTargetAlpha` applies `DefaultConfig.DimOpacityFactor` (0.5) when `_currentState == NonKorean && Dim`
- **F-key hotkeys**: `ParseHotkey` supports F1-F12 via pattern match → `VK_F1 + (fNum - 1)`
- **STJ source gen init-defaults workaround**: `MergeWithDefaults()` serializes default `AppConfig` to JSON, overlays user keys, then deserializes. `EnsureSubObjects()` remains as null safety net
- **Label DIB flip-flop fix**: Cache `_fixedLabelWidth` and invalidate `_lastRenderedState` when DIB is recreated
- **Foreground change detection**: `foregroundChanged` flag triggers focus event independently of `hwndFocus` comparison, fixing return-to-same-window after desktop switch
- **Console host fallback**: `hwndFocus == 0` + `ConsoleWindowClass` check → use foreground window as focus target
- **Position update ordering**: Detection loop sends `WM_POSITION_UPDATED` before `WM_IME_STATE_CHANGED`/`WM_FOCUS_CHANGED` to ensure `_lastForegroundHwnd` is current when handlers run
- **Tray WM_CONTEXTMENU**: `NOTIFYICON_VERSION_4` requires `WM_CONTEXTMENU` (not `WM_RBUTTONUP`) for right-click menu — shell grants foreground activation on `WM_CONTEXTMENU`
- **Always mode default**: `DisplayMode.Always` — indicator always visible (bright on events, dim at idle). `OnEvent` available via config
- **Multi-monitor default position**: `GetDefaultPosition(hwndForeground, processName, config)` uses `MonitorFromWindow(hwndForeground)` to place default position on the foreground app's monitor, not the overlay's current monitor
- **Customizable default indicator position**: `config.DefaultIndicatorPosition` (nullable `DefaultPositionConfig` record with `Corner` + `DeltaX` + `DeltaY`) stores a user-customizable default for apps without a saved position. `GetDefaultPosition` resolves the anchor against the foreground monitor's work area via `ResolveAnchor`, falling back to `DefaultConfig.DefaultIndicatorOffset{X,Y}` (hardcoded top-right) when null. Multi-monitor/resolution-stable because offsets are stored relative to a Corner, not as absolute coordinates. Tray menu "기본 위치 → 현재 위치로 설정" calls `Overlay.ComputeAnchorFromCurrentPosition()` which picks the nearest corner by Manhattan distance from `_lastX, _lastY` and computes the offset — user never has to think about corner selection. "초기화" sets the field back to null (grayed when already null). System input processes bypass this entirely — their special rule (`frame.Top - labelH - gap`) remains unchanged. `Corner` enum uses `[JsonStringEnumMemberName]` for snake_case JSON (`top_left`, `top_right`, `bottom_left`, `bottom_right`)
- **DPI change in Show()**: Compares new DPI with `_currentDpiScale` — on mismatch, resets `_fixedLabelWidth`, DIB size, and font cache before `EnsureResources`. Fixes first-render-small-then-normal issue (Initialize at 1.0x → first Show at 1.5x)
- **WM_MOVING drag DPI**: `Overlay.HandleMoving(ref RECT, ...)` is the WM_MOVING entry point. Internally calls private `HandleDragDpiChange` which detects monitor boundary crossing during drag, re-creates resources at new DPI, and calls `UpdateLayeredWindow` directly (bypassing `_isDragging` guard)
- **Shift-drag axis constraint**: While `HTCAPTION` system drag loop is running, `HandleMoving` checks `GetAsyncKeyState(VK_SHIFT)` per WM_MOVING tick. When held, the dominant axis (larger `|dx|` vs `|dy|` relative to `_dragStart{X,Y}` captured in `BeginDrag`) is locked to the start coordinate by rewriting the RECT's Top/Bottom or Left/Right (width/height preserved). `HandleMoving` returns true when modified; caller writes back via `Marshal.StructureToPtr` and returns `(IntPtr)1` from WM_MOVING. DPI check runs with the constrained coordinates so monitor-crossing along the unlocked axis still resizes the indicator correctly. Screen coordinates are absolute so multi-monitor works without special handling. Shift can be pressed/released mid-drag — axis flips if the user drags far enough in the opposite direction while holding Shift
- **Snap to windows during drag**: `config.SnapToWindows` (default true) toggles magnetic edge snapping to nearby top-level windows and the current monitor's work area. `BeginDrag(AppConfig)` captures `_dragHotPointX/Y` (cursor offset from window top-left via `GetCursorPos`) and enumerates candidates into `_snapRects` via `User32.EnumWindows` with a `[UnmanagedCallersOnly]` `EnumWindowsCallback` — filters out the overlay itself, non-visible windows, iconic windows, DWM-cloaked windows (`Dwmapi.IsCloaked` wrapping `Win32Constants.DWMWA_CLOAKED = 14`), and anything smaller than `SnapMinWindowSizePx` (80px). Each candidate's rect comes from `Dwmapi.TryGetVisibleFrame` so snap aligns with the DWM visible frame, not `GetWindowRect`'s invisible resize border. `ApplySnap` picks the smallest X and Y edge-pair distances within `SnapThresholdPx` (DPI-scaled), only applied to axes not already locked by Shift. `EndDrag` clears `_snapRects`. Tray menu `IDM_SNAP_TO_WINDOWS` (ID 4004) sits in the main menu between "크기" and the separator, grouped with the drag-time controls
- **WM_MOVING drift re-sync**: Non-idempotent modifications (like snap) inside `WM_MOVING` accumulate drift because the system uses the previous tick's modified rect as the base for the next tick's cursor delta (`new_rect = prev_modified + cursor_delta`). Without re-sync, a slow drag with snap active pulls the cursor further and further from the indicator. Fix: `HandleMoving` first re-syncs `movingRect` to `(cursor - _dragHotPoint)` via `GetCursorPos()` every tick, making the modification chain idempotent. Shift-drag worked without this fix because it always rewrites to a fixed start coordinate (also idempotent by construction), but snap needed explicit re-sync. `HandleMoving` now always returns true since the rect is always overwritten
- **EnumWindows NativeAOT callback**: Uses `delegate* unmanaged<IntPtr, IntPtr, int>` + `[UnmanagedCallersOnly]` instead of delegate marshaling — consistent with the rest of the project's `[LibraryImport]` style. Return type is `int` (not `bool`) because Win32 `BOOL` is 4-byte
- **Deferred lastHwndForeground**: Detection loop only updates `lastHwndForeground` after `ShouldHide` passes. If filtered (e.g., transient condition), next poll retries the foreground change
- **Runtime hwnd positions**: `Dictionary<IntPtr, (int, int)>` in Program.cs. Per-window position memory within a session — enables distinction of multiple windows of the same process (e.g., multiple Notepad/Chrome windows). Lost on restart, falls back to config process name positions
- **Cleanup dialog**: Win32 checkbox dialog in Tray.cs for selective cleanup of unused `indicator_positions` entries. Compares against running processes. Modal with `EnableWindow` + nested `GetMessageW` loop. `[UnmanagedCallersOnly]` WndProc for dialog class. DPI-scaled layout (`GetSystemMetricsForDpi` for non-client area sizing). System font (맑은 고딕 `CreateFontW` + `WM_SETFONT`), `COLOR_BTNFACE` background, description STATIC label + `SS_ETCHEDHORZ` separator
- **System input process position bypass**: `DefaultConfig.IsSystemInputProcess` lists StartMenuExperienceHost/SearchHost/SearchApp. `HandleOverlayDragEnd` skips both runtime and config saves for these; `GetAppPosition` ignores any stored value and calls `Overlay.GetDefaultPosition(hwnd, processName)` which positions the indicator just above the window's visual top-left corner (`SystemInputGapPx = 4`). Uses `Dwmapi.DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` via `Dwmapi.TryGetVisibleFrame` (shared helper in `Native/Dwmapi.cs`, falls back to `GetWindowRect` on failure) so the label aligns with the DWM-composited visible frame, not the invisible resize border. Y is clamped to `workArea.Top` to keep it on-screen when the window is near the top of the monitor. Prevents the indicator from being dragged into an unrecoverable zone under shell chrome
- **Shared-HWND system input rect tracking**: Win11 reuses a single HWND (e.g., SearchHost `0x30254`) for both Start Menu and Search modes, distinguishing them only by rect. `DetectionLoop` caches `lastSystemInputFrame` and treats any DWM frame change on the same HWND as a foreground change, re-posting `WM_POSITION_UPDATED`. `HandlePositionUpdated` has a `sysInput` branch that re-resolves position even when `hwndForeground == _lastForegroundHwnd`, so Start Menu ↔ Search transitions re-anchor the indicator to the new visible frame
- **Per-poll filter evaluation**: `DetectionLoop` evaluates `ResolveForApp + SystemFilter.ShouldHide` every tick (not only on foreground change) and uses a `lastFiltered` flag to suppress duplicate `WM_HIDE_INDICATOR` messages. Fixes the "desktop click → same app return" case where nothing appeared to change but the indicator needed to reappear. Hide message is emitted only on `!lastFiltered → filtered` transitions
- **`wasHidden` re-trigger**: `HandlePositionUpdated` treats `!_indicatorVisible` as a signal to re-resolve position and show, even when `hwndForeground == _lastForegroundHwnd`. Complements per-poll filter re-eval: detection thread posts `WM_POSITION_UPDATED` after filter clears, main thread sees the same hwnd but knows the indicator needs to come back
- **`HideOverlay` forceHidden**: System filter, hotkey toggle off, and tray toggle off all pass `forceHidden: true` to `Animation.TriggerHide` so Always mode collapses fully instead of sliding into dim-idle. "Hide" from these sources means "actually disappear," distinct from Always-mode idle dimming
- **Decimal indicator scale**: `config.IndicatorScale` is a `double` in range 1.0~5.0, rounded to 1 decimal place in `Settings.Validate`. Applied as `(int)Math.Round(baseValue * scale)` to `LabelWidth`, `LabelHeight`, `FontSize`, `LabelBorderRadius`, `BorderWidth`, and `LABEL_PADDING_X` inside `Overlay.EnsureResources` / `EnsureFont` / `CalculateFixedLabelWidth` / `RenderIndicator` *before* DPI scaling, so DPI and IndicatorScale compose multiplicatively. Tray menu "크기 ▸" submenu lists 5 integer presets (1배~5배) plus a "직접 지정" item that opens `ShowScaleInputDialog`. Radio check behavior: `IsIntegerScale(scale)` (tolerance 0.001) places the check on the matching integer preset; otherwise the check moves to "직접 지정" and the label becomes `I18n.FormatCustomScaleLabel(scale)` = "직접 지정 (2.3배)" so the user always sees the current non-integer value in the menu
- **Scale input dialog**: `ShowScaleInputDialog(double initialValue)` in Tray.cs is a Win32 modal dialog (class `KoEnVueScaleDlg`) spawned at cursor position, clamped to work area. Layout is DPI-scaled (`DpiHelper.Scale` on constants like `ScaleDlgWidth=320`, `ScaleDlgPad=16`). Uses `[UnmanagedCallersOnly]` `ScaleDlgProc`, STATIC prompt + EDIT (pre-filled via `initialValue.ToString("0.#")` — shows "2" for 2.0 and "2.3" for 2.3) + STATIC hint + OK/Cancel buttons. OK is `BS_DEFPUSHBUTTON` for Enter key default. Modal loop uses `EnableWindow(main, false)` + nested `GetMessageW` with `IsDialogMessageW` for Tab/Enter/ESC handling. `TryCommitScaleInput` parses with `double.TryParse` + `CultureInfo.InvariantCulture` (so "2.3" works regardless of OS locale), validates `[1.0, 5.0]`, and on failure shows a MessageBox then refocuses EDIT with `EM_SETSEL(0, -1)`. `ScaleDlgProc` accepts both `IDC_SCALE_OK || IDOK` and `IDC_SCALE_CANCEL || IDCANCEL` because `IsDialogMessageW` synthesizes `IDOK`/`IDCANCEL` for Enter/ESC. Result is rounded to 1 decimal and committed only when it differs from current by more than `ScaleTolerance`
- **Settings dialog**: `SettingsDialog.Show(hwndMain, config, updateConfig)` in UI/SettingsDialog.cs. Scrollable Win32 modal (class `KoEnVueSettingsDlg` + child viewport class `KoEnVueSettingsViewport`) exposing 59 non-tray-redundant fields across 13 sections (display mode, appearance — size/colors/text/theme, animation, detection, app profiles, hotkeys, tray, system, multi-monitor, advanced) as a 2-column `label | control` table inside a `WS_VSCROLL | WS_BORDER` viewport. Factory methods (`Bool`=checkbox, `Int`/`Dbl`/`Str`/`ColorField`=EDIT, `Combo`=`CBS_DROPDOWNLIST`) produce `FieldDef`s with per-field `Commit` closures returning `(AppConfig? Config, string? Error)` — TryCommit chains them so each commit reads from the working config produced by the previous field. Scroll implementation tracks every child widget in `_scrollChildren` as `(Hwnd, X, LogicalY)` and repositions them via `SetWindowPos(SWP_NOSIZE | SWP_NOZORDER)` on `WM_VSCROLL`/`WM_MOUSEWHEEL`; `SWP_NOSIZE` preserves COMBOBOX dropdown height (created at `rowH + ComboDropExtra=220`). On validation failure, `TryCommit` shows a MessageBox, calls `ScrollFieldIntoView` to bring the offending field into view, refocuses the control, and for EDITs selects all text via `EM_SETSEL`. Nested record updates use `c with { EventTriggers = c.EventTriggers with { OnFocusChange = v } }` / same for `Advanced`. Clamp ranges mirror `Settings.Validate`. `controlColW` is dynamically capped to `innerContentW - labelColW - colGap` so input boxes never encroach on the vertical scrollbar reserve area — a fixed `controlColW` would get clipped under the scrollbar at the default dialog width. Look and feel matches CleanupDialog/ScaleDialog (맑은 고딕 9pt, `COLOR_BTNFACE` background, `SS_ETCHEDHORZ` section separators, `[UnmanagedCallersOnly]` WndProc function pointers, nested `GetMessageW` modal loop with `IsDialogMessageW`). Excludes tray-menu duplicates (opacity, indicator_scale, default_indicator_position, startup_with_windows, snap_to_windows, animation_enabled, change_highlight, indicator_positions, tray_enabled) and complex collection fields (app_profiles, app_filter_list, system_hide_classes, ime_fallback_chain, overlay_class_name, config_version)
- **Portable-only config location**: `Settings.Load()` reads and writes `config.json` exclusively from `AppContext.BaseDirectory` (the exe's own folder). There is no APPDATA fallback and no dual portable/installed mode — `P5` (`app.manifest requireAdministrator`) guarantees the exe directory is writable, so a single source of truth is safe. Complete uninstall is "delete the exe folder" because `koenvue.log` and `config.json` both live next to the exe. `I18n.PortableLabel`/`InstalledLabel` and any mode-distinguishing tooltip prefix were removed — tray tooltip is just `"KoEnVue - {state}"`
- **Auto-create config on first run**: `Settings.Load()` writes a freshly constructed default `AppConfig` to disk immediately when the file is missing, rather than deferring creation to the next `Save()`. Ensures the exe-only distribution UX matches expectations — drop the exe, launch, `config.json` materializes next to it on the first run. Logs `"Config not found, creating defaults at {path}"` before calling `Save`
- **Delete-safe hot reload**: `Settings.CheckConfigFileChange` returns early via `File.Exists(_configFilePath)` *before* calling `GetLastWriteTimeUtc`. Reason: for a missing file, `File.GetLastWriteTimeUtc` returns the sentinel `1601-01-01` without throwing, which differs from the cached mtime and would trigger a spurious `WM_CONFIG_CHANGED` → `Load()` → silent reset to defaults → next `Save()` overwrites the user's real config when it reappears. Locking the file to forbid deletion was rejected because atomic-replace editors (VSCode, Notepad++) rely on `delete → rename` during save, so a read-side `File.Exists` guard is the correct fix
- **Corrupted config spam prevention**: `Settings.Load()`'s catch block updates `_lastConfigMtime` to the broken file's mtime even when `LoadFromFile` throws. Without this, the 5-second poll sees mtime ≠ cached value, re-posts `WM_CONFIG_CHANGED`, `Load()` fails with the same parse error, and the warning log spams forever until the user fixes or deletes the file. Catch intentionally does NOT `Save()` — the user's broken file stays on disk so they can inspect and recover manually
- **Startup task path auto-sync**: `Tray.SyncStartupPathAsync()` runs on a background thread immediately after `Tray.Initialize` in `Program.cs`. It invokes `schtasks.exe /query /tn ... /xml ONE`, extracts the `<Command>` element with plain string `IndexOf` (no `XmlDocument` to keep NativeAOT lean — just unescape `&amp;`/`&quot;`/etc. manually), normalizes both paths via `Path.GetFullPath` + `OrdinalIgnoreCase`, and re-registers the task with `/create /f` if the stored path differs from `Environment.ProcessPath`. Handles the "user moved the exe" case: the first boot after a move still misses because Task Scheduler launches the old path, but on the next manual launch the sync runs and subsequent boots pick up the corrected path. `QueryRegisteredTaskCommand` wraps `Process.Start` in try/catch so schtasks being absent or non-zero exit is silently ignored. Multi-user on a shared exe directory has no per-user isolation — that's an inherent property of portable mode, not a bug
- **Tray tooltip NIF_SHOWTIP**: `NOTIFYICON_VERSION_4` (set via `NIM_SETVERSION`) suppresses the standard `szTip` tooltip by default on Windows 7+. To restore hover tooltip display, both `NIM_ADD` and `NIM_MODIFY` calls must include `NIF_SHOWTIP` (0x00000080) alongside `NIF_TIP` in `uFlags`. Without `NIF_SHOWTIP`, `szTip` is correctly populated but the shell silently discards it and renders nothing on hover
- **Dialog metric helper**: `Utils/Win32DialogHelper.cs` centralizes the DPI-aware non-client size + 9pt system font calculations that CleanupDialog, ScaleDialog, and SettingsDialog all perform. `CalculateNonClientHeight(rawDpi) = SM_CYCAPTION + 2*SM_CYFIXEDFRAME + 2*SM_CXPADDEDBORDER`, `CalculateNonClientWidth(rawDpi) = 2*SM_CXFIXEDFRAME + 2*SM_CXPADDEDBORDER`, `CalculateFontHeightPx(dpiY, pointSize = 9.0) = -round(pointSize * dpiY / 72)`. `DefaultDialogFontPointSize = 9.0` and `PointsPerInch = 72.0` replace inline magic numbers. Note: `Overlay.EnsureFont`'s `Kernel32.MulDiv(scaledFontSize, dpiY, 72)` path is a separate case — overlay font size is `config.FontSize`-variable, so MulDiv is more precise than the fixed 9pt Math.Round path used by dialogs. Do not unify the two
- **DWMWA constants location**: `DWMWA_EXTENDED_FRAME_BOUNDS` and `DWMWA_CLOAKED` live in `Native/Win32Types.cs` under the `Win32Constants` class (section "--- DWM Window Attributes ---") rather than inside `Native/Dwmapi.cs`. P4 mandates that all Win32 structs and constants are centralized in Win32Types.cs regardless of which DLL they belong to, so DWM constants follow the same rule as the SM/WS/etc. families. `Dwmapi.cs` references them as `Win32Constants.DWMWA_*`
- **Silent catch diagnostics**: Policy for `catch` blocks in this codebase:
  1. **Type narrowing over bare `catch`**: Replace `catch { }` with `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` (or whichever specific types the `try` body can actually throw). Logic bugs (NullRef, IndexOutOfRange) propagate instead of hiding
  2. **Wide catch allowed when narrowing is impossible**: If `try` body is a single P/Invoke/COM call and expected exception types can't be listed (e.g., `[PreserveSig]` COM path in `SystemFilter.IsOnCurrentVirtualDesktop`), keep `catch (Exception ex)` + logging. Single-line `try` means wide catch can't mask logic bugs
  3. **Log level**: Hot-path / modal-internal swallowing is `Logger.Debug`; rare catastrophic paths (`CleanupDialog` outer Process.GetProcesses failure, `Tray.Remove` NIM_DELETE failure on shutdown) are `Logger.Warning`
  4. **Intentionally empty catches**: `Program.cs:62` (crash writer — Logger may be uninitialized, can't log anything anyway) and `Settings.cs:75` (nested mtime catch inside outer catch that already logs) remain empty. Both have single-line try bodies so wide catch poses no masking risk
  5. **Logger self-catches stay silent + narrowed**: `Logger.FlushQueue` and `Logger.Initialize` cannot recursively use `Logger.*` for their own file I/O failures, so they stay silent, but narrow to `IOException or UnauthorizedAccessException` so logic bugs in the drain loop / init path crash the drain thread and surface. `Initialize` also writes a single `Trace.WriteLine` fallback so the debugger has a hint. `StopDrainThread` uses `_fileWriter?.WriteLine(...)` + `Console.Error.WriteLine` when `Join` times out, bypassing the already-closed queue
  6. **Program.cs:163 is not a catch**: `CleanupPreviousTrayIcon`'s `Shell_NotifyIconW(NIM_DELETE)` is a P/Invoke `bool` return value ignored, not an exception swallow. Missing icon (normal path for clean startup) returns false, so logging would spam on every boot
- **Tray toggles for animation & highlight**: `IDM_ANIMATION_ENABLED = 4006` and `IDM_CHANGE_HIGHLIGHT = 4007` sit in the main tray menu directly below "창에 자석처럼 붙이기", mirroring `IDM_SNAP_TO_WINDOWS`'s `MF_CHECKED` / record-`with` pattern. Because the same two fields are also toggleable from `SettingsDialog`, both dialog rows were removed to avoid duplication — the settings dialog drops from 61 → 59 fields. `SlideAnimation` is deliberately not added to the tray because usage frequency is low and keeping the menu short is a UX goal. The three-toggle duplication (`SnapToWindows` + `AnimationEnabled` + `ChangeHighlight`) is kept as vertical copy rather than extracted to a helper because `ShowMenu` savings would be only 3 lines while `HandleMenuCommand`'s per-field `with`-expression getters/setters can't be mechanically abstracted without a delegate map or reflection (conflicts with NativeAOT + P1). Re-evaluate if a 4th bool toggle arrives

## Spec Files

- `docs/KoEnVue_PRD.md` — Product requirements document
