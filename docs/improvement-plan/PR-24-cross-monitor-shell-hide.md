# PR-24: 멀티모니터에서 다른 모니터의 셸 UI(작업표시줄)가 메인 인디를 숨기던 버그 수정

**상태**: 구현 완료 (2026-06-05)
**유형**: 버그 수정 (사용자 가시)
**전제**: PR-23 (UWP `hwndFocus=0` 폴백) 과 **동일 증상의 두 번째 독립 원인** — PR-23 이 먼저 머지된 뒤에도 멀티모니터 환경에서 잔존하던 케이스를 마저 닫는다.

## 동기

멀티모니터에서 Windows **설정 앱**을 만지는 동안 메인 인디케이터가 사라지던 결함의 **두 번째
독립 원인**. PR-23 은 설정 앱 *콘텐츠 클릭*(UWP `hwndFocus=0`)을 닫았으나, 사용자는 멀티모니터에서
**작업표시줄의 설정 앱 아이콘으로 앱을 전환**하면서 같은 "설정 만지는 동안 인디 소멸" 을 계속
겪었다.

레이아웃:

- **모니터1** — 작업표시줄 없음(`taskbar` 미배치). 설정 앱 + 메인 인디가 여기에 있다.
- **모니터2** — 작업표시줄(`Shell_TrayWnd`)이 여기에 있다. 사용자가 이 작업표시줄의 설정 앱
  아이콘을 클릭해 앱을 전환한다.

작업표시줄(모니터2)을 클릭하면 그 순간 `Shell_TrayWnd` 가 잠깐 foreground 가 된다. 그러면
[`SystemFilter.ShouldHide`] 의 **조건 4**(클래스 블랙리스트 — `Shell_TrayWnd` 포함)가 filtered 를
내고, 디바운스 streak 가 3 에 도달하면 HIDE 가 확정돼 **모니터1 의 인디가 사라진다** — 작업표시줄은
모니터2 에 있어 모니터1 의 인디를 가리지도 않는데도.

**스모킹건 로그**(DEBUG):

```
Filter triggered HIDE: fgClass=Shell_TrayWnd, streak=3
```

직전 정상 foreground 는 `ApplicationFrameHost`(모니터1, 설정 앱). HIDE 26 건이 전부
`Shell_TrayWnd` / `Progman` 였다 — 셸 UI 가 잠깐 foreground 를 훔치는 순간들.

## 근본 원인

조건 4 의 클래스 블랙리스트는 작업표시줄·바탕화면을 숨기려는 장치다. 단일 모니터에서는
작업표시줄이 인디와 같은 화면에 있어 숨김이 옳다. 그러나 **멀티모니터에서 작업표시줄이 인디와
다른 모니터에 있으면**, 작업표시줄이 잠깐 foreground 가 됐다고 인디를 숨길 이유가 없다 — 가리지
않으므로. 조건 4 는 "셸 UI 가 foreground 인가" 만 보고 "그 셸 UI 가 인디를 실제로 가리는가" 는
보지 않아 cross-monitor 에서 오탐한다.

## 설계 결정 (사용자 결정)

> "셸 UI 가 인디와 **다른 모니터**에 있으면 숨기지 않는다. **같은 모니터**면 기존대로 숨긴다."

이를 [`Program.TryHandleFilter`] 진입부의 **제3 케이스 "무시(ignore)"** 로 구현한다 — filtered 도
not-filtered 도 아닌 제3 상태. cross-monitor 인 작업표시줄이 foreground 면 **HIDE 미발신 +
`DetectionState` 전부 불변 + `return true`(tick 종료)** 로 현 인디 상태를 그대로 둔다.

"인디 모니터" 의 기준은 [`DetectionState.LastNonFilteredForeground`] — **인디가 떠 있던 직전
non-filtered 앱** 의 HWND. position_mode 가 `window`(앱 옆) 든 `fixed`(고정 코너) 든 인디는 직전
non-filtered 앱을 기준으로 배치되므로(아래 §"인디 모니터 = LastNonFilteredForeground" 참조), 그
앱의 모니터가 곧 인디의 모니터다.

