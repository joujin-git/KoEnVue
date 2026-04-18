# Implementation notes

Deep-dive details on render pipeline, drag/snap, animation, detection, hot reload, dialogs, and shutdown. Companion to [CLAUDE.md](../CLAUDE.md) and [KoEnVue_PRD.md](KoEnVue_PRD.md) — this file is where "why" explanations and non-obvious workarounds live.

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

### DIB / DC creation safety

`LayeredOverlayBase` 생성자는 `CreateCompatibleDC` 반환값이 `IntPtr.Zero`이면 `InvalidOperationException`을 던져 null DC로 후속 GDI 작업이 진행되는 것을 방지한다. `EnsureDib`의 `CreateDIBSection` 호출은 `out IntPtr ppvBits` 로컬 변수로 수신한 뒤 성공 시에만 `_ppvBits` 필드를 갱신한다. 실패 시 기존 유효 비트맵과 `_ppvBits`가 보존되어 해제된 메모리를 참조하는 위험을 제거한다.

### EnsureFont resource safety

`LayeredOverlayBase.EnsureFont` 는 `CreateFontW` 호출 결과를 먼저 검사해 `IntPtr.Zero` 이면 `Logger.Warning(family/size/bold)` + 조기 반환한다. 기존 `_currentFont` 와 캐시 키(`_cachedFontFamily/Size/IsBold/DpiScale`) 는 갱신하지 않아 다음 `EnsureFont` 호출에서 동일 파라미터로 재시도가 가능. 이 순서가 중요한 이유는 **먼저** Dispose 한 뒤 Create 하던 이전 흐름이 실패 시 (1) 이전 유효 폰트를 잃고 (2) 빈 HFONT 가 래핑된 `SafeFontHandle` 이 캐시에 고착되어, `_cachedFont*` 필드가 이미 "현재와 동일" 을 가리키므로 이후 호출이 조기 return 하여 영원히 재진입 없이 렌더가 실패하는 상태에 빠지는 회귀를 막기 위함이다. 성공 경로에서만 `_currentFont?.Dispose() → new SafeFontHandle(hFont, true) → 캐시 키 갱신 → GetTextMetricsW` 순서로 진행. 렌더러 측 3개 호출 지점(`Overlay.OnRenderToDib` 등)은 모두 `if (_currentFont is not null)` 가드를 가지고 있어 실패 경로에서 `_currentFont` 가 null 이거나 이전 값이더라도 크래시 없이 한 프레임을 스킵하고 다음 틱에서 자연 재시도한다.

### Label DIB flip-flop prevention

`_fixedLabelWidth` is cached inside `LayeredOverlayBase` after measuring all three labels (`OverlayStyle.MeasureLabels` tuple) and taking the max. This prevents the DIB from churning in width on state transitions (한→En, En→EN, etc.) because all three labels are computed at the same width.

The per-render skip uses `OverlayStyle` `record struct` value equality — `newStyle == _lastStyle` returns `true` when nothing visible has changed. Because `CapsLockOn` is a field inside the record, toggling it automatically breaks equality and forces a re-render.

---

## Indicator positioning

### Draggable floating window

The indicator is a separate TOPMOST window, not tied to any foreground window's geometry. `WM_NCHITTEST → HTCAPTION` enables native drag. `WM_ENTERSIZEMOVE` / `WM_EXITSIZEMOVE` track drag lifecycle.

### Drag modifier (drag initiation gate)

`config.drag_modifier` (`DragModifier` enum: `None` / `Ctrl` / `Alt` / `CtrlAlt`) gates whether a left-click on the indicator starts a drag. The gate is purely reactive — `WM_NCHITTEST` itself reads `GetAsyncKeyState(VK_CONTROL / VK_MENU)` at click time and returns either `HTCAPTION` (drag) or `HTCLIENT` (click consumed by overlay, no-op because there is no `WM_LBUTTONDOWN` handler). No timer, no hook, no cached ex-style.

- **None (default)** — `IsDragModifierPressed(None) → true`, so `WM_NCHITTEST` always returns `HTCAPTION`. Every left-click starts (or no-ops as a 0-px) drag. Matches pre-existing behavior.
- **Ctrl / Alt / CtrlAlt** — `IsDragModifierPressed` checks the exact state (`Ctrl` mode requires `Ctrl ∧ ¬Alt` so `Ctrl+Alt` cannot accidentally fire `Ctrl`). Modifier held → `HTCAPTION` (drag). Modifier released → `HTCLIENT`; the click lands on the overlay but is silently dropped because no client-area mouse handler exists.

