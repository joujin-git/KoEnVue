# Phase 3/5 — Task B: Animation 분리 (C10)

> 이 파일은 **단일 세션에서 붙여 넣어 실행**하기 위한 self-contained 프롬프트다.
> Phase 2가 끝나고 C9 커밋이 있는 상태에서 시작한다.

---

## Task

Stage 4 Task B를 수행한다. `App/UI/Animation.cs`의 **애니메이션 상태 머신 + WM_TIMER 보간 루프**를 `Core/Animation/OverlayAnimator`로 분리하고, App 쪽은 정적 facade로 남긴 채 내부에서 Core 기반 인스턴스를 보유하도록 리팩터링한다. `NonKoreanImeMode.Dim` 등 **App 도메인 로직은 Core로 이동하지 않는다**. 커밋 C10을 만든다.

## Context

- 저장소: `e:\dev\KoEnVue`
- 브랜치: `master`
- 직전 상태: Phase 2 C9 완료. `Core/Overlay/LayeredOverlayBase`가 동작 중이고 `App/UI/Overlay.cs`는 facade로 유지. Release exe는 Phase 1 기준 ±수 KB.
- 관련 문서:
  - `docs/reorg/05-stage4-core-extraction.md` Task B 섹션
  - `docs/reorg/09-risks-and-reuse.md` Risk 4 (ImeState leak), Risk 6 (UnmanagedCallersOnly entry point 충돌)
  - `CLAUDE.md` Animation/Slide 관련 Key Implementation Decisions

## Hard constraints

- **P1~P6 유지.** `git grep "KoEnVue\.App" Core/` → 0.
- Core/Animation은 `ImeState`, `AppConfig`, `DefaultConfig`를 **직접 참조하지 않는다** (Risk 4).
  - `GetTargetAlpha`의 `NonKoreanImeMode.Dim` 분기는 **App/UI/Animation**에 남기고, Core에는 raw 타겟 alpha만 전달한다.
- Core/Animation은 **`AnimationConfig`** record만 받는다. `config.EventDisplayDurationMs`, `AlwaysIdleTimeoutMs`, `FadeInMs`, `FadeOutMs`, `SlideAnimation`, `SlideSpeedMs`, `Opacity`, `IdleOpacity`, `ActiveOpacity`, `HighlightScale`, `HighlightDurationMs`, `ChangeHighlight` 등만 흡수.
- `App/UI/Animation.cs`의 static public API는 **시그니처 변경 금지**. Stage 3 시점 그대로.
- WM_TIMER ID는 Risk 6에 따라 Core에 hard-code하지 않는다. `AnimationTimerIds` record struct로 주입.
- Core/Animation은 `Overlay.Show`/`Overlay.UpdateColor` 같은 App facade를 직접 호출하지 않는다. Core/Overlay/LayeredOverlayBase를 직접 참조하거나, App이 주입하는 콜백을 호출한다.
- Release 퍼블리시 exe 크기는 Phase 2 기준 ±수 KB만 허용.

## 계획 재검토 원칙

**구현 중 계획 재검토가 필요하면 먼저 알려줘.** 에이전트가 `App/UI/Animation.cs`를 실제로 읽어본 결과, 이 프롬프트의 `AnimationConfig` 필드 구성, `AnimationTimerIds` 생성자 주입 방식, `NonKoreanImeMode.Dim` App 잔존 원칙, public static facade 시그니처 유지, `OverlayAnimator`가 `LayeredOverlayBase`와 통신하는 방식(직접 참조 vs 콜백 주입), Risk 4(ImeState/AppConfig 누출 금지) 중 하나라도 실제 코드와 중요한 불일치가 있다고 판단되면, **구현을 시작하기 전에** 메인 세션에 보고하고 사용자 판단을 받는다. Core/Animation에서 `ImeState`/`AppConfig` 참조가 불가피하다고 판단되거나 Program.cs/Overlay.cs를 함께 수정해야 한다고 판단되면 즉시 중단한다. 에이전트는 독자 판단으로 프롬프트의 계약을 바꾸지 않고, 보고 후 승인을 받을 때까지 기존 가정을 그대로 따른다.

## 0. Sanity check