## 변경

| 파일 | 변경 |
|------|------|
| [App/Config/DefaultConfig.cs](../../App/Config/DefaultConfig.cs) | `MonitorScopedShellClasses => ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"]` property 신규 (작업표시줄 2종만 — 의도 주석 포함) |
| [App/Detector/SystemFilter.cs](../../App/Detector/SystemFilter.cs) | `IsMonitorScopedShell(className)` + `SameMonitor(hwndA, hwndB)` `internal static` 헬퍼 2종 신규. **`ShouldHide` 는 무변경** |
| [Program.cs](../../Program.cs) | `DetectionState.LastNonFilteredForeground` 필드 + `TryHandleFilter` 진입부 cross-monitor 무시 게이트 + non-filtered 확정 분기에서 `LastNonFilteredForeground = hwndForeground` 갱신 |

### `DefaultConfig.MonitorScopedShellClasses` — 작업표시줄 2종만

```csharp
public static string[] MonitorScopedShellClasses => ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
```

작업표시줄(`Shell_TrayWnd` 주 / `Shell_SecondaryTrayWnd` 보조 모니터)은 **특정 모니터에만 존재**해
"다른 모니터" 개념이 성립한다. 반면 바탕화면(`Progman` / `WorkerW`)은 **전체 데스크톱을 덮어**
"다른 모니터" 가 없으므로 **의도적으로 제외** — 본 목록에 없어 cross-monitor 무시 대상이 아니고,
조건 4 블랙리스트로 **항상 숨김** 된다(기존 동작 보존).

이 property 는 `DefaultSystemHideClasses` 와 별개다 — `DefaultSystemHideClasses`(조건 4 의 전체
블랙리스트)는 그대로 두고, 그 부분집합인 "모니터-국한" 클래스만 따로 골라낸다. 두 목록은 의도가
달라(전자 = 숨길 대상 전체, 후자 = cross-monitor 면 봐주는 작업표시줄) 한쪽이 다른 쪽을 참조하지
않고 독립 유지한다.

### `SystemFilter` 헬퍼 2종

```csharp
internal static bool IsMonitorScopedShell(string className)
    => MatchesAny(className, DefaultConfig.MonitorScopedShellClasses, []);

internal static bool SameMonitor(IntPtr hwndA, IntPtr hwndB)
    => User32.MonitorFromWindow(hwndA, Win32Constants.MONITOR_DEFAULTTONEAREST)
       == User32.MonitorFromWindow(hwndB, Win32Constants.MONITOR_DEFAULTTONEAREST);
```

- `IsMonitorScopedShell` 는 셸 클래스 매칭의 단일 구현 [`MatchesAny`] 를 그대로 재사용(P4) —
  조건 4·커서 인디 호버 판정과 같은 OrdinalIgnoreCase 2-리스트 매처. 순수 문자열 매칭이라
  **단위 테스트 가능**(아래 §검증).
- `SameMonitor` 는 `MonitorFromWindow`(NEAREST)가 항상 유효 핸들을 반환하므로 핸들 동일성(`==`)
  으로 같은 모니터인지 판정. 라이브 Win32 의존이라 단위 테스트 비대상 — 수동 smoke.

### `Program.TryHandleFilter` — 제3 케이스 "무시"

`TryHandleFilter` 진입부, 기존 `ResolveForApp`/`ShouldHide` 평가 **이전**에:

```csharp
string fgClass = WindowProcessInfo.GetClassName(hwndForeground);
if (state.LastNonFilteredForeground != IntPtr.Zero
    && SystemFilter.IsMonitorScopedShell(fgClass)
    && !SystemFilter.SameMonitor(hwndForeground, state.LastNonFilteredForeground))
{
    Logger.Debug($"Filter IGNORE (cross-monitor shell): fgClass={fgClass}, anchorHwnd=0x{...:X}");
    appConfig = default!;
    return true;          // tick 종료 → EmitStateChanges 미도달 → 위치/IME/focus 갱신 0
}
```

세 조건의 AND:

1. `LastNonFilteredForeground != Zero` — 인디가 표시된 적이 있어야(anchor 존재). 첫 부팅·인디
   미표시면 anchor 가 0 이라 게이트가 통과하지 않고 **기존 숨김 동작으로 폴백**(작업표시줄을
   정상 숨김).
2. `IsMonitorScopedShell(fgClass)` — foreground 가 작업표시줄(모니터-국한)일 때만.
3. `!SameMonitor(fg, anchor)` — 작업표시줄이 인디(=anchor 앱)와 **다른 모니터**일 때만. 같은
   모니터면 통과하지 않고 기존 숨김 동작(조건 4)으로 정상 진행.

게이트 통과 시 **`return true`** 로 tick 을 종료한다 — `EmitStateChanges` 에 도달하지 않으므로
위치/IME/focus 갱신이 한 건도 일어나지 않고, 인디는 직전 non-filtered 앱(`LastNonFilteredForeground`)
의 위치·anchor 를 그대로 유지한다. `FilteredStreak` 도 **불변** — 올리면 셸 이탈 후 잔여 streak 가
오작동하고, 0 으로 리셋하면 not-filtered 진입(위치 갱신 동반)의 의미가 돼버린다.

non-filtered 확정 분기(`state.FilteredStreak = 0;` 직후)에서 `state.LastNonFilteredForeground =
hwndForeground;` 로 anchor 를 갱신한다 — 인디가 실제로 떠 있는 앱이 곧 다음 cross-monitor 비교의
기준이 된다.

## 핵심 설계 결정 — 왜 조건 4 fall-through 가 아니라 TryHandleFilter 제3 케이스인가

**1차 설계(폐기)**: `SystemFilter.ShouldHide` 조건 4 에서 "cross-monitor 작업표시줄이면 false
반환(=숨기지 않음)" 으로 fall-through. **이 설계는 두 가지 회귀를 만든다**:

1. **인디가 모니터2 로 점프** — 조건 4 에서 false 를 반환하면 작업표시줄이 **not-filtered** 로
   취급돼 `EmitStateChanges` → `WM_POSITION_UPDATED(작업표시줄)` 가 post 된다. `position_mode =
   window` 면 인디가 작업표시줄(모니터2) 옆으로 **점프**한다 — 숨김은 막았는데 위치가 깨진다.
2. **anchor 오염 → 다음 틱 게이트 무효화** — not-filtered 확정 분기가 `LastNonFilteredForeground`
   를 **작업표시줄로 덮어쓴다**. 그러면 다음 틱의 `SameMonitor(fg, anchor)` 비교가 "작업표시줄 vs
   작업표시줄" 이 돼 같은 모니터로 판정 → 게이트가 통과하지 못하고 HIDE 가 다시 확정된다. 게이트가
   스스로를 무력화한다.

그래서 "숨기지도 않고 not-filtered 로 만들지도 않는" **제3 상태(무시 = 현 상태 동결)** 가
필수다. `ShouldHide`(=filtered 판정)는 손대지 않고, `TryHandleFilter` 진입부에서 평가 자체를
건너뛰어 상태를 전부 동결한다.

→ **dev-note 에 "ShouldHide 조건 4 fall-through 시도 금지" 로 박제**:
[dev-notes/2026-06-05-cross-monitor-shell-hide.md](../dev-notes/2026-06-05-cross-monitor-shell-hide.md).

## 부수 설계 결정

### 인디 모니터 = `LastNonFilteredForeground` 의 모니터

position_mode 가 `window`(앱 옆) 든 `fixed`(고정 코너) 든, 인디는 **직전 non-filtered 앱** 을
기준으로 배치된다 — `window` 는 그 앱 창에 붙고, `fixed` 도 그 앱이 있는 모니터의 코너에 놓인다.
따라서 그 앱의 모니터가 곧 인디의 모니터이며, `LastNonFilteredForeground` 가 cross-monitor 비교의
정확한 anchor 다.

