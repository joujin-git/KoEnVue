# Implementation notes

Deep-dive details on render pipeline, drag/snap, animation, detection, hot reload, and dialogs. Companion to [CLAUDE.md](../CLAUDE.md) and [KoEnVue_PRD.md](KoEnVue_PRD.md) — this file is where "why" explanations and non-obvious workarounds live.

Conventions and policies (P1–P6, catch narrowing, .NET 10 quirks) are in **[conventions.md](conventions.md)**.

---

## Indicator rendering

### Style is hardcoded

Text label (`한` / `En` / `EN`) + `RoundedRect` shape. No style/shape selection is exposed. GDI-based pipeline: DIB section → `RoundRect` → `DrawTextW` → premultiplied alpha post-processing → `UpdateLayeredWindow`.

### CAPS LOCK bars

When CAPS LOCK is toggled on, two vertical bars (reusing the per-state `fg` color) are drawn on the left and right edges of the label, vertically inset by `ScaledBorderRadius` to avoid the rounded corners and horizontally inset by `max(ScaledBorderWidth, CapsLockBarInsetLogicalPx)`.

The right bar has an additional `CapsLockRightCompensationPx = 1` physical-px visual correction. The math is symmetric, but `RoundRect`'s right/bottom-exclusive semantics combined with `DrawTextW` AA weighting and premultiplied alpha compositing make the right gap look 1 px narrower without it.

All three constants (`CapsLockBarWidthLogicalPx`, `CapsLockBarInsetLogicalPx`, `CapsLockRightCompensationPx`) live as `private const` in [Overlay.cs](../App/UI/Overlay.cs) next to `SystemInputGapPx`. The bars are drawn via `FillRect` with `fg` color inside the existing `hBrush` try/finally block.

See [CAPS LOCK detection](#caps-lock-detection) below for the polling mechanism.

### DT_VCENTER glyph-vs-cell asymmetry fix

`DT_VCENTER` centers the font *cell* (`tmAscent + tmDescent`), not the visible glyph box. Most Korean fonts (맑은 고딕 included) have `tmInternalLeading > tmDescent` — the top of the cell reserves space for Latin diacritics that Korean and ASCII-uppercase glyphs don't use, so the visible glyph midpoint sits below the cell midpoint by `(tmInternalLeading - tmDescent) / 2` physical px. Without correction, "한"/"En"/"EN" labels appear visibly low inside the rounded background.

- **Measurement**: `LayeredOverlayBase.EnsureFont` calls `Gdi32.GetTextMetricsW` once per HFONT creation (after `SelectObject(hFont)` into `_memDC`) and caches `_textVCenterOffsetPx = (tm.tmInternalLeading - tm.tmDescent) / 2`. Gated by the font cache key (family + size + bold + DPI), so it only runs on boot + font/size/weight/DPI changes (~1–2 calls per session)
- **Exposure**: `OverlayMetrics.TextVCenterOffsetPx` (positive = shift textRect up by N physical px)
- **Application**: `Overlay.OnRenderToDib` constructs the textRect as `{ Top = -vOffset, Bottom = h - vOffset }` — height is preserved so `DT_VCENTER` still centers the cell normally inside the shifted rect, and the rect itself moves up so the visible glyph midpoint lands exactly at `h/2`
- **Limitation**: Formula is descender-free. Works for `한`/`En`/`EN` because none have descenders. Adding labels with `g`/`p`/`q` would over-correct and require re-derivation from per-glyph metrics

### GDI handle safety

`Overlay.OnRenderToDib` wraps the two created GDI handles (`hBrush` from `CreateSolidBrush` and the optional `hBorderPen` from `CreatePen`) in nested `try/finally` blocks so `DeleteObject` runs on every exit path. The outer `finally` also restores the NULL_PEN selection on the HDC. The discipline is kept visible because adding a future `throw`/`return` inside the callback must not leak GDI handles.

The stock pen from `GetStockObject(NULL_PEN)` is intentionally NOT deleted — it's a system-owned handle.

### Premultiplied alpha

`UpdateLayeredWindow` with `ULW_ALPHA` requires premultiplied RGB values. GDI output (`RoundRect`/`DrawTextW`) is non-premultiplied, and `DrawTextW` AA edges produce partial alpha pixels, so post-processing is required to multiply each pixel's RGB channels by its alpha.

### DIB is top-down

Negative `biHeight` in the BITMAPINFO so `(0, 0)` is top-left. Keeps the pixel arithmetic in the post-processing loop consistent with GDI's top-left origin.

### Label DIB flip-flop prevention

`_fixedLabelWidth` is cached inside `LayeredOverlayBase` after measuring all three labels (`OverlayStyle.MeasureLabels` tuple) and taking the max. This prevents the DIB from churning in width on state transitions (한→En, En→EN, etc.) because all three labels are computed at the same width.

The per-render skip uses `OverlayStyle` `record struct` value equality — `newStyle == _lastStyle` returns `true` when nothing visible has changed. Because `CapsLockOn` is a field inside the record, toggling it automatically breaks equality and forces a re-render.

---

## Indicator positioning

### Draggable floating window

The indicator is a separate TOPMOST window, not tied to any foreground window's geometry. `WM_NCHITTEST → HTCAPTION` enables native drag. `WM_ENTERSIZEMOVE` / `WM_EXITSIZEMOVE` track drag lifecycle.

### Two-tier position memory

1. **Runtime (`Dictionary<IntPtr, (int, int)>`)** — per-hwnd positions, enables distinguishing multiple windows of the same process (e.g., multiple Notepad/Chrome windows). Lost on restart
2. **Config (`indicator_positions`)** — per-process-name positions, persists across sessions as fallback

On foreground change, lookup order is: runtime hwnd → config process name → default position.

### Default position

`config.default_indicator_position` is a nullable `DefaultPositionConfig` record (`Corner` + `DeltaX` + `DeltaY`) that stores a user-customizable default for apps without a saved position. `GetDefaultPosition` resolves the anchor against the **foreground window's monitor work area** (not the overlay's current monitor), so the default position follows the active app.