**Cross-process click-through is not supported.** The overlay renders a translucent chip background (alpha > 0 over most of its rect), so the per-pixel-alpha auto-transparency that layered windows apply to `alpha == 0` regions does not cover the chip. `HTTRANSPARENT` is also insufficient — per Microsoft's `WM_NCHITTEST` documentation, hit-test forwarding via `HTTRANSPARENT` only reaches windows **in the same thread**, not cross-process targets such as Notepad or a browser. Achieving real click-through would require toggling `WS_EX_TRANSPARENT` dynamically based on modifier state, which in turn demands either a 30 Hz `WM_TIMER` poller (steady-state wakeups) or a `WH_KEYBOARD_LL` hook (NativeAOT callback risk, 300 ms per-event OS timeout that silently disables hooks on breach). The cost/complexity was judged not worth the payoff, so the feature is scoped to drag-initiation gating only.

Why just `GetAsyncKeyState` at hit-test time: the check runs only when the OS delivers `WM_NCHITTEST` to the overlay (typically once per click), costs microseconds, has zero steady-state overhead, and cannot get out of sync with the user's real key state. This is the minimum possible implementation for a drag gate — idle cost is literally zero.

Key properties:

- Hot reload of `drag_modifier` costs nothing extra — the next `WM_NCHITTEST` reads the current `_config.DragModifier` and is already accurate. `HandleConfigChanged` and the tray-menu `updateConfig` callback touch no additional state.
- Once a drag begins, Windows enters a modal `WM_ENTERSIZEMOVE` loop with mouse capture. Releasing the modifier mid-drag does not abort the drag — `SetCapture` persists until mouse-up / `WM_EXITSIZEMOVE`.
- `Shift` is reserved for axis-lock during an active drag (see [`LayeredOverlayBase.HandleMoving`](../Core/Windowing/LayeredOverlayBase.cs)) and is not offered as a drag-gate choice.
- Clicks outside the chip's alpha-nonzero pixels (e.g., the sparse corners of a rounded rectangle if the caller configures no padding) are still skipped by the OS due to per-pixel alpha — this is `WS_EX_LAYERED` behavior, unrelated to `drag_modifier`.

UI exposure: tray menu "드래그 활성 키" radio submenu (4 items) and settings dialog combo in the "다중 모니터" section.

### Position modes

`config.position_mode` (`PositionMode` enum: `Fixed` / `Window`) selects how the indicator is placed:

- **Fixed** (default) — screen-absolute coordinates. Existing two-tier memory (runtime hwnd + config process-name) is used
- **Window** — relative to the foreground window's DWM visible frame. Only config-level process-name storage is used (no runtime hwnd cache), because coordinates are re-resolved from `window rect + offset` every time

Mode selection is available in the tray menu as a radio submenu ("위치 모드 ▸ 고정 위치 / 창 기준") with `CheckMenuRadioItem`. System input processes (Start Menu, Search) always use the existing fixed-mode logic regardless of the selected mode.

### Two-tier position memory (Fixed mode)

1. **Runtime (`Dictionary<IntPtr, (int, int)>`)** — per-hwnd positions, enables distinguishing multiple windows of the same process (e.g., multiple Notepad/Chrome windows). Lost on restart
2. **Config (`indicator_positions`)** — per-process-name positions, persists across sessions as fallback

Process names are resolved via `WindowProcessInfo.GetProcessName(IntPtr hwnd)`. UWP apps (Settings, Microsoft Store, Calculator, etc.) are hosted by `ApplicationFrameHost.exe` — `GetWindowThreadProcessId` returns the frame host PID, not the actual app. `WindowProcessInfo` detects this and enumerates child windows via `EnumChildWindows` to find a child with a different PID, returning that child's process name (e.g., `"SystemSettings"`, `"WinStore.App"`). This ensures each UWP app gets its own position entry instead of all sharing `"ApplicationFrameHost"`.

On foreground change, lookup order is: runtime hwnd → config process name → default position.

### Window-relative position memory (Window mode)

`config.indicator_positions_relative` stores per-process-name entries as `int[3]`: `[(int)Corner, DeltaX, DeltaY]`. On foreground change, `GetAppPositionWindow` decodes the array, validates `Corner` via `Enum.IsDefined`, obtains the current window's DWM frame via `Dwmapi.TryGetVisibleFrame`, and resolves absolute coordinates with `Overlay.ResolveRelativePosition(frame, relConfig)`. Result is clamped to the visible area.

This design naturally handles the "same app, multiple windows" case: a single process-name entry (e.g., `"notepad": [1, -50, 10]`) produces different absolute coordinates for each window because each window has a different rect on screen. No runtime per-hwnd cache is needed.

On drag end, `Overlay.ComputeRelativeFromCurrentPosition(hwndForeground)` computes the nearest of the 4 DWM frame corners by Manhattan distance and stores the delta as the new relative offset.