잠정 HIDE 구간(디바운스 `streak < 3`)에서 `LastNonFilteredForeground` 를 갱신하지 않는 것은
[HIDE 디바운스의 "잠정 구간 상태 미갱신" 비대칭](../dev-notes/2026-06-02-explorer-hide-flipflop-debounce.md)
과 같은 결로, 본 anchor 도 그 불변식과 함께 동결된다 — 잠정 구간엔 인디가 아직 어느 앱에도
확정 배치되지 않았으므로 anchor 를 흔들지 않는다.

### IME / focus 폴링 누락은 무해

게이트가 `return true` 로 tick 을 끝내면 그 틱의 IME·focus 폴링이 스킵된다. 무해한 이유:

- cross-monitor 작업표시줄 위에는 텍스트 입력이 없어 IME 상태가 그 틈에 바뀔 일이 없다.
- IME 상태가 바뀌면 `EVENT_OBJECT_IME_CHANGE` 훅이 **폴링과 독립**으로 감지해 `WM_IME_STATE_CHANGED`
  를 post 한다 — 폴링 스킵과 무관하게 즉시 반영.
- 설정 앱으로 복귀하면 `foregroundChanged` 가 떠 focus·IME·위치가 **일괄 재동기화** 된다.

### 커서 인디 영향 0

커서 인디의 셸 UI 호버 숨김([`CursorOverlay.IsOverShellUi`])은 `MatchesAny` 만 호출하고
`ShouldHide` 는 호출하지 않는다. 본 PR 은 `ShouldHide` 도 `MatchesAny` 도 건드리지 않았으므로
(헬퍼 2종 신규 + 호출은 `Program` 쪽) 커서 인디 동작에 영향이 없다.

## P 규칙

- **P3 ✅** — 셸 클래스명 리터럴(`"Shell_TrayWnd"` / `"Shell_SecondaryTrayWnd"`)을
  `DefaultConfig.MonitorScopedShellClasses` property 단일 정의로 모음. 게이트는 named property 경유,
  인라인 매직 스트링 0. `MONITOR_DEFAULTTONEAREST` 도 기존 `Win32Constants` const 사용.
- **P4 ✅** — 셸 클래스 매칭은 [`SystemFilter.MatchesAny`] 단일 구현을 재사용(`IsMonitorScopedShell`
  가 위임). 조건 4·커서 호버 판정과 같은 매처라 매칭 의미가 한 곳에서만 정의된다.
- **P6 ✅** — `App/ → Core/` 단방향 유지. 신규 헬퍼는 `App/Detector/SystemFilter`(App)에 있고
  `User32.MonitorFromWindow` / `Win32Constants`(Core)만 호출. `MonitorScopedShellClasses` 는
  KoEnVue 의 필터 정책(도메인 어휘)이라 `App/Config/DefaultConfig`(App)가 정위치 —
  `DefaultSystemHideClasses` 와 같은 카테고리.
- P1 / P2 / P5 무관 (외부 NuGet 0, 사용자 가시 문자열 변화 0 — 트레이/다이얼로그 라벨 불변,
  manifest 불변).

## 검증

### 단위 테스트 (신규)

[tests/KoEnVue.Tests/Unit/SystemFilterMonitorScopeTests.cs](../../tests/KoEnVue.Tests/Unit/SystemFilterMonitorScopeTests.cs)
— `IsMonitorScopedShell` 분류 10 케이스(`[Theory]` `[InlineData]`):

- 작업표시줄 2종 + 대소문자 변형 2 → **true** (게이트 대상).
- 바탕화면 `Progman` / `WorkerW` → **false** (전체 데스크톱 덮음 — 게이트 제외, 항상 숨김).
- 일반 UWP 프레임 `ApplicationFrameWindow`(PR-23 대상) / 일반 앱 `Chrome_WidgetWin_1` / 빠른설정
  `ControlCenterWindow` / 빈 문자열 → **false**.

이 분류가 깨지면 cross-monitor 무시가 바탕화면까지 번져 보조 모니터 인디 미숨김 회귀가
생기거나(`Progman`→true), 작업표시줄을 못 잡아 본 PR 수정이 무효가 된다(`Shell_TrayWnd`→false).