- **Null fallback**: `DefaultConfig.DefaultIndicatorOffsetX = -200, Y = 10` (hardcoded top-right of work area)
- **Multi-monitor / resolution stability**: offsets are stored relative to a `Corner` anchor, not as absolute pixel coordinates
- **Tray menu "기본 위치 → 현재 위치로 설정"**: `Overlay.ComputeAnchorFromCurrentPosition()` picks the nearest corner by Manhattan distance from `_lastX, _lastY`. User never has to think about corner selection
- **Tray menu "초기화"**: sets the field back to null (menu item grayed when already null)

### Off-screen position clamp

`Program.ClampToVisibleArea(x, y)` wraps `GetAppPosition`'s two saved-position tiers (runtime hwnd dict + `config.IndicatorPositions`) before they are returned. Resolves the target monitor via `DpiHelper.GetMonitorFromPoint(x + w/2, y + h/2)` with `MONITOR_DEFAULTTONEAREST` semantics, so a coordinate whose original monitor has been disconnected re-routes to the nearest surviving monitor's work area.

Clamp bounds use `Math.Max(workArea.Left, workArea.Right - w)` as the upper limit so indicators larger than the work area collapse to `Left`/`Top` instead of flipping through `Math.Clamp`'s invalid-range exception.

**The saved value is never rewritten** — reattaching the original monitor restores the original position on the next lookup. Defends monitor removal / resolution change / DPI change scenarios that would otherwise leave the indicator unreachable.

Path 3 (default position) is not clamped because `GetDefaultPosition` already computes against the live foreground monitor's work area. System input processes bypass this entirely since they already route straight to `GetDefaultPosition`.

### System input process exception

`StartMenuExperienceHost` / `SearchHost` / `SearchApp` (`DefaultConfig.SystemInputProcesses`) are special. TOPMOST z-band cannot rise above these shell UI surfaces, so any saved position that ends up under them becomes unreachable.

- Drag is ignored (position never saved)
- `GetDefaultPosition` places the indicator just above the window's visual top-left corner: `(frame.Left, frame.Top - labelH - SystemInputGapPx)`, clamped to `workArea.Top`
- The "visual" frame is obtained via `Dwmapi.TryGetVisibleFrame` → `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` to exclude the invisible resize border
- **Full-screen DWM frame guard + cached frame reuse**: CoreWindow hosts (e.g., `StartMenuExperienceHost`) return DWM extended frame bounds covering the entire screen, not the visible panel. When the frame encloses the full work area (`Left ≤ workArea.Left && Top ≤ workArea.Top && Right ≥ workArea.Right && Bottom ≥ workArea.Bottom`), the static `_lastValidSystemInputFrame` cache is consulted — if a recent non-full-screen system input frame exists (typically from `SearchHost`, which always appears before `StartMenuExperienceHost` in the Win11 Start Menu opening sequence), that cached frame is used for positioning. Only when no cached frame is available does the code fall through to the general default position

### Shared-HWND system input rect tracking

Win11 reuses a single HWND (e.g., `SearchHost 0x30254`) for both Start Menu and Search modes, distinguishing them only by rect. `DetectionLoop` caches `lastSystemInputFrame` and treats any DWM frame change on the same HWND as a foreground change, re-posting `WM_POSITION_UPDATED`. `HandlePositionUpdated` has a `sysInput` branch that re-resolves position even when `hwndForeground == _lastForegroundHwnd`, so Start Menu ↔ Search transitions re-anchor the indicator.

### System input ESC-dismissal detection

시스템 입력 프로세스(`StartMenuExperienceHost`, `SearchHost`, `SearchApp`)는 `SystemFilter` 블랙리스트에 의도적으로 포함되지 않으므로(인디케이터를 표시해야 하므로), 이들 UI가 ESC 등으로 닫힐 때 인디를 숨기는 별도 메커니즘이 `DetectionLoop`에 있다. 두 가지 닫힘 패턴이 경험적으로 확인됨:

**(A) HWND 유지 + DWM cloaked — `StartMenuExperienceHost`**
ESC 후 foreground HWND가 수 초간 유지되며 DWM cloaked 상태(`DWMWA_CLOAKED`)가 된다. `DetectionLoop`가 매 틱마다 `Dwmapi.IsCloaked(hwndForeground)`를 확인하여 cloaked이면 `WM_HIDE_INDICATOR`를 보내고 `continue`한다. 이후 OS가 foreground를 이전 앱으로 돌리면 다음 틱에서 정상 표시 경로를 탄다.

