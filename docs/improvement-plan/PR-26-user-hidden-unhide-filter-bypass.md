# PR-26: UserHidden 해제 시 SystemFilter 미재평가 — 필터 대상 위 메인 인디 잔류

> 상태: **🚧 구현 완료 (커밋·머지 전)** — 2026-07-23. 조사(2026-07-22) → 설계 채택 **(a)+(b)+(c)** → 구현·검증 완료 (`dotnet build` 0/0, AOT publish 5,052,416 B, 102/102 PASS, DllImport 0).
>
> 조사 당시 대상: `v0.9.9.6` (`D:\_portable\KoEnVue\KoEnVue.exe`)

## 동기 (증상)

트레이 좌클릭 또는 우클릭 메뉴의 **"메인 인디케이터 숨김"(`user_hidden`)** 을 켰다가 다시 끄면, **원래 숨겨져야 할 창(바탕화면 `Progman`/`WorkerW`, 작업 표시줄 `Shell_TrayWnd` 등 SystemFilter 대상) 위에 메인 인디케이터가 표시되고, 감지 스레드가 그것을 다시 숨기지 못한다.**

가장 눈에 띄는 형태는 **시작 메뉴/검색 창을 거친 경우**다. 인디가 **이미 닫힌 검색 패널 좌표(화면 중앙)** 에 그려져 허공에 떠 있는 것처럼 보인다 — "바탕화면에 인디가 나타난다"는 최초 제보가 이 케이스였다.

## 근본 원인 — 세 결함이 곱해진다

### ① 해제 경로가 SystemFilter 를 재평가하지 않는다

[`Program.cs:1025~1041`](../../Program.cs) `ApplyUserHiddenTransition` 의 표시 분기:

```csharp
else
{
    if (_lastForegroundHwnd != IntPtr.Zero)
        ShowIndicatorAtForeground(_lastImeState, ResolveCurrent(), imeChanged: false);
}
```

가드는 `_lastForegroundHwnd != IntPtr.Zero` 하나뿐이다. 현재 포그라운드가 필터 대상인지 묻지 않는다. 트레이 좌클릭(`HandleTrayToggle` 1005)과 메뉴(`HandleMenuCommand` 람다 1061~1064)가 이 함수를 공유하므로 **두 경로 모두 동일**하다.

### ② `LastFiltered` 래치가 재숨김을 영구 봉인한다

[`Program.cs:1378`](../../Program.cs) `TryHandleFilter` 의 HIDE 발송 게이트:

```csharp
if (!state.LastFiltered && _indicatorVisible)      // ← 두 조건 AND
    PostMessageW(_hwndMain, WM_HIDE_INDICATOR, ...);
state.LastFiltered = true;                          // ← 조건문 바깥. 항상 찍힌다
```

인디가 **이미 꺼진 상태**(`_indicatorVisible == false`)로 필터가 확정되면 HIDE 를 보내지 않으면서 `LastFiltered = true` 만 마킹한다. 이후 인디가 **감지 스레드 외부 경로**(①)로 다시 켜져도 `!state.LastFiltered` 가 거짓이라 **HIDE 가 재전송되지 않는다.**

`LastFiltered` 를 `false` 로 되돌리는 지점은 [`Program.cs:1285`](../../Program.cs) 한 곳뿐이고, 그곳은 `TryHandleFilter` 가 "필터 비대상"으로 `false` 를 반환해야만 도달한다. 즉 **필터 대상에 머무는 한 자력 복구 경로가 없다.**

### ③ stale 상태가 "없는 창" 자리에 그린다

- `_lastForegroundHwnd` / `_currentProcessName` 의 유일한 라이터는 [`Program.cs:558`](../../Program.cs) `HandlePositionUpdated` 이고, 이는 `WM_POSITION_UPDATED`([`:1529~1533`](../../Program.cs))로만 호출된다. **필터 중에는 `TryHandleFilter` 가 조기 return 하므로 이 메시지가 발송되지 않는다** → 숨기기 직전 앱의 값으로 고착.
- 해제 시 [`Program.cs:701`](../../Program.cs) `GetAppPosition()` 이 stale `_currentProcessName` 으로 `IsSystemInputProcess` 분기를 타고, [`Overlay.cs:224`](../../App/UI/Overlay.cs) `GetDefaultPosition` 이 **`_lastValidSystemInputFrame` 캐시를 재사용**한다([`:248`](../../App/UI/Overlay.cs)).
- 이 캐시는 저장(`:246`)과 재사용(`:251`)만 있고 **무효화 코드가 없다.** → 닫힌 시작 메뉴/검색 패널 좌표에 인디를 그린다.

