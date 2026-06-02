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

All three constants (`CapsLockBarWidthLogicalPx`, `CapsLockBarInsetLogicalPx`, `CapsLockRightCompensationPx`) live as `private const` in [Overlay.cs](../App/UI/Overlay.cs) next to `SystemInputGapLogicalPx`. The bars are drawn via `FillRect` with `fg` color inside the existing `hBrush` try/finally block.

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

Negative `biHeight` in the BITMAPINFO so `(0, 0)` is top-left. Keeps the pixel arithmetic in the post-processing loop consistent with GDI's top-left origin. PR-18 부터 DIB section 생성 + memory DC `SelectObject` 호출은 [Core/Windowing/DibSectionFactory.TryCreate](../Core/Windowing/DibSectionFactory.cs) 단일 helper 로 위임 — overlay / cursor 두 엔진이 같은 형식 (top-down 32bpp BI_RGB) 의 DIB 를 만들던 ~25 LOC × 2 = 50 LOC 중복 차단. `UpdateLayeredWindow` 호출 + `BLENDFUNCTION`/`POINT`/`SIZE` 인라인 구성도 동일하게 [Core/Windowing/LayeredWindowBlit.Blit](../Core/Windowing/LayeredWindowBlit.cs) 로 위임 (overlay 2 호출 site + cursor 1 호출 site).

### DIB / DC creation safety

`LayeredOverlayBase` 생성자는 `CreateCompatibleDC` 반환값이 `IntPtr.Zero`이면 `InvalidOperationException`을 던져 null DC로 후속 GDI 작업이 진행되는 것을 방지한다. `EnsureDib`의 `CreateDIBSection` 호출은 `out IntPtr ppvBits` 로컬 변수로 수신한 뒤 성공 시에만 `_ppvBits` 필드를 갱신한다. 실패 시 기존 유효 비트맵과 `_ppvBits`가 보존되어 해제된 메모리를 참조하는 위험을 제거한다.

### EnsureFont resource safety

`LayeredOverlayBase.EnsureFont` 는 `CreateFontW` 호출 결과를 먼저 검사해 `IntPtr.Zero` 이면 `Logger.Warning(family/size/bold)` + 조기 반환한다. 기존 `_currentFont` 와 캐시 키(`_cachedFontFamily/Size/IsBold/DpiScale`) 는 갱신하지 않아 다음 `EnsureFont` 호출에서 동일 파라미터로 재시도가 가능. 이 순서가 중요한 이유는 **먼저** Dispose 한 뒤 Create 하던 이전 흐름이 실패 시 (1) 이전 유효 폰트를 잃고 (2) 빈 HFONT 가 래핑된 `SafeFontHandle` 이 캐시에 고착되어, `_cachedFont*` 필드가 이미 "현재와 동일" 을 가리키므로 이후 호출이 조기 return 하여 영원히 재진입 없이 렌더가 실패하는 상태에 빠지는 회귀를 막기 위함이다. 성공 경로에서만 `_currentFont?.Dispose() → new SafeFontHandle(hFont, true) → 캐시 키 갱신 → GetTextMetricsW` 순서로 진행. 렌더러 측 3개 호출 지점(`Overlay.OnRenderToDib` 등)은 모두 `if (_currentFont is not null)` 가드를 가지고 있어 실패 경로에서 `_currentFont` 가 null 이거나 이전 값이더라도 크래시 없이 한 프레임을 스킵하고 다음 틱에서 자연 재시도한다.

### Label DIB flip-flop prevention

`_fixedLabelWidth` is cached inside `LayeredOverlayBase` after measuring all three labels (`OverlayStyle.MeasureLabels` tuple) and taking the max. This prevents the DIB from churning in width on state transitions (한→En, En→EN, etc.) because all three labels are computed at the same width.

The per-render skip uses `OverlayStyle` `record struct` value equality — `newStyle == _lastStyle` returns `true` when nothing visible has changed. Because `CapsLockOn` is a field inside the record, toggling it automatically breaks equality and forces a re-render.

`CalculateFixedLabelWidth` also skips its own work via a 7-key measurement cache (`MeasureLabels` tuple + `PaddingXLogicalPx` + `LabelWidthLogicalPx` + `_currentDpiScale` + `_cachedFont{Family,Size,IsBold}`) + `_fixedLabelWidth > 0` guard. Cache hit elides three `GetTextExtentPoint32W` GDI calls, the `Max` reduction, and the downstream `EnsureDib` call — `EnsureResources` has already sized the DIB to `_fixedLabelWidth` before `CalculateFixedLabelWidth` runs, so no additional resize is needed. Invalidation is a single `_cachedLabelDpiScale = 0` write inside `HandleDpiChanged` (forces a DPI-match miss on next call); font-signature mismatch is caught automatically because `EnsureFont` updates `_cachedFont*` before `CalculateFixedLabelWidth` compares them.

---

## Cursor indicator rendering

> 본 섹션은 PR-B (커서 추종 인디케이터) 의 렌더 + 정지 검출 + 파사드 통합 파이프라인. 메인 인디와는 별도 엔진 (`LayeredCursorBase`) + 별도 HWND (`_hwndCursorOverlay`) 로 처리된다. PR-B 완료 — 사용자가 트레이 메뉴 "커서 인디케이터" 체크박스로 활성화 가능.

### 별도 엔진 사용 (P4 예외)

`Core/Windowing/LayeredCursorBase` 는 `LayeredOverlayBase` 와 책임이 겹치는 ~120 LOC (DIB 생성 / premultiply / `UpdateLayeredWindow`) 를 의도적으로 중복 보유한다. P4 ("하나의 구현만") 예외 정당화: 메인 인디 알파 race 미해결 영역에 변경면을 추가하지 않기 위한 의도적 분리. 메인 엔진은 폰트 / 드래그 / 라벨 측정 / `WindowSnapHelper` 책임이 있어 콜백 시그니처가 `Func<IntPtr hdc, OverlayStyle, OverlayMetrics, (int w, int h)>` (hdc 전달, DIB ppvBits 는 내부에서 `GetCurrentObject` + `GetObjectDibSection` 으로 재추출) 인 반면, cursor 엔진은 GDI 그리기 사용 없이 픽셀 셰이딩만 수행하므로 `Func<IntPtr ppvBits, CursorStyle, CursorMetrics, (int w, int h)>` 로 ppvBits 를 직접 전달 — main 의 `Gdi32.cs` 의 `GetCurrentObject` / `GetObjectDibSection` 헤더 변경 0. 자세한 결정 근거 + cursor-tray 학습 결과는 [dev-notes/2026-05-27-cursor-indicator.md](dev-notes/2026-05-27-cursor-indicator.md).

### Distance-field 분석적 AA 픽셀 셰이딩

`App/UI/CursorRenderer.Render` 는 DIB 의 BGRA32 픽셀에 직접 쓴다 — `DrawTextW` / `RoundRect` 등 GDI 그리기 미사용. 각 픽셀의 원 중심선까지 거리 `d_offset = |d - radius|` 가:
- `≤ coreT/2` → 코어 색상 (사용자 지정 ARGB) alpha 1.0 (양옆 0.5px AA via `Clamp01(coreHalf + 0.5 - dOffset)`)
- `≤ haloT/2` → 헤일로 색상 (흰색 × `HaloOpacity`, 코어 영역 제외 영역만)

코어 vs 헤일로 winner 는 각 픽셀에서 alpha 비교 — 큰 쪽 채택. 여러 동심원이 겹치는 경계 영역에서도 가장 강한 ring 의 alpha 가 채택된다 (`EvaluateRing` 의 `ringAlpha > bestAlpha` 비교).

### 헤일로 = 코어 양옆 (haloT - coreT) / 2 확장

사용자 명세 "코어 2px 양옆으로 흰 헤일로 0.5px 씩 비침 → 총 시각 두께 3px" 를 정확히 모델링: 헤일로 (3px) 가 코어 (2px) 보다 양옆 0.5px 씩 외부로 확장. `CursorStyle.BoundingBoxLogicalPx` 는 외측 반지름 + `(haloT - coreT + 1) / 2` (헤일로 외측 확장) + AA 여유 1px 로 DIB 정사각형 한 변을 계산.

### 동심원 3개 + CAPS OFF 시 외측 skip

`Inner` / `Middle` / `Outer` 3 원 — CAPS LOCK ON 시 모두 그려지고, OFF 시 외측 원의 `EvaluateRing` 호출 자체를 건너뜀 (`if (capsOn) EvaluateRing(d, outerR, ...)`). DIB bbox 는 CAPS 상태에 무관하게 항상 외측 반지름 기준이라 CAPS 토글 시 DIB 재생성 없이 같은 bbox 안에서 픽셀만 재계산.

`Render` 루프는 `dy * dy > maxOuterRSq` early exit (한 행 통째 skip) + `distSq > maxOuterRSq` per-pixel skip 으로 외곽 모서리의 빈 영역을 거른다.

### MinVisibleAlpha 가드 + cursor 전용 premultiply 정리 (외곽 잡티 차단)

2x2 supersampling 의 평균 알파 (`avgAlpha = accumA * 0.25`) 가 양수더라도 `Math.Round(avgAlpha * 255)` 가 0 으로 떨어지는 외곽 sub-sample 1개만 살짝 들어오는 픽셀 ("round-down 부산 픽셀") 이 발생한다. 이 픽셀이 출력에 들어가면 alpha=0 + RGB!=0 상태로 DIB 에 잔류해 `LayeredCursorBase.ApplyPremultipliedAlpha` 단계에 도달한다.

- **셰이더 가드** (`App/UI/CursorRenderer.cs`): `MinVisibleAlpha = 1.0 / 255.0` 상수를 두고 `ShadeDib` 픽셀 출력 분기를 `avgAlpha > 0.0` → `avgAlpha >= MinVisibleAlpha` 로 강화 — round-down 부산 픽셀 자체를 출력에서 제외
- **엔진 가드** (`Core/Windowing/LayeredCursorBase.cs`): `ApplyPremultipliedAlpha` 의 `a == 0 && (r | g | b) != 0` 분기에서 메인 `LayeredOverlayBase` 동명 가드 (GDI AA 엣지 보존 — `a = 255` 복구) 와 **의미가 다르게** RGB 도 0 으로 정리. cursor 셰이더는 GDI 그리기를 안 쓰고 alpha 를 명시적으로 쓰므로, 그 패턴의 픽셀은 GDI AA 엣지가 아니라 셰이더의 round-down 부산물이라 fully-opaque 점으로 복구하면 안 됨

두 가드는 중복 방어 — 셰이더 가드가 1차 차단, 엔진 가드가 셰이더 외 경로에서도 안전망. 메인 인디는 `DrawTextW` AA 가 alpha=0 RGB!=0 픽셀을 유효 엣지로 출력하므로 동명 가드의 의미가 정반대. 동일 이름 함수의 의미 차이가 cursor 도입 시 메인 가드의 무의식적 복사로 외곽 잡티 회귀를 만들었던 학습 — [dev-notes/2026-05-27-cursor-indicator.md "잠재 버그 fix — 외곽 잡티"](dev-notes/2026-05-27-cursor-indicator.md). PR-18 이 DIB 생성 + `UpdateLayeredWindow` 블리트 ~50 LOC 를 공유 helper 로 추출할 때 본 `ApplyPremultipliedAlpha` 는 의미 차이로 의도적 분기 보존 — 자세한 결정 근거 (옵션 A enum 전달 / 옵션 B 콜백 디스패치 양쪽의 거부 이유): [dev-notes/2026-05-28-pr-18-core-windowing.md](dev-notes/2026-05-28-pr-18-core-windowing.md).

### 색상 합성 (App 측 책임)

`CursorStyle` 의 3 색상 (`InnerColorArgb` / `MiddleColorArgb` / `OuterColorArgb`) 합성은 App 측 파사드 [`CursorOverlay.BuildStyle`](../App/UI/CursorOverlay.cs) 의 책임. Inner/Middle 은 현재 IME 색상 (`config.HangulBg` / `EnglishBg` / `NonKoreanBg` 중 하나). Outer (CAPS LOCK ON 시 표시) 는 "한글/비한글을 같은 카테고리로 묶고 영문만 반대편 카테고리" 정책 — 영문 IME → 한글 색상, 한글/비한글 IME → 영문 색상 (사용자 인터뷰 결정). Core 는 IME 상태를 모르므로 primitive `uint` (ARGB) 만 받는다. ARGB 조립 자체는 [`ColorHelper.HexToArgb`](../Core/Color/ColorHelper.cs) (`0xFFRRGGBB` — 기존 `HexToColorRef` BGR 의 형제, P4 색상 변환 단일 진실원) 에 위임하며 파사드는 IME→색상 라우팅만 담당한다.