**(B) 즉시 foreground 전환 — `SearchHost` / `SearchApp`**
ESC 후 cloaked 없이 foreground가 즉시 다른 앱의 HWND로 변경된다. `leavingSystemInput` 플래그(HWND 변경 시 이전 프로세스명이 시스템 입력인지 확인)가 true이고, 새 foreground가 시스템 입력이 아닌 일반 앱이면 `WM_HIDE_INDICATOR` 후 `continue`한다. `lastHwndForeground`를 갱신하지 않으므로 다음 틱에서 foreground 변경이 재감지되어 새 앱에 인디가 표시된다. 단, 인디가 이미 (A)에 의해 숨겨진 경우에는 `continue`하지 않고 fall-through하여 새 앱에 즉시 표시한다.

시스템 입력 간 전환(시작 메뉴 → 검색)은 (B)에서 제외되어 정상 표시 흐름을 유지한다.

---

## Drag and snap

### Shift-drag axis constraint

While the `HTCAPTION` system drag loop is running, `HandleMoving` checks `GetAsyncKeyState(VK_SHIFT)` per `WM_MOVING` tick. When held, the dominant axis (larger `|dx|` vs `|dy|` relative to `_dragStart{X,Y}` captured in `BeginDrag`) is locked to the start coordinate by rewriting the RECT's Top/Bottom or Left/Right (width/height preserved).

`HandleMoving` returns `true` when modified; caller writes back via `Marshal.StructureToPtr` and returns `(IntPtr)1` from `WM_MOVING`. DPI check runs with the constrained coordinates so monitor-crossing along the unlocked axis still resizes the indicator correctly. Screen coordinates are absolute, so multi-monitor works without special handling.

Shift can be pressed/released mid-drag — axis flips if the user drags far enough in the opposite direction while holding Shift.

### Snap to windows during drag

`config.SnapToWindows` (default `true`) toggles magnetic edge snapping to nearby top-level windows and the current monitor's work area. Tray menu toggle: `IDM_SNAP_TO_WINDOWS = 4004`.

- **`BeginDrag(bool snapToWindows)`** captures `_dragHotPointX/Y` (cursor offset from window top-left via `GetCursorPos`) and, when enabled, enumerates candidates into `_snapRects` via `User32.EnumWindows` with a `[UnmanagedCallersOnly]` callback
- **Filter**: excludes the overlay itself, non-visible windows, iconic windows, DWM-cloaked windows (`Dwmapi.IsCloaked` wrapping `DWMWA_CLOAKED = 14`), and anything smaller than `SnapMinWindowSizePx = 80`
- **Candidate rect source**: `Dwmapi.TryGetVisibleFrame` — snap aligns with the DWM visible frame, not `GetWindowRect`'s invisible resize border
- **`HandleMoving(ref RECT, style, snapToWindows, snapThresholdPx, snapGapPx)`** picks the smallest X and Y edge-pair distances within `snapThresholdPx = 10` (DPI-scaled) via the private `ApplySnap` helper. Window edge snaps apply a configurable gap (`snapGapPx`, default 2, DPI-scaled) to prevent the indicator from overlapping with the target window's border; screen (work area) edges snap flush with zero gap. Only applied to axes not already locked by Shift
- **`EndDrag`** clears `_snapRects`

### EnumWindows NativeAOT callback

Uses `delegate* unmanaged<IntPtr, IntPtr, int>` + `[UnmanagedCallersOnly]` instead of delegate marshaling — consistent with the rest of the project's `[LibraryImport]` style. Return type is `int` (not `bool`) because Win32 `BOOL` is 4 bytes.

### WM_MOVING drift re-sync

Non-idempotent modifications (like snap) inside `WM_MOVING` accumulate drift because the system uses the previous tick's modified rect as the base for the next tick's cursor delta (`new_rect = prev_modified + cursor_delta`). Without re-sync, a slow drag with snap active pulls the cursor further and further from the indicator.

**Fix**: `HandleMoving` first re-syncs `movingRect` to `(cursor - _dragHotPoint)` via `GetCursorPos()` every tick, making the modification chain idempotent. Shift-drag worked without this fix because it always rewrites to a fixed start coordinate (also idempotent by construction), but snap needed explicit re-sync. `HandleMoving` now always returns `true` since the rect is always overwritten.

### WM_MOVING drag DPI

`HandleMoving` → private `HandleDragDpiChange` detects monitor boundary crossing during drag, re-creates resources at new DPI, and calls `UpdateLayeredWindow` directly (bypassing `_isDragging` guard).

---

## Animation

### 5-state machine

Hidden → FadingIn → Holding → FadingOut → Idle, plus highlight and slide sub-phases. All transitions driven by `WM_TIMER`.

Timer IDs (injected via `AnimationTimerIds` record so Core stays ID-agnostic):

| Timer | Purpose | Source constant |
|-------|---------|-----------------|
| `Fade` | Fade-in / fade-out frame tick | `DefaultConfig.AnimationFrameMs = 16` (~60 fps) |
| `Hold` | Holding → next phase. OnEvent: FadingOut → Hidden. Always: FadeToIdle (→ IdleOpacity) | OnEvent: `config.EventDisplayDurationMs`, Always: `config.AlwaysIdleTimeoutMs` |
| `Highlight` | IME-change zoom (1.3× → 1.0×) | `config.HighlightDurationMs` |
| `Topmost` | Periodic `ForceTopmost` re-assert | `DefaultConfig.ForceTopmostIntervalMs = 5000` |
| `Slide` | Ease-out cubic position interpolation | `config.SlideSpeedMs` |

### NonKoreanImeMode Dim