`SameMonitor`(라이브 `MonitorFromWindow`) 와 `Program.TryHandleFilter` 게이트(라이브 foreground
상태)는 단위 테스트가 비현실적 — **수동 smoke** 가 검증 진실원.

### 수동 smoke

1. **수정 대상** — 멀티모니터(인디가 모니터1, 작업표시줄이 모니터2)에서 **모니터2 의
   작업표시줄을 클릭**(예: 설정 앱 아이콘)했을 때 **모니터1 의 인디가 유지**되는지.
2. **회귀가드 — 같은 모니터** — 인디와 작업표시줄이 **같은 모니터**일 때 작업표시줄을 클릭하면
   인디가 **여전히 숨겨지는지**(기존 동작 보존). `!SameMonitor` 가드가 깨지면 같은 모니터에서도
   안 숨는 회귀.
3. **회귀가드 — 단일 모니터** — 단일 모니터에서 작업표시줄 클릭 시 인디가 정상 숨김되는지(인디와
   작업표시줄이 항상 같은 모니터 → 게이트 미통과).
4. **회귀가드 — 바탕화면** — 바탕화면(`Progman`/`WorkerW`) 클릭 시 인디가 **다른 모니터라도
   숨겨지는지**(바탕화면은 `MonitorScopedShellClasses` 제외 → 항상 숨김).
5. **position_mode 양쪽** — `window` 와 `fixed` 모두에서 ①이 성립하고 인디가 모니터2 로 **점프하지
   않는지**(1차 설계 회귀 #1 가드).

### 자동 검증 (2026-06-05)

dotnet build 0/0 + AOT publish 4.66 MB (4,884,480 bytes, SHA256
`619063BDA30C4DBBA06C93C0E5C58CD05A3D5D205EC65190493CE4E3BD59F9AB`) AOT 경고 0 + **90 → 100
PASS**(신규 `SystemFilterMonitorScopeTests` 10 = `IsMonitorScopedShell` `[Theory]` 10 케이스).
`SameMonitor`(라이브 `MonitorFromWindow`) 와 `TryHandleFilter` 게이트(라이브 foreground 상태)는
단위 테스트가 직접 커버하지 못하므로 빌드·기존 회귀 스위트 통과 + 위 수동 smoke 가 검증 진실원.

### invariant grep

```bash
git grep -n "MonitorScopedShellClasses" App/Config/DefaultConfig.cs    # 1 (정의)
git grep -n "MonitorScopedShellClasses" App/Detector/SystemFilter.cs   # 2 (IsMonitorScopedShell 위임 + XML doc 주석)
git grep -n '"Shell_TrayWnd"' -- 'App/*.cs'                            # DefaultSystemHideClasses + MonitorScopedShellClasses 정의에만 (게이트 인라인 0)
git grep -n "LastNonFilteredForeground" Program.cs                     # 필드 선언 + 게이트 비교/로그 + non-filtered 분기 갱신 (주석 포함 6 매치)
```

## 참고

- **동일 증상의 1차 원인 (먼저 머지)**: [PR-23-uwp-focus-fallback.md](PR-23-uwp-focus-fallback.md)
  (UWP `hwndFocus=0` 폴백). PR-23 = 콘텐츠 클릭, PR-24 = 작업표시줄 클릭 — 같은 "설정 만지는 동안
  인디 소멸" 의 두 독립 경로.
- **제3 케이스 dev-note (fall-through 금지 박제)**:
  [dev-notes/2026-06-05-cross-monitor-shell-hide.md](../dev-notes/2026-06-05-cross-monitor-shell-hide.md).
- **HIDE 디바운스 / 상태 미갱신 비대칭** (anchor 동결의 동형 설계):
  [implementation-notes.md § Per-poll filter evaluation](../implementation-notes.md#per-poll-filter-evaluation) +
  [dev-notes/2026-06-02-explorer-hide-flipflop-debounce.md](../dev-notes/2026-06-02-explorer-hide-flipflop-debounce.md).