### Window movement tracking (Window mode)

In Window mode, the detection loop (80 ms) tracks `lastWindowFrame` and a `windowMoving` flag for the foreground window. When the DWM frame changes (window being moved/resized), the indicator is hidden (`WM_HIDE_INDICATOR`). When the frame stabilizes (no change for 1 tick ≈ 80 ms), `foregroundChanged` is set to `true`, triggering `WM_POSITION_UPDATED` → position re-resolve → indicator re-shown at the new window-relative position.

The `lastWindowFrame` and `windowMoving` state are reset on foreground window change. System input processes are excluded from this tracking (they have their own shared-HWND rect tracking block).

### Default position

Two nullable config fields store per-mode defaults for apps without a saved position:

- **Fixed mode**: `config.default_indicator_position` (`DefaultPositionConfig` record) — `Corner` + `DeltaX` + `DeltaY` resolved against the **foreground window's monitor work area**
- **Window mode**: `config.default_indicator_position_relative` (`RelativePositionConfig` record) — `Corner` + `DeltaX` + `DeltaY` resolved against the **foreground window's DWM frame**

Null fallbacks:
- Fixed: `DefaultConfig.DefaultIndicatorOffsetX = -200, Y = 10` (top-right of work area)
- Window: `DefaultConfig.DefaultRelativeCorner = TopRight, X = -50, Y = 10` (inside top-right of window)

Multi-monitor / resolution stability: offsets are stored relative to a `Corner` anchor, not as absolute pixel coordinates.

Tray menu:
- **"현재 위치로 설정"**: branches on current mode — Fixed calls `Overlay.ComputeAnchorFromCurrentPosition()` (work area corners), Window calls `Overlay.ComputeRelativeFromCurrentPosition(hwndForeground)` (window frame corners). Both use Manhattan distance to pick the nearest corner
- **"초기화"**: resets the current mode's field to null (menu item grayed when already null)

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

### EnumWindows / EnumChildWindows NativeAOT callbacks

Uses `delegate* unmanaged<IntPtr, IntPtr, int>` + `[UnmanagedCallersOnly]` instead of delegate marshaling — consistent with the rest of the project's `[LibraryImport]` style. Return type is `int` (not `bool`) because Win32 `BOOL` is 4 bytes.

`EnumWindows` is used in `LayeredOverlayBase.BeginDrag` for snap candidate collection. `EnumChildWindows` is used in `WindowProcessInfo.ResolveUwpProcessName` to find the actual UWP app process inside an `ApplicationFrameHost` window. The latter uses `[ThreadStatic]` bridge fields (not static fields) because `GetProcessName` is called from both the main thread and the detection thread.

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

System filter and tray toggle off both pass `forceHidden: true` to `Animation.TriggerHide` so Always mode collapses fully instead of sliding into dim-idle. "Hide" from these sources means "actually disappear", distinct from Always-mode idle dimming.

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

### Detection loop resilience

`DetectionLoop`의 while 본문은 `try-catch(Exception)`으로 래핑되어 단일 폴링 예외(예: `WindowProcessInfo.GetProcessName` 실패)가 감지 스레드를 종료시키지 않는다. 예외 발생 시 `Logger.Warning`으로 기록하고 다음 폴링 주기에서 정상 재개한다. `Thread.Sleep`은 try 밖에 위치하여 예외 후에도 폴링 간격이 유지된다. `_stopping` 필드는 `volatile`로 선언되어 `OnProcessExit`에서의 쓰기가 감지 스레드에서 즉시 가시적이다.

### Foreground change detection

`foregroundChanged` flag triggers focus event independently of `hwndFocus` comparison, fixing the return-to-same-window case after a desktop switch.

### Console host fallback

`hwndFocus == 0` + `ConsoleWindowClass` check → use foreground window as focus target. Console apps don't report focus to AccessibleObjects, so we fall back.

### Position update ordering

Detection loop sends `WM_POSITION_UPDATED` **before** `WM_IME_STATE_CHANGED` / `WM_FOCUS_CHANGED` to ensure `_lastForegroundHwnd` is current when those handlers run.

### Per-poll filter evaluation

`DetectionLoop` evaluates `ResolveForApp + SystemFilter.ShouldHide` every tick (not only on foreground change) and uses a `lastFiltered` flag to suppress duplicate `WM_HIDE_INDICATOR` messages. Fixes the "desktop click → same app return" case where nothing appeared to change but the indicator needed to reappear. Hide message is emitted only on `!lastFiltered → filtered` transitions.

### Modal dialog gate

