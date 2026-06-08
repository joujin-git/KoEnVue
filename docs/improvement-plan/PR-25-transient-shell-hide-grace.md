# PR-25: 설정앱 cold start 시 transient 작업표시줄 foreground 로 인한 인디 완전소멸 수정

> 상태: **보류 (won't-fix)** — 2026-06-08 구현·smoke 검증 결과 grace 가 cold start 지연(2~4초)을 흡수 못 함이 확인되어 **OS 의존 문제로 감수 결정**, 전부 롤백. 아래는 설계·실측 기록(향후 OS 동작 변화/dim 접근 재고용 보존). 결론은 문서 하단 [보류 결정](#보류-결정-2026-06-08) 참조.

## 동기

설정앱을 **작업표시줄 아이콘 클릭**으로 열 때(cold start), 그 클릭이 `Shell_TrayWnd`(작업표시줄)를 잠깐 foreground 로 만든다. 작업표시줄은 `system_hide_classes` 에 의도적으로 등록돼 있어 → `SystemFilter.ShouldHide`=true → 연속 3폴링(`HideHysteresisPolls`) → `WM_HIDE_INDICATOR` → `HideOverlay(forceHidden:true)` → `OverlayAnimator` FadingOut → `HandleFadeTimer` 의 `_forceHidden || !AlwaysMode` 분기에서 **완전소멸(α=0)**.

설정앱이 `fade_out`(400ms) 내에 foreground 복귀하면 `TriggerShow` FadingOut 분기([OverlayAnimator.cs](../../Core/Animation/OverlayAnimator.cs))의 `_forceHidden=false` 리셋으로 소멸을 회피하나, cold start 지연으로 복귀가 400ms 를 넘기면 소멸. **이 산발성이 버그의 정체.**

**확정 근거** (DIAG 2차 관측, 2026-06-08, 17건 일관, 예외 0):
- 딤(`HoldExpire`)은 항상 `forceHidden=False` → `decision=IDLE`(α=140). 완전소멸 절대 안 함.
- 완전소멸(`decision=HIDE`)은 항상 직전에 `Filter triggered HIDE: fgClass=Shell_TrayWnd, streak=3` → `TriggerHide(forceHidden=True)`.
- 06-05 의 "TrackWindowMove × 딤 경합" / "딤 stale forceHidden" 가설 **둘 다 반증**됨. 딤은 무관.

## 핵심 구분 키 (설계 근거)

| | transient (cold start 스침) | 실사용 (시작메뉴 열고 머묾) |
|---|---|---|
| `Shell_TrayWnd` foreground | 잠깐 (클릭 1발) | 지속 |
| 직후 foreground | 비-필터 창(`ApplicationFrameHost` 등) 복귀 | `Shell_TrayWnd` 유지 |
| 원하는 동작 | 인디 **유지**(소멸 흡수) | 인디 **숨김**(현 동작 유지) |

두 경우를 가르는 것은 **"HIDE 확정 후에도 짧은 유예 안에 비-필터 foreground 가 돌아오는가"**. 이 유예를 **detection 스레드 입력 측**에 두면, transient 는 흡수하고 실사용은 그대로 HIDE 된다. PR-23 과 같은 결(입력-측 디바운스)이고, 애니메이션 엔진/상태머신을 건드리지 않아 PR-24 부작용(anchor 추적·멀티모니터)을 원천 회피한다.

## 후보별 분석

### (a) grace period — detection 스레드에서 transient 작업표시줄 HIDE 유예 ★ 추천

**설계**: `TryHandleFilter` 에서 streak 가 `HideHysteresisPolls` 에 도달해도, **filtered 진입 원인이 transient 후보 클래스**(작업표시줄/바탕화면 류)인 경우에 한해 **추가 유예 폴링(grace) 동안 HIDE PostMessage 를 보류**한다. 유예 중 비-필터 foreground 가 돌아오면 HIDE 자체가 발화되지 않고 자연 복원. 유예가 끝날 때까지 계속 같은 transient 클래스로 filtered 면(=실사용) 정상 HIDE 확정.

- 핵심은 **잠정 구간 상태 미갱신 비대칭**의 연장이다 ([dev-notes/2026-06-02](../dev-notes/2026-06-02-explorer-hide-flipflop-debounce.md)). grace 구간에서도 `LastFiltered`/`LastHwndForeground` 를 갱신하지 않으므로, 다음 틱이 비-필터면 기존 filter-exit 경로가 `WM_POSITION_UPDATED`(Show)를 post → 인디 자연 복원. "첫 진입 Show 누락" 함정도 동일 비대칭으로 회피.
- **streak(=N) 디바운스는 flip-flop 흡수, grace(=M) 는 transient 스침 흡수**로 역할 분리. 둘은 직렬: filtered 가 연속 N+M 폴링 지속해야 HIDE 확정.

**transient 후보 선별**: 모든 필터 클래스에 grace 를 걸면 실제 숨김 대상(다른 앱 전체화면 등) HIDE 가 일률 지연되는 회귀가 생긴다. **작업표시줄/바탕화면 계열 클래스만** grace 대상으로 한정한다(`ShellTransientHideClasses` const). transient 판정은 foreground hwnd 클래스명 기준 — `WindowProcessInfo.GetClassName` 1회 + `MatchesAny` 재사용, `ShouldHide` 8조건 재평가 불요.

**트레이드오프**:
- 실사용 작업표시줄(시작메뉴 열고 머묾) HIDE 가 약 `PollIntervalMs × M`(80ms×grace) 지연. M=4 면 ~320ms — 체감 경미.
- grace 값 충분성은 cold start 복귀 지연 분포에 의존 → 수동 smoke 캘리브레이션 필요(아래).

**부작용**: detection 스레드 1메서드(`TryHandleFilter`) + const 2 + `SystemFilter` 헬퍼 1 + `DetectionState` 필드 1. 애니메이션 엔진/Core 무변경. PR-24 의 anchor 추적·멀티모니터 접근 전무.

**P규칙**: P1(NuGet/Win32 무추가) · P2(로그 영어) · P3(grace 폴링·클래스 모두 const) · P4(`MatchesAny` 단일 매칭 재사용) · P5(manifest 무변경) · P6(App 내부, Core 누출 0) 모두 충족.

### (b) hysteresis streak 증가 (`HideHysteresisPolls` 3 → 상향)

cold start 출렁임을 streak 디바운스로 흡수. **transient/실사용 구분 불가** — 작업표시줄 실사용·flip-flop·다른 앱 전환 HIDE 가 일률 지연되어 "숨김이 굼뜨다" 회귀. **둔한 단일 다이얼**. 최저비용(const 1개)이나 정밀도 최악 → 비추천.

### (c) transient 시스템창 forceHidden→dim 조건부 (Always 모드)

**치명적 결함**: `forceHidden:true` 경로가 두 곳 — ① [Program.cs](../../Program.cs) `HideOverlay`(이번 버그), ② [Animation.cs:76](../../App/UI/Animation.cs) `NonKoreanImeMode.Hide`(비한국어 IME 의도적 완전 숨김). `OverlayAnimator._forceHidden` 의 완전소멸 의미를 건드리면 NonKorean Hide 경로까지 오염(EN 일 때 숨겨야 하는데 dim 으로 남음). 애니메이션 상태 자체를 바꾸는 출력-측 고위험 변경(2026-05-20 가설 E/F 계열). → 비추천. P4·P6 압박.

### (d) 기각된 대안

- **`Shell_TrayWnd` 를 `system_hide_classes` 에서 제거**: 작업표시줄 실사용 숨김 의도 정면 회귀. 기각.
- **애니 엔진 FadingOut 재진입으로 흡수**: 2026-06-02 dev-note 가 "고위험 애니 엔진 → 범위 제외"로 남긴 영역. 이번 버그는 flip-flop 이 아니라 단발 transient HIDE 후 느린 복귀라 입력-측이 정확. 기각.

## 추천: (a-1) grace period + transient 클래스 표적

**근거**:
1. 확정된 **구분 키(transient=곧 비-필터 복귀 / 실사용=Shell_TrayWnd 유지)와 메커니즘이 1:1 정합**. grace 가 정확히 "복귀를 기다리는 창"을 구현.
2. **입력-측 국소 변경** — detection 스레드 1메서드 + const 2 + 헬퍼 1 + 필드 1. 애니메이션 엔진/Core/상태머신 **무변경** → PR-24 롤백 사유(anchor·멀티모니터·새 상태머신) 원천 회피.
3. 기존 streak 디바운스의 **검증된 비대칭(잠정 구간 상태 미갱신)을 그대로 연장** — "첫 진입 Show 누락" 함정 재발 없음.
4. (c)와 달리 `_forceHidden` 공유 의미를 건드리지 않아 NonKorean Hide 경로 안전.
5. (b)와 달리 transient 표적이라 실사용/다른 앱 HIDE 반응성 저하 최소.

## 단계별 실행 (파일별 변경 범위)

1. **[App/Config/DefaultConfig.cs](../../App/Config/DefaultConfig.cs)** — `HideHysteresisPolls` 인근 신규 const 2:
   - `GraceHidePolls`(초안 4 = 320ms) — transient 작업표시줄 HIDE 확정 후 비-필터 복귀를 기다리는 추가 폴링 수. 코드 레벨 const(config 키 아님).
   - `ShellTransientHideClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW"]` — grace 표적(작업표시줄·바탕화면). `DefaultSystemHideClasses` 의 부분집합이나 의미 분리. 메뉴/팝업류(`ControlCenter`·`XamlExplorer`) 제외 — 머물면 숨김이 맞음.
2. **[App/Detector/SystemFilter.cs](../../App/Detector/SystemFilter.cs)** — `IsShellTransientForeground(IntPtr hwndForeground)` 헬퍼: `GetClassName` → `MatchesAny(name, ShellTransientHideClasses)`. P4 단일 매칭.
3. **[Program.cs](../../Program.cs) `DetectionState`** — 신규 필드 `ShellTransientGraceStreak`.
4. **[Program.cs](../../Program.cs) `TryHandleFilter`** — streak ≥ `HideHysteresisPolls` 도달 후 HIDE PostMessage 직전에 grace 게이트:
   - filtered 원인이 transient 이고 `ShellTransientGraceStreak < GraceHidePolls` 면: streak++ + `Logger.Debug("Filter HIDE grace-deferred (transient shell, grace={n}/{M}, ...)")` 후 **상태 미갱신 return**(현 인디 유지). 기존 streak-deferred 와 동일 비대칭.
   - transient 아니거나 grace 만료면 기존대로 HIDE 확정.
   - non-filtered 진입 시 `FilteredStreak=0` 과 같은 자리에서 `ShellTransientGraceStreak=0` 리셋.
5. **DIAG 제거 (마무리 단계)** — 수정 확정·smoke 통과 후:
   - [OverlayAnimator.cs](../../Core/Animation/OverlayAnimator.cs) DIAG 5곳(이번 세션, 미커밋): `using` + TriggerShow / TriggerHide / FadeDone / HoldExpire.
   - [App/UI/Overlay.cs](../../App/UI/Overlay.cs) · [Program.cs](../../Program.cs) DIAG(06-05 세션, 커밋 43bf358 에 반영됨): ShowMain / ShowSkip / Overlay.Show.
   - grace 게이트의 `Filter HIDE grace-deferred` Debug 로그는 **영구 유지**(race 영역 무로깅 금지 — 2026-06-02 정신).
6. **문서** — CHANGELOG `### 수정`, INDEX progress matrix, 신규 dev-note(`2026-06-XX-transient-shell-hide-grace.md`, 2026-06-02 와 상호 링크), implementation-notes §HIDE 디바운스에 grace 절.

## 검증 방법

**재현 smoke (라이브 Win32 의존, 단위 테스트 비현실 — PR-23 선례)**:
1. **수정 대상** — 작업표시줄 설정 아이콘 클릭(cold start) → 설정앱 떠도 인디 **유지**(완전소멸 0). 10회 반복.
2. **회귀가드 ① 실사용** — 시작메뉴 열고 수 초 머묾 → 인디가 `PollIntervalMs × (HideHysteresisPolls + GraceHidePolls)` ≈ 560ms 내 정상 **소멸**.
3. **회귀가드 ② 바탕화면** — `Progman` 클릭해 머묾 → 정상 소멸.
4. **회귀가드 ③ 다른 앱** — 일반 앱 전환·전체화면 진입 시 HIDE 반응 불변(transient 표적이라 비-셸 클래스 grace 무적용).
5. **회귀가드 ④ flip-flop** — 파일 탐색기(`CabinetWClass`) 클릭 시 깜박임/소멸 부재(streak 디바운스 불변).

**DIAG/Debug 로그 확인 항목** (제거 전):
- 시나리오 1: `Filter HIDE grace-deferred (...)` → 비-필터 복귀 → `Filter triggered HIDE` 부재 → `DIAG FadeDone decision=HIDE` 0건 (소멸 흡수 입증).
- 시나리오 2: grace M 까지 차오른 뒤 `Filter triggered HIDE: streak=3` → `FadeDone decision=HIDE` 정상 (실사용 HIDE 보존 입증).
- **`GraceHidePolls` 캘리브레이션**: 시나리오 1 의 grace 로그 max k → `GraceHidePolls = max + 여유(1~2)`.

**자동 검증**: `dotnet build`(debug) + `dotnet publish -r win-x64 -c Release`(AOT, 경고 0) + `dotnet test tests\KoEnVue.Tests\KoEnVue.Tests.csproj`(기존 90 PASS 유지).

**invariant grep**: `GraceHidePolls` / `ShellTransientHideClasses` / `IsShellTransientForeground` 정의 각 1곳, `KoEnVue.App` in `Core/` = 0, `DllImport` = 0, `DIAG` in `Core/` = 0(마무리 후).

## 위험과 완화

- **R1 grace 과소 → transient 미흡수**: 캘리브레이션 1순위(grace 로그 max k). const 1줄이라 재조정 저비용.
- **R2 grace 과대 → 실사용 HIDE 지연**: transient 표적이라 셸 클래스 한정. `HideHysteresisPolls + GraceHidePolls` ≤ ~7폴링(560ms) 캡.
- **R3 transient 목록 누락/과다**: `system_hide_classes` 부분집합으로 보수적(4종). 메뉴/팝업류 제외.
- **R4 PR-24 부작용 재발**: 본 설계는 anchor 생존 확인·멀티모니터 좌표·새 상태머신을 전혀 도입하지 않음. 단일/같은 모니터에서도 동일 작동(monitor-agnostic).

## 롤백

단일 PR 커밋 revert 로 즉시 복귀(국소 변경). DIAG 제거는 별도 후행 커밋으로 분리해, grace 검증 중 재진단이 필요하면 DIAG 제거 전 상태로 부분 롤백 가능.

## 보류 결정 (2026-06-08)

(a-1) grace 를 구현·빌드·reviewer 검증·smoke 한 결과 **채택하지 않고 전부 롤백**했다.

**실측 (DIAG 2차 관측, koenvue.log 10:21~10:23)**:
- grace 게이트는 정상 작동(`Filter HIDE grace-deferred` 28건 기록)했으나 **매번 `grace=4/4` 만료 후 HIDE** 됨.
- 작업표시줄(`Shell_TrayWnd`)/바탕화면(`Progman`)이 설정앱 cold start 동안 **2~4초** foreground 로 지속 → `streak 3 + grace 4 = 7폴링`(~560ms)으로는 흡수 불가.
- 완전소멸(`FadeDone decision=HIDE`)이 설정앱(`ApplicationFrameHost`) 등장보다 **2~4초 *먼저*** 발생 — cold start 가 느려 그 사이 셸이 foreground 를 점유.

**보류 사유**:
- 완전소멸 타이밍이 **Windows 의 cold start foreground 동작**(설정앱 로딩 중 작업표시줄/바탕화면이 foreground 를 유지)에 의존 — KoEnVue 가 제어할 수 없는 OS 영역.
- grace 를 cold start 만큼(40폴링+, ~3초) 늘리면 작업표시줄/바탕화면 **실사용** 시에도 인디가 2~4초 잔존 = 명백한 회귀. 둘을 가를 신뢰 가능한 신호가 없음.
- 무리한 고정값 맞춤은 OS 업데이트마다 타이밍이 바뀌면 다시 깨질 위험.

→ 사용자 결정: **OS 의존 문제로 감수**. grace 코드 + 진단 DIAG 전부 롤백(working tree = PR-23 상태 복구).

**향후 재검토 트리거**: OS cold start foreground 동작이 바뀌거나, "셸 foreground 시 완전소멸 대신 dim(흐려짐) 유지"(후보 c) 의 트레이드오프(바탕화면/작업표시줄 실사용 시 인디 흐리게 잔존)를 수용하기로 할 때. 후보 c 는 `_forceHidden` 의미가 NonKorean IME Hide 경로와 공유되므로 "transient 셸만 dim, 그 외 HIDE" 분기가 전제.