`OverlayAnimator.GetTargetAlpha` applies `DefaultConfig.DimOpacityFactor = 0.5` when the state machine is in the Dim branch. Since Stage 4 this lives inside `OverlayAnimator` and is driven by `OverlayAnimator.SetDimMode(bool)` — the `Animation` facade routes `config.NonKoreanImeMode == Dim && state == NonKorean` into it so Core never sees the enum.

### Slide animation

Ease-out cubic interpolation: `1 - (1 - t)^3` via `TIMER_ID_SLIDE`. All animation timers use `DefaultConfig.AnimationFrameMs = 16 ms` (~60 fps).

### Always mode default

`DisplayMode.Always` — indicator always visible (bright on events, dim at idle). `DisplayMode.OnEvent` available via config for fade-out-after-hold behavior.

Idle dimming is driven by `FadeToIdle()` inside `OverlayAnimator`: Hold timer fires after `AlwaysIdleTimeoutMs` → fade from current alpha to `IdleOpacity` over `FadeOutMs`. On the next event, `TriggerShow` fades back from `IdleOpacity` to `ActiveOpacity` over `FadeInMs`.

### HideOverlay `forceHidden`

System filter, hotkey toggle off, and tray toggle off all pass `forceHidden: true` to `Animation.TriggerHide` so Always mode collapses fully instead of sliding into dim-idle. "Hide" from these sources means "actually disappear", distinct from Always-mode idle dimming.

---

## Detection

### Message pipeline

```
Detection thread (80 ms poll):
  1. Every poll: ResolveForApp + SystemFilter.ShouldHide
     - Filter entry (!lastFiltered → filtered):   WM_HIDE_INDICATOR
     - Filter exit or foreground change:          WM_POSITION_UPDATED(hwndForeground)
  2. IME state change → WM_IME_STATE_CHANGED(ImeState)
  3. Focus change    → WM_FOCUS_CHANGED(hwndFocus)

Main thread:
  WM_POSITION_UPDATED  → If foreground changed OR previously hidden: resolve position + TriggerShow
  WM_IME_STATE_CHANGED → Tray update + TriggerShow
  WM_FOCUS_CHANGED     → TriggerShow
  WM_HIDE_INDICATOR    → Animation.TriggerHide(forceHidden: true) — bypasses Always-mode dim
  WM_MOVING            → Shift axis lock (HandleMoving) + drag-time DPI re-compute
```

### Foreground change detection

`foregroundChanged` flag triggers focus event independently of `hwndFocus` comparison, fixing the return-to-same-window case after a desktop switch.

### Console host fallback

`hwndFocus == 0` + `ConsoleWindowClass` check → use foreground window as focus target. Console apps don't report focus to AccessibleObjects, so we fall back.

### Position update ordering

Detection loop sends `WM_POSITION_UPDATED` **before** `WM_IME_STATE_CHANGED` / `WM_FOCUS_CHANGED` to ensure `_lastForegroundHwnd` is current when those handlers run.

### Per-poll filter evaluation

`DetectionLoop` evaluates `ResolveForApp + SystemFilter.ShouldHide` every tick (not only on foreground change) and uses a `lastFiltered` flag to suppress duplicate `WM_HIDE_INDICATOR` messages. Fixes the "desktop click → same app return" case where nothing appeared to change but the indicator needed to reappear. Hide message is emitted only on `!lastFiltered → filtered` transitions.

### `wasHidden` re-trigger

`HandlePositionUpdated` treats `!_indicatorVisible` as a signal to re-resolve position and show, even when `hwndForeground == _lastForegroundHwnd`. Complements per-poll filter re-eval: detection thread posts `WM_POSITION_UPDATED` after filter clears, main thread sees the same hwnd but knows the indicator needs to come back.

### Deferred `lastHwndForeground`

Detection loop only updates `lastHwndForeground` **after** `ShouldHide` passes. If filtered (transient condition), the next poll retries the foreground change.

### IME state detection

- `WM_IME_CONTROL` + `IMC_GETOPENSTATUS` / `IMC_GETCONVERSIONMODE`
- `GetKeyboardLayout` LANGID check (for non-Korean IME identification)
- `EVENT_OBJECT_IME_CHANGE` WinEvent hook as supplementary signal

### System filter (9 conditions)

