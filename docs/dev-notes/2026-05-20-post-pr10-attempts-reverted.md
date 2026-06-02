# 2026-05-20 post-PR10 사용자 피드백 fix 시도 — 전체 revert

## 결론

v0.9.4.0 머지 (커밋 `a6c3084`, post-PR10 3 fix) 이후 사용자가 보고한 추가 회귀 5건을
세션 내에서 fix 시도. 작업 도중 가설/원인 추정이 여러 번 빗나가고 새 회귀가 거듭 발생.
최종적으로 사용자 판단으로 본 세션의 미커밋 작업 트리 변경 전체를 revert. 본 문서는
시도된 가설·코드 변경·실패 원인을 추후 재시도 시 같은 함정에 빠지지 않도록 남긴다.

복원 시작점: `a6c3084 fix(post-PR10): 3 user feedback fixes` — 트레이 더블클릭 MoveMode
제거 + SC_MOVE|2 키보드 모드 + 메뉴 회색 완화 커밋. 본 세션에서 그 위에 쌓은
미커밋 변경 8 파일은 모두 폐기.

## 사용자 보고 회귀 5건

| # | 증상 | 시도 결과 |
|---|------|----------|
| 1 | MoveMode 진입 시 메인 인디 안 보임 (트레이 우클릭 → "인디 위치 이동" 메뉴 클릭) | 부분 개선 후 간헐적 재발 — 미해결 |
| 2 | 부팅 직후 메인 인디 잠깐 보였다 사라짐 | 미해결 (→ 2026-06-02 디바운스로 해결, 아래 후기) |
| 3 | "커서 인디 보임" 토글 ON 상태에서 부팅 시 메인 인디 안 보였다가 다른 앱 클릭하면 보임 | 미해결 (#2 와 동일 회귀로 추정) |

> **후기 (2026-06-02)** — 회귀 #2/#3 "보였다 사라짐" 계열은 같은 증상이 파일 탐색기 클릭
> 시에도 재현됐고, 마침내 근본원인이 **감지 스레드 입력(`hwndFocus`)의 진동(flip-flop)** 임이
> 규명됐다. 본 세션의 가설 E/F (애니 엔진 `SnapToTargetAlpha` / 메시지 폭주) 는 *입력 측*
> 원인을 놓친 오진이었다. 시스템 필터 HIDE 에 N=3 폴링 디바운스를 걸어 단발 진동을 흡수해
> 해결. 상세: [2026-06-02-explorer-hide-flipflop-debounce.md](2026-06-02-explorer-hide-flipflop-debounce.md).
| 4 | 도넛 색상 / 투명도가 메인 인디 IdleOpacity 와 불일치 | 변경 적용 (revert) |
| 5 | 도넛 표시 방식 변경 — 외곽선 제거 + gradient fade-out | 변경 적용 (revert) |

## 시도된 가설과 코드 변경 (모두 revert 됨)

### 가설 A — `LayeredOverlayBase.Show` 의 `ShowWindow` 누락

**전제**: `Hide()` 는 `SW_HIDE` 호출하는데 `Show()` 는 `_isVisible` 플래그만 set 하고
`ShowWindow` 미호출. 메인 인디는 `Animation.TriggerShow` 의 `wasHidden` 분기가 `SW_SHOW`
로 우회했지만 `CursorOverlay` / `EnsureOverlayVisibleForMoveMode` 직접 경로는 미호출.

**변경**: `Core/Windowing/LayeredOverlayBase.cs` `Show()` 끝에 `SW_SHOWNOACTIVATE` 추가.
새 상수 `Core/Native/Win32Types.cs:SW_SHOWNOACTIVATE = 4` 도입.

**결과**: 커서 도넛 자체는 시각 확인 가능해졌으나 부팅 직후 메인 인디 안 보이는 새 회귀
발생. **이론**: layered window 가 `UpdateLayeredWindow` 로 비트맵 세팅되기 *전* 에
`ShowWindow` 가 먼저 들어가면 OS 가 "비트맵 없이 visible" 상태를 캐싱해 후속
`UpdateLayeredWindow` 가 화면에 반영되지 않는 케이스 — 그러나 후속 로그 분석 결과
회귀의 진짜 원인이 아니었음.

**리팩토링 시도**: `Show()` 의 `ShowWindow` 제거 + 별도 `ShowWindowNoActivate()` 메서드
신설 → 호출자 (`CursorOverlay.ShowInternal`) 가 `Render` 직후 명시 호출. 효과 없음.

### 가설 B — `OverlayAnimator` Fade/Hold 타이머 sizemove 중 발화 (회귀 #1)

**전제**: MoveMode 진입 시 사용자 우클릭으로 메뉴 열기 직전 detection thread 가
인디 hide 트리거 → 페이드아웃 진행 중 메뉴 클릭이 들어옴. `EnsureOverlayVisibleForMoveMode`
가 `Overlay.Show + UpdateAlpha(255)` 로 끌어올려도 sizemove 모달 중 WM_TIMER 가 발화해
`OverlayAnimator` 의 `Fade` 타이머가 alpha 를 다시 감소.

**변경**:
- `Core/Animation/OverlayAnimator.cs`: `SuspendForOverride()` 메서드 신규 — Fade/Hold/
  Highlight/Slide 타이머 일괄 해제 + alpha 를 active target (255) 으로 스냅 +
  `_phase = Holding` (Hold 타이머 미등록 → MoveMode 동안 무한 유지).
- `App/UI/Animation.cs`: `Animation.SuspendForOverride(config)` 파사드 신규.
- `Program.cs:EnterMoveMode`: `EnsureOverlayVisibleForMoveMode` 직후 `Animation.SuspendForOverride(_config)` 호출.
- `Program.cs:ExitMoveMode` 끝에 `Animation.TriggerShow(...)` 로 자연 복원.

**결과**: 사용자 보고 "드물기는 하지만 어쩔 때는 또 보이기도 함" → 부분 개선이지만 완전
해소 안 됨. 회귀의 일부분만 해결한 것으로 추정.

### 가설 C — 부팅 시 커서 도넛 z-order 가 메인 인디 가림 (회귀 #2, #3)

**전제**: 두 오버레이 모두 `WS_EX_TOPMOST`. 마지막에 `SW_SHOW` 된 윈도우가 TOPMOST
z-밴드 안에서 위. 커서 도넛 `SW_SHOWNOACTIVATE` 가 메인 인디 위로 올라옴.

**변경**: `App/UI/CursorOverlay.cs:ShowInternal` 끝에 `Overlay.ForceTopmost()` 호출 추가
→ 메인 인디를 z-order 최상으로 복귀.

**결과**: **가설 자체가 틀림** — 사용자 확인: "마우스 커서와 메인 인디 근처에 있지 않음".
로그 검증: 커서 위치 `(-1305, 911)`, 메인 인디 `(-220, 1302)`, 다른 모니터, 겹침 없음.
ForceTopmost 호출 제거.

### 가설 D — Overlay.ForceTopmost 의 `SetWindowPos` 가 페이드인 중인 layered window 상태를 깨뜨림

**전제**: `SetWindowPos(HWND_TOPMOST, NOMOVE|NOSIZE|NOACTIVATE)` 가 fade-in 중인 layered
window 에 영향.

**변경**: `Overlay.ForceTopmost()` 호출 제거 (가설 C 의 변경 되돌리기).

**결과**: 회귀 #2, #3 여전. ForceTopmost 가 원인 아님.

### 가설 E — `SnapToTargetAlpha` 가 Fade 타이머 안 죽임 (회귀 #2, #3)

**전제**: 부팅 시 detection thread 가 `WM_POSITION_UPDATED + WM_IME_STATE_CHANGED +
WM_FOCUS_CHANGED` 3 메시지를 연달아 post → 메인 스레드가 `Animation.TriggerShow` 를 3번
호출. 1번째는 `Hidden → FadingIn` 으로 `StartFade(0 → 242, 150ms)` 등록 + Fade 타이머
시작. 2번째 (FadingIn 재진입) 에서 `SnapToTargetAlpha` 가 alpha 를 즉시 242 로 스냅하지만
Fade 타이머는 안 죽임. 다음 Fade 틱이 `_fadeStartAlpha = 0` 부터 보간한 alpha = 15
(작은 값) 로 되돌림 → 사용자가 "보였다가 사라짐" 으로 인식.

**진단 로그**:
```
30.368 TriggerShow wasHidden=True       ← 1번째: Hidden→FadingIn
30.375 SW_SHOW
30.376 UpdateAlpha=242                   ← 2번째 TriggerShow 의 SnapToTargetAlpha
30.376 TriggerShow wasHidden=False imeChanged=True   ← 2번째
30.377 TriggerShow wasHidden=False imeChanged=False  ← 3번째
30.377 UpdateAlpha=15                    ← Fade 틱이 보간으로 alpha 되돌림
30.423 UpdateAlpha=89
30.470 UpdateAlpha=165
...
30.546 UpdateAlpha=242                   ← 페이드인 완료
```

**변경**: `Core/Animation/OverlayAnimator.cs:SnapToTargetAlpha()` 에 Fade `KillTimer` +
`_phase = Holding` 강제 추가.

**결과**: 사용자 보고 "안 고쳐졌어". 진단 로그 재추가 시도하다가 사용자 중단 — fix 효과
미검증.

**의문**: 동일 코드 경로가 cursor donut OFF 케이스에서도 실행되는데 사용자는 OFF 시
"처음부터 잘 보임" 으로 보고. 차이가 timing 인지 perception 인지 미검증 — 더 깊은
원인이 있을 가능성. 다른 layered window 가 messages queue 점유 시 fade-in 알파
보간이 사용자가 인지할 수 있는 시간 동안 낮은 값에 머무를 수 있다는 게 추정이지만 검증 안 됨.

### 가설 F — `OverlayAnimator` 의 `SnapToTargetAlpha` fix 가 부족 (다음 단계)

위 가설 E fix 의 부족함 추정:
- `BeginFadeIn` 자체에서 `_currentAlpha` 와 `fromAlpha` 불일치 가능 (re-entry 시
  `_currentAlpha = 242` 인데 `BeginFadeIn(_currentAlpha)` 가 호출되면 `from = 242` →
  스냅 후 페이드 재시작은 의도가 아님).
- detection thread 의 부팅 시 메시지 폭주 자체를 줄이는 방향 (PositionUpdated 만
  처리하고 IME / Focus 는 동일 tick 의 effect 가 다르지 않으면 skip) — 더 근본적인 접근.

본 세션에선 끝까지 들여다보지 못함.

## 회귀 #4, #5 (도넛 시각) 변경 사항

| 파일 | 변경 |
|------|------|
| `App/UI/CursorOverlay.cs:BuildStyle` | `baseAlpha` 계산을 `config.CursorAlpha == 0.85` 디폴트 시 `config.IdleOpacity` 사용하도록 변경 — 메인 인디 dim-idle 알파와 일치 |
| `App/UI/CursorRenderer.cs` | 외곽선 제거 (`outlineCov = 0`) + 도넛 외경 너머 25% 확장 영역의 gradient fade-out 추가 |

revert 시 함께 폐기됨.

## 회귀 #1 (MoveMode 인디 안 보임) 시도된 우회책

- `Program.cs:EnterMoveMode`: `EnableInteractive` 직후 명시 `ShowWindow(SW_SHOWNOACTIVATE)`
  + `UpdateColor` + `SetWindowPos(HWND_TOPMOST)` + `SetForegroundWindow` 시퀀스.
- `EnsureOverlayVisibleForMoveMode`: `Overlay.IsVisible` early return 제거 + `UpdateAlpha(255)` 명시.
- `Program.cs:HandleTrayCallback`: 트레이 더블클릭 race wait 코드 잔존분 제거 (post-PR10 의 트레이
  더블클릭 MoveMode 제거 보강).
- `Tray.cs`: `moveModeDisabled` 조건 단순화 (SystemFilter.ShouldHide 제거 — 메뉴 회색 회귀 회피).

이 변경들도 모두 revert 됨.

## 절대 다시 시도하지 말 것

1. **`LayeredOverlayBase.Show()` 에 `ShowWindow` 호출 추가** — `Render` (UpdateLayeredWindow) 전에
   `ShowWindow` 가 먼저 들어가면 일부 환경에서 layered window 가 비트맵 없이 캐싱돼 후속
   `UpdateLayeredWindow` 가 화면에 반영되지 않는 케이스 추정 (확정 검증 안 됨, 그러나 회귀 발생 사실은 확인됨).
   호출자가 명시 호출하는 패턴이 안전.

2. **`Overlay.ForceTopmost()` 를 커서 도넛 `ShowInternal` 마다 호출** — 의도와 무관한 회귀를
   만들 가능성. 실제 z-order 충돌이 발생하는 케이스만 좁혀서 호출.

3. **TriggerHide 의 `Environment.StackTrace` 진단 로그** — `Logger.Debug` 가 자동 stack
   include 안 하므로 명시 가능하나 NativeAOT 환경에서 심볼 디리졸브가 안 돼 `+0x5b5c1`
   같은 RVA 만 출력. 다른 디버깅 수단을 우선.

## 다음 시도 시 권장 순서

1. **회귀 #2/#3 (부팅 깜박임)** — `OverlayAnimator.SnapToTargetAlpha` fix 단독 적용 후 진단
   로그로 alpha 트레이스 재수집해 fix 효과 검증부터. 검증 안 되면 detection thread
   메시지 폭주 자체를 줄이는 방향 (`HandlePositionUpdated` 가 `_indicatorVisible = true`
   세팅한 직후 IME/Focus 메시지가 다시 TriggerShow 를 호출하는 경로를 1 tick suppress).

2. **회귀 #1 (MoveMode 인디)** — `Animation.SuspendForOverride` 만 단독 검증.
   `EnsureOverlayVisibleForMoveMode` 의 명시 `ShowWindow` + `SetWindowPos` 시퀀스는
   원인 미확인 상태에서 시도하지 말 것 — 다른 회귀를 추가할 가능성.

3. **회귀 #4/#5 (도넛 시각)** — 위 2건 안정화 후 별도 PR 로 분리.

## 참고

- 본 세션 시도된 코드 변경 8 파일은 `git diff a6c3084..` 로 복구 가능 (작업 트리에서
  revert 됐어도 reflog 에 남아 있음). 필요 시 `git stash` 또는 별도 branch 로 보존 후 작업 권장.
- 푸시 안 된 v0.9.4.0 작업 자체는 `a6c3084` 까지 안정 — 본 세션 미커밋 변경만 revert 대상.
