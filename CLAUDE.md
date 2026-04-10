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
| **P4** | Shared modules enforced: DPI→DpiHelper, Color→ColorHelper, GDI handles→SafeGdiHandles, P/Invoke→Native/, structs/constants→Win32Types.cs |
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
├── UI/          Overlay (GDI rendering + floating positioning), Animation (WM_TIMER state machine), Tray (system tray + schtasks + cleanup dialog + scale input dialog), TrayIcon (GDI icon)
├── Config/      DefaultConfig, Settings (load/save/validate/migrate/hot-reload/app-profiles), ThemePresets (6 themes)
├── Utils/       DpiHelper, ColorHelper (Hex↔ColorRef↔RGB), Logger, I18n (Ko/En UI text)
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
- **Snap to windows during drag**: `config.SnapToWindows` (default true) toggles magnetic edge snapping to nearby top-level windows and the current monitor's work area. `BeginDrag(AppConfig)` captures `_dragHotPointX/Y` (cursor offset from window top-left via `GetCursorPos`) and enumerates candidates into `_snapRects` via `User32.EnumWindows` with a `[UnmanagedCallersOnly]` `EnumWindowsCallback` — filters out the overlay itself, non-visible windows, iconic windows, DWM-cloaked windows (`Dwmapi.IsCloaked` wrapping `DWMWA_CLOAKED = 14`), and anything smaller than `SnapMinWindowSizePx` (80px). Each candidate's rect comes from `Dwmapi.TryGetVisibleFrame` so snap aligns with the DWM visible frame, not `GetWindowRect`'s invisible resize border. `ApplySnap` picks the smallest X and Y edge-pair distances within `SnapThresholdPx` (DPI-scaled), only applied to axes not already locked by Shift. `EndDrag` clears `_snapRects`. Tray menu `IDM_SNAP_TO_WINDOWS` (ID 4004) sits in the main menu between "크기" and the separator, grouped with the drag-time controls
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

## Spec Files

- `docs/KoEnVue_PRD.md` — Product requirements document