1. Secure desktop (no hwnd)
2. Invisible / minimized window
3. Other virtual desktop
4. Class name blacklist (`Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `XamlExplorerHostIslandWindow_WASDK` + user-specified)
4-b. Owner chain blacklist — walks `GetWindow(GW_OWNER)` up to 5 levels; hides only when owner class is in hide list **and** dialog/owner share the same process. This catches desktop-initiated system dialogs (e.g. Recycle Bin empty confirm: `#32770` owned by `Progman`, both `explorer.exe`) while allowing app-initiated Common File Dialogs (e.g. Notepad Save As: `#32770` owned by `Progman` but process `Notepad` ≠ `explorer`)
5. Process name blacklist (`ShellExperienceHost` + user-specified) — hides taskbar/desktop right-click context menus on Win11 where the popup becomes the foreground window with a null owner chain
6. No focus (`hide_when_no_focus`)
7. Fullscreen exclusive (covers monitor + no `WS_CAPTION`)
8. App blacklist / whitelist (`app_filter_list` + `app_filter_mode`)

---

## CAPS LOCK detection

### Why main thread, not detection thread

`GetKeyState(VK_CAPITAL)` is documented to read the **calling thread's input state**, not the global keyboard state. It's unsafe from the 80 ms detection background thread because that thread doesn't have the right input state attached.

Polling lives on the **main thread** via `WM_TIMER` (`TIMER_ID_CAPS = 6`, 200 ms, `DefaultConfig.CapsLockPollMs`). `Program.HandleCapsLockTimer` diffs against `_lastCapsLockState`, calls `Overlay.SetCapsLock(bool)` on change.

### Hidden-state handling

Conditionally re-invokes `Overlay.UpdateColor(_lastImeState)` only if `_indicatorVisible` so hidden-state transitions update the field without touching GDI.

### Startup initial state

Read twice on startup — once inside `Overlay.Initialize` (so the very first `PrepareResources` render is correct for a user who launched with CAPS LOCK already on) and once in `Program.Main` before `SetTimer` (so the first timer tick sees the same value and does not spuriously re-render).

### Record struct value equality breaks flip-flop guard

`Overlay._capsLockOn` is a `private static bool` field read by `BuildStyle` and flowed to the engine via the 14th field `OverlayStyle.CapsLockOn`. Because `OverlayStyle` is a `record struct`, toggling the bit automatically breaks `newStyle == _lastStyle` equality and forces a re-render.

---

## Config hot reload

### Pipeline

`Settings.Load()` runs through `JsonSettingsManager<T>.Load` which invokes 5 hooks in fixed order:

1. **Deserialize** — reads and deserializes the JSON
2. **`ApplyNullSafetyNet`** (EnsureSubObjects) — guards against null `AppProfiles` / `Advanced` etc. from malformed config
3. **`PostDeserializeFixup`** (MergeWithDefaults) — serializes default `AppConfig` to JSON, overlays user keys, deserializes back. Works around STJ source-gen init-default loss (see [conventions.md](conventions.md#net-10-compatibility-notes))
4. **`Migrate`** — version upgrades (when `config_version` changes)
5. **`Validate`** — range clamping and normalization
6. **`ApplyTheme`** — theme preset overlay (if `theme != custom`)

### Delete-safe hot reload

`Settings.CheckConfigFileChange` returns early via `File.Exists(_configFilePath)` **before** calling `GetLastWriteTimeUtc`. For a missing file, `File.GetLastWriteTimeUtc` returns the sentinel `1601-01-01` without throwing, which differs from the cached mtime and would trigger a spurious `WM_CONFIG_CHANGED` → `Load()` → silent reset to defaults → next `Save()` overwrites the user's real config when it reappears.

Locking the file to forbid deletion was rejected because atomic-replace editors (VSCode, Notepad++) rely on `delete → rename` during save.

### Corrupted config spam prevention

`Settings.Load()`'s catch block updates `_lastConfigMtime` to the broken file's mtime even when `LoadFromFile` throws. Without this, the 5-second poll sees `mtime ≠ cached value`, re-posts `WM_CONFIG_CHANGED`, `Load()` fails with the same parse error, and the warning log spams forever.

Catch intentionally does NOT `Save()` — the user's broken file stays on disk so they can inspect and recover manually.

### Auto-create config on first run

`Settings.Load()` writes a freshly constructed default `AppConfig` to disk immediately when the file is missing, rather than deferring creation to the next `Save()`. Ensures the exe-only distribution UX matches expectations — drop the exe, launch, `config.json` materializes next to it on the first run.

### Config file location

Exclusively read from and written to `AppContext.BaseDirectory` (the exe's own folder). **No APPDATA fallback**. P5 (`app.manifest requireAdministrator`) guarantees the exe directory is writable. Complete uninstall is "delete the exe folder" because `koenvue.log` and `config.json` both live next to the exe.

### Self-triggered reload prevention

`_lastConfigMtime` is updated **after** `Settings.Save()` to prevent `WM_CONFIG_CHANGED` from firing on our own writes.

### STJ source-gen init default workaround

`MergeWithDefaults()` serializes a freshly constructed default `AppConfig` to JSON, overlays the user's loaded keys, then deserializes the result. Required because STJ source generation drops `init` defaults for properties absent from JSON under NativeAOT — if the user's `config.json` omits `Opacity`, the deserialized object has `Opacity == 0.0` instead of `0.85`.

`EnsureSubObjects()` remains as null safety net for nested records (`EventTriggers`, `Advanced`) whose default construction can also be lost.

---

## Tray

### NIF_SHOWTIP

`NOTIFYICON_VERSION_4` (set via `NIM_SETVERSION`) suppresses the standard `szTip` tooltip by default on Windows 7+. Both `NIM_ADD` and `NIM_MODIFY` calls must include `NIF_SHOWTIP` (0x00000080) alongside `NIF_TIP` in `uFlags`. Without `NIF_SHOWTIP`, `szTip` is correctly populated but the shell silently discards it and renders nothing on hover.

### WM_CONTEXTMENU (not WM_RBUTTONUP)

`NOTIFYICON_VERSION_4` requires `WM_CONTEXTMENU` for right-click menu — shell grants foreground activation on `WM_CONTEXTMENU`. Handling `WM_RBUTTONUP` instead would result in menu items failing to respond because the tray app doesn't have keyboard focus.

### Tray callback routing

Handled in [Program.cs](../Program.cs) (not `Tray.cs`) because it needs `_indicatorVisible` access for the tray click-action toggle.

### Startup task path auto-sync

`Tray.SyncStartupPathAsync()` runs on a background thread immediately after `Tray.Initialize` in `Program.cs`. It:

1. Invokes `schtasks.exe /query /tn ... /xml ONE`
2. Extracts the `<Command>` element with plain string `IndexOf` (no `XmlDocument` — NativeAOT-friendly). Manually unescapes `&amp;` / `&quot;` / etc.
3. Normalizes both paths via `Path.GetFullPath` + `OrdinalIgnoreCase`
4. Re-registers the task with `/create /f` if the stored path differs from `Environment.ProcessPath`

Handles the "user moved the exe" case: the first boot after a move still misses because Task Scheduler launches the old path, but on the next manual launch the sync runs and subsequent boots pick up the corrected path. `QueryRegisteredTaskCommand` wraps `Process.Start` in try/catch so schtasks being absent or non-zero exit is silently ignored.

### Tray menu structure

```
새 버전 있음 (v0.9.0) — 다운로드       ← only when UpdateChecker finds an update
───
투명도 ▸       진하게 / 보통 / 연하게
크기 ▸         1배 / 2배 / 3배 / 4배 / 5배 / 직접 지정...
☑ 창에 자석처럼 붙이기
☑ 애니메이션 사용
☑ 변경 시 강조
───
☑ 시작 프로그램 등록
───
기본 위치 ▸    현재 위치로 설정 / 초기화
위치 기록 정리...
───
상세 설정...
───
종료
```

Menu IDs live in [Tray.cs](../App/UI/Tray.cs) as `private const int IDM_*`. The `IDM_UPDATE_DOWNLOAD = 4008` item + separator are only appended when `_pendingUpdate != null`.

### Quick opacity presets (`ApplyQuickOpacity`)

The three opacity presets (진하게/보통/연하게) apply mode-aware config changes via `Tray.ApplyQuickOpacity`. In Always mode, the preset value is written to `ActiveOpacity` and `IdleOpacity` is proportionally scaled (ratio preserved). In OnEvent mode, only `Opacity` is written. The radio check compares against `ActiveOpacity` in Always mode, `Opacity` in OnEvent mode.

### Three-toggle duplication with settings dialog

`SnapToWindows`, `AnimationEnabled`, and `ChangeHighlight` are toggleable from both the tray menu and `SettingsDialog`. The settings dialog drops these three rows to avoid duplication (62 → 60 fields). `SlideAnimation` is deliberately **not** added to the tray because usage frequency is low and keeping the menu short is a UX goal.

The duplication is kept as vertical copy rather than extracted to a helper because `HandleMenuCommand`'s per-field `with`-expression getters/setters can't be mechanically abstracted without a delegate map or reflection (conflicts with NativeAOT + P1).

---

## Dialogs

All three dialogs (`CleanupDialog`, `ScaleInputDialog`, `SettingsDialog`) share the same modal infrastructure:

- **`ModalDialogLoop.Run(hwndDialog, hwndOwner, ref isClosed)`** — Core helper for the `EnableWindow(owner, false) + while(GetMessageW... IsDialogMessageW(...)) { Translate/Dispatch } + EnableWindow(owner, true)` boilerplate. The `ref bool isClosedFlag` lets each dialog's WndProc signal close from inside `WM_COMMAND`/`WM_CLOSE` without the loop helper knowing the close semantics. When the nested loop consumes `WM_QUIT` (e.g., tray Exit while a dialog is open), it re-posts `PostQuitMessage` so the outer message loop also terminates
- **`Win32DialogHelper.CreateDialogFont(dpiY) → SafeFontHandle`** — 9 pt 맑은 고딕 with `SafeFontHandle` RAII
- **`Win32DialogHelper.CalculateDialogPosition(hMonitor, w, h, anchor?)`** — `null` anchor = center in work area (Cleanup/Settings pattern); `POINT` anchor = top-left at that point (ScaleInput cursor-anchored pattern). Both paths apply work-area clamping
- **`using var hFont = ...`** declared at the top of each dialog's `Show` method frame before `CreateWindowExW`. The `using` scope covers the full modal loop + `DestroyWindow` so the HFONT cannot be freed while child controls still reference it
- **`[UnmanagedCallersOnly]` WndProc function pointers** private to each file (no NativeAOT export name collision)
- **Tab/Enter/ESC** routed through `IsDialogMessageW`

### CleanupDialog

Checkbox list of **all** `indicator_positions` entries. Running processes are shown with a "(실행 중)" / "(running)" suffix. Full select/deselect toggle. "저장된 위치 기록이 없습니다" message when empty. When items exceed `DlgMaxVisibleItems` (15), a scrollable viewport child window with `WS_VSCROLL` + mouse wheel support is used — same pattern as `SettingsDialog.Scroll.cs`.

### ScaleInputDialog

Custom scale entry for values outside the 1.0–5.0 integer presets. Spawned at cursor position via `CalculateDialogPosition(POINT anchor)`. EDIT pre-filled via `initialValue.ToString("0.#")` (`"2"` for 2.0, `"2.3"` for 2.3).

Parsing uses `double.TryParse` + `CultureInfo.InvariantCulture`, so `"2.3"` works regardless of OS locale. Validation failure shows a MessageBox then refocuses EDIT with `EM_SETSEL(0, -1)` (select all) for easy re-entry.

`ScaleDlgProc` accepts both `IDC_SCALE_OK || IDOK` and `IDC_SCALE_CANCEL || IDCANCEL` because `IsDialogMessageW` synthesizes `IDOK`/`IDCANCEL` for Enter/ESC.

### SettingsDialog

60 fields across 13 sections. Split across 3 partial class files:

- **`SettingsDialog.cs`** (modal state, `Show`, `TryCommit`, dialog WndProc)
- **`SettingsDialog.Fields.cs`** (`FieldType` enum, `FieldDef`/`RowDef` records, `BuildRowDefs` 13-section spec, 6 factory methods: `Bool`/`Int`/`Dbl`/`Str`/`ColorField`/`Combo`)
- **`SettingsDialog.Scroll.cs`** (scroll state, `SetupScrollbar`, `ScrollTo`, `ScrollFieldIntoView`, `ResolveVScrollPosition`, viewport WndProc)

`partial class` shares all static state at compile time. No call-site changes — `SettingsDialog.Show(hwndMain, config, updateConfig)` is the same public entry point.

**Scroll implementation**: tracks every child widget in `_scrollChildren` as `(Hwnd, X, LogicalY)` and repositions them via `SetWindowPos(SWP_NOSIZE | SWP_NOZORDER)` on `WM_VSCROLL` / `WM_MOUSEWHEEL`. `SWP_NOSIZE` preserves COMBOBOX dropdown height (created at `rowH + ComboDropExtra = 220`).

**Validation failure handling**: `TryCommit` shows a MessageBox, calls `ScrollFieldIntoView` to bring the offending field into view, refocuses the control, and for EDITs selects all text via `EM_SETSEL`.

**`controlColW` dynamic cap**: capped to `innerContentW - labelColW - colGap` so input boxes never encroach on the vertical scrollbar reserve area — a fixed `controlColW` would get clipped under the scrollbar at the default dialog width.

**Excludes**: fields already toggleable from the tray menu (opacity, indicator_scale, default_indicator_position, startup_with_windows, snap_to_windows, animation_enabled, change_highlight, indicator_positions, tray_enabled), complex collection fields (app_profiles, app_filter_list, system_hide_classes, system_hide_processes, ime_fallback_chain), and internal-only fields (overlay_class_name, config_version).

### Decimal indicator scale

`config.IndicatorScale` is a `double` in range `[1.0, 5.0]`, rounded to 1 decimal place in `Settings.Validate`. Applied as `(int)Math.Round(baseValue * scale)` to `LabelWidth`, `LabelHeight`, `FontSize`, `LabelBorderRadius`, `BorderWidth`, and `LABEL_PADDING_X` — *before* DPI scaling, so DPI and `IndicatorScale` compose multiplicatively.

Tray menu "크기 ▸" submenu lists 5 integer presets (1배~5배) plus a "직접 지정..." item that opens `ShowScaleInputDialog`. Radio check behavior: `IsIntegerScale(scale)` (tolerance 0.001) places the check on the matching integer preset; otherwise the check moves to "직접 지정..." and the label becomes `I18n.FormatCustomScaleLabel(scale)` (e.g., "직접 지정... (2.3배)") so the user always sees the current non-integer value in the menu.

---

## Update check

### Why WinHTTP over HttpClient

`Core/Native/WinHttp.cs` hosts 9 `[LibraryImport("winhttp.dll")]` bindings + `SafeWinHttpHandle : SafeHandleZeroOrMinusOneIsInvalid`. `Core/Http/HttpClientLite.cs` wraps them as a single synchronous `GetString` method.

- **WinHTTP path**: ~40 KB NativeAOT publish impact
- **`System.Net.Http.HttpClient` path**: ~2.5 MB (full `System.Net.Http.dll` + dependency chain + reflection-heavy handler pipeline)

For a tray app that makes one HTTP call per boot, the **60× size delta** is decisive.

### Fire-once-per-boot

`UpdateChecker.CheckInBackground` is called exactly once from `Program.MainImpl` after IME hook registration, gated by `config.UpdateCheckEnabled` (default `true`). No periodic polling, no retry on failure, no rate limiting. Re-check requires an app restart.

GitHub's unauthenticated API rate limit is 60/hour per IP, and users who leave the tray app running for days don't need stale notifications — they need a single check at the time they most recently *launched* the app.

### Silent failure

Network error, HTTP non-200, empty body, unparseable JSON, draft/prerelease skip, or `current >= latest` version compare — all funnel to `Logger.Debug` and nothing else. The user never sees a "couldn't reach GitHub" popup because that would be intrusive for a passive indicator app.

`HttpClientLite.GetString` returns `null` on any failure. `UpdateChecker.RunCheck`'s catch is narrowed to `JsonException or NotSupportedException or ArgumentException` so logic bugs in version comparison propagate; `HttpClientLite.GetString` keeps a wide `catch (Exception)` because WinHTTP marshalling edge cases can't all be enumerated (single P/Invoke-chain try body).

### Version comparison

`UpdateChecker.NormalizeVersion` strips optional `v`/`V` prefix and semver prerelease/build suffixes (`-beta.1`, `+build.42`) via `ReadOnlySpan<char>`, then `System.Version.TryParse` parses the `N.N.N[.N]` portion. `IsNewer(current, latest)` returns `latestV > currentV`.

Semver prerelease ordering (`1.0.0-alpha < 1.0.0`) is intentionally ignored — combined with the `release.Prerelease || release.Draft → skip` filter, prereleases never trigger notifications. This is the right behavior: users on stable releases should not be pinged to upgrade to a beta.

### Thread marshaling

`UpdateChecker.CheckInBackground` spawns a `new Thread { IsBackground = true, Name = "UpdateChecker" }` that calls into `HttpClientLite` (blocking sync I/O). On success, the background thread invokes the caller's `onUpdateFound(UpdateInfo)` callback, which lives in `Program.OnUpdateCheckResult`. That method writes to `Program._pendingUpdate` (a `private static volatile UpdateInfo?` field) and calls `User32.PostMessageW(hwndMain, AppMessages.WM_APP_UPDATE_FOUND, 0, 0)`.

The main thread's WndProc picks up the message and calls `HandleUpdateFound` → `Tray.OnUpdateFound(info)`. Reusing the existing `WM_APP + N` pattern keeps the cross-thread signal path consistent with the detection thread.

### Tray menu injection

`Tray.OnUpdateFound` stores the `UpdateInfo` in a `private static UpdateInfo? _pendingUpdate` field (non-volatile because main thread is the sole accessor after the `WM_APP_UPDATE_FOUND` message crossed the thread boundary).

`Tray.ShowMenu` injects a `MF_STRING` item at the very top of the popup menu (ID `IDM_UPDATE_DOWNLOAD = 4008`, label from `I18n.FormatMenuUpdateAvailable(version)`) followed by a `MF_SEPARATOR`, then falls through to the normal "투명도" submenu. When no update is pending, neither the item nor the separator is appended, so the menu looks exactly as before.

Click handler `OpenUpdatePage` calls `Shell32.ShellExecuteW(0, "open", info.HtmlUrl, null, null, SW_SHOWNORMAL)`. Return ≤ 32 is logged as `Logger.Warning` (per `ShellExecuteW` docs, ≤ 32 means launch failure).

### Why no balloon/toast/tooltip prefix

Three notification surfaces were considered:

1. **Balloon (`NIIF_INFO`)** — rejected as too intrusive for a passive indicator app
2. **Windows 10+ Toast** — requires a registered `AppUserModelID` and a shortcut in the Start menu, which conflicts with the portable single-exe distribution model
3. **Tooltip prefix** ("⚡ Update available — 한글 모드") — rejected as too subtle; clutters the hover hint without being discoverable

The tray menu item is discoverable (user sees it when they right-click to exit or change settings) without being intrusive.

### Config toggle

`AppConfig.UpdateCheckEnabled : bool = true` lives in the `[시스템]` section next to `LogMaxSizeMb`. Not exposed in the tray menu (low-frequency toggle) — users who want to disable it edit `config.json` directly. Adding a row to [SettingsDialog.Fields.cs](../App/UI/Dialogs/SettingsDialog.Fields.cs) is a 3-line addition if needed later.

### End-to-end validation

Against v0.8.9.0 release (2026-04-14), both branches were exercised:

- **"no update"**: `AppVersion = 0.8.9.0` matches release tag, UpdateChecker fires on boot, HTTP 200 + JSON in 47 ms round-trip, `IsNewer` returns false, `Logger.Debug("UpdateChecker: current=0.8.9.0 latest=v0.8.9.0 (no update)")`, tray menu stays unchanged
- **"new version found"**: `AppVersion` temporarily patched to `0.8.8.0`, same run logs `Logger.Info("UpdateChecker: new version available — current=0.8.8.0 latest=v0.8.9.0")`, `_pendingUpdate` populated, `PostMessageW(WM_APP_UPDATE_FOUND)` dispatched, tray menu shows the update item, clicking opens the GitHub release page in the default browser via `ShellExecuteW`

Every link in the chain (`WinHttpSetTimeouts` inheritance, `SafeWinHttpHandle` RAII, JSON source gen, `NormalizeVersion` `v` prefix handling, 4-part `Version.TryParse`, volatile `_pendingUpdate` cross-thread hop, tray menu dynamic injection, `ShellExecuteW` browser launch) confirmed operational.

---

## Misc

### Delegate GC prevention

Static field retention for P/Invoke callbacks (e.g., `_imeChangeCallback` in [ImeStatus.cs](../App/Detector/ImeStatus.cs)). Without the static reference, the GC would collect the delegate mid-flight and the Win32 call would `AccessViolation`.

### COM init ordering

Main thread pre-initializes COM STA + forces `SystemFilter` static constructor before the detection thread starts, so the `IVirtualDesktopManager` COM object is usable from either thread.

### Overlay window class

Separately registered (shared WndProc with main window). `WM_DESTROY` guard checks `hwnd == _hwndMain` so app exit doesn't trigger when the overlay is destroyed.

### F-key hotkey parsing

`ParseHotkey` supports F1–F12 via pattern match → `VK_F1 + (fNum - 1)`. Default hotkey is `Ctrl+Alt+H`.

### DWMWA constants location

`DWMWA_EXTENDED_FRAME_BOUNDS` and `DWMWA_CLOAKED` live in [Core/Native/Win32Types.cs](../Core/Native/Win32Types.cs) under the `Win32Constants` class rather than inside `Core/Native/Dwmapi.cs`. P4 mandates that all Win32 structs and constants are centralized in `Win32Types.cs` regardless of which DLL they belong to.

### `volatile` + `Action<AppConfig>` callback

`_config` is a `volatile` field, and `ref` cannot be used with volatile, so config updates use an `Action<AppConfig>` callback pattern instead of `ref AppConfig`.

### `InvariantGlobalization`

Enabled in [KoEnVue.csproj](../KoEnVue.csproj) — strips ICU from the NativeAOT publish. Means no `CultureInfo` usage except for `CultureInfo.InvariantCulture`. IME language detection uses `GetUserDefaultUILanguage` P/Invoke instead of `CultureInfo.CurrentUICulture`.
