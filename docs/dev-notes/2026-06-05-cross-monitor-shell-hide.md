# 2026-06-05 멀티모니터에서 다른 모니터 작업표시줄이 메인 인디를 숨김 — TryHandleFilter 제3 케이스 "무시"

## 결론

사용자 보고 "멀티모니터에서 **모니터2 작업표시줄로 설정 앱을 전환**하면 모니터1 의 메인 인디가
사라진다" 를 fix. 근본원인은 [`SystemFilter.ShouldHide`](../../App/Detector/SystemFilter.cs)
**조건 4**(클래스 블랙리스트)가 "셸 UI 가 foreground 인가" 만 보고 "그 셸 UI 가 인디를 실제로
가리는가" 는 보지 않아, **다른 모니터의 작업표시줄**(`Shell_TrayWnd`)이 잠깐 foreground 를
훔치는 순간을 HIDE 로 오탐한 것이다.

수정은 [`Program.TryHandleFilter`](../../Program.cs) 진입부에 **제3 케이스 "무시(ignore)"** 를
추가 — cross-monitor 작업표시줄이 foreground 면 HIDE 미발신 + `DetectionState` 전부 불변 +
`return true`(tick 종료)로 현 인디 상태를 동결한다. **`ShouldHide` 는 무변경.** 신규 config 키 0
(`MonitorScopedShellClasses` 는 코드 레벨 property), 신규 P/Invoke 0(`MonitorFromWindow` 는 기존
`User32` 멤버).

이 증상은 [PR-23 UWP `hwndFocus=0` 폴백](../improvement-plan/PR-23-uwp-focus-fallback.md) 과
**같은 "설정 만지는 동안 인디 소멸" 의 두 번째 독립 원인**이다. PR-23 = 설정 앱 *콘텐츠 클릭*
(UWP 프레임/콘텐츠 분리로 `hwndFocus=0`), PR-24 = *작업표시줄 클릭*(셸 UI 가 잠깐 foreground).
두 경로가 독립이라 PR-23 머지 후에도 멀티모니터 사용자는 잔존 케이스를 계속 겪었다.

## 증상

| 항목 | 내용 |
|------|------|
| 트리거 | **모니터2** 작업표시줄(`Shell_TrayWnd`) 클릭 — 인디는 **모니터1** 에 있음 |
| 증상 | 모니터1 메인 인디가 사라진 채 박제 (작업표시줄은 모니터2 라 인디를 가리지도 않음) |
| 조건 | **인디와 작업표시줄이 다른 모니터일 때만** — 같은 모니터·단일 모니터는 무증상(정상 숨김) |
| 빈도 | 작업표시줄 아이콘으로 앱 전환 시마다 — 멀티모니터 사용자에게 빈발 |

## 근본원인

### 레이아웃

- **모니터1** — 작업표시줄 없음. 설정 앱 + 메인 인디.
- **모니터2** — 작업표시줄(`Shell_TrayWnd`). 사용자가 여기 설정 앱 아이콘을 클릭.

### 조건 4 의 오탐

작업표시줄(모니터2)을 클릭하면 `Shell_TrayWnd` 가 잠깐 foreground 가 된다. 그러면
[`SystemFilter.ShouldHide`](../../App/Detector/SystemFilter.cs) **조건 4**(클래스 블랙리스트 —
`Shell_TrayWnd` 포함)가 filtered 를 내고, [HIDE 디바운스](2026-06-02-explorer-hide-flipflop-debounce.md)
streak 가 `HideHysteresisPolls`(=3)에 도달하면 HIDE 가 확정 → **모니터1 인디 소멸**.

조건 4 는 단일 모니터를 가정한다 — 작업표시줄이 인디와 같은 화면에 있으면 숨김이 옳다. 그러나
**작업표시줄이 인디와 다른 모니터**에 있으면, 작업표시줄이 잠깐 foreground 가 됐다고 인디를 숨길
이유가 없다(가리지 않으므로). 조건 4 가 "가림 여부" 를 보지 않아 cross-monitor 에서 오탐한다.