- `git status` clean.
- `git log --oneline -3`에 C9 커밋 존재.
- `git grep "KoEnVue\.App" Core/` → 0.
- `git grep "AppConfig" Core/` → 0.
- `git grep "ImeState\|AppConfig\|DefaultConfig" Core/Overlay/` → 0 (Phase 2에서 설정된 가드 유지 확인).
- `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0.
- `dotnet build` 성공.

## 1. 작업 에이전트 구성

**1개의 general-purpose 에이전트**에게 구현을 위임한다.

### Agent — Task B 구현

프롬프트 지시:

- 목적: `App/UI/Animation.cs`의 상태 머신 + WM_TIMER 루프 + alpha 보간 로직을 `Core/Animation/OverlayAnimator`로 옮기고, App 쪽은 static facade + 내부 instance 보유 형태로 리팩터링.
- 필수 읽기:
  - `App/UI/Animation.cs` 전체
  - `App/UI/Overlay.cs` (Phase 2 결과 — facade 구조 파악)
  - `Core/Overlay/LayeredOverlayBase.cs` (Phase 2 결과)
  - `Core/Animation/OverlayAnimator.cs` + `Core/Animation/AnimationConfig.cs` + `Core/Animation/AnimationTimerIds.cs` (Phase 1 skeleton)
  - `App/Models/AppConfig.cs` (Animation 관련 필드)
  - `App/UI/AppMessages.cs`
  - `docs/reorg/05-stage4-core-extraction.md` Task B 상세
  - `docs/reorg/09-risks-and-reuse.md` Risk 4/6
- 구현 지침:
  1. `OverlayAnimator` (class):
     - 생성자: `(LayeredOverlayBase overlay, AnimationConfig config, AnimationTimerIds timerIds, IntPtr hwnd)`.
     - public 메서드: `TriggerShow(byte targetAlpha, bool highlight)`, `TriggerHide(bool forceHidden)`, `HandleTimer(nuint timerId)` (App facade가 `HandleTimer(nuint, AppConfig)`로 받아 그대로 전달하므로 `nuint`로 맞춘다), `UpdateConfig(AnimationConfig config)` 등.
     - 내부 필드: 현재 상태(enum), 보간 시작/종료 시각, slide 진행도 등.
     - `ImeState`를 직접 받지 않는다. 색상 및 alpha 결정은 App에서.
  2. `AnimationConfig` record:
     - 위 hard constraint 참조.
     - `NonKoreanImeMode`는 포함하지 않는다. 그 분기는 App에서 수행.
  3. `AnimationTimerIds` record struct:
     - 실제 타이머 ID는 `App/UI/AppMessages.cs`에 `TIMER_ID_FADE`, `TIMER_ID_HOLD`, `TIMER_ID_HIGHLIGHT`, `TIMER_ID_SLIDE`, `TIMER_ID_TOPMOST` 5개가 정의되어 있다. record struct 필드는 이 5개를 1:1 매핑하고 타입은 `nuint`로 맞춘다 (App facade의 `HandleTimer(nuint, ...)`와 타입 일치).
     - `TIMER_ID_TOPMOST`는 topmost 재설정 타이머라 순수 애니메이션은 아니지만 WM_TIMER 라우팅 일원화 관점에서 기본적으로 같은 record에 포함한다. Agent가 Core로 옮기기엔 부자연스럽다고 판단하면 App에 잔존시켜도 된다 — 그 경우 record에서 제외하고 프롬프트에 근거를 남긴다.
     - App/UI/Animation에서 기존 `AppMessages.TIMER_ID_*` 값을 그대로 주입.
  4. `App/UI/Animation.cs` 리팩터링:
     - `_animator` 정적 필드로 `OverlayAnimator` 인스턴스 보유.
     - 기존 static public 메서드는 `_animator.*`로 위임.
     - 기존 facade 시그니처 `TriggerShow(int x, int y, ImeState state, AppConfig config, bool imeChanged)`, `TriggerHide(AppConfig config, bool forceHidden = false)`, `HandleTimer(nuint timerId, AppConfig config)`, `Initialize(IntPtr hwndMain, IntPtr hwndOverlay, AppConfig config)`는 전부 유지. App facade 내부에서 `ImeState → bg/fg/alpha` 결정과 `NonKoreanIme.Hide/Dim` 분기를 수행한 뒤 Core `OverlayAnimator`에 넘긴다.
     - `NonKoreanImeMode.Dim` 분기는 `GetTargetAlpha` 헬퍼를 App에 유지.
     - `Animation.Initialize(hwndMain, hwndOverlay, config)` 시그니처는 유지.
     - `HandleConfigChanged`에서 `AnimationConfig` 새로 만들어 `_animator.UpdateConfig(..)` 호출.
  5. WM_TIMER 메시지 라우팅: `Program.cs`는 `Animation.HandleTimer(id)`를 부르고, 그 안에서 `_animator.HandleTimer(id)`로 포워딩. Program.cs는 수정 없음.
  6. **Risk 4 준수**: Core/Animation에 `using KoEnVue.App.*` 금지. `ImeState`, `AppConfig`, `DefaultConfig` 이름 금지.
  7. **Risk 6 준수**: Core/Animation에 새로운 `[UnmanagedCallersOnly]` 멤버가 생기지 않도록 주의. 생긴다면 EntryPoint 명명 고유화.
  8. `App/UI/Animation.cs`의 public static 시그니처 변경 없음. `Program.cs`, `Tray.cs`, `Overlay.cs` 수정 없음.
- 보고 형식: ≤400단어 요약 + 파일 목록 + 의사결정 포인트 + 미결 질문.

## 2. 본 세션 작업 — 에이전트 결과 검토

1. `Read`로 변경 파일(`App/UI/Animation.cs`, `Core/Animation/*.cs`) 전체 일독.
2. `git grep "KoEnVue\.App" Core/` → 0.
3. `git grep "using KoEnVue\.App" Core/Animation/` → 0.
4. `git grep "ImeState\|AppConfig\|DefaultConfig\|NonKoreanImeMode" Core/Animation/` → 0.
5. `git diff --name-only`에서 `Program.cs`, `App/UI/Tray.cs`, `App/UI/Overlay.cs`가 **수정되지 않았는지** 확인. 수정되었다면 facade 변경이니 원상복구 요청.

## 3. 빌드·스모크

- `dotnet build` 성공.
- `dotnet publish -r win-x64 -c Release` 성공.
  - exe 크기 확인 — Phase 2 기준 ±수 KB 편차만 허용.
- Release exe 실행:
  - 인디케이터가 페이드 인/아웃 하는지.
  - 한/영 전환 시 highlight 애니메이션이 동작하는지.
  - Always 모드에서 이벤트 종료 후 `IdleOpacity`로 dim 되는지.
  - `NonKoreanImeMode=Dim`으로 설정 후 비한국어 IME에서 dim 효과가 유지되는지 (App 로직 레이어에 남아 있어야 함).
  - 슬라이드 애니메이션 ON인 상태에서 포커스 이동 시 ease-out 이동이 되는지.

## 4. 버그 시 대응

- 에이전트에게 수정 지시 (1~2회 재시도).
- 실패 시 `git restore .` 후 원인 분석 → 에이전트 프롬프트 재작성.

## 5. 검증

모두 통과해야 커밋. 실패 시 `git restore .`로 폐기 후 재시도.

1. `git grep "KoEnVue\.App" Core/` → 0.
2. `git grep "using KoEnVue\.App" Core/Animation/` → 0.
3. `git grep "ImeState\|AppConfig\|DefaultConfig\|NonKoreanImeMode" Core/Animation/` → 0.
4. `git grep "AppConfig" Core/` → 0 (Risk 4 광역 재확인).
5. `dotnet build` / `dotnet publish -r win-x64 -c Release` 성공.
6. publish exe 크기 변화 +50KB 미만.
7. `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0 (Risk 5).
8. 스모크 매트릭스 (3번 섹션) 통과.

## 6. 커밋

- 커밋 메시지: `refactor(reorg): Stage 4 Task B — Animation state machine → Core/Animation/OverlayAnimator (C10)`
- 본문:
  - OverlayAnimator + AnimationConfig + AnimationTimerIds 신설 요약
  - App/UI/Animation.cs facade 유지
  - `NonKoreanImeMode.Dim` 분기는 App에 잔존
  - Risk 4/6 준수 확인
  - exe 크기 수치

## 7. Phase 종료 시 사용자에게 보고

- ≤200단어.
- 분리된 파일 목록(`Core/Animation/*`, `App/UI/Animation.cs` 변경 요약).
- exe 크기 수치 (Phase 2 대비 증감).
- Risk 4/5/6 grep 결과.
- 스모크 체크리스트.
- 다음 Phase에서 Task C(Settings) 수행 안내.
