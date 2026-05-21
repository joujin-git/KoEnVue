# OverlayAnimator 부분 분해 — TopmostWatchdog 만 격리, 나머지 4 트랙 보류 (2026-05-21)

> **결과**: PR-08 에서 `TopmostWatchdog` 만 [Core/Windowing/TopmostWatchdog.cs](../../Core/Windowing/TopmostWatchdog.cs) 로 격리. fade / hold / highlight / slide 4 트랙은 현재 `OverlayAnimator` 안에 유지.

## 배경

4-라운드 코드 리뷰의 **C5** 항목 — `OverlayAnimator` 가 5 개의 시간 트랙 (fade · hold · highlight · slide · topmost) 을 한 클래스에서 관리하던 것을 트랙 단위로 분해하자는 제안이 있었다. `OverlayAnimator.cs` 의 라인 수가 ~550 까지 자라난 시점.

## 결정

**TopmostWatchdog 만 분리**. 나머지 4 트랙은 현 상태 유지.

### 분리한 트랙 — TopmostWatchdog

`SetWindowPos(HWND_TOPMOST)` 재적용은 다음 조건을 만족해 안전:

- **시간 / 상태 의존성 없음**: 5 초마다 무조건 호출. fade phase 도, alpha 도, slide 진행도 모름.
- **다른 트랙과 상호작용 없음**: 다른 트랙의 timer ID 와 충돌하지 않고, 콜백(`_onForceTopmost`) 이 alpha / 크기 / 위치를 건드리지 않음.
- **분해해도 회귀 위험 최소**: timer ID 만 caller 가 제공하면 됨 (TopmostWatchdog 가 자기 ID 만 골라 dispatch).

→ 분리 후 OverlayAnimator 는 fade / hold / highlight / slide 4 트랙만 책임.

### 보류한 4 트랙 — fade / hold / highlight / slide

다음 이유로 추가 분해는 **postmortem 의 fragile 영역** 으로 분류되어 보류:

1. **공유 상태**: `_phase` (Hidden/FadingIn/Holding/FadingOut/Idle), `_currentAlpha`, `_targetAlpha`, `_lastX/Y` 가 4 트랙 모두에 걸쳐 read/write 된다. 트랙별 클래스로 쪼개면 이 상태를 어떤 형태로든 공유해야 하는데, 그러면 사실상 single class 의 fragmentation 일 뿐.
2. **타이밍 의존**: `TriggerShow` 의 분기(`Holding`/`FadingIn`/`Idle`/`FadingOut`/`Hidden`) 마다 `slide` 와 `highlight` 시작 여부 / `BeginFadeIn` 호출 여부가 다르다. 트랙을 클래스로 격리하면 이 컨디션 머신이 어디에 있어야 할지 애매해진다.
3. **dev-notes 의 두 postmortem** — [2026-05-15-click-through-attempts](./2026-05-15-click-through-attempts.md), [2026-05-20-post-pr10-attempts-reverted](./2026-05-20-post-pr10-attempts-reverted.md) — 가 본 영역의 회귀 민감도를 명시한다. 시각 효과 동시 발화 (예: slide 중 highlight 트리거) 시점에 단 1 프레임의 픽셀 어긋남도 사용자가 알아챈다.

→ 향후 4 트랙 분해를 다시 검토할 때는 **그 전에 충분한 자동 테스트 베이스** (예: timer mock + alpha sequence assertion) 가 갖춰져야 한다. 현재는 그 베이스가 없다.

## 측정

PR-08 후:

- `Core/Animation/OverlayAnimator.cs`: 554 → 546 줄 (-8 줄). 목표 < 530 미달.
- `Core/Windowing/LayeredOverlayBase.cs`: 900 → 767 줄 (-133 줄). 목표 < 800 통과 — WindowSnapHelper 분리 효과.

OverlayAnimator 라인 슬립은 본 결정의 비용 — TopmostWatchdog 분리 효과는 net ~8 줄 감소에 그쳐, ApplySnap 처럼 다발성 분리가 가능한 영역이 아님이 확인됨.

## 향후 트리거

다음 조건이 모두 충족되면 4 트랙 분해를 재검토:

- 타이머 mock + 상태 머신 인스펙션이 가능한 자동 테스트 베이스 (PR-10 의 후속 단계로 가능성)
- fade / slide / highlight 의 동시 발화 회귀를 측정할 수 있는 픽셀-단위 비교 도구
- 별도 PR 에서 OverlayAnimator 가 600 줄을 다시 넘어가면서 새로운 트랙이 추가되는 경우