### 스모킹건

```
Filter triggered HIDE: fgClass=Shell_TrayWnd, streak=3
```

직전 정상 foreground 는 `ApplicationFrameHost`(모니터1, 설정 앱). HIDE 26 건이 전부
`Shell_TrayWnd` / `Progman` — 셸 UI 가 foreground 를 잠깐 훔치는 순간들.

## 수정 — TryHandleFilter 제3 케이스 "무시"

사용자 결정: **"셸 UI 가 인디와 다른 모니터면 숨기지 않는다. 같은 모니터면 기존대로."**

[`Program.TryHandleFilter`](../../Program.cs) 진입부, `ResolveForApp`/`ShouldHide` 평가 **이전**에
세 조건 AND 게이트:

```csharp
string fgClass = WindowProcessInfo.GetClassName(hwndForeground);
if (state.LastNonFilteredForeground != IntPtr.Zero        // ① 인디가 떠 있던 적이 있어야(anchor)
    && SystemFilter.IsMonitorScopedShell(fgClass)         // ② foreground 가 작업표시줄(모니터-국한)
    && !SystemFilter.SameMonitor(hwndForeground, state.LastNonFilteredForeground))  // ③ 다른 모니터
{
    Logger.Debug($"Filter IGNORE (cross-monitor shell): ...");
    appConfig = default!;
    return true;     // tick 종료 → EmitStateChanges 미도달 → 위치/IME/focus 갱신 0
}
```

- **anchor = `LastNonFilteredForeground`** — 인디가 떠 있던 직전 non-filtered 앱. position_mode
  `window`/`fixed` 양쪽에서 인디는 직전 non-filtered 앱 기준으로 배치되므로, 그 앱의 모니터가
  곧 인디의 모니터다. non-filtered 확정 분기(`FilteredStreak = 0` 직후)에서 갱신.
- **`return true` 로 현 상태 동결** — `EmitStateChanges` 미도달 → 위치/IME/focus 갱신 0. 인디는
  직전 non-filtered 앱의 위치·anchor 그대로 유지.
- **`FilteredStreak` 불변** — 올리면 셸 이탈 후 잔여 streak 오작동, 0 리셋하면 not-filtered
  진입(위치 갱신 동반) 의미가 된다.
- **anchor 미설정(첫 부팅·인디 미표시) 폴백** — ①이 false 면 게이트 미통과 → 기존 숨김 동작(조건
  4)으로 작업표시줄 정상 숨김.

### 헬퍼 (P3/P4)

- `SystemFilter.IsMonitorScopedShell(className)` — `MatchesAny(className,
  DefaultConfig.MonitorScopedShellClasses, [])`. 셸 클래스 매칭의 단일 구현 [`MatchesAny`] 재사용
  (P4). 순수 문자열이라 단위 테스트 가능.
- `SystemFilter.SameMonitor(hwndA, hwndB)` — `MonitorFromWindow`(NEAREST) 핸들 동일성. 라이브
  Win32 의존 → 수동 smoke.
- `DefaultConfig.MonitorScopedShellClasses => ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"]` —
  작업표시줄 2종만. 바탕화면(`Progman`/`WorkerW`)은 **전체 데스크톱을 덮어 "다른 모니터" 가
  없으므로 제외** → 항상 숨김(기존 동작).

## 절대 다시 시도하지 말 것 — ShouldHide 조건 4 fall-through

> **`SystemFilter.ShouldHide` 조건 4 에서 "cross-monitor 작업표시줄이면 false 반환(=숨기지
> 않음)" 으로 fall-through 하는 1차 설계는 폐기됐다. 다시 시도하지 말 것.**

조건 4 에서 false 를 반환하면 작업표시줄이 **not-filtered** 로 취급돼 두 회귀가 발생한다:

1. **인디가 모니터2 로 점프** — not-filtered 면 `EmitStateChanges` → `WM_POSITION_UPDATED(작업
   표시줄)` 가 post 된다. `position_mode = window` 면 인디가 작업표시줄(모니터2) 옆으로 **점프**.
   숨김은 막았는데 위치가 깨진다.
2. **anchor 오염 → 게이트 자기 무력화** — not-filtered 확정 분기가 `LastNonFilteredForeground`
   를 **작업표시줄로 덮어쓴다**. 다음 틱 `SameMonitor(fg, anchor)` 가 "작업표시줄 vs 작업표시줄"
   → 같은 모니터 판정 → 게이트 미통과 → HIDE 재확정. 게이트가 스스로를 무력화한다.

→ 그래서 "숨기지도 않고 not-filtered 로 만들지도 않는" **제3 상태(무시 = 현 상태 동결)** 가
필수. `ShouldHide`(filtered 판정)는 손대지 않고 `TryHandleFilter` 진입부에서 평가 자체를 건너뛴다.

## 미해결 잔여 / 후속 주의

1. **anchor 가 stale 일 수 있는 좁은 창** — `LastNonFilteredForeground` 는 마지막 non-filtered
   앱을 가리키므로, 사용자가 그 앱을 **닫고** 작업표시줄을 누르면 anchor 가 이미 사라진 창을
   가리킨다. `MonitorFromWindow`(NEAREST)는 무효 HWND 라도 항상 유효 핸들을 반환하므로 크래시는
   없으나, 모니터 판정이 부정확할 수 있다. 다만 이 경우 잠시 후 `foregroundChanged` 가 다음 실제
   앱으로 anchor 를 갱신해 자연 수렴 — 실사용 영향은 무시 가능 수준으로 판단(미실측).
2. **IME/focus 폴링 1틱 스킵** — 게이트가 `return true` 로 그 틱의 IME·focus 폴링을 건너뛴다.
   무해 근거: 작업표시줄 위 무입력 + `EVENT_OBJECT_IME_CHANGE` 훅이 폴링과 독립 감지 + 설정 앱
   복귀 시 `foregroundChanged` 로 일괄 재동기화. (PR-24 §"IME/focus 폴링 누락은 무해" 참조.)
3. **모니터-국한 클래스 목록의 완전성** — `Shell_TrayWnd`/`Shell_SecondaryTrayWnd` 2종으로
   한정. 다른 모니터-국한 셸 표면(미발견)이 있으면 같은 오탐이 재현될 수 있으나, 작업표시줄이
   유일한 빈발 케이스라 보수적으로 2종만 등재(바탕화면은 의도적 제외).

## 참고

- **동일 증상의 1차 원인 (먼저 머지)**: [PR-23 UWP focus 폴백](../improvement-plan/PR-23-uwp-focus-fallback.md)
  + [implementation-notes "Console host + UWP frame fallback"](../implementation-notes.md). PR-23 =
  콘텐츠 클릭, PR-24 = 작업표시줄 클릭.
- **설계 단일 진실원**: [PR-24-cross-monitor-shell-hide.md](../improvement-plan/PR-24-cross-monitor-shell-hide.md).
- **HIDE 디바운스 / "잠정 구간 상태 미갱신" 비대칭** (anchor 동결의 동형 설계):
  [2026-06-02-explorer-hide-flipflop-debounce.md](2026-06-02-explorer-hide-flipflop-debounce.md) +
  [implementation-notes § Per-poll filter evaluation](../implementation-notes.md#per-poll-filter-evaluation).
- 변경 파일: [`Program.cs`](../../Program.cs) (`TryHandleFilter` + `DetectionState.LastNonFilteredForeground`),
  [`App/Detector/SystemFilter.cs`](../../App/Detector/SystemFilter.cs) (`IsMonitorScopedShell` +
  `SameMonitor`), [`App/Config/DefaultConfig.cs`](../../App/Config/DefaultConfig.cs)
  (`MonitorScopedShellClasses`).