`DetectionLoop` short-circuits when `ModalDialogLoop.IsActive` **and** the foreground window belongs to our own process: `GetWindowThreadProcessId(hwndForeground, out fgPid); if (ModalDialogLoop.IsActive && fgPid == (uint)Environment.ProcessId) { hide + lastFiltered=true + continue; }`. The three dialogs (`CleanupDialog`, `ScaleInputDialog`, `SettingsDialog`) and `MessageBoxW` are separate top-level windows with distinct HWNDs, so the `_hwndMain`/`_hwndOverlay` self-skip doesn't cover them — without the gate, the detection thread would resolve the dialog HWND as a regular foreground app and emit `WM_POSITION_UPDATED`, making the indicator jump next to the dialog (Window mode) and causing `TriggerShow` renders that interfered with the dialog's focus (delayed ESC dismissal until after the first render settled). The gate unifies OK/Cancel/Esc exit behavior: indicator hides on self-process modal entry, and `lastFiltered=true` forces `foregroundChanged=true` on the first post-modal tick so the original foreground app naturally re-triggers the show. Applies uniformly across `PositionMode` (Fixed/Window) and `DragModifier` (None/Ctrl/Alt/CtrlAlt) combinations.

**Process-ID scoping**: the gate is restricted to *our* process's windows, not "any foreground while a modal is open". If the user Alt+Tabs to another app while a dialog is up (Win32 dialogs are modal to the owner only, not system-wide), the foreground switches to an external process — the gate falls through and the indicator renders on that app as usual. `ModalDialogLoop.ActiveDialog` HWND comparison alone would miss `MessageBoxW` (its HWND is owned by `user32` and unknown to us), so PID comparison is the only robust way to cover custom dialogs + `MessageBoxW` while still allowing external-app rendering. `Environment.ProcessId` is a .NET BCL property — no P/Invoke needed. `GetWindowThreadProcessId` is hoisted above the gate so the following `GUITHREADINFO` path reuses the same `threadId` — one syscall per tick.

**External modals (`MessageBoxW`)**: `Tray` 의 두 경고 대화상자("이미 저장된 위치입니다", "저장된 위치 기록이 없습니다")는 `User32.MessageBoxW` 가 자체 메시지 루프를 돌리므로 `ModalDialogLoop.Run` 을 쓸 수 없다. 대신 `ModalDialogLoop.RunExternal(hwndSentinel, action)` 로 호출 구간만 감싸 `IsActive` 센티넬을 세팅/복원한다. `RunExternal` 은 메시지 펌프나 `EnableWindow` 은 건드리지 않고 감지 스레드 가드만 세우므로, `MessageBoxW` 가 활성인 동안 같은 프로세스 PID 이므로 위 게이트가 발동해 인디케이터가 해당 다이얼로그 근처로 튀는 폴링 부작용이 억제된다. 기존 활성 모달이 있으면 이전 값을 보관 후 finally 에서 복원하여 중첩을 지원한다.

### `wasHidden` re-trigger

`HandlePositionUpdated` treats `!_indicatorVisible` as a signal to re-resolve position and show, even when `hwndForeground == _lastForegroundHwnd`. Complements per-poll filter re-eval: detection thread posts `WM_POSITION_UPDATED` after filter clears, main thread sees the same hwnd but knows the indicator needs to come back.

### Deferred `lastHwndForeground`

Detection loop only updates `lastHwndForeground` **after** `ShouldHide` passes. If filtered (transient condition), the next poll retries the foreground change.

### IME state detection

- `WM_IME_CONTROL` + `IMC_GETOPENSTATUS` / `IMC_GETCONVERSIONMODE`
- `GetKeyboardLayout` LANGID check (for non-Korean IME identification)
- `EVENT_OBJECT_IME_CHANGE` WinEvent hook as supplementary signal

#### Tier 1 pass-through on `openResult = 0`

`ImeStatus.TryTier1` 의 `IMC_GETOPENSTATUS` 결과가 `0` (IME 비활성) 일 때 `ImeState.English` 로 단정하지 않고 `null` 을 돌려 Tier 2 → Tier 3 체인으로 위임한다. 한국어 IME 환경에서는 "IME 비활성 = 영문 입력" 이 맞지만, 비-한국어 로케일(일본어/중국어) 에서도 동일한 `openResult = 0` 이 나오므로 Tier 1 에서 `English` 로 확정하면 Tier 3 의 `GetKeyboardLayout` → langId 기반 `NonKorean` 판별 기회를 완전히 잃는다. 대부분의 비-한국어 IME 연관 창은 `ImmGetContext = 0` 이라 Tier 2 도 null 로 패스-스루되어 Tier 3 가 `langId != LANGID_KOREAN` → `NonKorean` 을 반환한다. 한국어 사용자 경로는 Tier 2 의 `ImmGetConversionStatus` 가 `IME_CMODE_HANGUL = 0` 을 돌려 `English` 를 반환하거나, 연관 컨텍스트가 없는 창에서는 Tier 3 가 `LANGID_KOREAN` → `English` 를 반환해 최종 결과는 기존과 동일. explicit `DetectionMethod.ImeDefault` 경로는 `TryTier1(hwndFocus) ?? ImeState.English` 폴백으로 감싸져 있어 변경 영향 없음.

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
6. **`ApplyTheme`** — theme preset overlay (if `theme != custom`). 프리셋 적용 시 기존 커스텀 색상을 `custom_backup_*` 필드에 백업하고, `custom` 복귀 시 복원 후 백업 소멸. `updateConfig` 콜백에서도 즉시 실행되어 상세 설정 변경이 앱 재시작 없이 반영됨