| 케이스 | 인디가 뜨는 곳 | 체감 |
|--------|----------------|------|
| 일반 앱 stale | 그 앱 창 위 | 창이 화면에 있어 자연스러움 — 놓치기 쉬움 |
| **시스템 입력 창 stale** | **닫힌 패널 자리(화면 중앙)** | **허공에 떠 보임 — 명백히 이상** |

## 실측 증거 (2026-07-22, `log_level=DEBUG`)

### 결함 조건이 정확히 갈린다

| # | 해제 시각 | 해제 시점 필터 상태 | 이후 HIDE | 인디 잔류 |
|---|-----------|---------------------|-----------|-----------|
| ① | 19:11:34 | 확정(`streak=3`) | ❌ 없음 | 4분 45초 |
| ② | 19:20:43 | 확정 | ❌ 없음 | 28.6초 |
| ③ | **19:33:32** | 확정 | ❌ 없음 | **18.2초** — 닫힌 `SearchHost` 좌표 `(1291,450)` |
| ④ | 19:38:51 | 확정 | ❌ 없음 | 5.4초 |
| ⑤ | 19:39:05 | 확정 | ❌ 없음 | 3.7초 |
| ⑥ | 19:39:34 | 확정 | ❌ 없음 | 4.1초 |
| **대조** | 19:21:51 · 19:32:21 · 19:46:01 등 | **미확정(`streak<3`)** | ✅ 정상 | 자기치유 |

**해제 시점에 필터가 이미 확정(3폴링 연속)이었는가**가 결함 발현 여부를 가른다. 셸 창이 foreground 가 된 지 약 240ms(`PollIntervalMs 80 × HideHysteresisPolls 3`) 이내에 해제하면 아직 `LastFiltered=false` 라 다음 확정 틱이 정상 HIDE 를 보낸다.

### 결정적 증거는 "없는 로그"

19:33 시퀀스 — `streak=2/3` 이후 2.6초(약 32폴링)가 지나 streak 은 분명 3에 도달했는데 `Filter triggered HIDE` 가 **찍히지 않았다**:

```
19:33:14.729  PositionUpdated: process=SearchHost, pos=(1291,450)   ← 검색창에 인디 표시(프레임 캐시 저장)
19:33:29.514  Tray toggle: UserHidden=True
19:33:29.516  HideOverlay called: source=UserHidden toggle           ← _indicatorVisible = false
19:33:29.573  Filter HIDE deferred (streak=2/3, …)
      (2.6초 침묵 — streak 3 도달하나 HIDE 미전송, LastFiltered=true 만 마킹)
19:33:32.210  Tray toggle: UserHidden=False                          ← 닫힌 패널 좌표에 인디 표시
      (18.2초 침묵 — !LastFiltered 가 거짓이라 재숨김 불가)
19:33:50.427  HideOverlay called: source=WM_HIDE_INDICATOR           ← 다른 창 클릭으로 정상 복귀
```

`Filter HIDE deferred` 는 `streak < HideHysteresisPolls` 일 때만 찍히므로([`:1370~1375`](../../Program.cs)), 확정 이후 구간은 **의도적으로 침묵**한다. 이 공백이 ②의 직접 증거다.

### 해소 경로는 하나뿐

"필터를 통과하는 다른 창이 foreground 가 되는 것"뿐이다. 그 순간 [`:1284`](../../Program.cs) `foregroundChanged = (…) || state.LastFiltered` 가 참이 되어 `WM_POSITION_UPDATED` → 정상 복귀. `display_mode=always`(기본)에서는 3초 뒤 `IdleOpacity`(0.55)로 흐려질 뿐 `Overlay.Hide` 가 호출되지 않아 **화면에 계속 남는다.**

## 사양 대조 — 결함이 맞다

| 출처 | 내용 |
|------|------|
| [PRD §2.4](../KoEnVue_PRD.md) `:81` | **숨김 조건** — 바탕화면 / 작업 표시줄 / 잠금 화면 (SystemFilter) |
| [PRD §7.2](../KoEnVue_PRD.md) `:354` | `lastFiltered` 플래그의 역할은 **"중복 메시지 억제"** 로만 규정 |
| [PR-25](PR-25-transient-shell-hide-grace.md) `:89` | 회귀가드 ② — `Progman` 클릭해 머물면 **정상 소멸**해야 함 |
| [PR-25](PR-25-transient-shell-hide-grace.md) `:124` | 셸 위 인디 잔류 = **"명백한 회귀"** |
| [PR-25](PR-25-transient-shell-hide-grace.md) `:129` | "셸 foreground 시 dim 유지"(후보 c)는 **미채택** — 재검토 트리거로만 보존 |

