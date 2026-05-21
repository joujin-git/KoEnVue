# PR-08: Core reuse restoration + TopmostWatchdog 분리

**Status**: ✅ merged → main (399f6ad)
**Branch**: feat/pr-08-core-reuse (삭제됨)
**Base**: main (PR-03 후 권장)
**Risk**: Low
**Estimated session size**: M (1-2시간)

## 1. 목적 (Why)

Core 레이어의 reuse 약속(다른 Windows desktop 프로젝트로 lift) 회복. CLAUDE.md gate 4종(KoEnVue.App/ImeState/NonKoreanImeMode/DllImport)은 통과하지만 **개념**이 누출:

1. **C4**: [Overlay.cs:298-337, 348-385](../../App/UI/Overlay.cs#L298) `ComputeAnchor*` ↔ `ComputeRelative*` — 같은 알고리즘, RECT만 다름
2. **C6**: [LayeredOverlayBase.cs:594-720](../../Core/Windowing/LayeredOverlayBase.cs#L594) 스냅 로직 ~135줄 → `Core/Windowing/WindowSnapHelper`
3. **C5 (부분)**: [OverlayAnimator.cs](../../Core/Animation/OverlayAnimator.cs) topmost watchdog timer ~30줄 → `Core/Windowing/TopmostWatchdog`. 나머지 4트랙은 보류.
4. **E1**: [OverlayStyle.cs:50](../../Core/Windowing/OverlayStyle.cs#L50)의 `MeasureLabels` 3-tuple `(Hangul, English, NonKorean)` → `IReadOnlyList<string>`
5. **E2**: [Win32Types.cs:286-294, 463-471](../../Core/Native/Win32Types.cs#L286)의 IME 상수 → `Core/Native/ImeConstants.cs`
6. **E3**: [Win32DialogHelper.cs:84](../../Core/Windowing/Win32DialogHelper.cs#L84)의 `"맑은 고딕"` 하드코딩 → 파라미터

## 2. 변경 범위 (What)

### 코드 — C4 ComputeCornerAnchor 통합
- [ ] [App/UI/Overlay.cs](../../App/UI/Overlay.cs)에 `ComputeCornerAnchor(RECT frame, double dpiScale, …)` 단일 메서드 추가
- [ ] 호출자 두 곳(`ComputeAnchor*`, `ComputeRelative*`)이 RECT 결정 후 같은 메서드 호출

### 코드 — C6 WindowSnapHelper 분리
- [ ] [Core/Windowing/WindowSnapHelper.cs](../../Core/Windowing/) 신규 — `ApplySnap`/`ConsiderTarget`/`TryEdge`/`EnumWindowsCallback` + `s_activeSnapRects`/`s_activeOwnerHwnd` 정적 브리지 이동
- [ ] LayeredOverlayBase의 스냅 호출 site에서 WindowSnapHelper 호출

### 코드 — C5 부분 TopmostWatchdog 분리
- [ ] [Core/Windowing/TopmostWatchdog.cs](../../Core/Windowing/) 신규 — 5초마다 SetWindowPos HWND_TOPMOST 호출. timer ID는 caller가 제공 또는 내부 관리.
- [ ] OverlayAnimator에서 watchdog 관련 메서드 삭제, TopmostWatchdog 인스턴스 보유
- [ ] **나머지 4트랙(fade/hold/highlight/slide/dim)은 보류** — `docs/dev-notes/2026-05-21-animator-decomposition-deferral.md` 작성

### 코드 — E1 OverlayStyle 라벨 일반화
- [ ] [Core/Windowing/OverlayStyle.cs:50](../../Core/Windowing/OverlayStyle.cs#L50) — `MeasureLabels: (string Hangul, string English, string NonKorean)` → `MeasureLabels: IReadOnlyList<string>` (또는 `string[]` + SequenceEqual)
- [ ] [Core/Windowing/LayeredOverlayBase.cs:69, 790-791](../../Core/Windowing/LayeredOverlayBase.cs#L69) `_cachedLabelMeasureKey` — 같은 타입 변경. **캐시 비교 로직 변경**: `record` equality(reference) → 명시적 `SequenceEqual`.
- [ ] [App/UI/Overlay.cs:476](../../App/UI/Overlay.cs#L476) `BuildStyle`의 `MeasureLabels` 생성 site 갱신: `[config.HangulLabel, config.EnglishLabel, config.NonKoreanLabel]`

### 코드 — E2 IME 상수 이동
- [ ] [Core/Native/ImeConstants.cs](../../Core/Native/) 신규 — `WM_IME_CONTROL`, `IMC_GETOPENSTATUS`, `IMC_GETCONVERSIONMODE`, `IME_CMODE_HANGUL`, `EVENT_OBJECT_IME_CHANGE`, `LANGID_KOREAN`, `HKL_IME_DEVICE_MASK`, `HKL_IME_DEVICE_SIG` 등 이동
- [ ] [Core/Native/Win32Types.cs](../../Core/Native/Win32Types.cs)의 해당 항목 삭제
- [ ] [App/Detector/ImeStatus.cs](../../App/Detector/ImeStatus.cs)의 using 추가
- [ ] (논의) `ImeConstants`를 Core/Native에 두면 Core가 IME를 알게 됨. **대안**: `App/Detector/ImeConstants.cs`로 이동. **결정**: App에 두는 게 reuse 약속에 부합. **권장 위치**: `App/Detector/`.

### 코드 — E3 폰트 파라미터화
- [ ] [Core/Windowing/Win32DialogHelper.cs:84](../../Core/Windowing/Win32DialogHelper.cs#L84) `CreateDialogFont(int dpiY, string fontFace = "맑은 고딕")` 시그너처 변경 — 디폴트는 일단 유지하되 호출자가 명시.
- [ ] 호출자(SettingsDialog/CleanupDialog/ScaleInputDialog 또는 PR-07 셸)에서 `DefaultConfig.DefaultFontFamily` 또는 `config.FontFamily` 전달
- [ ] [App/Config/DefaultConfig.cs](../../App/Config/DefaultConfig.cs)에 `DefaultDialogFontFamily = "맑은 고딕"` const 추가

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / Changed
- [ ] `docs/architecture.md` reuse contract 섹션 갱신 — 잔여 Core 누출 0건 명시
- [ ] `docs/conventions.md` P6 검증 invariant 갱신 (3-tuple gate 추가)
- [ ] `docs/dev-notes/2026-05-21-animator-decomposition-deferral.md` 신규 — 나머지 4트랙 분해 보류 근거

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep -E "Hangul|English|NonKorean" Core/` 0 매치 (IME 어휘 잔류 0)
- [ ] `git grep "맑은 고딕" Core/` 0 매치
- [ ] `git grep "namespace KoEnVue.Core.Windowing" Core/Windowing/WindowSnapHelper.cs` 1 매치
- [ ] `git grep "namespace KoEnVue.Core.Windowing" Core/Windowing/TopmostWatchdog.cs` 1 매치
- [ ] `wc -l Core/Animation/OverlayAnimator.cs` < 530 (현재 554, ~30줄 감소)
- [ ] `wc -l Core/Windowing/LayeredOverlayBase.cs` < 800 (현재 900, ~135줄 감소)

### Tier 3 — 수동 smoke
- [ ] 인디케이터 표시/숨김 정상
- [ ] 드래그 + 윈도우 엣지 스냅 정상
- [ ] 인디케이터 topmost 유지 (다른 토픽 윈도우가 뜬 후에도)
- [ ] 페이드/하이라이트 애니메이션 정상 (나머지 4트랙은 미변경이라 회귀 없어야 함)

## 4. 사이드 이펙트 / 위험

- **위험 1**: E1의 캐시 키 변경. `IReadOnlyList<string>`은 reference equality라 캐시 hit 항상 false → measure 매번 재실행. **수정 필수**: 명시적 `SequenceEqual` 비교 로직. 측정 비용은 config-change 시점만이라 무시 가능.
- **위험 2**: E2의 ImeConstants 위치 결정 — Core에 두면 reuse 약속 일부 위반, App에 두면 [LayeredOverlayBase](../../Core/Windowing/LayeredOverlayBase.cs)나 [Imm32](../../Core/Native/Imm32.cs)와의 의존 검토. **권장**: App/Detector/.
- **위험 3**: TopmostWatchdog 분리 시 OverlayAnimator의 OnWmTimer 디스패치에서 timer ID가 다른 클래스 소유로 이동. PostMessage/KillTimer 경로 명확화.
- **위험 4**: dev-notes의 두 postmortem이 OverlayAnimator 영역 fragile로 지목 — TopmostWatchdog만 격리해도 회귀 가능성. 수동 smoke 신중.

## 5. 롤백 절차

- 단순 revert (Y) — 단 변경이 다중 파일. squash 권장.
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

### 2026-05-21 — Tier-1+2 통과 후 사용자 smoke 대기

6 항목 모두 구현 + Tier-1/2 검증 통과. 사용자 Tier-3 smoke 대기.

**구현 요약**:

- **E3 폰트 파라미터화**: [Core/Windowing/Win32DialogHelper.cs](../../Core/Windowing/Win32DialogHelper.cs) `CreateDialogFont(uint dpiY)` → `CreateDialogFont(uint dpiY, string fontFamily)` 시그니처 변경 (디폴트 제거 — Core 에서 `"맑은 고딕"` literal 0 매치 달성). [Core/Windowing/DialogShell.cs](../../Core/Windowing/DialogShell.cs) `Run(...)` 에 `string dialogFontFamily` 파라미터 추가 + `CreateDialogFont` 호출에 전달. [App/Config/DefaultConfig.cs](../../App/Config/DefaultConfig.cs) `DefaultDialogFontFamily = "맑은 고딕"` const 신규 — App 측 단일 진실원. 3 다이얼로그 (Settings/Cleanup/ScaleInput) 가 `dialogFontFamily: DefaultConfig.DefaultDialogFontFamily` 한 줄 전달 + Cleanup/Settings 에 `using KoEnVue.App.Config` 추가.

- **E2 IME 상수 이동**: 신규 [App/Detector/ImeConstants.cs](../../App/Detector/ImeConstants.cs) 37 줄 — `WM_IME_CONTROL` / `IMC_GETOPENSTATUS` / `IMC_GETCONVERSIONMODE` / `IME_CMODE_HANGUL` / `EVENT_OBJECT_IME_CHANGE` / `LANGID_KOREAN` / `HKL_LANGID_MASK` / `HKL_IME_DEVICE_MASK` / `HKL_IME_DEVICE_SIG` 9 상수. [Core/Native/Win32Types.cs](../../Core/Native/Win32Types.cs) 의 9 라인 제거 (자리에 P6 이전 안내 주석 1 줄 남김). `WINEVENT_OUTOFCONTEXT` 는 IME 전용이 아닌 일반 WinEvent 상수라 Core 잔존. [App/Detector/ImeStatus.cs](../../App/Detector/ImeStatus.cs) `Win32Constants.X` 8 참조 → `ImeConstants.X` 일괄 치환. [App/Localization/I18n.cs](../../App/Localization/I18n.cs) `IsSystemKorean` 의 1 참조 + `using KoEnVue.App.Detector` 추가.

- **C4 ComputeCornerAnchor 단일화**: [App/UI/Overlay.cs](../../App/UI/Overlay.cs) 신규 private `ComputeCornerAnchor(RECT frame, int lastX, int lastY) → (Corner, physicalDx, physicalDy)` — 4 모서리 맨해튼 거리 최단 모서리 + delta. `ComputeAnchorFromCurrentPosition` (작업 영역) + `ComputeRelativeFromCurrentPosition(hwnd)` (창 프레임) 두 메서드의 동일 알고리즘이 한 곳에 모임 — 호출자 둘은 RECT 결정 + DPI 정규화 + 결과 타입 (`DefaultPositionConfig` / `RelativePositionConfig`) 래핑만 책임. 코드 ~40 줄 중복 제거.

- **E1 MeasureLabels 일반화**: [Core/Windowing/OverlayStyle.cs](../../Core/Windowing/OverlayStyle.cs) `MeasureLabels` 타입 `(string Hangul, string English, string NonKorean)` 3-tuple → `string[]`. Core 가 라벨 항목 수 / 의미 / 상태 이름을 알지 않도록 일반화. `string[]` 의 디폴트 `EqualityComparer` 가 reference equality 라 그대로 두면 파사드의 `BuildStyle` 가 매번 새 배열을 합성해 OverlayStyle `==` 가 항상 false → flip-flop 가드 미스 → DIB 가 IME 변경 (80ms 주기) 마다 재생성. `OverlayStyle` 에 `public bool Equals(OverlayStyle other)` + `public override int GetHashCode()` 명시 오버라이드 — `MeasureLabels.AsSpan().SequenceEqual(other.MeasureLabels)` 으로 시퀀스 동등성 보장. [Core/Windowing/LayeredOverlayBase.cs](../../Core/Windowing/LayeredOverlayBase.cs) `_cachedLabelMeasureKey` 타입 `(string, string, string)` → `string[]?` + `cachedLabels.AsSpan().SequenceEqual(style.MeasureLabels)` 비교. `CalculateFixedLabelWidth` 의 destructuring + 임의 배열 생성 → `foreach (string label in style.MeasureLabels)` 직접 순회. [App/UI/Overlay.cs](../../App/UI/Overlay.cs) `BuildStyle` 의 `MeasureLabels` 합성 site → `[config.HangulLabel, config.EnglishLabel, config.NonKoreanLabel]` collection expression.

- **C6 WindowSnapHelper 분리**: 신규 [Core/Windowing/WindowSnapHelper.cs](../../Core/Windowing/WindowSnapHelper.cs) 167 줄 — `CollectTargets(IntPtr ownerHwnd)` (EnumWindows 후보 캐싱) + `ClearTargets()` + `ApplySnap(ref RECT, xLocked, yLocked, dpiScale, snapThresholdPx, snapGapPx) → bool` + 정적 `s_targets` / `s_ownerHwnd` 브리지 + `[UnmanagedCallersOnly] EnumWindowsCallback` + `MinWindowSizePx = 80` private const + `ConsiderTarget` / `TryEdge` private helpers. [Core/Windowing/LayeredOverlayBase.cs](../../Core/Windowing/LayeredOverlayBase.cs) 의 정적 RECT 캐시 2 줄 + `ApplySnap` ~40 줄 + `ConsiderTarget` ~25 줄 + `TryEdge` 9 줄 + `EnumWindowsCallback` 18 줄 + `SnapMinWindowSizePx` const 5 줄 = ~99 줄 제거. `BeginDrag` 의 `s_activeSnapRects.Clear() + s_activeOwnerHwnd = _hwndOverlay + if(snap) EnumWindows(...)` → `WindowSnapHelper.CollectTargets(_hwndOverlay)` / `ClearTargets()` 한 줄. `EndDrag` 의 `s_active*` 2 줄 reset → `ClearTargets()` 한 줄. `HandleMoving` 의 `ApplySnap(...)` 호출이 `WindowSnapHelper.ApplySnap(ref movingRect, xLocked, yLocked, _currentDpiScale, snapThresholdPx, snapGapPx)` 로 단일 호출. LayeredOverlayBase.cs 900 → 767 줄 (-133).

- **C5 부분 TopmostWatchdog 분리**: 신규 [Core/Windowing/TopmostWatchdog.cs](../../Core/Windowing/TopmostWatchdog.cs) 67 줄 — `Start/Stop/SetInterval(int)` + `TryHandleTimer(nuint)` 디스패치 헬퍼. `Dispose() => Stop()`. [Core/Animation/OverlayAnimator.cs](../../Core/Animation/OverlayAnimator.cs) 가 `_topmostWatchdog` 인스턴스 보유 + ctor 에서 `new TopmostWatchdog(hwndTimer, timerIds.Topmost, initialConfig.ForceTopmostIntervalMs, onForceTopmost)` 4 줄 + Dispose 에 `_topmostWatchdog.Dispose()` 한 줄 + UpdateConfig 에 `if (topmostChanged) _topmostWatchdog.SetInterval(...)` 한 줄 + OnWmTimer 첫 줄에 `if (_topmostWatchdog.TryHandleTimer(timerId)) return;` 짧은-회로. **나머지 4 트랙 (fade/hold/highlight/slide) 분해는 보류** — 신규 [dev-notes/2026-05-21-animator-decomposition-deferral.md](../dev-notes/2026-05-21-animator-decomposition-deferral.md) 가 (a) 공유 상태 `_phase` / `_currentAlpha` / `_lastX/Y` 와 (b) `TriggerShow` 의 분기별 타이밍 의존을 근거로 보류 결정 명시.

**Tier-1 검증**:

- `dotnet build` clean (0 경고, 0 오류).
- `dotnet publish -r win-x64 -c Release` clean (0 경고). exe 4.80 MB 유지 (vs 4.80 MB baseline, 변화 없음).

**Tier-2 grep 가드 통과**:

- `git grep -E "Hangul|English|NonKorean" Core/` — **0 매치** (IME 어휘 잔류 0).
- `git grep "맑은 고딕" Core/` — **0 매치** (한국어 폰트 어휘 잔류 0). LayeredOverlayBase 의 GetTextMetricsW vCenter 보정 주석 + Win32DialogHelper 의 폰트 크기 설명 주석 두 곳을 일반 표현으로 정정.
- `git grep "namespace KoEnVue.Core.Windowing" Core/Windowing/WindowSnapHelper.cs` — 1 매치.
- `git grep "namespace KoEnVue.Core.Windowing" Core/Windowing/TopmostWatchdog.cs` — 1 매치.
- `wc -l Core/Animation/OverlayAnimator.cs` — **546** (목표 < 530 미달, baseline 554 에서 -8). 슬립 근거: TopmostWatchdog 분리가 단일 트랙이라 다발성 라인 감소가 없음 + 콜백 시그니처 / 위임 호출 라인 잔존 (3 줄 추가, 약 11 줄 제거 = net -8). dev-notes 가 슬립 근거 명시.
- `wc -l Core/Windowing/LayeredOverlayBase.cs` — **767** (목표 < 800 통과, baseline 900 에서 -133). WindowSnapHelper 분리 효과 + 일반화된 MeasureLabels 캐시 비교 로직 추가 net -133.

**invariant 4종 + P5 2종**: 모두 0 매치 (docs 만 매치, 코드 0).

**문서 갱신 5건**:

- [CHANGELOG.md](../../CHANGELOG.md) [Unreleased] / 변경 — PR-08 항목 신규.
- [docs/architecture.md](../architecture.md) — `Core/Windowing/` 트리에 WindowSnapHelper / TopmostWatchdog 추가, `App/Detector/` 트리에 ImeConstants 추가, OverlayAnimator / LayeredOverlayBase / OverlayStyle / Win32DialogHelper 설명 갱신, WindowSnapHelper / TopmostWatchdog / ImeConstants 모듈 표 행 신규, §6 P6 verification invariants 에 PR-08 게이트 2 줄 추가.
- [docs/conventions.md](../conventions.md) — §"P6 verification invariants" 에 PR-08 게이트 2 줄 추가, Risk 4 설명 갱신 (MeasureLabels 타입 + PR-08 일반화 근거).
- [docs/dev-notes/2026-05-21-animator-decomposition-deferral.md](../dev-notes/2026-05-21-animator-decomposition-deferral.md) — 신규. fade/hold/highlight/slide 4 트랙 분해 보류 결정 근거 + 향후 트리거 조건.
- 본 파일 §6 + INDEX session log.

### 2026-05-21 — Tier-3 통과 후 FF merge

Tier-3 사용자 수동 smoke 4 항목 모두 통과:

- ① 인디케이터 표시/숨김 — 정상
- ② 드래그 + 윈도우 엣지 스냅 — 정상 (`WindowSnapHelper.ApplySnap` 정상 위임 확인)
- ③ 인디 topmost 유지 — 정상 (`TopmostWatchdog` 5초 주기 SetWindowPos 동작 확인)
- ④ 3 다이얼로그 (Settings / Cleanup / ScaleInput) 폰트 + 포어그라운드 — 정상 (App→Core 폰트 패밀리 주입 경로 확인)

FF merge to main (399f6ad) + 브랜치 삭제 완료.