### Delete-safe hot reload

`Settings.CheckConfigFileChange` returns early via `File.Exists(_configFilePath)` **before** calling `GetLastWriteTimeUtc`. For a missing file, `File.GetLastWriteTimeUtc` returns the sentinel `1601-01-01` without throwing, which differs from the cached mtime and would trigger a spurious `WM_CONFIG_CHANGED` → `Load()` → silent reset to defaults → next `Save()` overwrites the user's real config when it reappears.

Locking the file to forbid deletion was rejected because atomic-replace editors (VSCode, Notepad++) rely on `delete → rename` during save.

### Atomic save (tmp + rename)

`JsonSettingsFile.WriteAllText` 는 단순 `File.WriteAllText(path, json)` 대신 `path + ".tmp"` 에 전체를 먼저 기록한 뒤 `File.Move(tmpPath, path, overwrite: true)` 로 교체한다. Windows 동일 볼륨에서 `MoveFileExW(MOVEFILE_REPLACE_EXISTING)` 는 원자적 rename 을 보장하므로 쓰기 도중 전원 차단/프로세스 강제 종료/크래시가 발생해도 원본 파일 또는 새 파일 중 하나는 항상 온전한 상태로 남는다 (truncate 된 반쪽 파일 불가능). `CheckConfigFileChange` 의 5 초 mtime 폴링은 타겟 경로 한 곳만 관찰하므로 `.tmp` 파일이 핫 리로드를 유발하지 않으며, 프로세스가 `.tmp` 쓰기 직후·Move 직전에 죽어 잔여물이 남더라도 다음 정상 저장에서 같은 이름에 덮어쓰기 때문에 누적되지 않는다. 원자성은 **동일 볼륨** 에 한정된 보장 — config 파일이 exe 옆에 고정되어 있으므로(§ Config file location) 볼륨을 건너뛸 수 없다.

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

### NIM_ADD / NIM_SETVERSION return value check