즉 2026-06-08에 "바탕화면에서 인디를 남길까"를 실제로 검토했으나 회귀로 규정하고 기각했다. **구현이 `lastFiltered` 를 설계 의도(중복 억제)를 넘어 HIDE 발송의 전제 조건으로 사용한 것**이 결함의 뿌리다. 중복 억제만이 목적이라면 `_indicatorVisible` 조건만으로 충분하다 — 숨기고 나면 그 값이 거짓이 되기 때문이다.

한편 **시작 메뉴/검색 창은 정반대로 "표시"가 사양**이다([PRD §2.2 `:74`](../KoEnVue_PRD.md), `SystemInputProcesses` = `StartMenuExperienceHost`/`SearchHost`/`SearchApp`). 2026-07-22 실측에서 `SearchHost`(6회)·`StartMenuExperienceHost`(1회) 모두 정상 표시됐고, 두 프로세스가 **동일 좌표 `(1291,450)`** 를 쓴 것으로 PRD §2.2 의 "직전 `SearchHost` 패널 rect 재사용" 보정이 실제 작동함도 확인됐다.

## 교차검증 — 워크플로우 `user-hidden-unhide-hunt`

4개 렌즈(state-machine / filter-conditions / entry-paths / stale-position) 병렬 탐색 + 발견별 적대적 반증. **26 에이전트, 실패 0, 확정 18건 · 기각 4건. 4렌즈 전부 `CONFIRMED`**(반증 실패).

반증 시도의 핵심 결과:

- `SystemFilter.ShouldHide` 호출부는 저장소 전체에서 **감지 스레드 [`:1359`](../../Program.cs) 와 부팅 워밍업 [`:223`](../../Program.cs) 둘뿐** → 메인 스레드에 필터 재평가 경로가 **존재하지 않음**.
- `WM_HIDE_INDICATOR` 발송 5곳(`1314/1381/1446/1462/1509`)·직접 `HideOverlay` 2곳(`1033/1144`) 중 필터 지속 중 도달 가능한 것은 `1381`·`1314` 뿐이며 **둘 다 같은 에지 트리거로 막혀 있음**.

### 같은 뿌리의 파생 결함 (확정)

| 항목 | 내용 |
|------|------|
| 두 번째 인스턴스 활성화 | `WM_APP_ACTIVATE` 경로([`:855`](../../Program.cs))도 필터 미확인 — **`user_hidden` 을 쓰지 않는 사용자도 겪음**(바탕화면 바로가기 더블클릭) |
| 전체화면 · 프로필 `enabled:false` | ①보다 나쁨 — 필터 대상이 앱 자신이라 **그 앱으로 돌아와도 해소 안 됨**. 제3의 창을 거쳐야 함 |
| config 핫리로드 (반대 방향) | `user_hidden` 을 `false→true` 로 직접 편집하면 인디가 숨겨지지 않고 **낡은 위치·낡은 IME 라벨로 동결** |
| `TryHandleModalGate` [`:1311`](../../Program.cs) | 동일한 `!LastFiltered` 에지 트리거 — 자체 모달 설정창 위 잔류 |
| IME WinEvent 훅 | [`:526~546`](../../Program.cs) `HandleImeStateChanged` 가 `UserHidden` 만 보고 필터는 안 봄. 필터 지속 중 `IME state:` 로그가 관측됨(정황) |
| 주석 오류 | [`:674~680`](../../Program.cs) 의 "감지 스레드가 한 틱 먼저 숨김 메시지를 보내므로 이 경로엔 도달하지 않는다"는 전제가 **거짓** |

## 채택 · 구현 (2026-07-23)

세 후보 **전부 채택**. 구현 요약:

1. **(a) HIDE 레벨 트리거** — `TryHandleFilter` / `TryHandleModalGate` 의 HIDE 게이트를 `_indicatorVisible` 만으로 발송. `LastFiltered` 는 중복억제·`foregroundChanged` 유도용으로 유지(PRD §7.2). 재HIDE 는 Debug 로그.
2. **(b) 강제 Show 시 라이브 필터 재평가** — `TryShowIndicatorIfForegroundAllowed`: 라이브 `GetForegroundWindow` + `ResolveFocusWindow` + `ResolveForApp` + `ShouldHide`. `ApplyUserHiddenTransition` 표시 분기·`HandleActivateRequest` 가 사용. `HandleConfigChanged`: `prev.UserHidden==false && UserHidden && visible` → `HideOverlay("config UserHidden")`.
3. **(c) 프레임 캐시 무효화** — `Overlay.ClearLastValidSystemInputFrame` — `HideOverlay` 에서 호출. SearchHost→StartMenu 가시 전환은 Hide 를 타지 않으므로 보정 캐시는 그 경로에서 유지.

> ⚠ 이 영역은 2026-06-08 PR-25(grace period)에서 **애니메이션 경합으로 인디가 사라진 채 박제되는 회귀**를 겪은 곳이다. `_forceHidden` 은 NonKorean IME Hide 경로와 의미를 공유한다. (a) 는 히스테리시스(`HideHysteresisPolls`)를 유지해 flip-flop 흡수 회귀를 피했다.

## 이번 PR 범위 밖 · 기록 유지 — 토탈 커맨더 인디 위치 진동

`TOTALCMD64` 사용 중 메인 인디 위치가 두 좌표를 **자동으로 왕복**한다:

```
19:51:59.003  pos=(3371,1334)
19:51:59.937  pos=(2511,1334)   ← 0.934초
19:52:00.782  pos=(3371,1334)   ← 0.845초
19:52:01.529  pos=(2511,1334)   ← 0.747초
19:52:02.276  pos=(3371,1334)   ← 0.747초
19:52:03.019  pos=(2511,1334)   ← 0.743초
```

- **4초간 6회, 간격이 0.75초로 수렴.** 사용자 조작(F3 열고 닫기)으로 설명 불가.
- 해당 구간에 `HideOverlay` 로그 **0건** → 숨김이 아니라 **순수 위치 이동**. 사용자에게는 "깜빡임/사라짐"으로 보인다.
- `WM_POSITION_UPDATED` 는 `foregroundChanged` 일 때만 발송되므로, **같은 프로세스의 두 top-level 창이 번갈아 foreground 를 잡고 있다**는 뜻.
- `TLister`(F3 내장 뷰어)가 필터에 걸린 기록 1회(`streak=1/3`, `hwndFocus` 정상). 사용자 확인 결과 **전체화면 아님** → 조건 7 배제. 창 생성 순간의 조건 2(`!IsWindowVisible`) 또는 조건 3(가상 데스크톱 미등록) 추정. 히스테리시스가 흡수해 실제 숨김으로는 이어지지 않음.
- 커서 인디케이터를 켠 뒤 왕복 간격이 1~2초 → 0.75초로 짧아짐(**상관만 확인, 인과 미검증**).

**진단 로그**: `PositionUpdated` 에 `hwnd`/`class` 추가는 **c359171 완료**(CHANGELOG Internal). 진동 원인 규명·수정은 **본 PR 범위 밖** — 기록만 유지. PR-27 캐럿 트윈·예측 외삽·Tier-3 관찰도 동일(작업 목록 제외).

## 코드 좌표

- [`Program.cs`](../../Program.cs): `ShowIndicatorAtForeground` 519 · `HandleImeStateChanged` 526 · `HandlePositionUpdated` 558 · `GetAppPosition` 698 · `ResolveCurrent` 주석 674~680 · `HandleTrayToggle` 1005 · **`ApplyUserHiddenTransition` 1025** · `HandleMenuCommand` 1043 · `DetectionState` 1174 · `ProcessDetectionTick` 1249 · `TryHandleModalGate` 1311 · **`TryHandleFilter` 1354(게이트 1378, 래치 1386)** · `TryHandleSystemInputClose` 1436 · `EmitStateChanges` 1529
- [`App/UI/Overlay.cs`](../../App/UI/Overlay.cs): `_lastValidSystemInputFrame` 198 · `GetDefaultPosition` 224(캐시 저장 246, 재사용 248~251)
- [`App/Detector/SystemFilter.cs`](../../App/Detector/SystemFilter.cs): `ShouldHide` 72(8조건)
- [`App/Config/DefaultConfig.cs`](../../App/Config/DefaultConfig.cs): `PollIntervalMs` 95 · `HideHysteresisPolls` 116 · `SystemInputProcesses` 153 · `DefaultSystemHideClasses` 246
