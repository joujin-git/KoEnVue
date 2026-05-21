# PR-08: Core reuse restoration + TopmostWatchdog 분리

**Status**: ⏳ pending
**Branch**: feat/pr-08-core-reuse
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

(empty)