`NotifyIconManager.Add`는 `Shell_NotifyIconW(NIM_ADD)` 반환값을 확인하여 실패 시 `_added = false`를 유지하고 즉시 반환한다. `NIM_ADD` 성공 후에만 `NIM_SETVERSION`을 호출하며, `NIM_SETVERSION` 실패는 `Logger.Warning`으로 기록하되 `_added = true`는 유지한다 (아이콘 자체는 등록된 상태이므로). 이 가드 덕분에 이후 `Modify` 호출이 등록되지 않은 아이콘에 대해 무한 실패하는 상황을 방지한다.

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
기본 위치 ▸       현재 위치로 설정 / 초기화
위치 모드 ▸       ○ 고정 위치 / ● 창 기준
드래그 활성 키 ▸  ● 없음 / ○ Ctrl / ○ Alt / ○ Ctrl + Alt
위치 기록 정리...
───
상세 설정...
───
종료
```

Menu IDs live in [Tray.cs](../App/UI/Tray.cs) as `private const int IDM_*`. The `IDM_UPDATE_DOWNLOAD = 4008` item + separator are only appended when `_pendingUpdate != null`. Position mode submenu uses `IDM_POSITION_FIXED = 3301` / `IDM_POSITION_WINDOW = 3302` with `CheckMenuRadioItem`.

### Quick opacity presets (`ApplyQuickOpacity`)

The three opacity presets (진하게/보통/연하게) apply mode-aware config changes via `Tray.ApplyQuickOpacity`. In Always mode, the preset value is written to `ActiveOpacity` and `IdleOpacity` is proportionally scaled (ratio preserved). In OnEvent mode, only `Opacity` is written. The radio check compares against `ActiveOpacity` in Always mode, `Opacity` in OnEvent mode.

### Three-toggle duplication with settings dialog

`SnapToWindows`, `AnimationEnabled`, and `ChangeHighlight` are toggleable from both the tray menu and `SettingsDialog`. The settings dialog drops these three rows to avoid duplication. `SlideAnimation` is deliberately **not** added to the tray because usage frequency is low and keeping the menu short is a UX goal.

The duplication is kept as vertical copy rather than extracted to a helper because `HandleMenuCommand`'s per-field `with`-expression getters/setters can't be mechanically abstracted without a delegate map or reflection (conflicts with NativeAOT + P1).

---

## Dialogs

All three dialogs (`CleanupDialog`, `ScaleInputDialog`, `SettingsDialog`) share the same modal infrastructure:

- **`ModalDialogLoop.Run(hwndDialog, hwndOwner, ref isClosed)`** — Core helper for the `EnableWindow(owner, false) + while(GetMessageW... IsDialogMessageW(...)) { Translate/Dispatch } + EnableWindow(owner, true)` boilerplate. The `ref bool isClosedFlag` lets each dialog's WndProc signal close from inside `WM_COMMAND`/`WM_CLOSE` without the loop helper knowing the close semantics. When the nested loop consumes `WM_QUIT` (e.g., tray Exit while a dialog is open), it re-posts `PostQuitMessage` so the outer message loop also terminates
- **`ModalDialogLoop.RunExternal(hwndSentinel, action)`** — `IsActive` 가드만 씌우는 경량 변형. `User32.MessageBoxW` 처럼 Win32 가 자체 메시지 루프를 돌려 `Run` 을 쓸 수 없는 외부 모달 구간에 사용한다 (현재 `Tray.ShowPositionError` / `Tray.CleanupPositions` 의 빈 목록 알림). 메시지 펌프 · `EnableWindow` 는 건드리지 않고 감지 스레드의 폴링 사이드-이펙트만 차단한다. 기존 활성 모달이 있으면 스택처럼 이전 값을 보관 후 `finally` 에서 복원
- **`Win32DialogHelper.CreateDialogFont(dpiY) → SafeFontHandle`** — 9 pt 맑은 고딕 with `SafeFontHandle` RAII
- **`Win32DialogHelper.CalculateDialogPosition(hMonitor, w, h, anchor?)`** — `null` anchor = center in work area (Cleanup/Settings pattern); `POINT` anchor = top-left at that point (ScaleInput cursor-anchored pattern). Both paths apply work-area clamping
- **`using var hFont = ...`** declared at the top of each dialog's `Show` method frame before `CreateWindowExW`. The `using` scope covers the full modal loop + `DestroyWindow` so the HFONT cannot be freed while child controls still reference it
- **`[UnmanagedCallersOnly]` WndProc function pointers** private to each file (no NativeAOT export name collision)
- **Tab/Enter/ESC** routed through `IsDialogMessageW`
- **Detection-thread gate**: `DetectionLoop` checks `ModalDialogLoop.IsActive` **and** `GetWindowThreadProcessId(hwndForeground) == Environment.ProcessId` together — suppresses polling side effects only when a modal is up **and** the foreground belongs to our own process. External-app focus while a dialog is open (Alt+Tab) falls through so the indicator renders on that app. See [Detection → Modal dialog gate](#modal-dialog-gate)

### CleanupDialog

Position-mode-agnostic: regardless of the current `position_mode` setting, shows the union of `indicator_positions` (Fixed) and `indicator_positions_relative` (Window) keys. Deletion removes from both dicts simultaneously, so switching modes later won't resurrect deleted entries. Running processes are shown with a "(실행 중)" / "(running)" suffix. Full select/deselect toggle. "저장된 위치 기록이 없습니다" message when empty. When items exceed `DlgMaxVisibleItems` (15), a scrollable viewport child window with `WS_VSCROLL` + mouse wheel support is used — same pattern as `SettingsDialog.Scroll.cs`.

### ScaleInputDialog

Custom scale entry for values outside the 1.0–5.0 integer presets. Spawned at cursor position via `CalculateDialogPosition(POINT anchor)`. EDIT pre-filled via `initialValue.ToString("0.#")` (`"2"` for 2.0, `"2.3"` for 2.3).

Parsing uses `double.TryParse` + `CultureInfo.InvariantCulture`, so `"2.3"` works regardless of OS locale. Validation failure shows a MessageBox then refocuses EDIT with `EM_SETSEL(0, -1)` (select all) for easy re-entry.

`ScaleDlgProc` accepts both `IDC_SCALE_OK || IDOK` and `IDC_SCALE_CANCEL || IDCANCEL` because `IsDialogMessageW` synthesizes `IDOK`/`IDCANCEL` for Enter/ESC.

### SettingsDialog

13 sections of settings (정확한 필드 수는 [SettingsDialog.Fields.cs](../App/UI/Dialogs/SettingsDialog.Fields.cs) 의 `BuildRowDefs` 참조). Split across 3 partial class files:

- **`SettingsDialog.cs`** (modal state, `Show`, `TryCommit`, dialog WndProc)
- **`SettingsDialog.Fields.cs`** (`FieldType` enum, `FieldDef`/`RowDef` records, `BuildRowDefs` 13-section spec, 6 factory methods: `Bool`/`Int`/`Dbl`/`Str`/`ColorField`/`Combo`)
- **`SettingsDialog.Scroll.cs`** (scroll state, `SetupScrollbar`, `ScrollTo`, `ScrollFieldIntoView`, `ResolveVScrollPosition`, viewport WndProc)

`partial class` shares all static state at compile time. No call-site changes — `SettingsDialog.Show(hwndMain, config, updateConfig)` is the same public entry point.

**Scroll implementation**: `ScrollTo` 는 스크롤 델타 `dy = _scrollPos - newPos` 를 계산한 뒤 `SetScrollInfo` 로 썸 위치를 갱신하고, `ScrollWindowEx(viewport, 0, dy, ..., SW_SCROLLCHILDREN | SW_INVALIDATE | SW_ERASE)` 한 번으로 모든 자식을 OS 가 BitBlt 로 이동시킨다. 노출된 띠 영역만 무효화 + 배경 지움 처리되므로, 기존 "N 개 자식에 대한 `SetWindowPos` 루프 + 전체 `InvalidateRect(viewport, null, true)`" 방식 대비 휠 틱당 작업량이 O(N) → O(1) 로 줄어 휠 스크롤 반응성이 크게 향상된다. 뷰포트는 `WS_CLIPCHILDREN` + `WS_EX_COMPOSITED` 조합으로 DWM off-screen 합성을 사용해 스크롤 중 플리커도 없다. 자식 윈도우 크기는 `ScrollWindowEx` 가 보존하므로 COMBOBOX 의 `rowH + ComboDropExtra = 220` 드롭다운 높이는 영향 없음.

`ScrollTo` / `ResolveVScrollPosition` / `CalculateWheelScrollPos` 3조합은 `CleanupDialog` 와 동일해 [Core/Windowing/ScrollableDialogHelper](../Core/Windowing/ScrollableDialogHelper.cs) 로 추출했다. 호출부는 expression-bodied 1-라이너로 축약되고 `WheelLineStep = 3` 상수도 헬퍼에서 소유 (P4 공통모듈 규칙).

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

## Multi-instance and tray recovery

### Mutex ordering — why acquire before cleanup

`Program.MainImpl` 초기화 순서에서 `TryAcquireMutex` 가 `CleanupPreviousTrayIcon` 보다 **먼저** 실행되어야 한다. 두 함수 모두 `DefaultConfig.AppGuid` 와 연관된 상태(Mutex 이름, 트레이 아이콘 GUID)에 작용하므로 순서가 역전되면 두 번째 인스턴스가 이미 실행 중인 정상 인스턴스의 트레이 아이콘을 `NIM_DELETE` 로 지워버린 뒤 Mutex 실패로 종료하는 부작용이 발생한다.

Mutex 획득 성공은 "이전 인스턴스가 존재하지 않는다" 는 보장이므로(크래시 시 OS 가 Mutex 를 자동 해제), 이 조건 하에서만 Cleanup 이 안전하게 "이전 크래시의 유령 아이콘을 정리" 한다는 의미를 가진다.

### Second-instance activation signal

`TryAcquireMutex` 실패 시 `NotifyExistingInstance` 가 호출된다. 메인 윈도우 클래스명(`"KoEnVueMain"`)으로 `User32.FindWindowW` 호출 → 기존 인스턴스의 HWND 를 얻고 `PostMessageW(hwnd, AppMessages.WM_APP_ACTIVATE, 0, 0)`. 두 번째 인스턴스는 즉시 종료한다.

기존 인스턴스의 WndProc 는 `WM_APP_ACTIVATE` (`WM_APP + 7`) 를 수신해 `HandleActivateRequest` 로 분기한다. 여기서 현재 포그라운드 앱 기준 좌표로 `Animation.TriggerShow` 를 호출해 인디케이터를 즉시 표시 — `DisplayMode` 와 `EventTriggers` 설정을 **무시**하고 강제 표시하는 이유는 사용자의 명시적인 재실행 행위에 대한 응답이기 때문.

메시지 전용 윈도우(HWND_MESSAGE parent) 가 아니라 일반 최상위 윈도우(데스크톱 parent + 화면 미표시)로 생성되므로 `FindWindowW` 가 정상 매칭한다. 탐색 실패(기존 창이 막 파괴 중이거나 클래스명이 달라진 경우)는 조용히 무시된다.

### TaskbarCreated — shell restart recovery

셸(`explorer.exe`) 재시작 시 이전에 등록된 모든 트레이 아이콘 정보는 소실된다. Windows 는 이를 보완하기 위해 `"TaskbarCreated"` 라는 이름의 **등록된 윈도우 메시지**를 모든 최상위 창에 브로드캐스트한다. 셸 업데이트, 크래시, 수동 재시작(`taskkill /im explorer.exe` 등) 시나리오에서 모두 발생.

`Program.MainImpl` 은 메인 윈도우 생성 직후 `User32.RegisterWindowMessageW("TaskbarCreated")` 로 메시지 ID 를 받아 `_taskbarCreatedMsgId` 필드에 저장한다. 동적 ID 이므로 WndProc 의 `switch` 에 넣을 수 없어 switch 앞단의 if 분기로 비교한다:

```csharp
if (msg != 0 && msg == _taskbarCreatedMsgId && hwnd == _hwndMain)
{
    HandleTaskbarCreated();
    return IntPtr.Zero;
}
```

`hwnd == _hwndMain` 체크는 오버레이 창도 최상위라 같은 브로드캐스트를 받는 문제를 피하기 위함 — 메인 창에서만 한 번 처리한다.

`HandleTaskbarCreated` 는 `config.TrayEnabled` 확인 후 `Tray.Recreate(_lastImeState, _config)` 를 호출한다. `Recreate` 는 `Remove` (내부 상태 초기화, `NIM_DELETE` 는 셸 측 등록이 없으므로 실패해도 무해) → `Initialize` (`NotifyIconManager` 재생성 + `NIM_ADD` + `NIM_SETVERSION`) 순서로 아이콘을 복구한다.

`RegisterWindowMessageW` 등록 실패(매우 드묾) 시에는 `Logger.Warning` 만 남기고 복구 기능만 비활성화된다 — 앱 자체 동작엔 영향 없음.

---

## Misc

### Delegate GC prevention

Static field retention for P/Invoke callbacks (e.g., `_imeChangeCallback` in [ImeStatus.cs](../App/Detector/ImeStatus.cs)). Without the static reference, the GC would collect the delegate mid-flight and the Win32 call would `AccessViolation`.

### COM init ordering

Main thread pre-initializes COM STA + forces `SystemFilter` static constructor before the detection thread starts, so the `IVirtualDesktopManager` COM object is usable from either thread.

### Overlay window class

Separately registered (shared WndProc with main window). `WM_DESTROY` guard checks `hwnd == _hwndMain` so app exit doesn't trigger when the overlay is destroyed.

### DWMWA constants location

`DWMWA_EXTENDED_FRAME_BOUNDS` and `DWMWA_CLOAKED` live in [Core/Native/Win32Types.cs](../Core/Native/Win32Types.cs) under the `Win32Constants` class rather than inside `Core/Native/Dwmapi.cs`. P4 mandates that all Win32 structs and constants are centralized in `Win32Types.cs` regardless of which DLL they belong to.

### `volatile` + `Action<AppConfig>` callback

`_config` is a `volatile` field, and `ref` cannot be used with volatile, so config updates use an `Action<AppConfig>` callback pattern instead of `ref AppConfig`.

### `OnProcessExit` cleanup sequence

`Program.Bootstrap.OnProcessExit`는 다음 순서로 리소스를 정리한다:

1. `_stopping = true` — 감지 스레드 종료 신호 (volatile)
2. IME 훅 해제 (`ImeStatus.UnregisterHook`)
3. CAPS LOCK 폴링 타이머 명시적 해제 (`KillTimer`)
4. 애니메이션 + 렌더링 리소스 해제 (윈도우 파괴 전)
5. 오버레이 + 메인 윈도우 명시적 파괴 (`DestroyWindow`)
6. 트레이 아이콘 제거 (`NIM_DELETE`)
7. Mutex 해제 (`Dispose` only — `ReleaseMutex`는 소유 스레드에서만 가능하나 `ProcessExit`는 다른 스레드일 수 있음)
8. COM 해제 (`CoUninitialize`)
9. 종료 로그 기록 + 로거 종료 (`Logger.Info` → `Logger.Shutdown`)

`Logger.Shutdown`은 반드시 마지막에 호출하여 이전 단계의 로그가 모두 기록되도록 보장한다. 타이머 해제와 윈도우 파괴는 리소스 해제(5단계) 이후에 수행하여 타이머 콜백이 해제된 리소스를 참조하는 것을 방지한다.

### `InvariantGlobalization`

Enabled in [KoEnVue.csproj](../KoEnVue.csproj) — strips ICU from the NativeAOT publish. Means no `CultureInfo` usage except for `CultureInfo.InvariantCulture`. IME language detection uses `GetUserDefaultUILanguage` P/Invoke instead of `CultureInfo.CurrentUICulture`.