**상태 배경색은 메인 인디 + 커서 인디 공용 단일 진실원**: `BuildStyle` 이 IME 상태별 배경색 (`HangulBg` / `EnglishBg` / `NonKoreanBg`) 을 **그대로** 커서 동심원 색 (`innerArgb` = Inner/Middle, `outerArgb` = CAPS Outer) 으로 라우팅하므로, 메인 라벨 배경에 쓰이는 동일 3 색이 커서 동심원에도 동시에 적용된다. 커서 전용 색 config 키는 없다 (의도 — 두 인디의 IME 색 정체성을 한 곳에서 관리). 이 공용성이 SettingsDialog 색상 섹션 구성의 근거다 ([Settings 다이얼로그 노출](#settings-다이얼로그-노출-pr-c--pr-21) 의 "인디케이터 — 색상 (메인/커서 인디케이터 공통)" 섹션 = 배경색 3 + 테마 → 공용이라 메인/커서 접두 없이 "인디케이터" 로 + 라벨에 `(메인/커서 인디케이터 공통)` 명시 + 메인·커서 모든 인디 섹션보다 앞에 배치, 글자색·투명도는 메인 전용이라 "메인 인디케이터 — 글자색·투명도" 로 분리). 반면 글자색 (`*Fg`) 은 메인 라벨 텍스트 전용 — 커서는 글자가 없어 헤일로를 흰색 고정으로 그리고, 투명도도 메인 전용 (커서 본체는 항상 불투명, 커서 헤일로 투명도는 `CursorHaloOpacity` 별도 키).

### IME 전환 스케일 팝 (PR-21)

IME 한↔영 전환 시 동심원이 잠깐 확대됐다 복귀하는 강조 효과 — 메인 인디 `OverlayAnimator` 의 Highlight 서브페이즈와 **동형이되 별도 구현**. config 키 3종 (`cursor_change_highlight` on/off 기본 ON / `cursor_highlight_scale` 시작 배율 1.3 / `cursor_highlight_duration_ms` 복귀 시간 300) 은 메인의 `change_highlight` / `highlight_scale` / `highlight_duration_ms` 와 평행.

**bbox 고정 → 팝 중 DIB 재생성 0**: `CursorStyle` 에 `HighlightScale` (매 프레임 배율, 기본 1.0) 필드 + `public const double MaxHighlightScale = 2.0` (팝 상한) 추가. `BoundingBoxLogicalPx` 가 매 프레임 변동하는 `HighlightScale` 이 아닌 **고정 상수 `MaxHighlightScale` 기준** 으로 DIB 한 변을 `Math.Ceiling((maxRadius + outsideMargin) * MaxHighlightScale)` 로 확대 계산한다. 그래서 `HighlightScale` 이 1.0↔`CursorHighlightScale` 사이에서 매 16ms 프레임마다 바뀌어도 DIB 크기는 불변 — CAPS 토글 시 DIB 재생성 0 과 동일 원리 (외측 반지름 기준 고정 bbox 안에서 픽셀만 재계산). App 측 `DefaultConfig.MaxCursorHighlightScale = CursorStyle.MaxHighlightScale` 로 clamp 상한이 Core bbox 상한을 참조 (App→Core 단일 진실원, P6 정방향) — 사용자가 `cursor_highlight_scale` 를 2.0 까지 올려도 정점이 항상 bbox 안. `App/UI/CursorRenderer.Render` 는 반지름 3종 + 두께 2종에 `style.HighlightScale` 을 곱해 동심원을 통째로 확대 (평상시 1.0 → 셰이더 무변화).

**경량 상태머신 (메인 `OverlayAnimator` 비재사용)**: 커서 인디는 별도 엔진 (P4 예외 영역) 이라 메인 `OverlayAnimator` (4-state + 5 트랙) 를 재사용하지 않고 `CursorOverlay` 내부 경량 상태 (`_popActive` + `_popStartTick` + `_hwndTimer`) 로 구현. 3 헬퍼:
- **`TriggerPop()`** — `SetImeState` 가 가시 상태 + `CursorChangeHighlight` 일 때 호출. `_popActive=true` + `_popStartTick=TickCount64` + `SetTimer(_hwndTimer, TIMER_ID_CURSOR_POP, AnimationFrameMs=16ms)` + **즉시 `HandleCursorPopTimer()` 1회 호출** (16ms 대기 없이 시작 배율로 첫 프레임 렌더). 가시 상태에서 IME 가 실제로 바뀌면 첫 프레임이 새 색 + 시작 배율로 렌더되므로 별도 색 갱신 `Render` 불요 — `CursorChangeHighlight` OFF 일 때만 색만 즉시 `Render`.
- **`HandleCursorPopTimer()`** (public — `Program.cs` WM_TIMER 분기에서 디스패치) — 16ms 프레임. `ratio = clamp(elapsed / durationMs, 0, 1)` (durationMs=0 이면 ratio=1 즉시 종료), `scale = CursorHighlightScale + (1.0 - CursorHighlightScale) * ratio` (메인 Highlight 와 동일 선형식 — 시작 배율에서 1.0 으로 수렴) → `_currentStyle with { HighlightScale = scale }` 재렌더. `ratio >= 1.0` 도달 시 `StopPop()`.
- **`StopPop()`** — `KillTimer` + `HighlightScale` 1.0 복원. 팝 자연 완료 (ratio 1.0) + 숨김 (셸 UI / 이동) + `HandleConfigChanged` (스타일 재합성 전 정리) + `Dispose` 에서 호출. `_popActive` 가드로 멱등 (이미 정지면 no-op).

`TIMER_ID_CURSOR_POP = 9` 는 `TIMER_ID_CURSOR_MOTION = 8` (모션 폴링) 과 별개 타이머 — 둘 다 메인 윈도우 (`_hwndTimer` = `_hwndMain`, `Program.EnableCursorOverlay` 가 `CursorOverlay.Initialize` 두 번째 인자로 주입) 에 걸린다. 트레이 메뉴 "커서 변경 시 강조" (`IDM_CURSOR_HIGHLIGHT = 4013`) 토글이 `CursorChangeHighlight` on/off 를 제어 — 메인 인디 `IDM_CHANGE_HIGHLIGHT` 와 동형 (`MF_CHECKED` = ON). 설계 단일 진실원: [improvement-plan/PR-21-cursor-pop-animation.md](improvement-plan/PR-21-cursor-pop-animation.md).

### 정지 검출 FSM + 항상 표시 모드

`CursorOverlay.HandleCursorMotionTimer` 는 두 모드로 동작:

- **정지 검출 모드 (`CursorAlwaysShow = false`)** — `TIMER_ID_CURSOR_MOTION` 이 `CursorMotionPollMs = 50ms` 로 호출. 매 tick `GetCursorPos` → 이전 좌표와 맨해튼 거리 `(|dx| + |dy|) > CursorMotionThresholdPx (5)` 이면 "이동" 으로 분류해 `_idleStartTick = 0` 리셋 + 가시 중이면 즉시 `Hide()`. "정지" 분류면 `_idleStartTick` 가 0 이면 현재 tick 으로 마킹, 이후 매 tick `now - _idleStartTick >= CursorIdleDelayMs (100ms)` 검사 → 도달 시 `RenderAtCursor` 호출 + 마킹 해제. 가시 상태에서는 정지 추가 검출 skip (이미 표시 중)
- **항상 표시 모드 (디폴트, `CursorAlwaysShow = true`)** — 타이머가 `CursorAlwaysPollMs = 16ms` 로 빠르게 호출. 이동/정지 분류 자체를 skip 하고 매 tick `RenderAtCursor` 만 호출 → cursor 위치 추종. 숨기지 않음

두 모드 전환은 `HandleConfigChanged` 의 "값 변경" 분기가 `KillTimer` + `SetTimer` 로 polling 주기를 즉시 교체해 흡수한다.

### cursor 윈도우 z-order 정책 — `WS_EX_TOPMOST` 생성 시 제거 + 첫 표시 시 명시 set + 주기 재적용

cursor 인디 enable 부팅 + 탐색기 실행 시 메인 인디가 ~2.5초 후 사라지던 회귀의 **진짜 원인** — cursor 윈도우의 첫 `UpdateLayeredWindow(alpha=255)` 가 DWM 합성 단계에서 `WS_EX_TOPMOST` z-band 의 다른 윈도우 (`Shell_TrayWnd` 도 topmost) 재정렬 trigger → ~1초 후 `Shell_TrayWnd` 가 잠시 foreground 변경 → detection thread `SystemFilter` 매칭 → `WM_HIDE_INDICATOR` → 메인 인디 hide. **explorer.exe** 가 shell process 라 `Shell_TrayWnd` 와 메시지 큐 공유 → race 빈도 높음. **Total Commander** 등 일반 third-party launcher 에서는 race 없음 (사용자 진단 확정).

fix:
- [`Program.Bootstrap.CreateCursorOverlayWindow`](../Program.Bootstrap.cs) 에서 `WS_EX_TOPMOST` **제거** — cursor 윈도우 생성 시 일반 z-order 로 시작. 부팅 sequence 동안 다른 topmost 윈도우 영향 0.
- [`CursorOverlay.RenderAtCursor`](../App/UI/CursorOverlay.cs) 첫 가시화 시 `ApplyTopmost()` → `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING)` 명시 호출 — topmost 진입 + `SWP_NOSENDCHANGING` 으로 `WM_WINDOWPOSCHANGING` 알림 차단 (다른 윈도우 z-order 재정렬 trigger 없음).
- `Win32Constants.SWP_NOSENDCHANGING = 0x0400` 신규 const.

이전 안전망 `BootGracePeriodMs (500→1500ms)` 는 z-order fix 가 진짜 원인 차단 후 불요 — 제거. cursor 첫 표시는 `idle_delay_ms` (100ms) 후 정상 등장.

**topmost 주기 재적용 (2026-06-01 후속 fix)** — 위 첫 표시 `SetWindowPos` 는 **1회**라, 다른 topmost 창 (풀스크린 게임 / 알림 토스트 / UAC) 이 위로 올라오면 cursor 인디가 그 아래 깔린 채 복구되지 않던 누락 (사용자 보고 "잘 동작하다가 갑자기 안 보임"). `ApplyTopmost()` (첫 표시 + 주기 재적용 단일 경로) 와 `MaybeReassertTopmost()` (`Environment.TickCount64` 게이트로 `DefaultConfig.CursorForceTopmostIntervalMs` 경과 시에만 `ApplyTopmost` (기본 5초) — 매 tick 호출되나 실제 `SetWindowPos` 는 5초당 1회) 헬퍼 신규. `HandleCursorMotionTimer` 의 **항상 표시 모드 + 정지 검출 모드 (가시 상태)** 양쪽 분기에서 `MaybeReassertTopmost()` 호출 — 두 모드 모두 보강 (정지 검출 모드도 가시 상태로 정지 중 다른 창에 가려질 수 있음). `CursorForceTopmostIntervalMs` 는 내부 const (AppConfig 키 아님 — config.json 오버라이드 불가, 메인 인디 `ForceTopmostIntervalMs` 와 같은 기본값이나 의미 분리, 0 이면 비활성). 가설 CC 회귀는 첫 표시와 동일한 `SWP_NOSENDCHANGING` 플래그 세트 + 주기 빈도 제어로 차단 (생성 시 `WS_EX_TOPMOST` 재도입 안 함). cursor 전용 게이트 1줄 재사용으로 메인 `TopmostWatchdog` 미재사용 (P4 예외) — 옵션 A/B 비교 + 가설 CC 차단 메커니즘: [dev-notes/2026-05-27-cursor-indicator.md "topmost 유실 후속 fix (주기 재적용)"](dev-notes/2026-05-27-cursor-indicator.md). 본 재적용은 무로깅이라 육안 검증에만 의존했으나 (2026-06-01) `MaybeReassertTopmost` 의 실제 `ApplyTopmost` 호출 시 `Cursor indicator topmost reasserted (interval=...ms)` Debug 로그를 추가 — 표시/숨김 전환·IME/CAPS 변경·config 반영·dispose 까지 상태 전환 엣지 7곳에 로그를 넣어 (핫패스 본문 제외, 스팸 0) 동작을 로그로 추적 가능하게 했다.

**시스템 UI z-order — 셸 UI 위에서 cursor 인디 숨김 (2026-06-01 추가 후속, 시행착오 → 재반전)** — 작업 표시줄 / 시작 버튼 / 검색 박스 / 트레이 아이콘 위에서 cursor 인디가 그 아래로 가려지던 문제. **초기 시도** (정지 주기 5초→1초 단축 + 항상 표시 모드 이동 중 매 tick 즉시 재적용 `ApplyTopmostNow`) 로 **작업 표시줄/트레이는 해결** (같은 `WS_EX_TOPMOST` 밴드라 재적용 빈도를 높이면 위로 올라감) 됐으나, **시작 메뉴/검색은 미해결** — 이들은 Windows immersive z-band (일반 topmost 위 계층) 라 `SetWindowPos(HWND_TOPMOST)` 빈도를 아무리 높여도 위로 못 올라간다 (게임 오버레이도 시작 메뉴 위엔 안 그려지는 알려진 OS 한계; `CreateWindowInBand` 비공개 API 만 가능 — P1/안정성 부적합). **사용자 결정** — z-order 싸움을 포기하고 **셸 UI 영역 위에서는 cursor 인디를 일관되게 숨긴다** (가려진 채 어색하게 두지 않음). 초기 시도의 즉시 재적용/1초 단축은 목적 소멸로 **롤백** (`ApplyTopmostNow` 제거, `CursorForceTopmostIntervalMs` 5초 원복) — `MaybeReassertTopmost` 5초 주기만 유지 (풀스크린/토스트/UAC 대비, 셸 UI 와 무관). 구현: `HandleCursorMotionTimer` 가 매 tick `IsOverShellUi(cursor)` 판정 → 셸 UI 면 `Hide()` + return. `IsOverShellUi` = `User32.WindowFromPoint(cursor)` (WS_EX_TRANSPARENT 통과 → cursor 인디 자체 미감지, dev-notes F2) → `GetAncestor(GA_ROOT)` 로 루트 (작업 표시줄 자식 버튼/시작/검색/트레이 → `Shell_TrayWnd` 등) → 클래스 매칭 (`SystemFilter.MatchesAny(SystemHideClasses)` 재사용 — P4 단일 구현, `MatchesAny` `private→internal`) + 프로세스 매칭 (`DefaultConfig.IsSystemInputProcess` = `StartMenuExperienceHost`/`SearchHost` + `SystemHideProcesses`). 클래스 우선 단락으로 작업 표시줄 등은 가벼운 `GetClassName` 만; 루트 hwnd 캐시 (`_lastShellHwnd`/`_lastShellResult`) 로 같은 창 호버 중 `GetProcessName` (OpenProcess) 반복 회피. **캐시 무효화 (2026-06-01 감사 Medium ⑧)**: `HandleConfigChanged` 가 `_lastShellHwnd = IntPtr.Zero` 로 캐시를 리셋한다 — `SystemHideClasses` / `SystemHideProcesses` 가 hot-reload 로 바뀌어도 마우스가 루트 창 경계를 넘을 때까지 stale 판정이 유지되던 결함 차단 (`Initialize` 는 이미 0 으로 시작). `Zero` 단일 리셋으로 충분 — 다음 `IsOverShellUi` 의 루트 (유효 hwnd) ≠ `Zero` 라 무조건 재평가. 이전 `WindowFromPoint` hide 구현 (`06d0d3f` 도입 → `cc25bf6` '일관 표시'로 revert) 의 복원 + 시작/검색 프로세스 축 보강 + GA_ROOT 루트 상승 추가.

### 시스템 창 호버 시 cursor 인디 정책 — 일관 표시

마우스가 작업 표시줄 / 시작 버튼 / 검색 박스 / 트레이 아이콘 위에 있을 때 cursor 인디는 일반 영역과 동일하게 표시. 시스템 창은 system topmost 라 cursor 인디 (일반 `WS_EX_TOPMOST`) 가 시각적으로 가려질 수 있으나, 사용자 결정 — "작업 표시줄에 가려지겠지만 일관적이면 괜찮음". 이전 `WindowFromPoint + SystemHideClasses` 매칭 시 hide 분기는 제거됨 + `User32.WindowFromPoint` LibraryImport 도 제거 (cursor 만의 사용처였음). **(2026-06-01 재반전)** 위 '일관 표시' 결정을 사용자가 번복 — 셸 UI (작업 표시줄/시작/검색/트레이) 위에서는 cursor 인디를 **숨긴다** (위 "셸 UI 위에서 숨김" 문단). 시작 메뉴/검색이 immersive z-band 라 일반 topmost 가 위로 못 올라가는 OS 한계 때문에, 가려진 채 두기보다 깔끔히 숨기는 쪽으로 전환. `IsOverShellUi` 가 `WindowFromPoint + GetAncestor(GA_ROOT)` 로 판정한다.

### 트레이 메뉴 lazy 생성 / dispose 흐름

디폴트 (`CursorIndicatorEnabled = true`) 에서는 부팅 시 윈도우/엔진/타이머가 정상 생성. 사용자가 트레이 "커서 인디케이터 숨김" 체크박스를 클릭해 `CursorIndicatorEnabled = false` 로 끈 동안에는 윈도우/엔진/타이머 **모두 해제** — 메모리/CPU 비용 0. 다시 켜면 트레이 메뉴 클릭 → `IDM_CURSOR_TOGGLE` → `updateConfig(config with { CursorIndicatorEnabled = true })` → `HandleConfigChanged` OFF→ON 분기 → `Program.EnableCursorOverlay()` 가 (1) `CreateCursorOverlayWindow` (별도 HWND, `WS_EX_TRANSPARENT` 영구 ON) → (2) `CursorOverlay.Initialize(hwnd, config, _lastImeState, _lastCapsLockState)` 가 엔진 + 첫 DIB 사전 생성 → (3) `SetTimer(TIMER_ID_CURSOR_MOTION, CursorMotionPollMs or CursorAlwaysPollMs)`. config.json 으로 `cursor_indicator_enabled = false` 를 명시 저장한 채 부팅하면 lazy 게이트가 동일하게 작동해 비용 0 보장.

메뉴 체크 의미는 메인 인디 `IDM_USER_HIDDEN` 과 동일 — 라벨 "커서 인디케이터 숨김" + `MF_CHECKED` = **현재 숨김 상태** (= `CursorIndicatorEnabled = false`). 클릭 시 enabled 반전.

OFF 토글 시 `DisableCursorOverlay()` 가 역순으로 `KillTimer` → `CursorOverlay.Dispose()` (엔진/DIB/GDI 핸들 해제) → `DestroyWindow(_hwndCursorOverlay)` → `_hwndCursorOverlay = IntPtr.Zero` (lazy 재생성 게이트 복귀). `OnProcessExit` 도 동일 cleanup 을 명시적으로 호출.

3 분기 (OFF→ON / ON→OFF / 값 변경) 는 `Program.ApplyCursorConfigChange()` 헬퍼로 추출되어 **두 경로**에서 호출된다: (1) `HandleConfigChanged()` — `config.json` 직접 편집의 mtime 폴러 리로드 경로, (2) `HandleMenuCommand` 람다 — 트레이 메뉴 즉시 적용 경로. 후자가 헬퍼를 직접 호출하지 않으면 트레이 토글이 작동 안 한다 — 람다 내부 `Settings.Save` 는 mtime self-bump 로 `WM_CONFIG_CHANGED` 를 차단 (감지 스레드 폴러가 본인 변경을 다시 알리지 않게 막는 의도된 정책) 하므로 `HandleConfigChanged` 가 호출되지 않음.

별도 HWND 선택 이유: 메인 `_hwndOverlay` 는 사용자 드래그 (HTCAPTION) 와 hit-test 가 필요해 `WS_EX_TRANSPARENT` 를 켤 수 없는데, cursor 인디는 마우스를 절대 가로채면 안 되므로 영구 클릭 통과가 필수. [dev-notes/2026-05-15-click-through-attempts.md](dev-notes/2026-05-15-click-through-attempts.md) F2 (WS_EX_TRANSPARENT 영구 ON) 패턴 재사용.

### cursor 중심 정렬 — `ShowAtCenter` API

`LayeredCursorBase.Show(x, y)` 는 좌상단 좌표만 받으므로 호출자가 `halfBbox = GetBaseSize().w / 2` 로 좌상단 계산. 그러나 DPI 다른 모니터로 cursor 가 진입한 직후 첫 호출은 `GetBaseSize` 가 **이전 모니터 DPI 기준** 의 DIB 크기를 반환 → 새 모니터의 정확한 halfBbox 와 어긋남. `Show` 가 `UpdateDpiFromPoint` 로 `_currentDpiScale` 갱신 + 캐시 무효화 → 다음 `Render` 가 새 DPI 기준 DIB 재생성하지만, 이미 잘못된 좌상단으로 한 프레임 표시된 후 `UpdatePosition` 으로 보정하는 비대칭이 사용자에게 "원 크기 변화가 살짝 이상함" 으로 가시화.

`LayeredCursorBase.ShowAtCenter(centerX, centerY, style)` 는 (1) `UpdateDpiFromPoint(centerX, centerY)` 로 새 DPI 갱신, (2) `DpiHelper.Scale(style.BoundingBoxLogicalPx, _currentDpiScale)` 로 정확한 새 bbox 계산, (3) `_lastX = centerX - bbox/2`, `_lastY = centerY - bbox/2` 로 좌상단 직접 산출 — race 없이 정확한 중심 보장. `CursorOverlay.RenderAtCursor` 가 본 API 를 사용해 좌표 정밀도 + DPI 전환 시각 일관성 동시 확보.

### Render 후 SW_SHOW 호출자 패턴 + alpha 디폴트 255

`LayeredCursorBase.Show(x, y)` 는 좌표/DPI 캐시만 갱신하고 `ShowWindow` 를 부르지 않는다. CursorOverlay (호출자) 가 `Render` 직후 명시 `ShowWindow(SW_SHOW)` 호출 — [dev-notes/2026-05-20-post-pr10-attempts-reverted.md](https://github.com/joujin-git/KoEnVue/blob/feat/v094-integration/docs/dev-notes/2026-05-20-post-pr10-attempts-reverted.md) 가설 A 함정 (Render 전 SW_SHOW 가 layered window 비트맵 없이 visible 캐싱 → 후속 UpdateLayeredWindow 가 화면에 안 나타남) 회피. 메인 인디 ([Animation.cs:100](../App/UI/Animation.cs#L100)) 와 동일 `SW_SHOW` 사용.

`_lastAlpha` 디폴트 0 으로 두면 첫 `Render → UpdateLayeredWindow` 가 `SourceConstantAlpha=0` (완전 투명) 으로 그려져 사용자가 못 본다. cursor 인디는 페이드 없이 항상 100% 표시이므로 [`LayeredCursorBase`](../Core/Windowing/LayeredCursorBase.cs) 에서 `_lastAlpha = 255` 디폴트로 초기화. 메인 인디의 OverlayAnimator 알파 보간과 무관.

### Settings 다이얼로그 노출 (PR-C / PR-21)

PR-C 에서 cursor 10 config 키를 트레이 메뉴 "상세 설정" 다이얼로그의 12번째 섹션 "커서 인디케이터" 로 GUI 노출. 새 컨트롤/팩토리/I18n 테이블 변경 0 — 기존 Bool/Int/Dbl 팩토리 + 인라인 `("한국어", "English")` 튜플 패턴 재사용. Min/Max 는 `DefaultConfig.MinCursor*` / `MaxCursor*` const 를 직접 참조해 **단일 진실원** 유지 (config-reference.md 표의 범위와 자동 일치). PR-C 당시는 디폴트 OFF 였으나 디폴트가 ON + 항상 표시 모드로 전환 — 다이얼로그 노출은 끄거나 세부 조정하려는 사용자의 발견 가능성을 보장.

**PR-21 — 13→14 섹션 + 3 대분류 재배치**: 기존 13 섹션이 메인·커서·전역 설정이 뒤섞인 순서였던 것을 **일반 (앱 전역) → 메인 인디 → 커서 인디** 3 대분류로 재정렬. `BuildRowDefs` 변경: (1) "일반" + "일반 — 트레이" 를 맨 앞으로 이동 (기존 "시스템" 섹션의 언어/로그/업데이트/관리자 권한 → "일반", 기존 "트레이" 섹션 → "일반 — 트레이"). (2) 메인 인디 8 섹션 모두 `"메인 인디 — XXX"` (`Main — XXX`) 접두. (3) 커서 인디 = `"커서 인디 — 동심원"` (`Cursor — Rings`, 기존 10 필드) + 신규 `"커서 인디 — 전환 효과"` (`Cursor — Transition`) 섹션 (`cursor_highlight_scale` / `cursor_highlight_duration_ms` 2 필드). (4) "고급" 은 14번째 말미. `cursor_change_highlight` on/off 는 메인 `change_highlight` 와 동일하게 트레이 메뉴 토글 전용이라 다이얼로그 제외 (애니메이션 on/off 는 메뉴, 세부 수치는 다이얼로그). 각 `FieldDef` 의 get/Commit 람다가 독립적이라 섹션 순서 변경에도 동작 정합 — `_fields` 인덱스와 컨트롤 짝은 `Add` 순서만 보장하면 됨 (70→72 필드). 컨트롤/팩토리/I18n 키 변경 0 (섹션 제목 문자열만 이동·개명).

**2026-06-01 후속 — 색상 섹션 2분할 + 테마 흡수 + "인디" → "인디케이터" 전체 표기 (섹션 수 14 유지)**: PR-21 의 색상 섹션 구성을 색상의 적용 범위(공용 / 메인 전용) 기준으로 재분할. (1) PR-21 의 "메인 인디 — 색상·투명도" (배경 3 + 글자 3 + 투명도 2) + 독립 "메인 인디 — 테마" 두 섹션을 → **"인디케이터 — 색상 (메인/커서 인디케이터 공통)"** (`Indicator — Colors (Main/Cursor Indicator)`, 테마 + 배경색 3) + **"메인 인디케이터 — 글자색·투명도"** (`Main — Foreground & Opacity`, 글자색 3 + 투명도 2) 로 재편. 배경색 3 (`HangulBg`/`EnglishBg`/`NonKoreanBg`) 은 [색상 합성](#색상-합성-app-측-책임) 의 공용 단일 진실원 (메인 배경 = 커서 동심원 색) 이라 메인/커서 접두 없이 "인디케이터" 로 + 라벨에 `(메인/커서 인디케이터 공통)` 명시, 테마는 그 배경색(+글자색)을 일괄 지정/복원하는 프리셋이라 같은 섹션으로 흡수 (PR-21 의 독립 테마 섹션 소멸). 글자색·투명도는 메인 라벨 전용이라 "메인 인디케이터" 접두 유지. 공용 색이라 이 "색상" 섹션을 **인디케이터 섹션들 맨 앞(3번)** 으로 배치 (일반·일반-트레이 2 섹션 다음, 메인·커서 모든 인디 섹션보다 앞) → "메인 인디케이터 — 표시 모드" 3→4, "메인 인디케이터 — 크기·테두리" 4→5 로 밀림. (2) PR-21 의 모든 `"메인 인디 — XXX"` / `"커서 인디 — XXX"` 라벨을 `"메인 인디케이터 — XXX"` / `"커서 인디케이터 — XXX"` 전체 표기로 통일. 섹션 수 14 불변 (2 섹션 → 2 섹션 재편, 순증 0), 필드 수·get/Commit 람다·config 키·컨트롤·팩토리·I18n 키 변경 0 (섹션 제목 + ColorField 재배치만). `_fields` 인덱스/컨트롤 짝은 `Add` 순서가 보장.

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
- **Class cursor must be `IDC_ARROW` explicit.** Without `hCursor`, the OS applies `IDC_APPSTARTING` (arrow + small hourglass) over the client area for the per-process startup grace period (default ~5 s, `HourglassWaitTime` registry). Invisible when `drag_modifier == None` because hit-test always returns `HTCAPTION` and the caption-area path of `DefWindowProc` forces `IDC_ARROW`, but exposed by `Ctrl` / `Alt` / `CtrlAlt` modes where neutral hover returns `HTCLIENT` and falls back to the class cursor — without the explicit `hCursor`, those modes leak the startup-busy cursor for several seconds after first launch. Same defect was latent in the five dialog window classes (`Cleanup{Dlg,Viewport}`, `Settings{Dlg,Viewport}`, `ScaleDlg`); to prevent recurrence, all `WNDCLASSEXW` registration is funneled through [`Win32DialogHelper.RegisterStandardClass`](../Core/Windowing/Win32DialogHelper.cs), which always loads `IDC_ARROW` and exposes only `(className, wndProc, hbrBackground?)` — making it impossible to register a window class without a class cursor.

UI exposure: tray menu "드래그 활성 키" radio submenu (4 items) and settings dialog combo in the "인디케이터 조작" section.

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

`config.indicator_positions_relative` stores per-process-name entries as `int[3]`: `[(int)Corner, DeltaX, DeltaY]`. `DeltaX` / `DeltaY` are **logical pixels** (96 DPI baseline), not physical pixels. On foreground change, `GetAppPositionWindow` decodes the array, validates `Corner` via `Enum.IsDefined`, obtains the current window's DWM frame via `Dwmapi.TryGetVisibleFrame`, queries the target monitor's DPI scale via `DpiHelper.GetScale(User32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST))`, and resolves absolute coordinates with `Overlay.ResolveRelativePosition(frame, relConfig, dpiScale)`. The resolver multiplies the logical delta by the target monitor's DPI scale (via `DpiHelper.Scale` with `Math.Round`) before adding to the frame corner, so the indicator lands at the same logical-pixel offset from the window corner on every monitor regardless of DPI. Result is clamped to the visible area.

This design naturally handles the "same app, multiple windows" case: a single process-name entry (e.g., `"notepad": [1, -50, 10]`) produces different absolute coordinates for each window because each window has a different rect on screen. No runtime per-hwnd cache is needed.

On drag end, `Overlay.ComputeRelativeFromCurrentPosition(hwndForeground)` computes the nearest of the 4 DWM frame corners by Manhattan distance in physical pixels, then divides by the foreground window's monitor DPI scale to normalize the delta into the 96 DPI logical baseline before storing. This save-time normalization plus the apply-time multiplication keeps the indicator's visual position invariant across monitors with different DPI.

### Window movement tracking (Window mode)

In Window mode, the detection loop (80 ms) tracks `lastWindowFrame` and a `windowMoving` flag for the foreground window. When the DWM frame changes (window being moved/resized), the indicator is hidden (`WM_HIDE_INDICATOR`). When the frame stabilizes (no change for 1 tick ≈ 80 ms), `foregroundChanged` is set to `true`, triggering `WM_POSITION_UPDATED` → position re-resolve → indicator re-shown at the new window-relative position.

The `lastWindowFrame` and `windowMoving` state are reset on foreground window change. System input processes are excluded from this tracking (they have their own shared-HWND rect tracking block).

### Default position

Two nullable config fields store per-mode defaults for apps without a saved position. In both modes `DeltaX` / `DeltaY` are **logical pixels** (96 DPI baseline); the resolver multiplies by the target monitor's DPI scale (`DpiHelper.Scale` with `Math.Round`) before anchoring.

- **Fixed mode**: `config.default_indicator_position` (`DefaultPositionConfig` record) — `Corner` + `DeltaX` + `DeltaY` (logical px) resolved against the **foreground window's monitor work area** via `Overlay.ResolveAnchor(workArea, anchor, dpiScale)` (`dpiScale = DpiHelper.GetScale(hMonitor)`)
- **Window mode**: `config.default_indicator_position_relative` (`RelativePositionConfig` record) — `Corner` + `DeltaX` + `DeltaY` (logical px) resolved against the **foreground window's DWM frame** via `Overlay.ResolveRelativePosition(frame, rel, dpiScale)`

Null fallbacks (hardcoded, also logical px — scaled at apply time):
- Fixed: `DefaultConfig.DefaultIndicatorOffsetX = -200, Y = 10` (top-right of work area, scaled by target-monitor DPI before anchoring)
- Window: `DefaultConfig.DefaultRelativeCorner = BottomRight, X = -69, Y = -58` (inside bottom-right of window, scaled by target-monitor DPI before anchoring). `AppConfig.DefaultIndicatorPositionRelative` 의 init 디폴트는 본 3 const 를 직접 참조하는 `RelativePositionConfig` 객체 — 사용자가 명시적으로 `null` 을 저장한 경우에만 Overlay 폴백 경로가 동일 const 를 재참조 (두 경로 단일 진실원 일치)

Multi-monitor / resolution stability: offsets are stored relative to a `Corner` anchor, not as absolute pixel coordinates, and both Fixed-mode default-anchor and Window-mode relative deltas are DPI-normalized to 96 DPI logical pixels. The indicator's visual position relative to the anchor (work area corner for Fixed default, window frame corner for Window) is invariant across monitors of differing DPI scale. See "Window-relative position memory" above for the save/apply math — Fixed-mode default anchor follows the same pattern via `ComputeAnchorFromCurrentPosition` (divide by source monitor scale) and `ResolveAnchor` (multiply by target monitor scale).

Tray menu:
- **"현재 위치로 설정"**: branches on current mode — Fixed calls `Overlay.ComputeAnchorFromCurrentPosition()` (work area corners), Window calls `Overlay.ComputeRelativeFromCurrentPosition(hwndForeground)` (window frame corners). Both use Manhattan distance to pick the nearest corner
- **"초기화"**: resets the current mode's field to null (menu item grayed when already null)

### Off-screen position clamp

`Program.ClampToVisibleArea(x, y)` wraps `GetAppPosition`'s two saved-position tiers (runtime hwnd dict + `config.IndicatorPositions`) before they are returned. Resolves the target monitor via `DpiHelper.GetMonitorFromPoint(x + w/2, y + h/2)` with `MONITOR_DEFAULTTONEAREST` semantics, so a coordinate whose original monitor has been disconnected re-routes to the nearest surviving monitor's work area.

Clamp bounds use `Math.Max(workArea.Left, workArea.Right - w)` as the upper limit so indicators larger than the work area collapse to `Left`/`Top` instead of flipping through `Math.Clamp`'s invalid-range exception.

**Read path — stored value is never rewritten.** `GetAppPositionFixed` clamps only the returned coordinate; `_hwndPositions` / `config.IndicatorPositions` entries retain their original values. Reattaching the original monitor restores the original position on the next lookup. Defends monitor removal / resolution change / DPI change scenarios that would otherwise leave the indicator unreachable.

**Write path — new values are clamped before persistence.** `HandleOverlayDragEnd` (Fixed mode branch) applies `ClampToVisibleArea` to the drag-end coordinate before writing to `_hwndPositions` and `config.IndicatorPositions`. Normal drag produces in-screen coordinates because the OS drag loop keeps the cursor on screen, so this is a no-op in the common case — the guard exists for edge conditions such as monitor unplug mid-drag or work-area reduction between drag start and drag end. Keeps `config.json` free of off-screen coordinates even at these boundaries; does not mutate pre-existing entries (see read-path invariant above). Window mode stores a frame-relative offset and is exempt — its absolute resolution is already clamped at read time.

Path 3 (default position) is not clamped because `GetDefaultPosition` already computes against the live foreground monitor's work area. System input processes bypass this entirely since they already route straight to `GetDefaultPosition`.

### System input process exception

`StartMenuExperienceHost` / `SearchHost` / `SearchApp` (`DefaultConfig.SystemInputProcesses`) are special. TOPMOST z-band cannot rise above these shell UI surfaces, so any saved position that ends up under them becomes unreachable.

- Drag is ignored (position never saved)
- `GetDefaultPosition` places the indicator just above the window's visual top-left corner: `(frame.Left, frame.Top - labelH - DpiHelper.Scale(SystemInputGapLogicalPx, monitorDpiScale))`, clamped to `workArea.Top` — the gap constant is logical px (96 DPI baseline), multiplied by the target monitor's DPI scale so the visual spacing is invariant across monitors
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

**콜백 경계 예외 가드 (감사 High ②, 2026-06-01)**: `WindowSnapHelper.EnumWindowsCallback` 과 `WindowProcessInfo.EnumChildCallback` 의 본문 전체를 `try/catch (Exception ex)` 로 감싼다. 관리 예외가 `[UnmanagedCallersOnly]` 콜백을 빠져나가 unmanaged 열거 함수의 경계를 넘으면 NativeAOT 런타임이 예외를 전파할 수 없어 **프로세스를 종료**시키기 때문 — 평범한 콜스택이라면 호출자가 받았을 예외가 여기서는 곧 크래시다. 한 창(권한 상승 프로세스의 HWND, 전환 중인 UWP host 등)에서 발생한 예외로 전체 열거 + 앱이 죽는 대신, `LogProvider.Sink?.Debug("...skipped a window: ...")` 후 `return 1` (열거 계속) 로 그 창만 누락 — 스냅 후보 수집·UWP 이름 해석 모두 best-effort 라 정당. `GetProcessName` 내부의 `OpenProcess` 실패는 대부분 자체 흡수되지만 그 밖의 예외를 경계에서 한 번 더 방어하는 안전망. 본문이 P/Invoke + BCL 혼합이라 narrowing 이 불완전하지만 (Detection loop catch 와 동형) 목적이 "런타임 종료 회피" 라 wide catch 가 정당하며, "skipped" 단어로 Debug 레벨 "failed" 회피 ([conventions §11](conventions.md)).

### WM_MOVING drift re-sync

Non-idempotent modifications (like snap) inside `WM_MOVING` accumulate drift because the system uses the previous tick's modified rect as the base for the next tick's cursor delta (`new_rect = prev_modified + cursor_delta`). Without re-sync, a slow drag with snap active pulls the cursor further and further from the indicator.

**Fix**: `HandleMoving` first re-syncs `movingRect` to `(cursor - _dragHotPoint)` via `GetCursorPos()` every tick, making the modification chain idempotent. Shift-drag worked without this fix because it always rewrites to a fixed start coordinate (also idempotent by construction), but snap needed explicit re-sync. `HandleMoving` now always returns `true` since the rect is always overwritten.

### WM_MOVING drag DPI

`HandleMoving` → private `HandleDragDpiChange` detects monitor boundary crossing during drag, re-creates resources at new DPI, and calls `UpdateLayeredWindow` directly (bypassing `_isDragging` guard).

---

## Animation

### 5-state machine

Hidden → FadingIn → Holding → FadingOut → Idle, plus highlight and slide sub-phases. All transitions driven by `WM_TIMER`.

**`SnapToTargetAlpha` Fade-track cleanup**: `TriggerShow` 의 `Holding` / `Idle` 분기는 `BeginFadeIn(...)` → `SnapToTargetAlpha()` 패턴으로 alpha 를 즉시 target 으로 끌어와 다음 프레임까지의 가시 깜박임을 억제한다. 단 `SnapToTargetAlpha` 는 set 만 수행하므로, 이 패턴이 `_phase == FadingIn` 상태에서 호출되면 Fade 타이머가 살아남아 다음 16ms 틱이 `_fadeStartAlpha` 부터의 보간 값으로 alpha 를 되돌려 사용자가 "한 번 떴다가 사라졌다가 다시 뜨는" 깜박임을 본다 (부팅 시 detection thread 가 `WM_POSITION_UPDATED + WM_IME_STATE_CHANGED + WM_FOCUS_CHANGED` 를 1ms 내 연쇄 post → `TriggerShow` 3회 호출 → 2~3번째가 FadingIn 재진입). 방어: `SnapToTargetAlpha` 자체가 `_phase == FadingIn` 일 때 Fade 타이머를 `KillTimer` 하고 `_phase` 를 `Holding` 으로 전이 + Hold 타이머를 재등록한다. 호출자 분기가 phase 일관성을 신경 쓰지 않아도 되고 Idle 분기에서도 페이드 인 애니메이션 skip + 즉시 target alpha 표시로 의도 부합. [dev-notes/2026-05-27-snap-fade-killtimer.md](dev-notes/2026-05-27-snap-fade-killtimer.md).

Timer IDs (injected via `AnimationTimerIds` record so Core stays ID-agnostic):

| Timer | Purpose | Source constant |
|-------|---------|-----------------|
| `Fade` | Fade-in / fade-out frame tick | `DefaultConfig.AnimationFrameMs = 16` (~60 fps) |
| `Hold` | Holding → next phase. OnEvent: FadingOut → Hidden. Always: FadeToIdle (→ IdleOpacity) | OnEvent: `config.EventDisplayDurationMs`, Always: `config.AlwaysIdleTimeoutMs` |
| `Highlight` | IME-change zoom (1.3× → 1.0×) | `config.HighlightDurationMs` |
| `Topmost` | Periodic `ForceTopmost` re-assert | `config.Advanced.ForceTopmostIntervalMs` (default 5000) |
| `Slide` | Ease-out cubic position interpolation | `config.SlideSpeedMs` |

### NonKoreanImeMode Dim

`OverlayAnimator.GetTargetAlpha` applies `DefaultConfig.DimOpacityFactor = 0.5` when the state machine is in the Dim branch. Since Stage 4 this lives inside `OverlayAnimator` and is driven by `OverlayAnimator.SetDimMode(bool)` — the `Animation` facade routes `config.NonKoreanImeMode == Dim && state == NonKorean` into it so Core never sees the enum.

### Slide animation

Ease-out cubic interpolation: `1 - (1 - t)^3` via `TIMER_ID_SLIDE`. All animation timers use `DefaultConfig.AnimationFrameMs = 16 ms` (~60 fps).

**Slide + Highlight 합성 (종합 감사 ⑩)**: slide 트랙(위치만)과 highlight 트랙(위치 + stretch 크기)이 같은 레이어드 윈도우의 `UpdateLayeredWindow` 를 16ms 간격 last-writer-wins 로 번갈아 set 하면 base↔확대 크기 진동 + 위치 점프가 보였다. **중간 단계**에서는 `TryStartSlide(prevX, prevY, newX, newY, willHighlight)` 진입부 가드 `if (_highlightActive || willHighlight) return;` 로 강조가 진행/예정이면 슬라이드를 **보류**했으나, slide 를 생략하면 모니터 간 이동 등에서 인디가 출발 위치에 머무는 미관 문제가 있어 **합성(두 효과 동시 진행, blit 은 한 writer 가 전담)으로 재구현**했다. 핵심은 slide 진행 중(`_slideActive`)에는 slide 가 **위치만 추적**하고 highlight 가 **그리는 일을 전담**한다는 것:

- slide 보간 위치를 매 틱 `_slideCurX/_slideCurY` 에 저장. `_highlightActive` 면 `_onTrackPosition(x, y)` (→ [`LayeredOverlayBase.TrackPosition`](../Core/Windowing/LayeredOverlayBase.cs) 이 `_lastX/Y` 만 갱신, **blit 없음**) 호출, 아니면 종전대로 `_onPositionOffset(x, y)` 로 직접 blit (slide 단독).
- `HandleHighlightTimer` 의 확대 blit 은 `_slideActive ? (_slideCurX, _slideCurY) : (_lastX, _lastY)` 를 중심으로 계산 — slide 진행 중이면 slide 의 현재 위치를 따라가며 확대된 인디를 그린다. highlight 종료 시 원래 크기 복원도 같은 중심 기준.
- 위치를 `_lastX/Y` 에 동기화(TrackPosition)하므로, slide+highlight 합성 중 페이드/Hold/복원이 옛 출발 위치가 아닌 **현재 slide 위치** 기준으로 동작한다 (모니터 간 이동 시 출발점에서 사라지던 버그 방지).
- `willHighlight` 는 더 이상 slide 판정에 쓰지 않는다 (`_ = willHighlight;` — 시그니처는 호환 유지).

**모니터 간 slide 생략 (즉시 이동)**: `TryStartSlide` 는 prev/new 좌표가 같은 모니터일 때만 slide 한다 (`IsSameMonitor` = 두 점의 `User32.MonitorFromPoint(MONITOR_DEFAULTTONEAREST)` 핸들 비교). 모니터 간 이동은 DPI/확대비율 전환으로 인디 크기가 변동해 경계를 넘는 애니가 어색하므로, 다른 모니터면 slide 없이 즉시 이동한다 (파사드 `Show` 가 이미 목적지에 도착 모니터 DPI 로 정확히 그려둠).

회귀 가드: [tests/KoEnVue.Tests/Unit/OverlayAnimatorTests.cs](../tests/KoEnVue.Tests/Unit/OverlayAnimatorTests.cs) 3 테스트 (`Slide_DuringHighlight_TracksPositionWithoutBlit` / `Slide_WithoutHighlight_BlitsPosition` / `Highlight_RunsRegardlessOfSlide`).

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

`DetectionLoop`의 while 본문은 `catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or COMException or ArgumentException)` 로 래핑되어 단일 폴링 예외(예: `WindowProcessInfo.GetProcessName` 실패, UAC 전환 중 일시적 `COMException`)가 감지 스레드를 종료시키지 않는다. 로직 버그(`NullReferenceException` 등) 는 이 필터에 걸리지 않아 표면화된다. `Thread.Sleep`은 try 밖에 위치하여 예외 후에도 폴링 간격이 유지된다. `_stopping` 필드는 `volatile`로 선언되어 `OnProcessExit`에서의 쓰기가 감지 스레드에서 즉시 가시적이다.

**지수 백오프 + 중복 로그 스팸 억제**: 예외가 반복되면 `Thread.Sleep(PollIntervalMs + backoffMs)` 의 `backoffMs` 를 매 실패마다 `DefaultConfig.DetectionBackoffStepMs = 200` 씩 누적 (`DetectionBackoffMaxMs = 2000` 상한). 드문 COM apartment 과도기 상황에서 초당 12건의 Warning 이 수십 초간 누적되어 로그 파일을 오염시키던 시나리오를 차단한다. 동일 예외 메시지가 연속 발생하면 첫 발생만 `Logger.Warning` 으로 기록하고 이후는 `Logger.Debug` 로 강등, 새 메시지는 다시 Warning 1회. 성공 tick 이 돌아오면 `backoffMs = 0` 리셋 + "Detection loop recovered after backoff (prev=Nms)" Info 로그. 백오프 상한이 2초로 캡핑되어 있어 최악 경우 `OnProcessExit` 의 `_stopping = true` 신호가 `PollIntervalMs 80ms + backoff 2000ms ≈ 2.08초` 이내 전파된다.

### Foreground change detection

`foregroundChanged` flag triggers focus event independently of `hwndFocus` comparison, fixing the return-to-same-window case after a desktop switch.

### Console host fallback

`hwndFocus == 0` + `ConsoleWindowClass` check → use foreground window as focus target. Console apps don't report focus to AccessibleObjects, so we fall back.

### Position update ordering

Detection loop sends `WM_POSITION_UPDATED` **before** `WM_IME_STATE_CHANGED` / `WM_FOCUS_CHANGED` to ensure `_lastForegroundHwnd` is current when those handlers run.

### Per-poll filter evaluation

`DetectionLoop` evaluates `ResolveForApp + SystemFilter.ShouldHide` every tick (not only on foreground change) and uses a `lastFiltered` flag to suppress duplicate `WM_HIDE_INDICATOR` messages. Fixes the "desktop click → same app return" case where nothing appeared to change but the indicator needed to reappear. Hide message is emitted only on `!lastFiltered → filtered` transitions.

#### HIDE 디바운스 (flip-flop 흡수)

`TryHandleFilter` 는 filtered 가 떴다고 곧바로 HIDE 를 보내지 않고, `DetectionState.FilteredStreak` 로 **연속 filtered 폴링 수**를 세어 `DefaultConfig.HideHysteresisPolls`(= 3, AppConfig 키 아닌 코드 레벨 const) 회 연속일 때만 HIDE 를 확정한다. 일부 창(파일 탐색기 `CabinetWClass` 등)은 포커스 전환 직후 `hwndFocus` 가 `0 ↔ 정상` 으로 진동(flip-flop)해 조건 6(`hwndFocus == 0 && HideWhenNoFocus`) 이 매 폴링 filtered ↔ non-filtered 로 뒤집히는데, 디바운스가 이 단발 진동을 흡수한다. 잠정(`streak < N`) 구간에서는 `Filter HIDE deferred` 로그만 남기고 **`lastFiltered`/`lastHwndForeground` 등 상태를 갱신하지 않은 채 return** — 현 인디 상태를 유지하므로, 다음 틱이 진동의 반대 위상이면 filter exit 경로의 `WM_POSITION_UPDATED` 가 자연 복원한다. 이 "잠정 구간 상태 미갱신" **비대칭** 이 핵심으로, [Deferred `lastHwndForeground`](#deferred-lasthwndforeground) 와 같은 결을 이루며 "첫 진입 Show 누락" 함정을 피하면서 진동만 걸러낸다. non-filtered 진입 시 `FilteredStreak = 0` 으로 리셋. 작업표시줄 등 실제 숨김 대상은 연속 filtered 라 약 `PollIntervalMs × (N − 1)` 만큼만 HIDE 가 지연될 뿐 정상 동작한다.

이 디바운스가 없던 시절, 애니메이션 ON 에서는 교대 post 된 HIDE(`forceHidden: true` → FadingOut 400ms) 와 Show 가 경합해 메인 인디가 깜박이다 마지막 이벤트가 HIDE 면 `Hidden` 으로 박제됐다 — `OverlayAnimator` 의 FadingOut 재진입엔 `SnapToTargetAlpha` 가 없어 alpha 가 점진 0 으로 수렴하기 때문(C안 미해결 잔여). 애니메이션 OFF 는 즉시 토글이라 무증상이었다. 자세한 race 분석은 [docs/dev-notes/2026-06-02-explorer-hide-flipflop-debounce.md](dev-notes/2026-06-02-explorer-hide-flipflop-debounce.md).

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

#### Tier 3 HKL IME device signature gate

`ImeStatus.TryTier3` 는 `langId != LANGID_KOREAN(0x0412)` 를 곧바로 `NonKorean` 으로 분류하지 않고, HKL 상위 니블 `0xE` (IME 디바이스 시그니처, `HKL_IME_DEVICE_MASK = 0xF0000000` / `HKL_IME_DEVICE_SIG = 0xE0000000`) 가 일치할 때만 `NonKorean` 을 반환한다. 콘솔 호스트(`conhost.exe`) 처럼 IME 가 아직 스레드에 붙지 않은 프로세스는 Tier 1 (`ImmGetDefaultIMEWnd`) / Tier 2 (`ImmGetContext`) 가 `null` 로 떨어지고 Tier 3 가 기본 키보드 레이아웃(예: en-US `0x0409_0409` — `langId=0x0409`, 상위 니블 `0x0`) 을 보게 되는데, 이를 `NonKorean` 으로 분류하면 `Animation.TriggerShow` 의 `NonKoreanImeMode.Hide` 가드(기본값) 가 `TriggerHide(forceHidden: true)` 로 인디를 강제 숨김해 "플래시 후 사라짐" 증상을 유발한다. IME 장착 HKL(한글 `0xE001_0412` · 일본어 `0xE001_0411` · 중국어 `0xE00E_0804`) 은 상위 니블이 `0xE` 로 시그니처를 가지므로 이 검사로 구분된다. "IME 미장착 스레드 = IME 미활성" 으로 간주해 `English` 로 폴백 — 한/영 토글 1회 시 한글 IME 가 스레드에 결합되면서 `langId == LANGID_KOREAN` 분기로 안착해 동일 결과에 수렴. 콘솔 호스트 외에도 Emacs/mintty 등 비-네이티브 Win32 창 전반에서 동일 증상 해소.

#### WinEvent hook honors `detection_method`

IME 감지 경로는 두 가지다 — (1) 디텍션 스레드 80ms 폴링 (`DetectionLoop`), (2) 메인 스레드 `EVENT_OBJECT_IME_CHANGE` WinEvent 훅 (`ImeStatus.OnImeChange`). 둘 다 사용자가 `config.json` 의 `"detection_method"` 로 선택한 단일-tier 경로(`ime_default` / `ime_context` / `keyboard_layout`) 를 따라야 하지만, 훅은 `WINEVENT_OUTOFCONTEXT` 콜백이라 `AppConfig` 인스턴스에 직접 접근할 수 없다. 해결: `ImeStatus` 가 `volatile DetectionMethod _detectionMethod` 정적 필드를 보유하고, 메인 스레드가 `RegisterHook(hwndMain, config.DetectionMethod)` 로 초기값 주입 + `UpdateDetectionMethod(config.DetectionMethod)` 로 핫 리로드 갱신(설정 다이얼로그 저장 + `config.json` 외부 편집 + 트레이 메뉴 전환 3경로). `OnImeChange` 가 `Detect(hwndFg, threadId, _detectionMethod)` 3-파라미터 오버로드를 호출해 폴링 경로와 동일한 분기. `volatile` 은 메인 스레드가 쓰고 동일 스레드의 콜백이 읽어 현재 구조에서는 불필요하지만 향후 스레드 변경 방어.

### System filter (8 conditions)

1. Secure desktop (no hwnd)
2. Invisible / minimized window
3. Other virtual desktop
4. Class name blacklist (`Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `XamlExplorerHostIslandWindow_WASDK`, `TopLevelWindowForOverflowXamlIsland`, `ControlCenterWindow` + user-specified) — last two cover the Win11 tray overflow flyout and Quick Settings (`Win+A` / volume / Wi-Fi / battery)
4-b. Owner chain blacklist — walks `GetWindow(GW_OWNER)` up to 5 levels; hides only when owner class is in hide list **and** dialog/owner share the same process. This catches desktop-initiated system dialogs (e.g. Recycle Bin empty confirm: `#32770` owned by `Progman`, both `explorer.exe`) while allowing app-initiated Common File Dialogs (e.g. Notepad Save As: `#32770` owned by `Progman` but process `Notepad` ≠ `explorer`)
5. Process name blacklist (`ShellExperienceHost` + user-specified) — hides taskbar/desktop right-click context menus on Win11 where the popup becomes the foreground window with a null owner chain
6. No focus (`hide_when_no_focus`)
7. Fullscreen exclusive (covers monitor + no `WS_CAPTION`)
8. App blacklist / whitelist (`app_filter_list` + `app_filter_mode`)

**Per-tick 프로세스명 메모이제이션**: `ShouldHide` 본문에 `string? hwndProcess = null; string ResolveHwndProcess() => hwndProcess ??= WindowProcessInfo.GetProcessName(hwnd);` 로컬 클로저를 둔다. 조건 4-b 의 owner 루프(루트까지 최대 5단계 상승)가 각 노드마다 동일 hwnd 의 프로세스명을 재조회하고, 조건 5 직접 비교 + 조건 8 blacklist 조회가 또 한 번 호출하던 중복을 제거 — `GetProcessName` 은 내부적으로 `OpenProcess` + `QueryFullProcessImageNameW` + `Path.GetFileNameWithoutExtension` 체인이라 호출당 NT 핸들 오픈 + 커널 모드 전환이 발생하는 무거운 경로이다. 감지 스레드 80ms 핫패스에서 1 tick 당 평균 3~5회의 P/Invoke 체인을 절감한다. cross-tick 캐시는 불필요 — `DetectionState.LastForegroundProcessName` 이 이미 foreground 전환 레벨의 cross-tick 캐시 역할을 담당하므로, `ResolveHwndProcess` 는 1 tick 내부 국소 최적화 한정이다.

### Lock screen hiding (WTS session notification)

`hide_on_lock_screen` (기본 `true`) 은 **WTS Session Notification** 으로 구현된다. `Program.MainImpl` 이 메인 윈도우 생성 직후 `Wtsapi32.WTSRegisterSessionNotification(_hwndMain, NOTIFY_FOR_THIS_SESSION)` 을 호출해 `WM_WTSSESSION_CHANGE` (0x02B1) 메시지를 받도록 등록하고, `OnProcessExit` 가 `DestroyWindow` 전에 `WTSUnRegisterSessionNotification` 으로 해제한다(wtsapi32 내부 핸들 매핑 누수 방지).

메인 스레드의 `HandleSessionChange(uint wParam)` 가 `WTS_SESSION_LOCK` / `WTS_SESSION_UNLOCK` 두 이벤트만 처리:

- **LOCK**: `volatile bool _sessionLocked = true` 설정 + (HideOnLockScreen 활성 && 인디 표시 중이면) 즉시 `HideOverlay()`
- **UNLOCK**: `_sessionLocked = false` 해제. 별도 show 호출 없음 — 잠금 해제 직후 사용자가 창을 포커스하면 감지 스레드의 foreground-changed 경로가 자연스럽게 인디를 다시 켠다
- 로그오프 / 콘솔 접속 전환 등 그 외 이벤트는 무시

감지 스레드(`ProcessDetectionTick`) 는 진입 직후 `if (_sessionLocked && _config.HideOnLockScreen) { state.LastFiltered = true; return; }` 가드로 한 틱을 통째로 스킵한다. 이유: `SystemFilter` 의 기본 클래스 블랙리스트가 LogonUI 의 창 클래스(`CredentialDialogXamlHost` / `LockAppHost`) 를 포함하지 않아 잠금 화면 동안에도 필터가 뚫려 인디가 다시 표시될 수 있기 때문이다. WTS 이벤트는 LogonUI 보다 **먼저** 도착하므로 이 플래그가 감지 루프를 잠금 구간 동안 확실히 침묵시킨다. `LastFiltered = true` 는 잠금 해제 후 첫 정상 틱이 `foregroundChanged` 판정을 유도하도록 하는 sentinel.

`Wtsapi32` P/Invoke 는 `Core/Native/Wtsapi32.cs` 에 `[LibraryImport]` 로 분리되어 있고 상수 4종 (`NOTIFY_FOR_THIS_SESSION = 0`, `WM_WTSSESSION_CHANGE = 0x02B1`, `WTS_SESSION_LOCK = 0x7`, `WTS_SESSION_UNLOCK = 0x8`) 은 `Core/Native/Win32Types.cs` 의 `Win32Constants` 블록에 있다(P3 매직 숫자 금지).

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
4. **`Migrate`** — `JsonSettingsManager<T>` 의 pass-through virtual 훅. 현재 단독 사용자 단계라 `AppSettingsManager` 는 override 하지 않으며, 파일에 version 필드도 저장하지 않는다. 향후 공개 배포 전환 시 override 부활 예정
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

[`App/Config/PortablePath`](../App/Config/PortablePath.cs) resolves the active path with a write-probe (`File.Create` + `Delete`) cached for the process lifetime. Priority order: (1) if `BaseDirectory\config.json` already exists, use it (v0.9.2.x → v0.9.3.x migration); (2) if BaseDirectory is writable, use `BaseDirectory\config.json` (portable default); (3) otherwise fall back to `%LOCALAPPDATA%\KoEnVue\config.json`. `koenvue.log` follows the same path resolution.

P5 (`app.manifest asInvoker`, PR-03, v0.9.3.0) intentionally drops the `requireAdministrator` guarantee — the BaseDirectory might be `Program Files` and unwritable. The fallback root is the user's `%LOCALAPPDATA%\KoEnVue\` (created if missing). Complete uninstall is now "delete the exe folder *and* `%LOCALAPPDATA%\KoEnVue\` if you used the fallback path".

`config.json:log_file_path` is sanitized by [`PortablePath.SanitizeLogPath`](../App/Config/PortablePath.cs) — paths outside the two allowed roots (`BaseDirectory` / `%LOCALAPPDATA%\KoEnVue`) are rejected with a `Logger.Warning` and the default `koenvue.log` location is used. This defends against config.json mis-edits like `"log_file_path": "C:\\Windows\\evil.log"` even after Admin token surface is gone.

### Self-triggered reload prevention

`_lastConfigMtime` is updated **after** `Settings.Save()` to prevent `WM_CONFIG_CHANGED` from firing on our own writes.

### STJ source-gen init default workaround

`MergeWithDefaults()` serializes a freshly constructed default `AppConfig` to JSON, overlays the user's loaded keys, then deserializes the result. Required because STJ source generation drops `init` defaults for properties absent from JSON under NativeAOT — if the user's `config.json` omits `Opacity`, the deserialized object has `Opacity == 0.0` instead of `0.85`.

**재귀 머지 (P0 fix, 2026-06-01).** 초기 구현은 *최상위 키만* 순회해 사용자 JSON 에 있는 키는 객체째 통째 교체했다. 이 때문에 사용자가 중첩 객체를 **부분만** 지정하면 — 예: `"event_triggers":{"on_ime_change":false}` — 누락된 형제 필드 (`on_focus_change` true→false), `"advanced"` 부분지정 시 `force_topmost_interval_ms` 5000→0 — 가 STJ source-gen 의 init-default 드롭으로 `default(T)` 가 되어 사용자가 건드리지 않은 설정이 조용히 리셋됐다. 신규 `MergeObjects(writer, defaultObj, userObj)` 재귀 헬퍼가 **양쪽 모두 객체인 키만** 내려가 머지하므로 누락 형제는 기본 객체의 값을 유지한다. 배열·Dictionary 는 의도적으로 통째 교체한다 — 인덱스/키 단위 머지는 사용자가 리스트 (`system_hide_processes` 등) 나 dict (`app_profiles`/`indicator_positions`) 의 항목을 **줄이려는** 의도를 막기 때문. `default_indicator_position` (기본 `null`) 만은 머지 기준 객체가 없어 부분지정 보존이 불가능 — 본질적 한계이며 재귀화로 악화되지는 않는다.

**관용 파싱 (`UserJsonDocOptions`).** 사용자 JSON 파싱은 `JsonDocument.Parse(userJson, new JsonDocumentOptions{ CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })` 로 한다. 소스 생성 컨텍스트 (`AppConfigJsonContext`) 가 `ReadCommentHandling.Skip` + `AllowTrailingCommas` 를 켰음에도, 기본 `JsonDocumentOptions` 는 둘 다 **거부**하므로 주석/트레일링 콤마가 든 **정상** config 가 머지 전처리 단계에서 `JsonException` 으로 던져져 catch 블록의 "손상" 경로로 빠졌다 — 사용자 설정 전체가 디폴트로 묵음 무시되고 mtime 캐시까지 갱신돼 재시도 0. 기본 `AppConfig` 직렬화 산물 (`defaultJson`) 은 주석/콤마가 없으므로 기본 옵션으로 파싱한다.

`EnsureSubObjects()` remains as null safety net for nested records (`EventTriggers`, `Advanced`) whose default construction can also be lost — 완전 `null` 인 경우만 잡으므로 부분지정 형제 보존을 책임지는 `MergeObjects` 와 보완 관계다 (`MergeWithDefaults` 가시성 `internal` = [tests/KoEnVue.Tests/Unit/JsonSettingsMergeTests.cs](../tests/KoEnVue.Tests/Unit/JsonSettingsMergeTests.cs) 머지 매트릭스 노출).

### 프로필 머지 파이프라인

`Settings.MergeProfile` (감지 스레드 핫패스) 는 글로벌 `AppConfig` 를 JSON 으로 직렬화한 결과에 매칭된 프로필 객체의 키만 덮어쓰고 다시 역직렬화하는 JSON-level merge 다. 역직렬화 직후의 객체는 디스크 로드 파이프라인과 동일한 후처리를 거쳐야 한다 — 그렇지 않으면 `ThemePresets.Apply` 가 호출 안 돼 프리셋 색상이 머지된 인스턴스에 박히지 않고, `"poll_interval_ms":999999` 같은 범위 외 값이 clamp 안 된 채 통과하며, `Theme.Custom` 백업/복원 로직도 작동 안 한다.

`AppSettingsManager.ApplyMergedProfilePipeline(AppConfig)` 정적 헬퍼가 단일 진입점:

1. **`EnsureSubObjects`** — null 보정 (`AppProfiles` / `Advanced` 등)
2. **`Migrate`** — App 레벨 override 가 없는 identity. 추후 profile 단위 schema 진화가 필요해지면 여기에 instance 경유로 hookup
3. **`Validate`** (`Settings.Validate`) — 범위 클램핑 + enum 검증 + `advanced.overlay_class_name` 폴백
4. **`ApplyTheme`** (`ThemePresets.Apply`) — 프리셋 색상 오버레이 + Custom 백업/복원

순서는 디스크 로드 경로(`JsonSettingsManager.Load`) 와 동일. 차이점은 `PostDeserializeFixup` (indicator_positions Dictionary 수동 재조립) 이 빠진다는 것 — 프로필은 글로벌의 indicator_positions 를 그대로 상속하며 머지 전 globalJson 캐시가 글로벌 인스턴스 단위로 1회 직렬화된다(같은 글로벌 → 캐시 hit). LRU 캐시는 50엔트리 상한, 글로벌 인스턴스 교체 / `WM_SETTINGCHANGE` · `WM_THEMECHANGED` 수신 시 클리어.

**메인 스레드 — `ResolveCurrent()` 헬퍼로 렌더 경로까지 도달 (PR-13)**. 감지 스레드의 `resolved` AppConfig 는 처음에 `TryHandleFilter` / `TrackWindowMove` / `ImeStatus.Detect` 3개 분기에만 사용됐고 메인 스레드로 전달되는 메시지(`WM_FOCUS_CHANGED` / `WM_IME_STATE_CHANGED` / `WM_POSITION_UPDATED`) 는 `resolved` 를 페이로드로 싣지 않았다 — 시각 필드 override 가 렌더링까지 도달하지 않는 미배선 상태. PR-13 에서 메인 스레드에 `ResolveCurrent()` 정적 헬퍼를 도입해 같은 `Settings.ResolveForApp(_config, _lastForegroundHwnd)` 를 재호출하는 방식으로 해결 — LRU 캐시가 같은 프로세스명 키에서 즉시 hit 하므로 메인 스레드 핫패스 비용이 무시할 수 있다. `Animation.TriggerShow` / `Overlay.Show` / `Overlay.UpdateColor` 시그니처가 `AppConfig` 를 명시 인자로 받도록 확장되어, `HandleImeStateChanged` / `HandleFocusChanged` / `HandlePositionUpdated` / `HandleConfigChanged` / `HandleActivateRequest` / `HandleCapsLockTimer` / `HandleDisplayChange` / `HandleSettingChange` / `ApplyUserHiddenTransition` / `HandleMenuCommand` updateConfig lambda / `HandleOverlayDragEnd` 의 모든 렌더 호출(18개) 이 `ResolveCurrent()` 결과를 사용한다. 추가로 `HandleImeStateChanged` / `HandleFocusChanged` 의 `DisplayMode` + `EventTriggers` 평가도 `resolved` 기반으로 전환 — `display_mode` / `event_triggers` 의 per-app override 도 동시 활성. `Overlay._config` 필드는 렌더 경로에서 의존이 제거되었고 `HandleConfigChanged` 시점의 캐시 재빌드 + 드래그 경로(`HandleMoving`) + 기본 위치 계산(`GetDefaultPosition`) 의 글로벌-only 경로에만 잔존. 메시지 페이로드 마샬링 대신 메인 스레드 재호출을 택한 이유는 변경 표면 최소화 + race window 가 다음 틱에서 자연 수렴 + LRU 캐시 hit 비용 무시. 단점은 메인 스레드 `_lastForegroundHwnd` 와 감지 스레드 `state.LastHwndForeground` 가 일시적으로 다를 수 있다는 점이지만 80ms 폴링이 즉시 수렴시킨다.

### Window class name validation

`Settings.Validate.ValidateAdvanced` 가 `AppConfig.Advanced.OverlayClassName` 을 영문/숫자/언더스코어 + 길이 1-255 로 검증해 위반 시 `"KoEnVueOverlay"` 기본값으로 폴백한다. 이 문자열은 `Program.Bootstrap.RegisterWindowClasses` / `CreateOverlayWindow` 의 `RegisterClassExW` / `CreateWindowExW` 에 그대로 흘러가므로 비정상 값(빈 문자열·과도한 길이·제어문자·공백/슬래시 등 ASCII 외 문자) 이 들어오면 등록 자체가 실패해 부팅이 침묵 종료된다. 사용자가 `config.json` 을 손으로 편집해도 부팅 경로가 끊기지 않도록 단일 폴백 경로로 흡수. 검증 실패 시 `Logger.Warning` 1회만 남기고 정상 부팅을 보장.

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

### Tray click actions (`tray_click_action`) and `UserHidden` persistence

`HandleTrayCallback` in [Program.cs](../Program.cs) routes `WM_LBUTTONUP` through a switch on `_config.TrayClickAction`:

- `Toggle` (default) → `HandleTrayToggle()` (see below).
- `Settings` → `Tray.OpenConfigFile()` — `ShellExecuteW(0, "open", "notepad.exe", "\"{path}\"", ...)`. Notepad is hard-coded instead of dispatching through the `.json` default handler because typical end-user machines have no `.json` association and the default path triggers Windows' "choose an app" dialog or silent no-op. Notepad ships on every Windows SKU, displays UTF-8 JSON correctly, and its save is picked up by the mtime poller so hot reload still applies after the user edits. `lpParameters` wraps the path in quotes to survive spaces (e.g. `C:\Program Files\...`).
- `None` — no-op.

`HandleTrayToggle` flips `AppConfig.UserHidden`, calls `Settings.Save(_config)` immediately (so the state survives restart), rebuilds the tray icon via `Tray.UpdateState` (to add/remove the strikethrough overlay), and delegates overlay transition to the shared `ApplyUserHiddenTransition(wasHidden, isHidden)` helper:

- `UserHidden: false → true` — invokes `HideOverlay()` (`Animation.TriggerHide(forceHidden: true)` + sets `_indicatorVisible = false`).
- `UserHidden: true → false` — sets `_indicatorVisible = true` + calls `Animation.TriggerShow` with the current foreground app's resolved position so the indicator reappears immediately.

The menu path (`IDM_USER_HIDDEN` → `updateConfig(config with { UserHidden = !config.UserHidden })`) reaches the same helper: the `HandleMenuCommand` lambda in [Program.cs](../Program.cs) captures `wasHidden = _config.UserHidden` before applying `newConfig`, then calls `ApplyUserHiddenTransition` when the bit actually flipped (otherwise falls through to the existing `_indicatorVisible`-based config-changed branch). This ensures the right-click "인디케이터 숨김" toggle produces identical overlay/tray-icon behavior as the left-click toggle, even when `tray_click_action` has been set to `"settings"` or `"none"` (in which case the right-click menu is the only GUI path back from `user_hidden = true`).

Five event handlers gate on `_config.UserHidden` to prevent detection-thread events from re-showing a user-hidden indicator: `HandleImeStateChanged`, `HandleFocusChanged`, `HandlePositionUpdated`, `HandleConfigChanged` (only skips the `TriggerShow` branch — tray icon still rebuilds so a hot-reloaded strikethrough change renders), and `HandleActivateRequest`. The detection thread itself does **not** read `UserHidden` — `_indicatorVisible = false` is sufficient to suppress the `TriggerShow` path in main-thread handlers, and cost of the `_config.UserHidden` check is trivial compared to the `WindowProcessInfo.GetProcessName` P/Invoke chain in the detection tick.

Reset paths: (1) right-click tray → "인디케이터 숨김" menu toggle, (2) left-click tray when `tray_click_action = "toggle"`, (3) delete `config.json` (STJ's default unmapped-member handling reinstates `user_hidden = false`), (4) hand-edit the field — mtime polling reapplies the new config at the next detection event. Persisting the state in config deliberately makes it "sticky" — restart/resume preserves the user's explicit hide intent.

### Tray icon strikethrough (`TrayIcon.DrawStrikeThrough`)

Drawn on top of the existing caret+dot when `config.UserHidden == true`. A single thick horizontal rectangle centered at `iconH / 2`, spanning X from `1` to `iconW - 1` (1-px edge inset on both sides). Thickness `Math.Max(iconH / 4, 3)` — 4 px at 16-px icons, 5 px at 20-px icons. `CreateIcon` resolves a state-keyed `fgHex` (`HangulFg` / `EnglishFg` / `NonKoreanFg`) via `ColorHelper.HexToColorRef` alongside `bgHex` and passes the resulting `fgColor` to both `DrawCaretDot` and `DrawStrikeThrough` — the strikethrough thus always matches the caret+dot color and inherits the theme-author's chosen contrast against the background. `try/finally` GDI cleanup pattern identical between the two draw methods. v0.9.2.4 shipped `DrawDoubleStrikeThrough` (two horizontal lines at `iconH * 1/3` and `iconH * 2/3`, thickness `iconH / 6`) but at 16-px tray size the combined vertical coverage (~6 px of 16 px) overdrew the caret+dot silhouette to the point of visual collapse; v0.9.2.5 traded the two-line symbolism for a single bolder cross-cut that leaves the caret+dot intact above and below. A hardcoded white overlay (same `WhiteColorRef` both draw methods used before) was dropped in the same pass because the `pastel` preset's light backgrounds (`#86EFAC` / `#FDE68A` / `#C4B5FD`) rendered the white strike as low-contrast noise; the theme's own `Fg` (e.g. `#14532D` dark green for pastel-Hangul) is dark on pastel and white on dark presets, so delegating to Fg solves both ends without branching. Gray-fill alternatives were rejected because `NonKoreanBg` defaults (`#6B7280` custom, `#9CA3AF` / `#D1D5DB` / `#374151` presets) already land in gray and would have collided semantically with the non-Korean IME state; pure RGB inversion was rejected because its contrast collapses on mid-gray backgrounds (inversion of `#6B7280` is `#948D7F`, both near L=127).

### Startup task registration & path/delay auto-sync

Registration uses `schtasks /create /xml` with an embedded `<LogonTrigger><Delay>PT15S</Delay></LogonTrigger>` (constant `Tray.StartupTaskDelay`). The 15-second logon delay avoids the "Shell_NotifyIconW NIM_ADD failed" race at boot where the task fires before `explorer.exe` has initialized the tray — the `NIM_ADD` retry timer still recovers if the delay is ever absent, but the delay prevents the warn log line from appearing on every boot. `RegisterStartupTaskWithXml` writes the XML to `%TEMP%\koenvue-task-{pid}.xml` as UTF-16 LE with BOM (the encoding schtasks expects) and deletes it in `finally`.

`Tray.SyncStartupPathAsync()` runs on a background thread immediately after `Tray.Initialize` in `Program.cs`. It:

1. Invokes `schtasks.exe /query /tn ... /xml ONE`
2. Extracts the `<Command>` and `<Delay>` elements with plain string `IndexOf` (no `XmlDocument` — NativeAOT-friendly) via `ExtractTagFromXml`. Manually unescapes `&amp;` / `&quot;` / etc. for `Command` (Delay is raw ISO 8601 so no unescape).
3. Normalizes both paths via `Path.GetFullPath` + `OrdinalIgnoreCase`
4. Re-registers the task via XML if either the stored path differs from `Environment.ProcessPath`, OR the stored `<Delay>` is missing/different from `PT15S` (this also migrates older `/tr`-registered tasks to the new XML form on the next launch)

Handles the "user moved the exe" case: the first boot after a move still misses because Task Scheduler launches the old path, but on the next manual launch the sync runs and subsequent boots pick up the corrected path. `QueryRegisteredTask` wraps `Process.Start` in try/catch so schtasks being absent or non-zero exit is silently ignored.

### Tray menu structure

```
KoEnVue v0.9.2.5 — GitHub                       ← always-visible header (MF_DEFAULT bold)
   or KoEnVue v0.9.2.5 → v0.9.3.0 — 다운로드    ← label flips when UpdateChecker finds update
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
☐ 인디케이터 숨김
───
상세 설정...
───
종료
```

Menu IDs live in [Tray.cs](../App/UI/Tray.cs) as `private const int IDM_*`. The header uses `IDM_HOMEPAGE = 4010` with `MF_STRING | MF_DEFAULT` **plus** an explicit `User32.SetMenuDefaultItem(hMenu, (uint)IDM_HOMEPAGE, 0)` call right after `AppendMenuW` — the flag alone sets the internal default bit but does not reliably trigger bold rendering on Windows 11; `SetMenuDefaultItem` is the canonical Win32 API for this and MSDN explicitly recommends it (the `ModifyMenu` page directs callers to it, and the `SetMenuDefaultItem` page guarantees "displayed in bold type"). Only one item per popup can be the default — verified once via grep that no other `MF_DEFAULT` exists in the codebase. The `_pendingUpdate is not null` branch swaps the label, and the click handler dispatches to `OpenUpdatePage` or `OpenHomepage` accordingly. The `4008` slot is intentionally vacant (formerly `IDM_UPDATE_DOWNLOAD`, removed in v0.9.2.6 with the header unification). Position mode submenu uses `IDM_POSITION_FIXED = 3301` / `IDM_POSITION_WINDOW = 3302` with `CheckMenuRadioItem`. `IDM_USER_HIDDEN = 4009` renders as `MF_CHECKED` when `config.UserHidden == true` — its handler calls `updateConfig(config with { UserHidden = !config.UserHidden })`, and the `Program.HandleMenuCommand` lambda routes the resulting transition through `ApplyUserHiddenTransition` (shared with `HandleTrayToggle`).

### Quick opacity presets (`ApplyQuickOpacity`)

The three opacity presets (진하게/보통/연하게) apply mode-aware config changes via `Tray.ApplyQuickOpacity`. In Always mode, the preset value is written to `ActiveOpacity` and `IdleOpacity` is proportionally scaled (ratio preserved). In OnEvent mode, only `Opacity` is written. The radio check compares against `ActiveOpacity` in Always mode, `Opacity` in OnEvent mode.

### Three-toggle duplication with settings dialog

`SnapToWindows`, `AnimationEnabled`, and `ChangeHighlight` are toggleable from both the tray menu and `SettingsDialog`. The settings dialog drops these three rows to avoid duplication. `SlideAnimation` is deliberately **not** added to the tray because usage frequency is low and keeping the menu short is a UX goal.

The duplication is kept as vertical copy rather than extracted to a helper because `HandleMenuCommand`'s per-field `with`-expression getters/setters can't be mechanically abstracted without a delegate map or reflection (conflicts with NativeAOT + P1).

---

## Dialogs

All three dialogs (`CleanupDialog`, `ScaleInputDialog`, `SettingsDialog`) share the same modal infrastructure:

- **`DialogShell.Run(...)`** — 모달 라이프사이클 통합 진입점. reentry guard (`ModalDialogLoop.IsActive` ?) → `GetCursorPos` → `MonitorFromPoint` → DPI/font/class 등록 → `CalculateDialogPosition` → `CreateWindowExW` outer dialog → `ShowWindow` → 선택적 `SetForegroundWindow` → `onAfterShow` → `ModalDialogLoop.Run` → `DestroyWindow` (try/finally) 의 11단계를 단일 메서드로 흡수. 각 다이얼로그는 `measureDlgHeight` 콜백 (DPI-스케일 다이얼로그 높이) + `useCursorAnchor` (Scale 만 true) + `bringToForeground` + `buildChildren` (자식 컨트롤 생성) + `onAfterShow` (Scale 의 SetFocus/EM_SETSEL) 만 책임지고, WndProc 와 다이얼로그-고유 정적 상태 (`_hwndDialog`, `_hwndViewport`, `_dlgResult`, `_dlgClosed` 등) 는 호출자가 소유. `DialogShellMetrics` (record struct: DPI/non-client/pad/dlgWidth) 와 `DialogShellContext` (sealed: + HwndDialog/HFont/DlgHeight + ClientW/ClientH 파생 + `Scale(int logical)` 메서드) 두 단계로 정보 전달. `HandleStandardCommands(wmCommandId, idOk, idCancel, ref result, ref closed, tryCommit?)` 헬퍼는 IDOK/IDCANCEL + 다이얼로그-고유 OK/Cancel ID 동시 수락 패턴을 흡수 — IsDialogMessageW 가 Enter→IDOK / ESC→IDCANCEL 변환해 보내는 경로와 사용자 클릭 경로 양쪽 동일 분기
- **`ModalDialogLoop.Run(hwndDialog, hwndOwner, ref isClosed)`** — DialogShell 의 내부 모달 루프. Core helper for the `EnableWindow(owner, false) + while(GetMessageW... IsDialogMessageW(...)) { Translate/Dispatch } + EnableWindow(owner, true)` boilerplate. The `ref bool isClosedFlag` lets each dialog's WndProc signal close from inside `WM_COMMAND`/`WM_CLOSE` without the loop helper knowing the close semantics. When the nested loop consumes `WM_QUIT` (e.g., tray Exit while a dialog is open), it re-posts `PostQuitMessage` so the outer message loop also terminates
- **`ModalDialogLoop.RunExternal(hwndSentinel, action)`** — `IsActive` 가드만 씌우는 경량 변형. `User32.MessageBoxW` 처럼 Win32 가 자체 메시지 루프를 돌려 `Run` 을 쓸 수 없는 외부 모달 구간에 사용한다 (현재 `Tray.ShowPositionError` / `Tray.CleanupPositions` 의 빈 목록 알림 / TryCommit 검증 실패 메시지박스). 메시지 펌프 · `EnableWindow` 는 건드리지 않고 감지 스레드의 폴링 사이드-이펙트만 차단한다. 기존 활성 모달이 있으면 스택처럼 이전 값을 보관 후 `finally` 에서 복원
- **`Win32DialogHelper.CreateDialogFont(dpiY) → SafeFontHandle`** — 9 pt 맑은 고딕 with `SafeFontHandle` RAII. DialogShell 의 `using var hFont` 스코프가 모달 루프 + DestroyWindow 구간 전체를 덮어 HFONT 가 자식 컨트롤 살아있는 동안 해제되지 않음을 보장
- **`Win32DialogHelper.CalculateDialogPosition(hMonitor, w, h, anchor?)`** — `null` anchor = center in work area (Cleanup/Settings pattern); `POINT` anchor = top-left at that point (ScaleInput cursor-anchored pattern). DialogShell 의 `useCursorAnchor` bool 이 두 패턴을 선택. work area 는 `GetMonitorInfoW` 를 직접 호출하지 않고 `DpiHelper.GetWorkArea(hMonitor)` 를 재사용 (감사 High ③, 2026-06-01). 이유: 원래 코드는 `GetMonitorInfoW` 반환값을 무시한 채 `MONITORINFOEXW.rcWork` 를 그대로 썼는데, `hMonitor` 가 무효이거나 조회가 실패하면 `mi` 가 `default` 인 채 `rcWork` 가 `(0,0,0,0)` → 중앙 계산식이 음수 폭 절반을 내고 clamp 가 모두 0 으로 떨어져 **다이얼로그가 화면 좌상단 (0,0) 에 박히는** 결함. `GetWorkArea` 는 동일 실패 시 primary 모니터 work area 로 1회 폴백하는 로직을 이미 내장하므로, 폴백을 여기 또 작성하는 대신 그 단일 구현을 재사용 (P4). `Win32DialogHelper` 에 `using KoEnVue.Core.Dpi;` 추가 — 같은 Core 레이어 내 의존이라 P6 무관
- **`[UnmanagedCallersOnly]` WndProc function pointers** private to each file (no NativeAOT export name collision). DialogShell 의 `delegate* unmanaged<...>` 파라미터로 셸에 전달
- **a11y baseline (PR-07 H4-b)**: 모든 입력 컨트롤에 `WS_TABSTOP`, 섹션/그룹 시작 컨트롤에 `WS_GROUP` (화살표 키 그룹 경계). STATIC 라벨이 입력 컨트롤 직전 z-order 에 배치되어 UIA `LabeledBy` 자동 연결
- **Tab/Enter/ESC** routed through `IsDialogMessageW`
- **Detection-thread gate**: `DetectionLoop` checks `ModalDialogLoop.IsActive` **and** `GetWindowThreadProcessId(hwndForeground) == Environment.ProcessId` together — suppresses polling side effects only when a modal is up **and** the foreground belongs to our own process. External-app focus while a dialog is open (Alt+Tab) falls through so the indicator renders on that app. See [Detection → Modal dialog gate](#modal-dialog-gate)

### CleanupDialog

Position-mode-agnostic: regardless of the current `position_mode` setting, shows the union of `indicator_positions` (Fixed) and `indicator_positions_relative` (Window) keys. Deletion removes from both dicts simultaneously, so switching modes later won't resurrect deleted entries. Running processes are shown with a "(실행 중)" / "(running)" suffix. Full select/deselect toggle. "저장된 위치 기록이 없습니다" message when empty. When items exceed `DlgMaxVisibleItems` (15), a scrollable viewport child window with `WS_VSCROLL` + mouse wheel support is used — same pattern as `SettingsDialog.Scroll.cs`.

### ScaleInputDialog

Custom scale entry for values outside the 1.0–5.0 integer presets. Spawned at cursor position via `CalculateDialogPosition(POINT anchor)`. EDIT pre-filled via `initialValue.ToString("0.#")` (`"2"` for 2.0, `"2.3"` for 2.3).

Parsing uses `double.TryParse` + `CultureInfo.InvariantCulture`, so `"2.3"` works regardless of OS locale. Validation failure shows a MessageBox then refocuses EDIT with `EM_SETSEL(0, -1)` (select all) for easy re-entry.

`ScaleDlgProc` accepts both `IDC_SCALE_OK || IDOK` and `IDC_SCALE_CANCEL || IDCANCEL` because `IsDialogMessageW` synthesizes `IDOK`/`IDCANCEL` for Enter/ESC.

### SettingsDialog

14 sections of settings (PR-21 — 일반 / 메인 인디 / 커서 인디 3 대분류 재배치, 정확한 필드 수는 [SettingsDialog.Fields.cs](../App/UI/Dialogs/SettingsDialog.Fields.cs) 의 `BuildRowDefs` 참조). Split across 3 partial class files:

- **`SettingsDialog.cs`** (modal state, `Show`, `TryCommit`, dialog WndProc)
- **`SettingsDialog.Fields.cs`** (`FieldType` enum, `FieldDef`/`RowDef` records, `BuildRowDefs` 13-section spec, 6 factory methods: `Bool`/`Int`/`Dbl`/`Str`/`ColorField`/`Combo`)
- **`SettingsDialog.Scroll.cs`** (scroll state, `SetupScrollbar`, `ScrollTo`, `ScrollFieldIntoView`, `ResolveVScrollPosition`, viewport WndProc)

`partial class` shares all static state at compile time. No call-site changes — `SettingsDialog.Show(hwndMain, config, updateConfig)` is the same public entry point.

**Scroll implementation**: `ScrollTo` 는 스크롤 델타 `dy = _scrollPos - newPos` 를 계산한 뒤 `SetScrollInfo` 로 썸 위치를 갱신하고, `ScrollWindowEx(viewport, 0, dy, ..., SW_SCROLLCHILDREN | SW_INVALIDATE | SW_ERASE)` 한 번으로 모든 자식을 OS 가 BitBlt 로 이동시킨다. 노출된 띠 영역만 무효화 + 배경 지움 처리되므로, 기존 "N 개 자식에 대한 `SetWindowPos` 루프 + 전체 `InvalidateRect(viewport, null, true)`" 방식 대비 휠 틱당 작업량이 O(N) → O(1) 로 줄어 휠 스크롤 반응성이 크게 향상된다. 뷰포트는 `WS_CLIPCHILDREN` + `WS_EX_COMPOSITED` 조합으로 DWM off-screen 합성을 사용해 스크롤 중 플리커도 없다. 자식 윈도우 크기는 `ScrollWindowEx` 가 보존하므로 COMBOBOX 의 `rowH + ComboDropExtra = 220` 드롭다운 높이는 영향 없음.

`ScrollTo` / `ResolveVScrollPosition` / `CalculateWheelScrollPos` 3조합은 `CleanupDialog` 와 동일해 [Core/Windowing/ScrollableDialogHelper](../Core/Windowing/ScrollableDialogHelper.cs) 로 추출했다. 호출부는 expression-bodied 1-라이너로 축약되고 `WheelLineStep = 3` 상수도 헬퍼에서 소유 (P4 공통모듈 규칙).

**Validation failure handling**: `TryCommit` shows a MessageBox, calls `ScrollFieldIntoView` to bring the offending field into view, refocuses the control, and for EDITs selects all text via `EM_SETSEL`.

**`controlColW` dynamic cap**: capped to `innerContentW - labelColW - colGap` so input boxes never encroach on the vertical scrollbar reserve area — a fixed `controlColW` would get clipped under the scrollbar at the default dialog width. The cap 결과는 `Math.Max(1, ...)` 로 한 번 더 감싸 최소 1px 을 보장한다 — 고배율 DPI + 매우 좁은 다이얼로그 조합에서 우변이 음수가 되면 컨트롤 생성 시 GDI 가 혼란 상태에 빠질 수 있기 때문이다.

**Excludes**: fields already toggleable from the tray menu (opacity, indicator_scale, default_indicator_position, snap_to_windows, animation_enabled, change_highlight, indicator_positions, tray_enabled) and "시작 프로그램 등록" (schtasks 기반 — config 필드 아님), complex collection fields (app_profiles, app_filter_list, system_hide_classes, system_hide_processes), and internal-only fields (overlay_class_name).

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

Network error, HTTP non-200, empty body, draft/prerelease skip, or `current >= latest` version compare — all funnel to `Logger.Debug` and nothing else. The user never sees a "couldn't reach GitHub" popup because that would be intrusive for a passive indicator app. **예외**: HTTP 200 응답을 받은 뒤 JSON 파싱에서 실패하는 경로 (`JsonException` / `NotSupportedException` / `ArgumentException`) 는 `Logger.Warning` 으로 승격 — 전송 자체는 성공했으므로 일시적 네트워크 이슈가 아닌 GitHub API 스키마 변동(응답 필드 rename, 새 릴리즈 포맷 도입 등) 가능성이 높고, Debug 로 묻히면 업데이트 체크가 사일런트하게 무력화된 상태를 사용자/개발자가 인지할 방법이 없다.

`HttpClientLite.GetString` returns `null` on any failure. `UpdateChecker.RunCheck`'s catch is narrowed to `JsonException or NotSupportedException or ArgumentException` so logic bugs in version comparison propagate; `HttpClientLite.GetString` keeps a wide `catch (Exception)` because WinHTTP marshalling edge cases can't all be enumerated (single P/Invoke-chain try body).

### Version comparison

`UpdateChecker.NormalizeVersion` strips optional `v`/`V` prefix and semver prerelease/build suffixes (`-beta.1`, `+build.42`) via `ReadOnlySpan<char>`, then `System.Version.TryParse` parses the `N.N.N[.N]` portion. `IsNewer(current, latest)` returns `latestV > currentV`.

Semver prerelease ordering (`1.0.0-alpha < 1.0.0`) is intentionally ignored — combined with the `release.Prerelease || release.Draft → skip` filter, prereleases never trigger notifications. This is the right behavior: users on stable releases should not be pinged to upgrade to a beta.

### Thread marshaling

`UpdateChecker.CheckInBackground` spawns a `new Thread { IsBackground = true, Name = "UpdateChecker" }` that calls into `HttpClientLite` (blocking sync I/O). On success, the background thread invokes the caller's `onUpdateFound(UpdateInfo)` callback, which lives in `Program.OnUpdateCheckResult`. That method writes to `Program._pendingUpdate` (a `private static volatile UpdateInfo?` field) and calls `User32.PostMessageW(hwndMain, AppMessages.WM_APP_UPDATE_FOUND, 0, 0)`.

The main thread's WndProc picks up the message and calls `HandleUpdateFound` → `Tray.OnUpdateFound(info)`. Reusing the existing `WM_APP + N` pattern keeps the cross-thread signal path consistent with the detection thread.

### Tray menu injection

`Tray.OnUpdateFound` stores the `UpdateInfo` in a `private static UpdateInfo? _pendingUpdate` field (non-volatile because main thread is the sole accessor after the `WM_APP_UPDATE_FOUND` message crossed the thread boundary).

`Tray.ShowMenu` always emits a header line at the very top (`IDM_HOMEPAGE = 4010`, `MF_STRING | MF_DEFAULT`, separator below). The label has two modes:

- **No update pending**: `KoEnVue v{DefaultConfig.AppVersion} — GitHub` — click opens the repo root via `OpenHomepage()` (URL composed from `DefaultConfig.UpdateRepoOwner` / `UpdateRepoName` compile-time constants, no prefix check needed).
- **Update pending** (`_pendingUpdate is not null`): `KoEnVue v{cur} → {newTag} — {I18n.MenuDownload}` — click opens the release page via `OpenUpdatePage()` (uses `info.HtmlUrl` from GitHub API, validated against `https://github.com/{owner}/{name}/` prefix to defend against scheme injection through MITM/account-compromise tampering).

`MF_DEFAULT` (only one allowed per popup) makes the system render this entry in bold, giving "menu header" structural emphasis without owner-draw. The separator below it visually separates the header from the rest of the menu. The `_pendingUpdate.Version` field carries the GitHub release `tag_name` directly (e.g., `"v1.0.1"`), so the label uses `→ {tag}` (not `→ v{tag}`) to avoid double-`v`.

Both `OpenHomepage` and `OpenUpdatePage` call `Shell32.ShellExecuteW(0, "open", url, null, null, SW_SHOWNORMAL)`. Return ≤ 32 is logged as `Logger.Warning` (per `ShellExecuteW` docs, ≤ 32 means launch failure).

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

Every link in the chain (`WinHttpSetTimeouts` inheritance, `SafeWinHttpHandle` RAII, JSON source gen, `NormalizeVersion` `v` prefix handling, 4-part `Version.TryParse`, volatile `_pendingUpdate` cross-thread hop, tray menu header label switch, `ShellExecuteW` browser launch) confirmed operational.

### Header label race window (acknowledged, unmitigated)

While the tray popup is showing, `TrackPopupMenu` runs an internal message pump — `WM_APP_UPDATE_FOUND` can arrive mid-display, flipping `_pendingUpdate` from null to non-null. The label is already drawn (won't change visually), but the click handler reads `_pendingUpdate` at click time and dispatches based on the *current* value. Result: user clicked the "GitHub" label expecting `OpenHomepage`, gets `OpenUpdatePage`. UpdateChecker fires only once at boot, so the window is sub-second on cold launch only — practical probability ~0. Capturing a snapshot at menu-build time (e.g., into `MENUITEMINFO.dwItemData`) would close it but adds GCHandle / pinned-string lifetime concerns under NativeAOT for a near-zero-probability outcome that still ends at a legitimate GitHub URL. Decision: accept.

---

## Multi-instance and tray recovery

### Mutex ordering — why acquire before cleanup

`Program.MainImpl` 초기화 순서에서 `TryAcquireMutex` 가 `CleanupPreviousTrayIcon` 보다 **먼저** 실행되어야 한다. 두 함수 모두 `DefaultConfig.AppGuid` 와 연관된 상태(Mutex 이름, 트레이 아이콘 GUID)에 작용하므로 순서가 역전되면 두 번째 인스턴스가 이미 실행 중인 정상 인스턴스의 트레이 아이콘을 `NIM_DELETE` 로 지워버린 뒤 Mutex 실패로 종료하는 부작용이 발생한다.

Mutex 획득 성공은 "이전 인스턴스가 존재하지 않는다" 는 보장이므로(크래시 시 OS 가 Mutex 를 자동 해제), 이 조건 하에서만 Cleanup 이 안전하게 "이전 크래시의 유령 아이콘을 정리" 한다는 의미를 가진다.

### Self-relaunch race blocking — KOENVUE_RELAUNCH_PARENT_PID + WaitForExit

PR-15 의 부팅 시점 self-elevation 경로는 "원본이 mutex 안 잡은 상태에서 자식 spawn → 자식이 깨끗하게 createdNew=true" 흐름이라 race 0. 하지만 **트레이 메뉴 "관리자 권한으로 실행" 토글 재시작 경로** ([`Tray.cs`](../App/UI/Tray.cs) `IDM_ADMIN_ELEVATION` YES 분기) 는 원본이 mutex + trayicon GUID + WTS notification + IME hook + log file lock 등 모든 리소스를 보유한 정상 실행 상태에서 자식을 spawn 한다. 원본의 `OnProcessExit` cleanup 시퀀스 (PR-19 step 0~7) 가 `_mutex?.Dispose()` 까지 수백 ms 소요되어 자식이 그 사이 mutex `createdNew=false` + 부가 리소스 race 에 빠진다 — `Logger.Initialize` 도달 전 종료라 `koenvue.log` 의 starting 라인이 없고 `koenvue_crash.txt` 만 남는 진단 패턴.

fix (PR-15 후속, 2026-05-28) — **자식이 mutex 시도 전 부모 종료를 명시 wait** 하는 환경변수 패턴:

- `KOENVUE_RELAUNCH_PARENT_PID=<PID>` — `ShellExecuteW` 의 환경변수 상속을 이용해 자식 (또는 손자) 에 부모 PID 전달. `KOENVUE_ELEVATED` 와 동일 명명 prefix
- [`Program.MainImpl`](../Program.cs) 의 step 0b-1 (`Settings.Load` 직후, `TryRelaunchAsAdmin` 직전) 에서 `AdminElevation.WaitForRelaunchParentIfAny()` 호출 — 환경변수에 PID 있으면 `Process.GetProcessById(N).WaitForExit(5000)` + 환경변수 클리어. 정상 부팅 (환경변수 없음) 에는 첫 줄에서 noop
- 호출 site 3 곳:
  - `Tray.HandleMenuCommand` `IDM_ADMIN_ELEVATION` YES 분기의 `ClearReentryGuard` 직후 + `UriLauncher.Open` 직전 → `AdminElevation.SetRelaunchParentPidForTrayRestart()`
  - `AdminElevation.TryRelaunchAsAdmin` 의 `ShellExecuteW("runas")` 직전 → 환경변수 재설정 (손자 generation 에도 정확한 부모 PID = 자식 PID 전달)
  - `Program.MainImpl` step 0b-1 → `WaitForRelaunchParentIfAny` consume

catch narrowing 4 타입 (`ArgumentException or InvalidOperationException or Win32Exception or SystemException`) — `Process.GetProcessById` / `WaitForExit` 의 가능 예외 흡수, 모두 "wait 불가, 진행" 동일 처리. PID 재사용 paranoid (부모 종료 후 OS 가 같은 PID 재할당 → `GetProcessById` 가 새 프로세스 wait) 와 부모 hang 두 케이스는 5초 timeout 으로 영구 hang 회피 — race window 0 에 매우 가깝지만 절대 0 보장은 timeout 무한 대기만 가능하므로 정직하게 timeout + Log 명시.

본 패턴이 차단하는 race 5종 (mutex / trayicon GUID NIM_ADD/DELETE / WTS notification / IME hook 중복 등록 / log file sharing violation) 은 모두 부모 프로세스 객체가 살아있는 한 OS 가 핸들/리소스 ownership 을 유지하기 때문에, **부모 종료 wait 한 메커니즘이 모두를 한 번에 해결**. Option A (원본 spawn 직전 명시 `_mutex?.Dispose()`) 는 mutex 외 race 미해결, Option C (Mutex Wait + Retry) 는 KoEnVue 의 단일 인스턴스 정책과 충돌 — Option B (본 패턴) 가 최적. 자세한 시계열 진단 + 가설 비교 + 검증 매트릭스: [dev-notes/2026-05-28-pr-15-relaunch-race.md](dev-notes/2026-05-28-pr-15-relaunch-race.md).

### Admin elevation toggle — 4-case 분기 매트릭스 (PR-15 후속 fix #2)

위 self-relaunch race 차단이 자식 spawn 자체의 race 를 차단한 다음 단계 — **`IDM_ADMIN_ELEVATION` 토글의 4 출발/도착 케이스가 각자 다른 동작** 을 가지며 그 중 한 케이스 (admin → 일반 권한 down-grade) 는 Windows token 모델 한계로 자동 spawn 자체가 불가하다. [`Shell32.ShellExecuteW("open")`](../App/UI/Tray.cs) 가 부모 토큰을 상속하므로 admin 부모는 일반 권한 자식을 spawn 할 수 없음 — 권한 강등은 사용자 동의 없이 발생할 수 없다는 OS 보안 정책 (UAC 는 반대 방향 medium → high 전용).

[`Tray.HandleMenuCommand`](../App/UI/Tray.cs) 의 `IDM_ADMIN_ELEVATION` 핸들러는 `updateConfig(newAdminConfig)` + `StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig)` 직후 4-case 분기:

| # | 출발 | 도착 | `newAdminConfig.AdminElevation` | `AdminElevation.IsCurrentProcessElevated()` | `isDowngrade` | 동작 |
|---|------|------|---|---|---|------|
| 1 | 일반 | admin | `true` | `false` | `false` | YESNO → YES 시 spawn → 자식이 PR-15 self-check 로 UAC 1회 (기존 흐름) |
| 2 | 일반 | 일반 | `false` | `false` | `false` | YESNO → YES 시 spawn → 자식이 일반 권한 (기존 흐름, 일반 부모 토큰 상속) |
| 3 | admin | admin | `true` | `true` | `false` | YESNO → YES 시 spawn → 자식이 admin 토큰 상속 (기존 흐름, 의도 일치) |
| 4 | admin | 일반 | `false` | `true` | **`true`** | **MB_OK 안내만 + 자동 spawn 안 함** (신규) — 사용자 트레이 "종료" + 수동 재실행 필요 |

case 4 만 신규 분기 (`isDowngrade = !newAdminConfig.AdminElevation && AdminElevation.IsCurrentProcessElevated()` → `true` 면 `User32.MessageBoxW(I18n.AdminElevationDowngradeNotice, Win32Constants.MB_OK) + break`). case 1/2/3 의 기존 자동 spawn 흐름 (`ClearReentryGuard` → `SetRelaunchParentPidForTrayRestart` → `UriLauncher.Open` → `PostMessageW(WM_CLOSE)`) 은 한 줄도 변경 안 됨.

**보완 동작**: 분기 직전 이미 `updateConfig` + `StartupTaskManager.ReregisterIfAdminChanged` 가 실행됨 — config.json 의 `admin_elevation: false` 즉시 저장 + schtasks `<RunLevel>LeastPrivilege</RunLevel>` 재등록. case 4 의 사용자 수동 종료/재실행은 **"지금 즉시 적용" 만의 비용** — 다음 부팅부터는 schtasks 가 무조건 일반 권한으로 자동 시작.

**메시지 액션 단어 일관성**: `I18n.AdminElevationDowngradeNotice` 의 한국어 '종료' / 영어 'Exit' 단어가 `I18n.MenuExit` 라벨과 정확 일치해야 한다 — 사용자가 안내 메시지의 "트레이 메뉴의 '종료'" 를 읽고 트레이 메뉴를 열었을 때 정확히 동일 단어를 찾을 수 있게. 메시지 안 액션 단어 ↔ 메뉴 라벨 일관성은 silent fail 정책 정신과 정합 (UI 마찰 silent 회피).

우회 후보 (Option A `SaferCreateLevel` + `CreateProcessAsUserW` / Option B explorer.exe COM 위임) 는 변경면 +200 LOC + 회귀 표면 다층이라 거부. 미래 진입 조건 + 4-case 매트릭스 + Option A/B/C 비교 + Windows token 모델 분석: [dev-notes/2026-05-28-pr-15-admin-downgrade.md](dev-notes/2026-05-28-pr-15-admin-downgrade.md) + [improvement-plan/PR-15-admin-elevation.md §7.2](improvement-plan/PR-15-admin-elevation.md).

### Admin elevation toggle — 4 case 통일 흐름 (PR-15 후속 fix #3, 2026-05-29)

위 fix #2 의 4-case 비대칭 (case 1/2/3 자동 spawn + case 4 안내 + `MB_YESNO`) 이 사용자 mental model 충돌 (메시지박스 표시 **전** 에 `updateConfig` + `ReregisterIfAdminChanged` 이미 disk 반영 완료 — Yes/No 컨벤션의 "취소" 직관과 불일치) + 메인 인디 잔존 회귀 (MB_OK + `break` 흐름의 자연 결과 — `WM_CLOSE` 미발화 → `OnProcessExit` 미진입 → `Overlay.Dispose` 미실행) 동시 보고를 사용자 직접 제안 (4 case 단일 메시지 + `MB_OK` 단일 버튼 + 자동 종료) 으로 통합 해결.

[`Tray.HandleMenuCommand`](../App/UI/Tray.cs) 의 `IDM_ADMIN_ELEVATION` 핸들러는 fix #3 부터 **4 단계 단일 흐름** (~14 LOC):

```csharp
case IDM_ADMIN_ELEVATION:
{
    AppConfig newAdminConfig = config with { AdminElevation = !config.AdminElevation };
    updateConfig(newAdminConfig);                                  // ① config 즉시 저장 (mtime self-bump)
    StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig);   // ② schtasks RunLevel 즉시 재등록 (등록 안 됐으면 noop)
    User32.MessageBoxW(hwndMain, I18n.AdminElevationChangeNotice,  // ③ 통일 안내 (단일 메시지 + 단일 OK 버튼)
        "KoEnVue", Win32Constants.MB_OK);
    User32.PostMessageW(hwndMain, Win32Constants.WM_CLOSE,         // ④ 자동 종료 → OnProcessExit → Overlay.Dispose
        IntPtr.Zero, IntPtr.Zero);
}
break;
```

자동 spawn 안 함 — Windows token 모델의 admin→일반 down-grade 한계를 사용자 수동 재실행으로 자연 회피. 사용자가 일반 권한 재실행 시 `config.AdminElevation=true` → [`AdminElevation.TryRelaunchAsAdmin`](../App/Bootstrap/AdminElevation.cs) 가 UAC 1회로 admin 자동 진입, `false` → 일반 권한 그대로. admin 환경 재실행은 토큰 상속 (KoEnVue 통제 외 — fix #2 §7.2 의 down-grade 한계 그대로 보존).

**책임 분담 명료화**:

| 메커니즘 | 책임 | 시점 | 비고 |
|---------|------|------|------|
| 트레이 토글 | **옵션 변경** (config 디스크 저장 + schtasks 재등록 + 종료) | 사용자가 트레이 메뉴 클릭 시 | fix #3 의 4 단계 단순 흐름 |
| 부팅 self-elevation ([`TryRelaunchAsAdmin`](../App/Bootstrap/AdminElevation.cs)) | **옵션 효력 발생** (`config.AdminElevation=true` + 일반 권한 부모 → UAC 1회 + admin 자식) | 사용자 일반 권한 재실행 시 mutex 획득 전 (step 0c) | PR-15 UIPI 우회 가치 자체 |

둘은 별개 책임 — 부팅 self-elevation 제거 시 PR-15 UIPI 우회 가치 (관리자 콘솔 한/영 표시) 자체 소멸. 트레이 토글 자동 spawn 제거 시 `TryRelaunchAsAdmin` 무가치 추론은 **부적절**.

**자연 제거된 메서드** (사용처 0):

- [`AdminElevation.ClearReentryGuard()`](../App/Bootstrap/AdminElevation.cs) — fix #2 의 트레이 YES 분기 직전 호출 site 였으나 fix #3 의 자동 spawn 폐기로 사용처 0
- [`AdminElevation.SetRelaunchParentPidForTrayRestart()`](../App/Bootstrap/AdminElevation.cs) — 동일 (fix #1 의 race 차단 패턴이 트레이 자동 spawn 흐름 한정 → fix #3 에서 흐름 자체 제거)

**유지 (인프라 가치)**:

- [`TryRelaunchAsAdmin`](../App/Bootstrap/AdminElevation.cs) — 부팅 시점 self-elevation (옵션 효력 발생)
- [`WaitForRelaunchParentIfAny`](../App/Bootstrap/AdminElevation.cs) — `TryRelaunchAsAdmin` 의 손자 generation 부모 wait (UAC 다이얼로그 통과 후 손자가 자식 종료를 명시 wait — race 차단)
- 환경변수 2종 (`KOENVUE_ELEVATED` 재진입 가드 + `KOENVUE_RELAUNCH_PARENT_PID` 부모 PID 전파) — 부팅 self-elevation 의 손자 generation race 차단 인프라

**I18n 키 정리**:

| 키 | 변경 | 비고 |
|---|------|------|
| `AdminElevationRestartPrompt` | **제거** (fix #3) | `MB_YESNO` "다음 실행부터 적용됩니다. 지금 재시작하시겠습니까?" — Yes/No mental model 충돌 |
| `AdminElevationDowngradeNotice` | **제거** (fix #3) | fix #2 의 case 4 안내 — 통일 흐름으로 흡수 |
| `AdminElevationChangeNotice` | **신규** (fix #3) / **단순화** (fix #4) | ko "관리자 권한 옵션이 변경되어 KoEnVue를 종료합니다. KoEnVue를 다시 실행해 주세요." / en "The admin elevation option has been changed. KoEnVue will now exit. Please launch KoEnVue again." (fix #3 시점 원본은 "관리자 권한 옵션은 다음 실행부터 적용됩니다." / "the change will apply from the next launch." — 사용자 종료 → 수동 재실행 흐름에서 "다음 실행" 시점 자명, redundant 제거) |

**트레이드오프 정직**: 일반→admin 케이스 (가장 흔한 use case) 의 자동 UAC spawn UX 가 약간 후퇴 — 기존 fix #2 까지는 `MB_YESNO` → YES → 자동 spawn → 자식이 UAC 1회 → 즉시 admin 인스턴스 시작. fix #3 부터는 사용자가 안내 OK → 자동 종료 → 사용자 수동 재실행 → UAC 1회. 단계 +1 의 마찰 비용. 분담 명료화 ("트레이 토글 = 옵션 변경" / "부팅 self-elevation = 옵션 효력 발생") 로 보상.

자세한 시계열 + 사용자 직접 제안 채택 근거 + 트레이드오프 정직 + 분담 명료화: [dev-notes/2026-05-29-pr-15-tray-toggle-unified.md](dev-notes/2026-05-29-pr-15-tray-toggle-unified.md) + [improvement-plan/PR-15-admin-elevation.md §7.3](improvement-plan/PR-15-admin-elevation.md).

### Admin elevation tray menu check — OR logic (PR-15 후속 fix #4, 2026-05-29)

위 fix #3 의 4 case 통일 흐름 박은 직후, 사용자 ultrathink 질문 — "관리자 권한으로 실행된 Total Commander 등에서 KoEnVue 를 실행할 경우, `admin_elevation` 옵션 값과 상관없이 '관리자 권한으로 실행' 항목에 체크가 되어 있어야 하지 않을까?" 가 fix #3 까지의 메뉴 체크 분기 (`config.AdminElevation ? MF_CHECKED : MF_UNCHECKED`) 에서 **case 2** (`config=false` + `IsCurrentProcessElevated()=true`) 의 시각 충돌을 정확히 식별. admin 환경 외부 spawn (admin Total Commander 가 KoEnVue.exe 실행 → admin 토큰 상속) 케이스에서 메뉴 체크는 OFF 인데 실 권한은 admin 인 상태.

[`Tray.Menu.ShowMenu`](../App/UI/Tray.Menu.cs) 의 `IDM_ADMIN_ELEVATION` 항목 체크 표시는 fix #4 부터 **OR 로직**:

```csharp
// 체크 표시 = config.AdminElevation OR IsCurrentProcessElevated() (PR-15 후속 fix #4, 2026-05-29).
bool isAdminEffective = config.AdminElevation || AdminElevation.IsCurrentProcessElevated();
uint adminElevationFlags = isAdminEffective ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
```

**4-case 매트릭스 (메뉴 체크)**:

| # | `config.AdminElevation` | `IsCurrentProcessElevated()` | fix #3 체크 | fix #4 체크 (OR) | 의미 |
|---|---|---|---|---|------|
| 1 | `false` | `false` | OFF | OFF | 일반 권한 + 옵션 OFF |
| 2 | **`false`** | **`true`** | **OFF (충돌)** | **ON ✓** | **admin 환경 외부 spawn — case 2 해결** |
| 3 | `true` | `false` | ON | ON | 일반 권한 + 옵션 ON (다음 실행 self-elevate) |
| 4 | `true` | `true` | ON | ON | admin 권한 + 옵션 ON |

case 2 만 fix #4 의 OR 로 동작 변경 — 다른 3 case 는 fix #3 와 동일. "현재 권한 OR 다음 실행 시 권한" 으로 정직한 시각 노출.

**토글 클릭 의미는 보존** — `IDM_ADMIN_ELEVATION` 분기 (fix #3 의 4 단계 단일 흐름) 는 한 줄도 변경 안 됨. 토글 클릭 = config 만 변경 (`updateConfig(config with { AdminElevation = !config.AdminElevation })`) + schtasks 재등록 + 안내 + `WM_CLOSE`. Windows token 모델 한계로 실 권한은 다음 부팅까지 영향 없음 — 클릭이 "지금 실 권한" 을 바꾸지 못함을 `MessageBoxW` 안내가 사용자 가이드.

**왜 다른 메뉴 항목은 OR 안 함**: Snap / Animation / Cursor indicator 등 다른 메뉴 항목은 모두 `config.*` 직접 반영 — 외부 환경 영향 받는 항목이 0. admin 항목만 **부모 셸 토큰 상속** 이라는 외부 환경 영향을 받는 유일한 케이스라 OR 정당. fix #4 는 메뉴 빌더 한 곳만 OR — 다른 메뉴 빌더에 OR 패턴 확산하지 않음.

**호출처 변화**: `AdminElevation.IsCurrentProcessElevated()` 는 본 fix 이전까지 `Program.cs` (부팅 시점 분기) 단일 호출처. fix #4 부터 `App/UI/Tray.Menu.cs` (메뉴 빌더) 추가 — 2 호출처. fix #3 가 `Tray.cs` 에서 제거했던 `using KoEnVue.App.Bootstrap;` import 가 fix #4 에서 같은 partial class 의 다른 파일 `Tray.Menu.cs` 에 재추가.

**AOT 페이지 흡수**: AOT publish 사이즈 4,864,512 → 4,864,512 bytes (**±0 bytes**). 신규 호출 (`AdminElevation.IsCurrentProcessElevated` 메서드 호출 site 1) + doc comment 추가 IL 분량이 AOT 의 4 KB 페이지 경계 안에 흡수. 65/65 PASS, SHA256 `e7dfc79d93836d052d1e8f72aece1397998fd3771d55509b90275418f79a3dc1`.

자세한 시계열 + 토글 의미 보존 검증 + Windows token 모델 한계 정합 + AOT 페이지 흡수: [dev-notes/2026-05-29-pr-15-tray-menu-or-logic.md](dev-notes/2026-05-29-pr-15-tray-menu-or-logic.md) + [improvement-plan/PR-15-admin-elevation.md §7.4](improvement-plan/PR-15-admin-elevation.md).

### Admin elevation tray menu label — case 2 `Config = User` hint suffix (PR-15 후속 fix #5, 2026-05-29)

위 fix #4 의 OR 로직 박은 **직후** 사용자 추가 질문 — "관리자 권한 total commander 등에서 KoEnVue 실행 시 **설정값도 같이 알 수 있으면 제일 좋을 것 같아**". fix #4 의 OR 로 case 2 의 실 권한 시각 노출은 해결됐지만, 같은 case 의 **`config.AdminElevation` 값** (사용자 의도 = User) 은 여전히 invisible — 사용자가 admin Total Commander 에서 KoEnVue 실행 후 메뉴 체크가 ON 인 것만 보고 "옵션이 ON 인지 외부 환경 영향인지" 구분 불가.

[`Tray.Menu.ShowMenu`](../App/UI/Tray.Menu.cs) 의 `IDM_ADMIN_ELEVATION` 항목 라벨은 fix #5 부터 **case 2 한정 동적 분기**:

```csharp
// 라벨 hint (PR-15 후속 fix #5, 2026-05-29) — case 2 (config=false + IsElevated=true,
// admin 환경 외부 spawn) 전용 "(Config = User)" suffix 노출. fix #4 OR 의 visible 정합 +
// case 2 의 config 값 명시 = 사용자가 두 신호 (실 권한 + config 값) 모두 인지. case 1/3/4
// 는 기본 라벨 — case 1 일관 OFF / case 3 사용자 명시 토글 결과 / case 4 일관 ON.
bool isCurrentlyElevated = AdminElevation.IsCurrentProcessElevated();
bool isAdminEffective = config.AdminElevation || isCurrentlyElevated;
bool isExternalElevation = !config.AdminElevation && isCurrentlyElevated;  // case 2
string adminElevationLabel = isExternalElevation
    ? I18n.MenuAdminElevationExternal
    : I18n.MenuAdminElevation;
uint adminElevationFlags = isAdminEffective ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
```

**4-case 매트릭스 (fix #4 → fix #5 변화)**:

| # | `config.AdminElevation` | `IsCurrentProcessElevated()` | 체크 (fix #4 OR) | **라벨 (fix #5)** | 의미 |
|---|---|---|---|------|------|
| 1 | `false` | `false` | OFF | "관리자 권한으로 실행" | 일관 OFF |
| **2** | **`false`** | **`true`** | **ON ✓ (OR)** | **"관리자 권한으로 실행, Config = User"** | **외부 환경 영향 — 두 신호 모두 visible** |
| 3 | `true` | `false` | ON | "관리자 권한으로 실행" | 사용자 명시 토글 결과 |
| 4 | `true` | `true` | ON | "관리자 권한으로 실행" | 일관 ON |

case 2 만 라벨 분기 — case 1/3/4 = 기본 라벨 (사용자 명시 의도 케이스, noise 회피).

**자동 동기화 거절 (ultrathink)**: 옵션 A — case 2 진입 시 `config.AdminElevation` 자동 true 저장 — 시각 충돌 자동 해결 + disk 동기화. 시나리오 2/3 부작용으로 거절: (a) 일회성 admin 환경 사용자 시나리오 — `config` 자동 저장 → `StartupTaskManager.ReregisterIfAdminChanged` → schtasks `<RunLevel>HighestAvailable</RunLevel>` 재등록 → 다음 부팅마다 admin 자동 시작 = 사용자 명시 의도 0 의 부팅 환경 영구화, (b) 사용자 명시 토글 직후 외부 spawn 시나리오 — 직전 사용자 명시 의도 (`User`) 가 case 2 진입 한 번에 reset (`Admin`) = 의도 무시. 채택: 옵션 B (사용자 직접 제안) — 라벨 hint suffix 노출, 자동 동기화 0, 사용자 명시 의도 100% 존중.

**ko/en 영문 mix 정당성 (P2 정합)**:

- 메인 동사구 한국어 유지 ("관리자 권한으로 실행") — P2 정합
- suffix 영문 mix ("Config = User") 정당화: (a) IT 통용어 (Windows 표준 어휘 — 한국어 "설정값" / "사용자" 보다 짧음 + 변수명/값 직관 명시), (b) KoEnVue 의 주 사용자 (admin 콘솔 사용자) 친화 — 개발자/IT 사용자가 즉시 파악 가능, (c) 직설성 — "변수명 = 값" 형식 코드 직관, (d) 길이 trade-off 균형 — 한 줄 메뉴 라벨 부담 감소
- 사용자 직접 표현 ("관리자 권한으로 실행, Config = User") + 정정 (괄호 → 쉼표) 100% 존중

**fix #4 OR 로직 보존**: `isAdminEffective` 한 줄 변경 0 — 체크 표시는 두 신호 OR 그대로, 라벨만 case 2 hint 단독. `isCurrentlyElevated` 변수 분리로 한 우클릭 안에서 `IsCurrentProcessElevated()` 1회 호출 (두 분기 공유).

**AOT 페이지 경계 초과**: AOT publish 사이즈 4,864,512 → 4,865,024 bytes (**+512 bytes**, 페이지 흡수 없음). 신규 i18n 키 ko/en 문자열 (UTF-16 ~104 bytes) + enum + `_table` + public surface property + `Tray.Menu.cs` 분기 로직 (~30 bytes IL) 합이 AOT 4 KB 페이지 경계 초과 → +512 bytes 누적. fix #4 의 ±0 (페이지 흡수) → fix #5 의 +512 (페이지 경계 초과) 정직 보고. 65/65 PASS, SHA256 `417A877C66560F6861D6AA1408BD0CE1D2FC29B3F0B0D42FB1F509DB295C024F`.

자세한 시계열 (사용자 질문 → 자동 동기화 ultrathink 거절 → 사용자 직접 제안 → 라벨 표기 정정) + 시나리오 2/3 자동 동기화 부작용 정밀 박제 + ko/en 영문 mix 정당성 + 4-case 매트릭스 fix #4 → fix #5 변화 + 라벨 표기 정정 시계열: [dev-notes/2026-05-29-pr-15-tray-menu-config-hint.md](dev-notes/2026-05-29-pr-15-tray-menu-config-hint.md) + [improvement-plan/PR-15-admin-elevation.md §7.5](improvement-plan/PR-15-admin-elevation.md).

### Admin elevation MessageBoxW cleanup 트레일 (PR-15 후속 fix #2 cleanup, 2026-05-29)

[fix #2](#admin-elevation-toggle--4-case-분기-매트릭스-pr-15-후속-fix-2) (2026-05-28) 이 [`Win32Constants.MB_OK = 0x00000000`](../Core/Native/Win32Types.cs) const 도입 후 [`Tray.cs`](../App/UI/Tray.cs) 의 down-grade 안내 + [`AdminElevation.cs:ShowDeniedMessage`](../App/Bootstrap/AdminElevation.cs) 2 곳만 정리하고 App/UI 의 나머지 5 spot 이 positional `0` 인 채로 잔존하던 P3 부분 일관성을 **100% 회복**.

**변경 5 spot** (의미 변화 0, 마지막 positional `0` → `uType: Win32Constants.MB_OK`):

| # | 파일 | 호출 site | 분기 |
|---|------|---------|------|
| 1 | [`App/UI/Tray.cs:543`](../App/UI/Tray.cs) | `ShowPositionError` | `TrayPositionUnavailable` 안내 박스 |
| 2 | [`App/UI/Tray.cs:599`](../App/UI/Tray.cs) | `CleanupPositions` empty 분기 | `TrayPositionHistoryEmpty` 안내 박스 |
| 3 | [`App/UI/Dialogs/SettingsDialog.cs:337`](../App/UI/Dialogs/SettingsDialog.cs) | 필드 commit 에러 | 사용자 입력 invalid 안내 |
| 4 | [`App/UI/Dialogs/ScaleInputDialog.cs:177`](../App/UI/Dialogs/ScaleInputDialog.cs) | invalid input | 숫자 파싱 실패 박스 |
| 5 | [`App/UI/Dialogs/ScaleInputDialog.cs:187`](../App/UI/Dialogs/ScaleInputDialog.cs) | out of range | Min/Max 범위 초과 박스 |

`KoEnVue.Core.Native` import 이미 모든 파일에 있어 import 변경 0. 시리즈 카운트: fix #2 시점 2 (Tray.cs + AdminElevation.cs) → [fix #3](#admin-elevation-toggle--4-case-통일-흐름-pr-15-후속-fix-3-2026-05-29) 의 `AdminElevationChangeNotice` 1건 누적 6 → cleanup 5건 추가로 **7 통일** (Tray.cs ×3 + ScaleInputDialog.cs ×2 + SettingsDialog.cs ×1 + AdminElevation.cs ×1). App/UI 의 `MessageBoxW` 단일 OK 버튼 호출 site 100% named `Win32Constants.MB_OK` 패턴.

**신규 invariant grep** ([conventions.md](conventions.md) L73 추가) — 기존 `uType:\s*0\b` 가드는 named argument 만 잡기 때문에 positional 형태는 별도 가드:

```bash
git grep -nE "User32\.MessageBoxW\([^)]*,\s*0\s*\)" App/   # 0 매치 — positional uType=0 금지
```

dev-note 신규 불요 (5 LOC cleanup), [PR-15 design doc §7](improvement-plan/PR-15-admin-elevation.md) 도 변경 불요 — 본 단락이 단일 박제처. fix #1~#5 시계열은 그대로 보존 (역사 박제 무손실).

### Second-instance activation signal

`TryAcquireMutex` 실패 시 `NotifyExistingInstance` 가 호출된다. 메인 윈도우 클래스명(`"KoEnVueMain"`)으로 `User32.FindWindowW` 호출 → 기존 인스턴스의 HWND 를 얻고 `PostMessageW(hwnd, AppMessages.WM_APP_ACTIVATE, 0, 0)`. 두 번째 인스턴스는 즉시 종료한다.

기존 인스턴스의 WndProc 는 `WM_APP_ACTIVATE` (`WM_APP + 7`) 를 수신해 `HandleActivateRequest` 로 분기한다. 여기서 현재 포그라운드 앱 기준 좌표로 `Animation.TriggerShow` 를 호출해 인디케이터를 즉시 표시 — `DisplayMode` 와 `EventTriggers` 설정을 **무시**하고 강제 표시하는 이유는 사용자의 명시적인 재실행 행위에 대한 응답이기 때문.

메시지 전용 윈도우(HWND_MESSAGE parent) 가 아니라 일반 최상위 윈도우(데스크톱 parent + 화면 미표시)로 생성되므로 `FindWindowW` 가 정상 매칭한다. 탐색 실패(기존 창이 막 파괴 중이거나 클래스명이 달라진 경우)는 조용히 무시된다.

기존(1st) 인스턴스가 admin(High IL) 으로 떠 있고 2nd 인스턴스가 Medium IL 로 남는 경로(① `admin_elevation` self-relaunch 의 UAC 취소 → 일반 권한 계속 ② admin 환경 외부 spawn 인데 `config.AdminElevation=false` ③ 설정 변경 과도기)에서는, 2nd 의 `PostMessageW(WM_APP_ACTIVATE)` 가 UIPI(Medium → High) 로 차단돼 "이미 실행 중" 인디 즉시 표시 피드백이 소실된다. 이를 막기 위해 `Program.MainImpl` 은 메인 윈도우 생성 직후(8a-2) `User32.ChangeWindowMessageFilterEx(_hwndMain, AppMessages.WM_APP_ACTIVATE, MSGFLT_ALLOW, ...)` 로 이 메시지를 UIPI 화이트리스트에 등록한다(바로 아래 TaskbarCreated 와 동일 메커니즘). `WM_APP_ACTIVATE` 는 정적 상수라 `RegisterWindowMessageW` 불요. 동일 IL(일반 사용자) 끼리는 애초에 UIPI 차단이 없어 무해한 no-op (감사 ⑫, 2026-06-02).

### TaskbarCreated — shell restart recovery

셸(`explorer.exe`) 재시작 시 이전에 등록된 모든 트레이 아이콘 정보는 소실된다. Windows 는 이를 보완하기 위해 `"TaskbarCreated"` 라는 이름의 **등록된 윈도우 메시지**를 모든 최상위 창에 브로드캐스트한다. 셸 업데이트, 크래시, 수동 재시작(`taskkill /im explorer.exe` 등) 시나리오에서 모두 발생.

`Program.MainImpl` 은 메인 윈도우 생성 직후(8a) `User32.RegisterWindowMessageW("TaskbarCreated")` 로 메시지 ID 를 받아 `_taskbarCreatedMsgId` 필드에 저장하고, 곧바로 `User32.ChangeWindowMessageFilterEx(_hwndMain, _taskbarCreatedMsgId, MSGFLT_ALLOW, ...)` 로 이 메시지를 UIPI 화이트리스트에 등록한다 — `requireAdministrator`/self-elevation 으로 High IL 인 경우 Medium IL 인 explorer 의 `TaskbarCreated` 브로드캐스트가 UIPI 로 차단되므로, 필터 없이는 셸 재시작 복구 자체가 무력화된다(위 `WM_APP_ACTIVATE` 화이트리스트와 동일 메커니즘). 동적 ID 이므로 WndProc 의 `switch` 에 넣을 수 없어 switch 앞단의 if 분기로 비교한다:

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

`Main` 에 `[STAThread]` 를 붙여 CLR 이 메인 스레드를 STA 로 초기화하도록 위임한다 (NativeAOT 도 이 속성을 존중한다). CLR 이 `Main` 진입 전 `CoInitializeEx(COINIT_APARTMENTTHREADED)` 를 부르고 프로세스 종료 시 짝을 맞춰 `CoUninitialize` 도 수행하므로, 앱 코드에서는 별도의 명시적 초기화/해제 호출이 없다.

이전 구조는 `Ole32.CoInitializeEx` 를 앱이 직접 불렀는데, CLR 기본 설정(MTA)과 충돌해 `RPC_E_CHANGED_MODE`(0x80010106) 로 실패하고 VDM / WinEventHook 이 사일런트하게 degrade 되는 버그가 있었다. `[STAThread]` 로 CLR 이 먼저 STA 를 잡아주게 하면 이 경로 전체가 사라진다. 또한 `ProcessExit` 가 finalizer 스레드에서 돌아 `CoUninitialize` 를 메인 스레드 apartment 와 매칭되지 않은 곳에서 부르던 잠재 결함도 함께 제거됐다.

메인 스레드 STA 초기화가 보장된 직후 `SystemFilter.ShouldHide` 를 1회 호출해 static constructor 를 강제 실행하고 `IVirtualDesktopManager` COM 객체를 생성해둔다 — 이후 메인·감지 스레드 양쪽에서 안전하게 사용 가능.

### Overlay window class

Separately registered (shared WndProc with main window). `WM_DESTROY` guard checks `hwnd == _hwndMain` so app exit doesn't trigger when the overlay is destroyed.

### DWMWA constants location

`DWMWA_EXTENDED_FRAME_BOUNDS` and `DWMWA_CLOAKED` live in [Core/Native/Win32Types.cs](../Core/Native/Win32Types.cs) under the `Win32Constants` class rather than inside `Core/Native/Dwmapi.cs`. P4 mandates that all Win32 structs and constants are centralized in `Win32Types.cs` regardless of which DLL they belong to.

### `volatile` + `Action<AppConfig>` callback

`_config` is a `volatile` field, and `ref` cannot be used with volatile, so config updates use an `Action<AppConfig>` callback pattern instead of `ref AppConfig`.

cross-thread 접근하는 `Program.cs` 의 hwnd 3종 (`_hwndMain` / `_hwndOverlay` / `_hwndCursorOverlay`) 도 PR-18 5/5 부터 `volatile` 마킹 — 메인 스레드의 `CreateWindowExW` write 와 감지 스레드의 `PostMessageW` / `IsKoenvueWindow` read 사이의 비대칭 회복. x64 의 TSO (Total Store Order) + 단일 init-then-read 패턴 덕에 회귀 0 이지만, ARM64 weak memory model + NativeAOT codegen 조합의 silent no-op 회귀 (감지 스레드가 첫 폴링 tick 에서 stale `IntPtr.Zero` 읽고 `PostMessageW(0, ...)` silent fail) 방어. cost 0 (keyword 6 글자 × 3), evidence 부재의 방어적 패치 — 자세한 근거: [dev-notes/2026-05-28-pr-18-core-windowing.md](dev-notes/2026-05-28-pr-18-core-windowing.md).

### `OnProcessExit` cleanup sequence

`Program.Bootstrap.OnProcessExit`는 다음 순서로 리소스를 정리한다:

1. `_stopping = true` — 감지 스레드 종료 신호 (volatile)
2. IME 훅 해제 (`ImeStatus.UnregisterHook`)
3. CAPS LOCK 폴링 타이머 명시적 해제 (`KillTimer`)
4. 애니메이션 + 렌더링 리소스 해제 (윈도우 파괴 전)
5. 오버레이 + 메인 윈도우 명시적 파괴 (`DestroyWindow`)
6. 트레이 아이콘 제거 (`NIM_DELETE`)
7. Mutex 해제 (`Dispose` only — `ReleaseMutex`는 소유 스레드에서만 가능하나 `ProcessExit`는 다른 스레드일 수 있음)
8. 종료 로그 기록 + 로거 종료 (`Logger.Info` → `Logger.Shutdown`)

step 1 의 `_stopping = true` 와 step 5 의 `DestroyWindow(_hwndMain)` 사이에는 **step 0 — 감지 스레드 합류** 가 끼어 race window 를 좁힌다 (PR-19). `Program.cs` 의 `_detectionThread` field (`StartDetectionThread` 가 로컬 변수 대신 field 에 보관) 를 `_detectionThread?.Join(500)` 으로 합류시켜, `_stopping=true` 신호 후 한 폴링 주기 (50ms) 안에 자발 종료하는 감지 스레드가 step 5 이전에 끝나도록 강제한다. 감지 스레드는 `IsBackground = true` 라 OS 가 프로세스 종료 시 강제 회수하지만, 그 사이 `DestroyWindow(_hwndMain)` 과 감지 스레드의 `PostMessageW(_hwndMain, WM_UPDATE_INDICATOR, ...)` 가 겹치면 `GetLastWin32Error = 1400 (ERROR_INVALID_WINDOW_HANDLE)` marshal race 가 발생할 수 있다. 500 ms 타임아웃은 stuck 스레드가 메인 종료를 영구 블록하지 않게 하는 상한 — IsBackground 안전망과 명시 합류의 절충.

COM 해제는 `[STAThread]` 기반으로 CLR 이 메인 스레드 종료 시 자동 수행하므로 `ProcessExit` 에서는 건드리지 않는다. `ProcessExit` 는 finalizer 스레드에서 돌기 때문에 여기서 `CoUninitialize` 를 불러도 메인 스레드 apartment 와 매칭되지 않는다.

`Logger.Shutdown`은 반드시 마지막에 호출하여 이전 단계의 로그가 모두 기록되도록 보장한다. 타이머 해제와 윈도우 파괴는 리소스 해제(5단계) 이후에 수행하여 타이머 콜백이 해제된 리소스를 참조하는 것을 방지한다.

`ProcessExit` 미발화 경로 (FailFast / Access Violation Exception 등 비정상 종료) 에서는 `Program.cs` 의 `AppDomain.CurrentDomain.UnhandledException` 핸들러가 `Logger.Shutdown` 전에 `CleanupPreviousTrayIcon()` 을 best-effort 호출해 트레이 좀비 아이콘을 줄인다 (PR-19). 핸들러가 실패해도 다음 부팅의 `CleanupPreviousTrayIcon` 자기치유 (mutex 획득 직후 step 2) 가 안전망 — 즉시 재실행 시 셸 알림 영역의 좀비 잔류 시간만 좁히는 보조 차단.

### `InvariantGlobalization`

Enabled in [KoEnVue.csproj](../KoEnVue.csproj) — strips ICU from the NativeAOT publish. Means no `CultureInfo` usage except for `CultureInfo.InvariantCulture`. IME language detection uses `GetUserDefaultUILanguage` P/Invoke instead of `CultureInfo.CurrentUICulture`.

### `theme:system` 데이터 소스 및 시그널

[App/Config/ThemePresets.cs:ApplySystemTheme](../App/Config/ThemePresets.cs#L111) 는 사용자가 `theme:system` 을 선택했을 때 시스템 강조색을 인디케이터 한글 배경에 직접 적용하고 영문 배경은 보색으로 계산한다. PR-14 이후 데이터 소스는 두 경로의 2단 분기:

1. **`Dwmapi.DwmGetColorizationColor(out uint argb, out bool opaqueBlend)`** — Win11 personalization accent 의 source-of-truth. "제목 표시줄과 창 테두리에 강조색 표시" 옵션 ON/OFF 와 무관하게 항상 최신 accent 를 반환한다. 반환값은 0xAARRGGBB ARGB DWORD — `Dwmapi.TryGetColorizationRgb` 헬퍼가 R/G/B 3 채널로 분리 (alpha 무시). HRESULT 비-0 이면 false.
2. **`User32.GetSysColor(Win32Constants.COLOR_HIGHLIGHT)`** 폴백 — DWM composition 비활성 / 안전 모드 등 예외 경로용. Win11 에서 위 옵션이 OFF 면 personalization accent 변경이 `COLOR_HIGHLIGHT` 에 즉시 반영되지 않는 known limitation 이 있어 일반 환경에선 1번 경로가 절대 우선.

**ARGB byte 순서 주의**: DWM 의 0xAARRGGBB (R 이 high byte) 와 `GetSysColor` 의 COLORREF 0x00BBGGRR (B 가 high byte) 는 R/B 순서가 반대다. 두 경로가 같은 `ColorHelper` 헬퍼를 공유하지 않고 각자 분리한다 — `ColorHelper.ColorRefToRgb` 는 COLORREF 전용이라 ARGB 에 그대로 쓰면 색이 뒤집힌다.

**메시지 시그널** (모두 같은 `HandleSettingChange` 핸들러로 라우팅 — `Settings.ClearProfileCache` + `ThemePresets.Apply` + `Animation.TriggerShow` 를 순서대로 트리거):

- `WM_SETTINGCHANGE` (0x001A) — 시스템 색 / 시각 설정 전반의 광역 브로드캐스트
- `WM_THEMECHANGED` (0x031A) — 비주얼 스타일 / 다크 모드 토글 시 별도 브로드캐스트 (PR-01 에서 추가)
- `WM_DWMCOLORIZATIONCOLORCHANGED` (0x0320) — DWM colorization color 변경 시 정확히 발화. Win11 에서 "제목 표시줄 강조색 표시" OFF 인 경우 `WM_THEMECHANGED` 가 발화하지 않을 수 있어 본 시그널이 누락 없는 안전망 (PR-14 에서 추가)

### `app.manifest` 구성

[app.manifest](../app.manifest) 는 다음 4가지 선언을 합쳐 한 리소스로 임베드한다:

1. **`asInvoker` (`trustInfo`)** — P5 (PR-03, v0.9.3.0). 사용자 권한으로 충분(IME 감지·WinEventHook·인디케이터 렌더링·`WTSRegisterSessionNotification`·schtasks `LeastPrivilege`·user-writable config.json 모두 elevation 불요). v0.9.x 의 `requireAdministrator` 가 만들던 보안 표면(B1 write-anywhere log path, B2 schtasks symlink TOCTOU, B5 Admin-elevated notepad on config 편집) 이 자연 해소된다. exe 가 user-non-writable 위치(Program Files 등)에 있을 때는 [`App/Config/PortablePath`](../App/Config/PortablePath.cs) 가 `%LOCALAPPDATA%\KoEnVue\` 로 config/log 를 자동 fallback. 사용자별 격리 + Admin 토큰 불요. Tray.cs 의 `OpenUpdatePage` URL prefix 화이트리스트는 asInvoker 후에도 유지(외부 응답을 그대로 ShellExecute 에 넘기면 사용자 컨텍스트 임의 핸들러 실행으로 번질 수 있음).
2. **`supportedOS` (`compatibility.v1`)** — Win10/11 단일 GUID `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`. 이 블록이 없으면 Windows 가 `GetVersionEx`/`RtlGetVersion` 등 일부 API 에 legacy compatibility shim 을 적용해 Win8 로 자기 신원을 위장한다. 본 앱은 `DwmGetColorizationColor` / personalization accent / Win11 Snap Layout 인지 등 Win10 1607+ API 만 사용하므로 더 오래된 OS 는 명시적으로 unsupported.
3. **`dpiAwareness` (`SMI/2016/WindowsSettings`) + `dpiAware` (`SMI/2005/WindowsSettings`) 페어** — `PerMonitorV2` 우선, fallback `true/pm`. Windows 10 1703 이전에선 `dpiAwareness` 가 무시되고 `dpiAware` 의 `true/pm` 이 PerMonitor V1 으로 동작. 모든 GDI / `GetSystemMetricsForDpi` / `AdjustWindowRectExForDpi` 호출은 `Core/Dpi/DpiHelper` 를 통해 per-monitor DPI 를 받는다.
4. **`longPathAware` (`SMI/2016/WindowsSettings`)** — `windowsSettings` 블록 내에 위치. 사용자가 `config.json` / `koenvue.log` 를 매우 깊은 디렉토리(>260 chars) 에 두는 시나리오 방어. **실제 활성 조건**: 시스템 레지스트리 `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled = 1` 이 별도 필요. manifest 의 `longPathAware` 는 "이 프로세스는 long path 를 받아도 안전" 이라는 *수용성* 선언일 뿐이고, 시스템 차원의 OFF 면 여전히 MAX_PATH 가 적용된다. 따라서 만 명시했다 해서 코드 측 path 처리가 변경되어야 하는 건 아니다 — `Path.Combine` + `AppContext.BaseDirectory` 기반 portable 정책은 그대로 유효.

**의도적 미선언**: `gdiScaling` (`SMI/2017/WindowsSettings`). 본 앱은 PerMonitorV2 인지 + 자체 DPI 스케일링 핸들링이라 GDI auto-scaling 은 무의미하며, `gdiScaling=true` 는 legacy unaware/system-aware 프로세스용 옵션이다. 혼선 회피 목적으로 manifest 에서 명시적으로 빼둔다.

PE 임베드 검증: PowerShell + Win32 `FindResource(hMod, MAKEINTRESOURCE(1), RT_MANIFEST=24)` 로 publish exe 의 manifest 리소스를 추출하면 위 4 선언이 그대로 들어 있어야 한다. 빌드 시점에 manifest XML 이 malformed 이면 `ResourceUpdate` 가 실패해 build error 로 즉시 노출 (실패-가시화 경로).
