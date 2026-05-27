# 부팅 메인 인디 깜박임 — SnapToTargetAlpha Fade KillTimer (2026-05-27)

> **결과**: `Core/Animation/OverlayAnimator.cs:SnapToTargetAlpha()` 에 FadingIn 중 호출 시 Fade `KillTimer` + `_phase = Holding` + Hold 타이머 재등록 11 줄 추가. PR-A 단독.

## 무엇 (What)

부팅 직후 메인 인디케이터가 잠깐 보였다가 사라지는 회귀 (이전 명명 회귀 #2/#3). 진단 로그:

```
30.368 TriggerShow wasHidden=True       ← 1번째: Hidden→FadingIn
30.375 SW_SHOW
30.376 UpdateAlpha=242                   ← 2번째 TriggerShow 의 SnapToTargetAlpha
30.376 TriggerShow wasHidden=False imeChanged=True   ← 2번째
30.377 TriggerShow wasHidden=False imeChanged=False  ← 3번째
30.377 UpdateAlpha=15                    ← Fade 틱이 보간으로 alpha 되돌림  ← 깜박임
30.423 UpdateAlpha=89
30.470 UpdateAlpha=165
30.546 UpdateAlpha=242                   ← 페이드인 완료 (그러나 사용자는 "사라졌다 다시 나타남" 으로 인식)
```

Detection thread 가 부팅 시점에 `WM_POSITION_UPDATED + WM_IME_STATE_CHANGED + WM_FOCUS_CHANGED` 3 메시지를 1 ms 내에 연달아 post → 메인 스레드가 `Animation.TriggerShow` 를 3 회 호출:

1. **1번째**: `_phase = Hidden` → `BeginFadeIn(0)` → `StartFade(0, 244, 150)` + Fade 타이머 등록. `_phase = FadingIn`.
2. **2번째** (`_phase = FadingIn`): line 183 분기 → Hold 타이머 재등록 + `SnapToTargetAlpha()`.
3. **3번째** (`_phase = FadingIn` 같음): 같은 분기 재진입, 동일 처리.

`SnapToTargetAlpha()` 가 `_currentAlpha = _targetAlpha = 244` 로 set 하지만 **Fade 타이머는 안 죽임**. 다음 Fade 틱 (16 ms 후) 이 `_fadeStartAlpha = 0` 부터 보간한 작은 값 (예 alpha = 15) 으로 `_onAlphaChange` 호출 → 사용자 가시 깜박임.

**fix**: `SnapToTargetAlpha()` 끝에 분기 추가:
```csharp
if (_phase == AnimPhase.FadingIn)
{
    User32.KillTimer(_hwndTimer, _timerIds.Fade);
    _phase = AnimPhase.Holding;
    User32.SetTimer(_hwndTimer, _timerIds.Hold, (uint)_holdDurationMs, IntPtr.Zero);
}
```

Hold 타이머 재등록은 idempotent (같은 ID 의 `SetTimer` 는 `KillTimer` + 새 등록과 동일 — MSDN 명시).

## 왜 (Why)

### 회귀의 1차 발견 + revert 이력

본 회귀는 v0.9.4.0 봉인 (커밋 `a6c3084`) 직후 사용자 보고로 발견. cursor 인디케이터 도입 작업 (feat/cursor-tray 브랜치, PR1~PR10) 도중 6 가설 (A~F) 시도 → 모두 미해결 또는 새 회귀 → 사용자 판단으로 작업 트리 8 파일 일괄 revert. 미커밋 변경 폐기. 본 dev-note 는 `feat/v094-integration` 브랜치에만 존재하는 [2026-05-20-post-pr10-attempts-reverted.md](https://github.com/joujin-git/KoEnVue/blob/feat/v094-integration/docs/dev-notes/2026-05-20-post-pr10-attempts-reverted.md) 의 가설 E 진단 결과를 main 으로 끌어와 fix 형태로 적용한다.

### 가설 E 의 신뢰도

- **진단 로그가 단일 시간축에서 1ms 정밀도로 race 를 재현**: alpha 244 → 15 의 reverse 가 메시지 큐 순서와 정확히 일치.
- **수정 위치가 명확**: `SnapToTargetAlpha()` 가 Fade phase 를 종료하지 않는 게 의도와 다름 — 그 의도가 "alpha 즉시 target 으로 set 해 다음 프레임까지 깜박임 억제" (line 252-256 주석 명시) 인데, Fade 타이머가 살아있으면 의도와 정반대 동작.
- **fix 자체의 단순성**: 11 줄. 다른 분기에 영향 없음 (TriggerShow Idle 분기에서도 `_phase = FadingIn` 직후 SnapToTargetAlpha 호출 → 본 fix 가 즉시 Holding 으로 전이 + Hold 타이머 등록 → 의도와 정확히 일치).

### 왜 단독 PR 인가 — 격리 검증

본 fix 가 효과 있는지 확인은 사용자 부팅 가시 검증 (×3 회 부팅 + 깜박임 0 건). 이를 cursor 인디 도입 작업 (10 파일, ~800 LOC) 과 묶으면 fix 효과를 격리 측정 불가. dev-notes 명시 "fix 효과 미검증 상태로 revert" 함정을 회피한다. 사용자 검증 통과 후에야 cursor 인디 PR-B 진입.

## 대안 (Alternatives considered)

### A. TriggerShow 분기에서 SnapToTargetAlpha 호출 전에 Fade `KillTimer`

```csharp
if (_phase == AnimPhase.Holding || _phase == AnimPhase.FadingIn)
{
    User32.KillTimer(_hwndTimer, _timerIds.Hold);
    User32.SetTimer(_hwndTimer, _timerIds.Hold, ...);
    User32.KillTimer(_hwndTimer, _timerIds.Fade);  // ← 추가
    _phase = AnimPhase.Holding;                     // ← 추가
    ...
    SnapToTargetAlpha();
}
```

**기각**: 책임 위치가 분기 호출자 쪽으로 옮겨감. 같은 패턴이 Idle 분기에도 필요 → 두 곳 중복. SnapToTargetAlpha 내부로 옮기면 호출자 분기 단순 유지 + 호출자가 phase 일관성 신경 안 써도 됨.

### B. Detection thread 메시지 폭주 자체 줄이기 (가설 F)

부팅 시 `WM_POSITION_UPDATED + WM_IME_STATE_CHANGED + WM_FOCUS_CHANGED` 가 1ms 내 연쇄 발사되는 게 근본 — `HandlePositionUpdated` 가 `_indicatorVisible = true` 세팅한 직후 IME/Focus 메시지 1 tick suppress.

**기각**: Detection thread 의 메시지 정책 변경은 영향 범위가 큼 (모든 IME 전환 케이스). 본 fix 는 메인 스레드의 phase 관리 일관성만 보강 → 영향 면 좁음. 가설 F 는 본 fix 실패 시 다음 후보.

### C. `BeginFadeIn(_currentAlpha)` 가 fromAlpha 와 _currentAlpha 일치 보장

`BeginFadeIn` 이 `_currentAlpha` 인자를 받지만 본질은 `StartFade(fromAlpha, _targetAlpha, ...)` — re-entry 시 같은 alpha 부터 보간 시작이라 부드럽지만 본 race 와 무관 (race 는 Fade 타이머 leak 그 자체).

## 회귀 위험

### R1 — Holding 으로의 강제 전이가 다른 트랙 (Highlight / Slide) 에 영향

`_phase` 는 fade/hold 트랙만 관리하는 enum. Highlight (`_highlightActive`) / Slide (`_slideActive`) 는 별개 flag 라 phase 변화에 영향 받지 않음. **영향 없음**.

### R2 — Hold 타이머 재등록 중복 (Holding/FadingIn 분기)

line 183 분기에서 `SetTimer(Hold, _holdDurationMs)` 한 직후 SnapToTargetAlpha 가 또 같은 호출. MSDN: "If the timer identification value already exists, the existing timer is replaced by the new one." → 중복 호출 안전 (effectively no-op for hold timer behavior). **영향 없음**.

### R3 — Idle 분기에서 SnapToTargetAlpha 가 FadingIn 즉시 종료

Idle 분기 (line 196-206): `BeginFadeIn(_currentAlpha)` → `_phase = FadingIn` → `SnapToTargetAlpha()`. 본 fix 가 phase 를 즉시 Holding 으로 전이 → 사용자 가시: 페이드 인 애니메이션 skip + 즉시 target alpha 표시. 

**의도 부합**: Idle 탈출은 사용자 입력 (IME 토글 등) 에 즉시 반응해야 함 — 페이드 인 150ms 가 의미 없음. 기존 코드의 line 252-256 주석 "다음 HandleFadeTimer 프레임까지의 깜빡임을 억제" 도 동일 의도 (단지 Fade 타이머 정리를 잊었던 것). **개선**.

### R4 — Hidden 분기 (1번째 TriggerShow) 는 SnapToTargetAlpha 호출 안 함

Hidden 분기 (line 218-235) 는 `BeginFadeIn(0)` 만 호출. SnapToTargetAlpha 없음 → 본 fix 가 1번째 TriggerShow 에 영향 0. 정상 페이드 인이 그대로 시작. 2번째 이후 TriggerShow 에서만 fix 효과 발동. **의도 부합**.

### R5 — `_holdDurationMs` 가 0 또는 미초기화 상태

Idle 분기 진입 시 line 172-174 에서 `_holdDurationMs` 가 새로 계산됨 (`AlwaysIdleTimeoutMs` 또는 `EventDisplayDurationMs`). SnapToTargetAlpha 가 호출되는 모든 시점에 `_holdDurationMs` 는 유효 값. **영향 없음**.

## 측정 계획

본 fix 의 효과는 **사용자 부팅 가시 검증**:

1. KoEnVue 실행 중 종료 → 즉시 재부팅 → 메인 인디가 한 번에 페이드 인 (중간 alpha 떨어짐 0 회)
2. 위 1을 ×3 회 반복
3. 추가: 한/영 토글 5회 + cursor 모드 OFF/ON 토글 (cursor PR-B 머지 전이라 이 단계는 skip — PR-B 머지 후 검증)
4. 깜박임 1회라도 관측 시 PR-A revert + 가설 F (메시지 폭주) 영역 별도 조사

코드 측 텔레메트리: 추가 없음. dev-note 의 진단 로그 패턴 (`alpha=244 → 15`) 이 사용자 로그에 재출현하면 fix 미작동 신호.

## 관련 자료

- 가설 E 1차 진단: `feat/v094-integration:docs/dev-notes/2026-05-20-post-pr10-attempts-reverted.md` (main 미반영)
- 클릭 통과 시도 이력: [2026-05-15-click-through-attempts.md](2026-05-15-click-through-attempts.md)
- 코드: [Core/Animation/OverlayAnimator.cs:257](../../Core/Animation/OverlayAnimator.cs#L257) `SnapToTargetAlpha`
- 후속: PR-B (cursor 인디케이터 신규 추가) — 본 PR 사용자 검증 통과 후 진입
