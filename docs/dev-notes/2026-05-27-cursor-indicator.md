# 커서 추종 인디케이터 — 엔진 분리 + P4 예외 정당화 (2026-05-27)

> **상태**: PR-B 완료 + 4 fix (3 commits — PR-B-1 엔진/Style/Renderer + PR-B-2 AppConfig 10 키 + PR-B-3 App 파사드 `CursorOverlay` + 트레이 토글 + 사용자 가시 통합 + 1 fix commit). 사용자가 트레이 메뉴 "커서 인디케이터 숨김" 체크박스 (체크 = 현재 숨김) 로 즉시 활성화 가능. 본 섹션은 PR-B 종합 회고.

## 사후 fix (PR-B 첫 사용자 검증 직후, 2026-05-27)

사용자 보고 2건:

1. **커서 인디 표시 안 됨** — 메뉴 토글 후에도 화면에 안 나타남. 원인 2개:
   - `LayeredCursorBase.Show(x, y)` 가 `ShowWindow(SW_SHOW)` 호출 안 함 — `_isVisible` 플래그만 set. 메인 인디는 `Animation.cs:100` 가 `wasHidden` 분기에서 `SW_SHOW` 호출하는 패턴인데 cursor 의 호출자 (`CursorOverlay.RenderAtCursor`) 도 명시 호출 누락.
   - `_lastAlpha` 디폴트 `0` — 첫 `Render → UpdateLayeredWindow` 가 `SourceConstantAlpha=0` (완전 투명) 으로 그려져 사용자가 못 봄. cursor 인디는 페이드 없이 항상 alpha 255 인데 초기 0 으로 둔 게 버그.

   **fix**:
   - `LayeredCursorBase._lastAlpha = 255` 디폴트 (페이드 없는 cursor 인디 의도와 일치).
   - `CursorOverlay.RenderAtCursor` 끝에 `if (!_isVisible) User32.ShowWindow(_engine.Hwnd, SW_SHOW)` — 호출자 명시 패턴. **dev-notes 가설 A 회피** ([2026-05-20-post-pr10-attempts-reverted.md](https://github.com/joujin-git/KoEnVue/blob/feat/v094-integration/docs/dev-notes/2026-05-20-post-pr10-attempts-reverted.md): Render 전 SW_SHOW 가 layered window 비트맵 없이 visible 캐싱 → 후속 UpdateLayeredWindow 가 화면에 안 나타남). Render 후 호출 패턴이 안전.

2. **트레이 메뉴 라벨 변경**: "커서 인디케이터" → "커서 인디케이터 숨김" — 메인 인디 "인디케이터 숨김" 과 동일 패턴 (MF_CHECKED = 현재 숨김 상태). 인터뷰 초기 답 ("라벨 자체가 기능명, 체크 = ON") 을 사용자가 검토 후 변경. 의미 반전 (`!config.CursorIndicatorEnabled ? MF_CHECKED : MF_UNCHECKED`).

**그러나 fix 후 2차 검증에도 cursor 인디 표시 안 됨** — 추가 회귀 발견:

3. **트레이 메뉴 클릭이 cursor lifecycle 진입 안 함** — `Program.HandleMenuCommand` 의 람다 (line 956-990) 는 `mtime self-bump` 정책 때문에 `HandleConfigChanged` 우회하고 `Overlay.HandleConfigChanged` + 등 효과를 직접 호출. **여기에 cursor 인디 lifecycle 분기가 빠져있어** 트레이 토글 클릭 시 `Settings.Save` 만 호출되고 `EnableCursorOverlay` 가 진입 안 됨 — 윈도우/엔진/타이머 미생성.

   **fix**: cursor lifecycle 3 분기를 `Program.ApplyCursorConfigChange()` 헬퍼로 추출. `HandleConfigChanged` 와 `HandleMenuCommand` 람다 양쪽에서 호출. D7 (단일 진실원) 패턴.

이 fix 가 PR-B 의 첫 사용자 가시 검증을 통과시킴.

## 사후 fix 2차 (사용자 가시 검증 직후, 2026-05-27)

사용자가 4가지 문제 보고:

1. **커서/캐럿 위치가 원 중심과 정확하지 않음** + 2. **DPI 다른 모니터로 cursor 이동 시 원 크기 변화가 살짝 이상함** — 같은 근본 원인. `CursorOverlay.RenderAtCursor` 가 `_engine.GetBaseSize().w / 2` 로 halfBbox 계산해 `Show(cursor.X - halfBbox, cursor.Y - halfBbox)` 호출. 그러나 GetBaseSize 가 이전 DIB 크기 (이전 모니터 DPI 기준) → DPI 다른 모니터 진입 시 한 프레임 잘못된 위치 표시 + 후속 `UpdatePosition` 으로 보정하는 비대칭. **fix**: `LayeredCursorBase.ShowAtCenter(centerX, centerY, style)` API 신설 — 내부에서 `UpdateDpiFromPoint` 갱신 + `BoundingBoxLogicalPx * 새 DPI scale` 직접 계산해 정확한 좌상단 set. RenderAtCursor 가 본 API 사용 → race 없음 + 좌표 정밀도 = 픽셀.

3. **콘솔 호스트에서 한/영 전환 미감지 (캡스락은 정상)** — **메인 인디의 기존 limitation**. 콘솔 호스트 (conhost.exe / Windows Terminal legacy) 의 IME 모델이 일반 Win32 IME 와 달라 `WM_INPUTLANGCHANGE` / `ImmGetConversionStatus` 가 콘솔 윈도우에 도달 안 함. 캡스락은 글로벌 `GetKeyState(VK_CAPITAL)` 로 감지하므로 콘솔 무관. **본 PR (cursor 인디) 범위 밖** — 메인 인디도 같은 증상. 별도 작업으로 분리 (legacy console IME 감지는 `INPUT_RECORD` 폴링 또는 별도 hook 필요, 비용 큼).

4. **인디와 cursor 인디가 함께 이동하는 케이스** — 시나리오 구체 확인 필요. 가설:
   - (a) 메인 인디 드래그 중 cursor 도 마우스 따라 이동 (정상 — 드래그가 마우스 위치 따라가니 cursor 인디도 cursor 위치 따라감)
   - (b) cursor 인디 좌표 계산에 메인 인디 위치 영향 받는 버그 (코드상 cursor 좌표만 사용하므로 가능성 낮음)
   - (c) 두 인디가 동시 위치한 상태 (cursor 가 메인 인디 위에 호버 — 인터뷰 답 "별도 처리 없음")
   - 사용자 추가 정보 대기 후 진단.

추가 보고 (5)~(7) — 사용자 가시 추적:

5. **트레이 메뉴가 즉시 사라짐 + 메인 인디 잠시 cursor 따라 이동**: cursor 인디 `ShowWindow(SW_SHOW)` 호출이 z-order 변경 트리거 → 트레이 메뉴 modal loop 가 다른 윈도우 활성화로 인지 → dismiss. 메인 인디 점프는 `SetForegroundWindow(_hwndMain)` (트레이 ShowMenu 워크어라운드) → WinEvent hook → `HandleFocusChanged` → `TriggerShow` → 메인 인디가 `_hwndMain` (0x0 hidden) 기준 위치로 점프. cursor 인디는 cursor 위치, 메인 인디는 (0, 0) 근처 — 사용자 시각엔 "함께 이동" 으로 인지.

   **fix**: cursor 윈도우를 **WS_VISIBLE 영구 박음** + `Hide()` 가 `SW_HIDE` 대신 `SourceConstantAlpha=0` 으로 `UpdateLayeredWindow` 호출 (시각만 완전 투명, z-order 변경 0). `PrepareResources` 가 첫 alpha=0 UpdateLayeredWindow 호출해 dev-notes 가설 A (Render 전 visible → 비트맵 없이 캐싱) 회피. `CursorOverlay.RenderAtCursor` 의 `ShowWindow` 호출 제거. 메뉴 dismiss + 메인 인디 점프 둘 다 차단 (메인 인디 점프는 cursor 의 z-order 변경이 trigger 였을 가능성).

6. **작업 표시줄 위 cursor 인디 가려짐**: 작업 표시줄은 system topmost, 일반 앱 `WS_EX_TOPMOST` 라도 위로 못 올라감.

   **fix**: `WindowFromPoint(cursor)` → 클래스명 → `config.SystemHideClasses` 매칭 시 cursor 인디 즉시 hide. 메인 인디의 `SystemFilter` 와 같은 목록 (`Shell_TrayWnd` / `Progman` / `WorkerW` / Win11 시스템 UI) 재사용. `WindowFromPoint` 는 `WS_EX_TRANSPARENT` 윈도우 통과 (dev-notes F2) → cursor 인디 자체 미감지. `Core/Native/User32.WindowFromPoint` LibraryImport 신규 1건.

7. **cursor 인디 원 가장자리 깍뚜기 (jagged edge)**: 1x sample box-filter AA 가 코어 2px 좁은 가장자리에서 충분히 부드럽지 않음.

   **fix**: **2x2 supersampling** — 픽셀당 4 sub-sample (0.25/0.75 오프셋) → alpha-weighted 색상 평균 + alpha 평균. 가장자리 4배 부드러움. 비용 ~4배지만 DIB 96x96 + early exit 후 ~30% 실효 영역 + Render 가 정지 시점에만 호출 → 폴링 50ms 안에서 무시.

## 사후 fix 4차 (사용자 검증 4차 직후, 2026-05-27)

사용자 보고 1건 + 추가 2건:

8. **(WS_VISIBLE fix 도입 후) cursor 인디 또 다시 표시 안 됨** — 본 PR 의 사후 fix 3차 (commit 06d0d3f) 가 도입한 회귀. `PrepareResources` 와 `Hide()` 가 `UpdateOverlay(_lastX, _lastY, _w, _h, 0)` 호출 → 내부의 `UpdateOverlay` 가 `_lastAlpha = 0` 으로 캐시 갱신 → 후속 `Render` 가 `UpdateOverlay(_, _, _, _, _lastAlpha=0)` 호출 → **alpha=0 으로 그려져 시각 invisible**. fix: `PrepareResources` 와 `Hide()` 끝에 `_lastAlpha = 255` 명시 복원 — UpdateOverlay 의 alpha 캐시 갱신 후 cursor 인디 의도 (항상 255 표시) 복원. 2줄 fix.

9. **(해결됨 — 사후 fix 5차 + 진단 3차 + 사후 정리 참조)** 부팅 시 메인 인디 보였다가 사라짐 회귀 — PR-A 의 `SnapToTargetAlpha Fade KillTimer` fix 가 작동 안 함 (사용자가 PR-A 단독 머지 후 정상 동작 확인했으나 PR-B 머지 후 회귀). cursor 인디 도입이 detection thread 메시지 race 를 trigger 하는 가설 — `EnableCursorOverlay` 의 윈도우 생성 + `SetTimer` 가 부팅 sequence 늘림, 또는 `HandleImeStateChanged` 의 `CursorOverlay.SetImeState` 추가 호출이 fade race 영향. dev-notes/2026-05-20 가설 F (detection thread 메시지 폭주 자체 줄이기) 영역으로 fix 진입 필요. **진단 요청**: cursor 인디 enabled=false 상태에서도 회귀 재현되는지 — 재현되면 cursor PR 무관 (메인 인디 자체 회귀), 정상이면 cursor PR 이 trigger. → **5차/진단 3차에서 cursor PR 진짜 trigger 확정** → z-order fix (`3d9e0bd`) 가 진짜 원인 (`WS_EX_TOPMOST` z-band 재정렬 → Shell_TrayWnd 잠시 FG → SystemFilter hide) 차단. 사후 정리에서 안전망 (`BootGracePeriodMs` 1500ms, FG changed 진단) 제거 완료.

10. **(별도 PR 분리됨 — cursor PR 범위 밖)** 콘솔 호스트 한/영 회귀 — 이전 정상 동작 명시. cursor 인디 PR 의 ImeStatus / detection thread 변경 0. **5차 진단 결과**: cursor enable=ON/OFF 둘 다 회귀 → cursor PR 무관 (메인 인디 자체 회귀, PR-A 영역 또는 더 이전). 별도 PR 로 분리.

## 사후 fix 5차 (사용자 케이스 A/B 진단 결과, 2026-05-27)

사용자가 cursor enable=ON/OFF 두 케이스 비교 검증 보고:

| 케이스 | (2) 부팅 깜박임 | (3) 콘솔 한/영 |
|--------|--------------|--------------|
| A (cursor ON) | 회귀 | 회귀 |
| B (cursor OFF) | 정상 | 회귀 |

해석:
- **(2) cursor ON 에서만 회귀** → cursor PR 이 trigger. 가설: cursor motion timer 첫 발화 (50ms 후) 가 detection thread 메시지 폭주 + 메인 인디 fade race 와 충돌. PR-A 의 `SnapToTargetAlpha` fix 가 cursor 추가 race window 에서 충분히 차단 못 함.
- **(3) cursor ON/OFF 둘 다 회귀** → **cursor PR 무관**. 메인 인디 자체 회귀 (PR-A 영역 또는 더 이전). 별도 PR 로 분리.

**(2) fix**: cursor 인디 부팅 grace period 500ms 도입. `CursorOverlay.Initialize` 가 `_bootTick = Environment.TickCount64` 마킹, `HandleCursorMotionTimer` 진입부에 `if (TickCount - _bootTick < 500) return;`. 부팅 직후 500ms 동안 cursor 표시 skip → detection thread 첫 80ms 폴링 + 메인 인디 SnapToTargetAlpha 안정화 완료 후 cursor 첫 표시. cursor 가시 첫 표시 지연 = 500ms 라 사용자 인지 미세. HandleConfigChanged 의 OFF→ON 토글 시에도 `_bootTick` 리셋 (`Initialize` 재호출 경로).

**(3) 별도 PR 분리**: cursor PR 머지 후 진단. 가설:
- PR-A 의 `SnapToTargetAlpha` fix 가 콘솔 호스트의 한/영 IME 시각 갱신 시 다른 race 영향
- 또는 더 이전 회귀 (PR-A 와 무관)
- 진단 방법: PR-A 이전 commit (`fac0251`) 빌드로 콘솔 한/영 검증 → PR-A 전후 회귀 여부 확인. 회귀 위치 격리 후 fix.

## 사후 fix 6차 (사용자 검증 5차 직후, 2026-05-27)

사용자 추가 보고 2건:

11. **작업 표시줄 / 시작 버튼 / 검색 박스 / 트레이 아이콘 호버 시 cursor 인디 일관 표시 요청** — 사후 fix 3차 (`06d0d3f`) 의 `WindowFromPoint + SystemHideClasses` hide 분기 revert. 사용자 결정: "작업 표시줄에 가려지겠지만 일관적이면 괜찮음". **fix**: `CursorOverlay.HandleCursorMotionTimer` 의 시스템 창 체크 분기 + `IsSystemHideWindow` 함수 + `User32.WindowFromPoint` LibraryImport (cursor 만의 사용처였음) 모두 제거.

12. **(임시 안전망 — 사후 정리에서 제거됨)** 부팅 grace 500ms 가 부족 — 사용자 2차 보고: cursor enable 상태로 부팅 시 메인 인디 1초 정도 표시 후 사라짐. 가설: cursor 첫 `RenderAtCursor` 의 `ShadeDib` (2x2 supersampling) 가 메인 스레드 ~수십ms 점유 → 메인 인디 `OverlayAnimator` fade tick (16ms) 1-2 누락 → 잘못된 phase 전이로 빠른 fade-out. **임시 fix**: `BootGracePeriodMs` 500 → 1500ms 로 늘림. → **진단 3차에서 가설 오류 확정** — 진짜 원인은 cursor 윈도우의 `WS_EX_TOPMOST` z-band 재정렬 trigger (가설 CC). z-order fix (`3d9e0bd`) 후 `BootGracePeriodMs` 안전망 불요 → 사후 정리에서 **`_bootTick` 필드 + 가드 + 상수 모두 제거**. cursor 첫 표시 = `idle_delay_ms` (100ms) 후.

  대안 (미적용, 후속 검토): 가설 F (detection thread 메시지 폭주 자체 줄임) 적용. `HandlePositionUpdated` 가 `_indicatorVisible = true` 세팅한 직후 IME/Focus 메시지 1 tick suppress. 메인 인디 영역 변경이라 cursor PR 범위 밖. → **z-order fix 가 진짜 원인 차단 후 본 대안도 불요.**

## 사후 진단 로그 추가 (사용자 검증 6차 후, 2026-05-27)

사용자 보고 (12) 의 회귀 잔존: cursor enable 상태로 부팅 시 메인 인디 1.5초 (cursor 등장) 후 ~1초 더 보였다가 사라짐. publish 폴더 config 확인 — `display_mode: "always"` 확정. AlwaysMode 면 fade-out 안 되어야 정상인데 cursor ON 시 사라짐 = 진짜 회귀.

**AlwaysMode 에서 사라지는 유일 경로**: `HideOverlay()` → `Animation.TriggerHide(forceHidden: true)` → AlwaysMode 도 fade-out → Hidden. `HideOverlay` 호출자:
- `WM_HIDE_INDICATOR` 메시지 처리 (line 326)
- `ApplyUserHiddenTransition` (UserHidden 토글)
- `HandleSessionChange` (lock screen)

`WM_HIDE_INDICATOR` post 위치:
- `TryHandleModalGate` (ModalDialogLoop 활성 + foreground 가 KoEnVue 자체)
- `TryHandleFilter` (resolved=null OR SystemFilter.ShouldHide=true)

부팅 시 ModalDialogLoop 비활성, UserHidden=false, Lock 안 됨 → 후보는 `TryHandleFilter` 의 SystemFilter.ShouldHide.

**진단 로그 추가 (Program.cs)** — 정확한 trigger 식별:
- `HideOverlay(string source)` 시그니처 변경 + Logger.Info 진입 로그
- `WM_HIDE_INDICATOR` 메시지 처리 → source = "WM_HIDE_INDICATOR"
- `ApplyUserHiddenTransition` → source = "UserHidden toggle"
- `HandleSessionChange` → source = "Session lock"
- `TryHandleModalGate` post 시 Logger.Info (fgPid + processId + ModalActive)
- `TryHandleFilter` post 시 Logger.Info (hwndFg + hwndFocus + resolved_null + fgClass)

사용자 재실행 후 koenvue.log 의 부팅 직후 5초 라인 → 정확한 trigger 식별 → 가설 확정 후 fix.

**진단 1차 결과 (publish/koenvue.log 14:43:21)**: `Filter triggered HIDE: fgClass=Shell_TrayWnd` → 작업 표시줄이 foreground → SystemFilter trigger → HideOverlay. 사용자 보고 — 마우스 위치는 탐색기 내였음. foreground (keyboard focus 받는 윈도우) 가 마우스 위치와 무관하게 Shell_TrayWnd 가 된 메커니즘 추적 필요.

**진단 2차 (commit 후속)**: detection thread 의 self-ignore 가드에 `_hwndCursorOverlay` 추가 (안전망 — cursor 윈도우 자체가 foreground 되는 케이스 차단) + `ProcessDetectionTick` 에 `FG changed: 0x... (className)` 진단 로그 추가 (foreground 변화 시점 trace). 사용자 publish exe 재실행 후 부팅 후 8초간 FG sequence 식별.

**진단 3차 결과 (publish/koenvue.log 14:55:39 — cursor enable ON)**:
```
14:55:39.832 KoEnVue starting
14:55:39.943 FG changed: CabinetWClass (탐색기 정상)
14:55:42.369 FG changed: Shell_TrayWnd   ← cursor 첫 표시 (1.5s) 후 ~1초 (= ~2.5s 시점)
14:55:42.370 Filter triggered HIDE: fgClass=Shell_TrayWnd
14:55:42.370 HideOverlay called: source=WM_HIDE_INDICATOR
```
**14:55:49 cursor OFF 동일 시나리오** — Shell_TrayWnd FG 변화 0 → 메인 인디 표시 유지. → **cursor PR 진짜 회귀 확정**.

**가설 CC (확정)**: cursor 첫 `UpdateLayeredWindow(alpha=255)` (BootGracePeriodMs 1500ms 후 첫 RenderAtCursor) → DWM 합성 → cursor 윈도우의 `WS_EX_TOPMOST` 가 다른 topmost z-band 윈도우 (Shell_TrayWnd 도 topmost) 재정렬 trigger → Shell_TrayWnd 가 약 1초 후 foreground 변경 (DWM 합성 사이클 또는 Windows 의 z-order 갱신 지연) → detection thread → SystemFilter → hide.

**fix (commit 후속)**:
- `Program.Bootstrap.CreateCursorOverlayWindow` 에서 **`WS_EX_TOPMOST` 제거** — cursor 윈도우는 생성 시 일반 z-order 로 시작. 부팅 sequence 동안 다른 topmost 윈도우 (Shell_TrayWnd) 영향 0.
- `CursorOverlay.RenderAtCursor` 의 첫 가시화 시 명시 `SetWindowPos(HWND_TOPMOST, SWP_NOSENDCHANGING | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE)` 호출 — topmost 진입 + **`SWP_NOSENDCHANGING` 으로 다른 윈도우에 `WM_WINDOWPOSCHANGING` 알림 차단** → Shell_TrayWnd 등 다른 topmost 재정렬 trigger 없음.
- `Core/Native/Win32Types.Win32Constants.SWP_NOSENDCHANGING = 0x0400` 신규 (P/Invoke 아닌 const 추가).

탐색기 vs Total Commander 차이 해석 (cursor enable 무관 시): explorer.exe = shell process 라 Shell_TrayWnd 와 같은 프로세스 → 메시지 처리 큐 공유 → race 빈도 높음. Total Commander = 일반 third-party → race 없음.

## 사후 정리 (사용자 검증 통과 후, 2026-05-27)

z-order fix (`3d9e0bd`) 가 진짜 원인 차단 확정 (사용자 검증: cursor enable 부팅 + 탐색기 실행 → 13초 동안 Shell_TrayWnd FG 변화 0건, 메인 인디 정상 유지). 이전 안전망 코드 정리:

1. **`BootGracePeriodMs` (1500ms) 제거** — z-order fix 가 진짜 race 차단 후 cursor 첫 표시까지 인위적 지연 불요. `_bootTick` 필드 + 가드 + 상수 모두 제거. cursor 첫 표시 = `idle_delay_ms` (100ms) 후 — 사용자 가시 즉시 등장.

2. **`FG changed` 진단 Logger.Info 제거** — 임시 진단용 (회귀 trigger 식별 완료), 매 foreground 변화 시 라인 → log noise. `ProcessDetectionTick` 의 trace 분기 제거.

3. **유지 (영구 — 회귀 재발 시 즉시 trigger 식별 가치)**:
   - `HideOverlay(string source)` + Logger.Info 진입 로그
   - `Filter triggered HIDE: ...` (hwndFg + hwndFocus + resolved_null + fgClass)
   - `ModalGate triggered HIDE: ...` (fgPid + processId + ModalActive)
   - `_hwndCursorOverlay` 자기 무시 가드 (안전망 — cursor 윈도우가 어떤 이유로 foreground 잡혀 SystemFilter 평가하는 race 차단)
   - WS_EX_TOPMOST 제거 + SetWindowPos(SWP_NOSENDCHANGING) (fix 자체)

## 무엇 (What — PR-B-1 시점)

신규 3 파일로 커서 추종 인디케이터의 렌더 엔진 + Style + Renderer 도착. 사용자 가시 기능 미완성 — PR-B-3 도착 시 트레이 / 설정 다이얼로그 토글로 활성화 가능.

| 파일 | LOC | 역할 |
|------|-----|-----|
| `Core/Windowing/CursorStyle.cs` | ~55 | `CursorStyle` (engine 입력 10 필드) + `CursorMetrics` (engine→callback 출력 3 필드) record struct 쌍 + `BoundingBoxLogicalPx` 헬퍼 |
| `Core/Windowing/LayeredCursorBase.cs` | ~250 | `LayeredOverlayBase` 의 형제 엔진 — DIB 생성 + premultiply + `UpdateLayeredWindow` 만 책임. 콜백 시그니처 `Func<IntPtr ppvBits, CursorStyle, CursorMetrics, (int w, int h)>` |
| `App/UI/CursorRenderer.cs` | ~150 | distance-field 분석적 AA 픽셀 셰이더. 동심원 3개 (Inner / Middle / Outer) + 코어 / 헤일로 분리. CAPS OFF 시 Outer skip |

## 왜 별도 엔진 (P4 예외 정당화)

### 기본 원칙 vs 본 결정

CLAUDE.md P4: "하나의 구현만 — 공유는 `Core/`". `LayeredOverlayBase` (메인 인디 엔진) 가 이미 DIB 생성 / premultiply / `UpdateLayeredWindow` 의 ~120 LOC 를 보유. 본 PR-B 의 cursor 인디도 동일 패턴이 필요 — 통상이라면 `LayeredOverlayBase` 를 generic 화 (예: callback 시그니처를 다양화하거나 책임을 더 잘게 쪼개거나) 해서 공유했어야 함.

### 본 결정: 별도 엔진 (`LayeredCursorBase`) 신규 작성

**1차 근거**: 메인 인디는 알파 race 미해결 영역 (부팅 시점 깜박임 #2/#3 — 본 세션 PR-A 의 `SnapToTargetAlpha` Fade KillTimer fix 가 부분 해소했으나 detection thread 의 3 메시지 연쇄 자체는 여전) 이 있어, 메인 엔진에 새 변경면 (예: 콜백 시그니처 일반화, 새 책임 주입) 을 추가하면 회귀 위험. 본 PR-B 의 신규성 (커서 추종 / 동심원 / 헤일로) 을 메인 엔진 분해/일반화와 합치는 것은 위험·범위 양쪽 증가.

**2차 근거**: 콜백 시그니처 자체가 본질적으로 다르다 — 메인 엔진은 GDI 그리기 (DrawTextW / RoundRect) 를 사용하므로 콜백에 `hdc` 를 전달하고 내부에서 `GetCurrentObject` + `GetObjectDibSection` 으로 DIB ppvBits 재추출. cursor 엔진은 GDI 그리기 사용 없이 픽셀 셰이딩만 수행하므로 `ppvBits` 를 직접 전달. 일반화하려면 메인의 `Gdi32.cs` 의 `GetCurrentObject` / `GetObjectDibSection` 시그니처 유지 + 새 분기 추가가 되는데, 이는 메인 인디 핫패스에 변경면 추가.

**3차 근거**: 책임 표면 차이가 크다 — 메인 엔진은 폰트 (`EnsureFont` + `GetTextMetricsW` + `_textVCenterOffsetPx` 캐시) / 드래그 (`BeginDrag` + `HandleMoving` + `EndDrag`) / 라벨 측정 (`CalculateFixedLabelWidth` + 7-key 캐시) / `WindowSnapHelper` 위임의 4 책임 영역을 보유. cursor 엔진은 이 중 0 개가 필요 — 폰트도, 드래그도, 라벨도, 스냅도 없음. 공통 분모는 진정으로 ~120 LOC 의 DIB 생성 + premultiply + UpdateLayeredWindow 뿐.

### 비용 vs 편익

**비용**: ~120 LOC 중복 + 향후 어느 한쪽 버그 fix 시 양쪽 동시 점검 부담 (예: `CreateDIBSection` 실패 처리 / `[LibraryImport]` Win32 호출 형식 변경 / DPI 갱신 정책 변경).

**편익**: 메인 인디 핫패스의 변경면 0 + cursor 엔진의 단순화 (250 줄 << 메인 엔진 767 줄, 책임 1/4 수준). 회귀 차단 가치가 비용 우위.

### Update 트리거 (재검토 조건)

다음 조건이 발생하면 본 결정 재검토 — `LayeredOverlayBase` ↔ `LayeredCursorBase` 통합 후보:

1. 메인 인디 알파 race 영역 (`OverlayAnimator._phase` / `_currentAlpha` 와 `LayeredOverlayBase._lastStyle` 의 상호작용) 이 완전 해소되어 메인 엔진 표면에 변경 위험이 사라짐
2. 양 엔진 모두에 동일한 회귀 (예: Win32 API 변경, DPI 정책 변경, DIB 생성 실패 처리 변경) 가 반복 3회 이상 발생해 중복 유지 비용이 회귀 차단 가치를 초과
3. cursor 엔진에 폰트 / 드래그 / 라벨 등 책임이 추가되어 책임 표면 차이 우위가 사라짐

## cursor-tray 브랜치 학습 결과

cursor-tray 브랜치 (19 commits, 실험적 prototyping) 의 결과를 본 PR-B 에서 무엇 채택 / 무엇 거부했는지:

- **채택**:
  - **WS_EX_TRANSPARENT 영구 ON** — cursor 인디 윈도우는 사용자 드래그/hit-test 가 필요 없으므로 클릭 통과를 OS 차원에서 보장하는 가장 단순한 방식. [dev-notes/2026-05-15-click-through-attempts.md](2026-05-15-click-through-attempts.md) F2 패턴을 메인 인디에는 못 썼지만 cursor 인디에는 자연 적용
  - **별도 HWND** — 메인 `_hwndOverlay` 와 분리. 같은 윈도우 클래스로 두 인스턴스 생성 가능 (WNDCLASSEXW 1회 등록 + CreateWindowExW 2회 호출)
  - **트레이 메뉴 체크박스 토글** — 라벨 자체가 기능명 (`I18n.MenuCursorIndicator = "커서 인디케이터" / "Cursor indicator"`), MF_CHECKED = ON. "고급 → 보조 인디" 같은 서브메뉴 계층 없이 우클릭 메뉴 한 클릭 노출 — 발견 가능성 우위
- **거부**:
  - **`WH_MOUSE_LL` 글로벌 마우스 후킹** — NativeAOT 콜백 risk + 300ms OS timeout 위반 시 silent 비활성화 + 다중 모니터 좌표 정규화 부담. `WM_TIMER` 50ms 폴링이 cursor 정지 검출에 충분하고 항상 표시 모드도 16ms 폴링으로 사용자 인지 가능 부드러움 달성
  - **`WM_INPUT` (raw input)** — 마우스 device handle 등록 + 메시지 분기 + 좌표 변환 비용이 정지 검출 정도의 요구에 과대. 폴링 모델로 충분
  - **설정 다이얼로그 신규 섹션** — cursor 인디는 디폴트 OFF 라 다이얼로그 노출 시 일반 사용자 대상 노이즈. config.json 직접 편집으로 가이드 (config-reference.md). 활성화 사용자 수 충분히 누적되면 다이얼로그 추가 재검토
- **부분 채택**:
  - **항상 표시 모드** — cursor-tray 의 "항상 표시 + fade" 디자인에서 fade 부분 제거. 정지 검출 모드와 항상 표시 모드 양분, fade 는 alpha race 와 상호작용 위험 회피
  - **CAPS 정책** — cursor-tray 의 "CAPS 별도 색상" 에서 "한글/비한글 같은 카테고리, 영문만 반대편" 으로 단순화 (사용자 인터뷰). 색상 3개 (Hangul/English/NonKorean) 중 2개 만 cursor 인디에 노출, 다이얼로그 단순화

## PR-B-3 트레이 메뉴 정책 인터뷰 결정

PR-B-3 진입 직전 사용자와 트레이 메뉴 UI 결정:

- **위치**: `IDM_USER_HIDDEN` ("인디케이터 숨김") 바로 아래. 기존 사용자 가시 인디 토글 라인과 함께 묶어 "인디 ON/OFF" 의미 그룹화
- **라벨**: "커서 인디케이터" — 기능명을 라벨로 직접. 별도 ▸ 화살표 + 서브메뉴 계층 없이 한 클릭 토글. 기능 1개 = 메뉴 항목 1개
- **체크 의미**: MF_CHECKED = ON (활성) — `user_hidden` 의 "체크 = 숨김" 반대 의미와 혼동 가능하나, cursor 인디는 "기능 자체 ON/OFF" 라 일반 토글 의미 (체크 = 켜짐) 가 자연. 사용자도 이 의미 통일에 동의
- **부수 메뉴 미추가**: 반지름/두께/색상은 config.json 편집 가이드. 디폴트 OFF + 디폴트 사용자가 켜는 첫 순간에 동심원이 즉시 보이므로 GUI 튜닝 미노출 결정

## PR-C: Settings GUI (2026-05-27, PR-B 머지 직후)

PR-B-3 진입 직전 인터뷰 결정 "부수 메뉴 미추가 — config.json 편집 가이드" 의 명시적 **재검토 트리거** 가 cursor-tray 학습 결과 "거부 — 설정 다이얼로그 신규 섹션 / 활성화 사용자 수 충분히 누적되면 다이얼로그 추가 재검토" 였음. PR-B 가 사용자 검증 통과 + 머지 (`6f95038`) 직후 본 트리거가 충족되어 PR-C 로 GUI 노출 결정.

### 무엇

`App/UI/Dialogs/SettingsDialog.Fields.cs` 의 `BuildRowDefs` 에 12번째 섹션 **"커서 인디케이터"** 추가 (기존 12번 "고급" 은 13번으로 이동). 10 필드:
- Bool ×2: `CursorIndicatorEnabled` / `CursorAlwaysShow`
- Int ×7: `CursorOuterRadius` / `CursorMiddleRadius` / `CursorInnerRadius` / `CursorCoreThickness` / `CursorHaloThickness` / `CursorIdleDelayMs` / `CursorMotionThresholdPx`
- Dbl ×1: `CursorHaloOpacity`

docstring 갱신: `BuildRowDefs(12 섹션)` → `BuildRowDefs(13 섹션)`, "60개 설정 필드" → "70개" (`SettingsDialog.cs`).

### 왜 (위치 결정 — 고급 앞 12번째)

대안 비교:
- **(a) 위치 13번 (고급 뒤, 끝)**: 거부 — "고급" 은 마지막에 두는 관례 (TOPMOST 강제 주기 등 일반 사용자 미접근 항목) 와 충돌
- **(b) 위치 7번 (색상 뒤, IME 색상 그룹과 인접)**: 거부 — cursor 인디는 IME 색상을 재사용할 뿐 별개 인디라 그룹화하면 의미 혼동 ("커서 표시 토글 vs IME 색상 변경" 이 한 묶음으로 보임)
- **(c) 위치 12번 (고급 직전)**: **채택** — 메인 인디 관련 항목 (1~11) 다 본 다음 보조 인디 → 다음 "고급". 사용자 학습 흐름 자연 ("주 기능 → 부가 기능 → 고급")

### 왜 (디자인 — 단일 진실원 + 새 컨트롤 0)

**Min/Max 는 `DefaultConfig.MinCursor*` / `MaxCursor*` const 직접 참조**:
```csharp
Add(Int("외부 원 반지름 (px)", "Outer radius (px)",
    DefaultConfig.MinCursorOuterRadius, DefaultConfig.MaxCursorOuterRadius,
    c => c.CursorOuterRadius, (c, v) => c with { CursorOuterRadius = v }));
```
PR-B-2 의 `Settings.ValidateAndRepair` 가 같은 const 로 클램프하므로 GUI 범위와 자동 일치. config-reference.md 표의 "8 ~ 96" 같은 수치를 GUI 에 수동 복제하지 않음 — 한 곳 수정 시 양쪽 동시 갱신.

**새 컨트롤/팩토리/I18n 테이블 변경 0**:
- 기존 6 팩토리 (`Bool`/`Int`/`Dbl`/`Str`/`ColorField`/`Combo`) 그대로 사용
- 라벨은 인라인 `("한국어", "English")` 튜플 패턴 (다른 12 섹션과 동일)
- I18n.cs 의 키-값 테이블에 항목 추가 없음 — 다이얼로그 라벨은 코드 사이트에서 직접 (디자인 결정)

이는 PR-B 의 "콜백 시그니처 본질적 차이" P4 예외와 같은 정신: cursor 인디의 GUI 도 메인 인디의 GUI 컨트롤 인프라를 그대로 사용 가능했고 (인디 종류와 무관한 Bool/Int/Dbl/표 레이아웃 책임), 새 인프라 추가 정당화가 안 됨.

### 부수 영향

- 다이얼로그 스크롤 영역 +10 행 (~250 px 추가) — `SettingsDialog.Scroll.cs` 변경 0 (높이 동적 계산 기반)
- 빌드 영향: 코드 +39 라인 (Fields.cs), 새 파일 0
- 디폴트 동작 변경 0 — `CursorIndicatorEnabled = false` 디폴트 유지, 다이얼로그 노출만 추가

### 후속 트리거 (다음 재검토 조건)

다음 조건 시 cursor 인디 노출 추가 고려:
1. 사용자가 색상 (Inner/Middle/Outer) 도 GUI 편집 요청 — 현재 IME 색상 자동 따라가는 정책이라 별도 색상 노출 안 함
2. 트레이 메뉴 "커서 인디케이터 숨김" 옆에 빠른 토글 (예: "항상 표시 모드") 직접 노출 요청 — 다이얼로그 진입 없이 한 클릭 변경

## 콘솔 회귀 사전 진단 (post-PR-C, 2026-05-27)

PR-C (#3) 머지 대기 중 explorer 위임으로 [App/Detector/ImeStatus.cs](../../App/Detector/ImeStatus.cs) + [Program.cs](../../Program.cs) 의 detection 경로 코드 read. 사용자 실측 전 코드만으로 확인 가능한 사실 + 가설 정리 — 다음 진단 사이클 출발점 명확화.

### 사실 정정 — 본 dev-note line 33 의 초기 분석 부정확

초기 분석 "`WM_INPUTLANGCHANGE` / `ImmGetConversionStatus` 가 콘솔 윈도우에 도달 안 함" 은 코드와 일치하지 않음:

- **`WM_INPUTLANGCHANGE`**: KoEnVue 코드 전체에서 수신/관찰 0건. 즉 콘솔이 이 메시지 보내는지 자체가 무관.
- **`ImmGetConversionStatus`**: `App/Detector/ImeStatus.cs:TryTier2` 에서만 호출. `DetectionMethod.ImeContext` 선택 시 또는 `Auto` 의 2단계 fallback 에서만 실행.
- 즉 콘솔 IME 가 진짜 limitation 인지는 코드만으론 확정 불가. **사용자 실측 진단 필요**.

### 감지 경로 정리 (코드 사실)

두 경로 병행:
1. **폴링** — [Program.cs:1109 DetectionLoop](../../Program.cs#L1109) 가 `PollIntervalMs` (80ms 디폴트) 주기로 `ImeStatus.Detect(hwndFocus, threadId, DetectionMethod)` 호출. 변화 시 `PostMessage(WM_IME_STATE_CHANGED)`.
2. **WinEvent 훅** — [App/Detector/ImeStatus.cs:76 RegisterHook](../../App/Detector/ImeStatus.cs#L76) 가 `EVENT_OBJECT_IME_CHANGE` (0x8029, `WINEVENT_OUTOFCONTEXT`) 등록. `OnImeChange` 콜백이 `GetForegroundWindow` → `Detect` → 변화 시 같은 메시지 post.

`Detect()` 의 `Auto` 분기 = 3-tier fallback:
- **Tier 1**: `ImmGetDefaultIMEWnd` + `SendMessageTimeoutW(WM_IME_CONTROL, IMC_GETOPENSTATUS / IMC_GETCONVERSIONMODE)` + `IME_CMODE_HANGUL` 비트
- **Tier 2**: `ImmGetContext` + `ImmGetConversionStatus`
- **Tier 3**: `GetKeyboardLayout(threadId)` + `HKL_IME_DEVICE_SIG` (0xE) 가드 + `LANGID_KOREAN` (0x0412)

콘솔 호스트 분기는 1군데만: [Program.cs:1242 ResolveFocusWindow](../../Program.cs#L1242) 가 `GUITHREADINFO.hwndFocus == 0` 인 경우 `fgClass == ConsoleWindowClass` 일 때만 `hwndFocus = hwndForeground` 폴백. Cascadia / Windows Terminal (`CASCADIA_HOSTING_WINDOW_CLASS`) 별도 분기 0.

### 가장 그럴듯한 회귀 가설

**`TryTier3` 의 `HKL_IME_DEVICE_SIG` (0xE) 가드 (v0.9.1.9 추가)**:
- conhost 의 IME 미장착 스레드에서 `GetKeyboardLayout(threadId)` 이 IME HKL 시그니처 (0xE) 가 아니라 일반 keyboard layout 만 리턴
- 가드가 `English` 폴백 → `state.LastImeState` 변화 없음 → `WM_IME_STATE_CHANGED` 미발화
- 즉 한국어 IME ON 상태에서도 콘솔 포커스면 영문 표시 + 한/영 토글 무반응

가설 검증을 위한 핵심 미지수 (코드만으론 모름, 사용자 실측 필요):
1. conhost 의 `GetKeyboardLayout` 이 한국어 IME 활성화 시 어떤 HKL 리턴? (시그니처 0xE 가지나 안 가지나)
2. `EVENT_OBJECT_IME_CHANGE` WinEvent 가 conhost 한/영 키에 발화하는가?

### `fac0251` 회귀 시점 가설 신뢰도 하락

본 dev-note line 80-83 의 진단 방법은 "PR-A 이전 commit (`fac0251`) 빌드로 콘솔 한/영 검증" — 그러나 코드 read 결과:

- `fac0251~3..fac0251` 모두 docs/harness 만 (.cs 변경 0)
- `ee4e6be..HEAD` 의 .cs 변경도 cursor 인디 신규/fix 만, IME 감지 로직 변경 0
- **PR-A `SnapToTargetAlpha` 자체는 alpha 조작만** — IME 감지 결과를 인디케이터에 반영하는 경로와 무관

→ **"PR-A 가 회귀 원인" 가설 신뢰도 낮음**. 더 이전 (HKL_IME_DEVICE_SIG 가드 추가 시점, v0.9.1.9) 가능성.

### 다음 진단 단계 (사용자 실측 필요)

1. [App/Detector/ImeStatus.cs:201 TryTier3](../../App/Detector/ImeStatus.cs#L201) 에 HKL 값 + `HKL_IME_DEVICE_SIG` 비교 결과 + `LANGID_KOREAN` 매치 결과 Debug 로깅 임시 추가
2. config 의 `log_level: "debug"` 확인 + `dotnet publish` 재빌드
3. 사용자에게: **콘솔 (cmd 또는 powershell)** 실행 + 한국어 IME 활성화 + 한/영 키 5회 토글 + 캡스락 5회 토글 (대조군)
4. `publish/koenvue.log` 직접 read (메모리 정책 — 사용자에게 요청 X) → trigger 식별:
   - `Detect` 호출 빈도 + HKL 변화 여부
   - 가드 통과 / 미통과 분기
   - `WM_IME_STATE_CHANGED` post 빈도
5. 결과 분기:
   - **HKL 변화 + 가드 미통과** → 가드 우회 fix (Tier 3 가드 완화 또는 Tier 1/2 와 다른 폴백 추가)
   - **HKL 변화 자체 0** → 콘솔 IME 모델 limitation (회귀 아님, dev-note 결론 갱신 + 사용자 안내)
   - **변화 감지 + 메시지 미발화** → 메시지 라우팅 별도 진단

### 진행 상태 (2026-05-27)

- **Step 1 진입**: [App/Detector/ImeStatus.cs:201 TryTier3](../../App/Detector/ImeStatus.cs#L201) 에 Debug 로깅 2줄 임시 추가 (`// [TEMP DIAG: 콘솔 한/영 회귀 — 머지 전 제거]` 마킹). Step 2 (`log_level: debug` + publish) 진행 중. 사용자 실측 (Step 3) → 로그 read (Step 4) → 결과 분기 (Step 5) 대기.

- **1차 진단 결과 (TryTier3)**: cmd 에서 `GetKeyboardLayout` 가 HKL=0 (NULL) 반환 — 가설 정정 (가드 미통과 아니라 HKL 자체 NULL). WindowsTerminal/메모장은 Tier1/2 성공으로 정상. 사용자 증언 "이전엔 cmd 에서도 한/영 정상 동작" + git log "v0.9.1.9 이후 ImeStatus 본질 알고리즘 변화 없음" → Windows 환경 변화 가능성. **2차 사이클 진입**: [App/Detector/ImeStatus.cs](../../App/Detector/ImeStatus.cs) `TryTier1` / `TryTier2` 각 분기에 Debug 로깅 추가 (동일 `// [TEMP DIAG]` 마킹) — cmd 에서 Tier1/2 가 어디서 NULL 반환하는지 확정 목적.

- **종결**: 아래 "확정 결론" 절로 본 진행 상태 종료. Step 5 대기 종결 — 임시 진단 코드는 `git checkout HEAD -- App/Detector/ImeStatus.cs` 로 원본 복원 + publish/config.json `log_level` INFO 복구. 코드 변경 0.

### 확정 결론 (2026-05-27)

1. **회귀 원인 확정**: v0.9.3.0 PR-03 의 `app.manifest` `requireAdministrator` → `asInvoker` 전환 (BREAKING). ImeStatus 알고리즘은 v0.9.2.8 과 동일 (`Win32Constants` → `ImeConstants` 단순 리네임만 — `git diff v0.9.2.8..HEAD -- App/Detector/ImeStatus.cs` 확인).

2. **메커니즘 — UIPI (User Interface Privilege Isolation)**: KoEnVue 가 Medium IL (asInvoker) 일 때 admin 권한 콘솔 (High IL) 의 IME 윈도우에 `WM_IME_CONTROL` 메시지 → UIPI 차단 → `SMTO_ABORTIFHUNG` 즉시 ABORT. 1차 진단의 "HKL=0" 결과는 Tier3 폴백이 NULL 반환 시점만 캡처, 진짜 차단 지점은 Tier1 의 `SendMessageTimeoutW` ABORT.

3. **검증 매트릭스 4 케이스**:
   - asInvoker KoEnVue + admin cmd: **차단** (현재 회귀)
   - admin KoEnVue + admin cmd: OK (v0.9.2.8 / 사용자 확인 OK)
   - admin KoEnVue + 일반 cmd: OK (High→Medium 메시지 OK)
   - asInvoker KoEnVue + 일반 cmd: OK 예상 (Medium↔Medium, 미실측이지만 메모장/WT 가 같은 IL 로 정상)

4. **회귀 아닌 의도된 BREAKING**: PR-03 의 매 부팅 UAC 프롬프트 제거 정책. admin 콘솔 사용은 일반적이지 않음 — 다만 admin 콘솔 자주 쓰는 사용자에게는 부작용.

5. **다음 세션 fix 설계 (통합)** — 사용자 제안 기반 단일 옵션, 모든 실행 경로 통합:

   **단일 config 키**: `admin_elevation: bool` (default false). 매니페스트는 `asInvoker` 유지 (PR-03 정책 보존). 옵션 비활성 사용자에겐 UAC 0.

   **메커니즘 분담** (두 경로 동시 커버):
   - **자체 elevation (self-check)**: KoEnVue 시작 시 config + 자기 IL 확인. `true + Medium` → `ShellExecute("runas")` 자기 자신 재실행 + 원본 종료. 단일 실행 / 직접 실행 경로 커버. UAC 1회 (사용자 인지 시점).
   - **schtasks `/RL HIGHEST`**: 부팅 자동 시작 사용자를 위해 추가. 등록 시점 UAC 1회 후 부팅마다 자동 admin (자체 elevation 우회 — 이미 admin IL 이라 self-check 미트리거).

   **UAC 빈도 매트릭스**:

   | 옵션 | 부팅 자동 시작 | 단일 실행 |
   |------|--------------|----------|
   | OFF (default) | UAC 0 | UAC 0 |
   | ON | UAC 0 (schtasks elevation) | UAC 1회 (자체 elevation, 사용자 인지 시점) |

   **Portable 영향**:
   - **자체 elevation 만 사용 (단일 실행 on-demand 패턴)**: config 만 동행 → portable 100%. schtasks 불필요.
   - **schtasks 추가 (부팅 자동 시작 패턴)**: schtasks 작업은 시스템 영역 — 새 PC 에서 재등록 (1회 UAC).

   **UI** — Settings 다이얼로그 또는 트레이 메뉴에 단일 체크박스 **"관리자 권한으로 실행"**:
   - 도움말 툴팁: *"자동 시작과 단일 실행 모두 적용. 단일 실행 시 UAC 프롬프트 1회, 자동 시작 시 UAC 없음."*

   **Planner 검토 항목**:
   - Fallback 정책 — UAC 거부 시 (a) 일반 권한 계속 / (b) 종료 / (c) 1회 알림 후 일반 진행
   - exe path 이동 감지 — schtasks 등록 path 와 자기 ImagePath 불일치 시 자동 갱신 제안
   - schtasks 등록 실패 graceful 처리 (Group Policy 차단 등)
   - 구현 분담 — `App/Bootstrap/AdminElevation.cs` (신규) + `App/Autostart/SchtasksHelper.cs` (기존 확장) + `App/Config/AppConfig.cs` + Settings/TrayMenu UI + `App/Program.cs` (Main 진입점 self-check 호출)

## 잠재 버그 fix — 외곽 잡티 (점 픽셀)

> **상태**: PR-B-1 의 회귀가 아닌 **처음 도입 시 잠재 버그**. PR-B 영역의 메인 인디 알파 race (선행 PR-A 가 부분 해소) 와 무관한 별도 이슈. cursor 인디 enable 상태로 살펴보면 동심원의 외곽 ~1px 거리에 옅은 점(또는 1px 가량의 흩어진 도트) 들이 보이는 현상.

### 증상

cursor 인디 동심원의 시각적 외곽 (코어 + 헤일로 영역 밖) 에서 1px 가량의 부산 픽셀이 산발적으로 또는 띄엄띄엄 보임. 동심원 자체는 정상 — 외곽 모서리에만 점.

### 원인

두 단계의 의미 불일치가 결합:

1. **셰이더 출력 threshold 부재** — [`App/UI/CursorRenderer.cs`](../../App/UI/CursorRenderer.cs) 의 `ShadeDib` 가 2x2 supersampling 후 `avgAlpha > 0.0` 픽셀이면 일단 출력. avgAlpha 가 양수 (예: 0.0008) 라도 `Math.Round(avgAlpha * 255)` 가 0 으로 떨어지는 외곽 sub-sample 1개만 살짝 들어오는 픽셀이 있음. 출력 시 alpha=0 + RGB!=0 으로 DIB 에 잔류 ("round-down 부산 픽셀").

2. **엔진 가드의 의미 불일치** — [`Core/Windowing/LayeredCursorBase.cs:277-289`](../../Core/Windowing/LayeredCursorBase.cs#L277) 의 `ApplyPremultipliedAlpha` 가 메인 [`LayeredOverlayBase.ApplyPremultipliedAlpha`](../../Core/Windowing/LayeredOverlayBase.cs) 의 `a == 0 && RGB != 0 → a = 255` 가드를 그대로 복사. 그러나 두 엔진의 셰이더 의미가 정반대:
   - 메인 엔진: `DrawTextW` AA 가 alpha=0 RGB!=0 픽셀을 정당한 글자 엣지로 출력 → 알파 복구가 올바름
   - cursor 엔진: alpha 를 명시적으로 셰이더가 결정 → alpha=0 RGB!=0 픽셀은 셰이더 round-down 부산물뿐 → 알파 복구하면 부산 픽셀이 fully-opaque 점으로 변환되어 잡티가 됨

v0.9.2.8 → 현재 cursor 인디 (`2528ce2` 이후) 의 본 가드가 메인 가드로부터 의미 검토 없이 복사된 것이 문제. PR-B 의 P4 예외 ("별도 엔진 신규 작성") 선택 시 두 엔진 사이의 동명 함수가 의미 검토 없이 같은 구현이 된 한 사례.

### Fix

1. **셰이더에 MinVisibleAlpha threshold** (`App/UI/CursorRenderer.cs`):
   - 상수 `MinVisibleAlpha = 1.0 / 255.0` 추가 (L31)
   - `ShadeDib` 픽셀 출력 분기를 `avgAlpha > 0.0` → `avgAlpha >= MinVisibleAlpha` 로 강화 (L132~140) — round-down 부산 픽셀을 출력에서 제외

2. **엔진 가드 의미 정정** (`Core/Windowing/LayeredCursorBase.cs:277-289`):
   - `a == 0 && (r | g | b) != 0` 분기에서 `a = 255` 부풀림 → `RGB = 0` 으로 정리
   - 주석에 메인 동명 가드와의 의미 차이 명시 (GDI AA 엣지 보존 vs cursor 셰이더 round-down 부산 픽셀 정리)

두 가드는 중복 방어 — 셰이더 가드가 1차 차단 (출력 자체 제외), 엔진 가드가 셰이더 외 경로에서도 안전망 (출력되더라도 정리).

### 학습

P4 예외로 별도 엔진 신규 작성 시 메인 엔진의 동명 함수를 의미 검토 없이 복사하면 정반대 의미를 가진 동일 코드가 잔류할 수 있다. PR-B 영역에서는 셰이더 패턴이 정반대 (GDI 그리기 vs 픽셀 셰이딩) 라 동일 코드가 정반대 결과를 만들었음. 향후 cursor 엔진과 메인 엔진의 형제 함수에 추가 변경 시 의미 차이를 다시 점검할 것.

## topmost 유실 후속 fix (주기 재적용) (2026-06-01)

> **상태**: 위 "사후 정리" 의 z-order fix (`3d9e0bd`) 가 **첫 표시 시 1회** `SetWindowPos(HWND_TOPMOST...)` 만 수행하던 한계의 후속 fix. 본 dev-note 가 **가설 CC 의 단일 진실원**이므로, 주기 재적용에서도 가설 CC 회귀가 차단되는 메커니즘을 여기 박제.

### 증상

사용자 보고 — "잘 동작하다가 갑자기 커서 인디가 안 보임". 항상 표시 모드 (`cursor_always_show: true`) 에서 cursor 윈도우 (`_hwndCursorOverlay`) 의 `HWND_TOPMOST` z-order 가 첫 표시 후 영구히 재적용되지 않음. 다른 topmost 창 (풀스크린 게임 / 알림 토스트 / UAC 프롬프트 등) 이 z-order 상 위로 올라오면 cursor 인디가 그 아래로 깔린 채 **복구 불가** — 메인 인디의 `TopmostWatchdog` (PR-08 C5, `config.Advanced.ForceTopmostIntervalMs` 5초 주기) 같은 재적용 인프라가 cursor 엔진에는 없었기 때문.

### 원인

"사후 정리" 의 z-order fix 가 두 부분으로 구성됐는데:
1. `Program.Bootstrap.CreateCursorOverlayWindow` 에서 `WS_EX_TOPMOST` 제거 (생성 시 일반 z-order)
2. `CursorOverlay.RenderAtCursor` 첫 가시화 시 `SetWindowPos(HWND_TOPMOST, SWP_NOSENDCHANGING ...)` 명시 1회

(2) 가 **첫 표시 1회** 만이라, 그 이후 다른 창이 topmost 진입하면 cursor 가 그 아래로 영구히 밀림. 메인 인디는 `Topmost` 타이머 트랙으로 5초마다 재적용하지만 cursor 엔진은 별도 엔진 (`LayeredCursorBase`, 기존 P4 예외 영역) 이라 그 5-트랙 애니메이션 인프라 자체가 없음.

### fix — 옵션 A (모션 타이머 재사용 + TickCount64 게이트)

- [`App/Config/DefaultConfig.cs`](../../App/Config/DefaultConfig.cs) — `CursorForceTopmostIntervalMs = 5000` **내부 const** 추가. AppConfig 키가 **아님** (config.json 오버라이드 불가). 메인 인디 `ForceTopmostIntervalMs` (5000) 와 같은 기본값이나 의미 분리 — 커서/메인 주기를 독립 조정 가능. 0 이면 주기 재적용 비활성 (첫 표시 set 만 유지 = fix 전 동작 = 회귀 시 즉시 무력화 가능한 escape hatch).
- [`App/UI/CursorOverlay.cs`](../../App/UI/CursorOverlay.cs):
  - `_lastTopmostTick` (long, `Environment.TickCount64` ms) 필드 신규 — 주기 재적용 게이트 기준점. `Initialize` / `Dispose` 에서 0 리셋.
  - `ApplyTopmost()` 헬퍼 — `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING)`. **첫 표시 + 주기 재적용의 단일 코드 경로** (SWP 플래그 세트가 한 곳에만 존재 → 가설 CC 차단 플래그 누락 불가).
  - `MaybeReassertTopmost()` 헬퍼 — `now - _lastTopmostTick < interval` 이면 early return, 경과 시에만 `ApplyTopmost()` + `_lastTopmostTick = now`. 매 tick 호출되나 실제 `SetWindowPos` 는 5초당 1회.
  - `RenderAtCursor` 첫 표시 분기 → `ApplyTopmost()` + `_lastTopmostTick = TickCount64` (주기 카운터 첫 기준점).
  - `HandleCursorMotionTimer` 의 **항상 표시 모드 + 정지 검출 모드 (가시 상태)** 양쪽 분기에서 `MaybeReassertTopmost()` 호출 — 두 모드 모두 보강 (정지 검출 모드도 가시 상태로 정지 중 다른 창에 가려질 수 있음).

### 가설 CC 회귀 차단 (단일 진실원)

위 "진단 3차 결과" 의 **가설 CC** — cursor 의 topmost 진입이 DWM 합성에서 다른 topmost z-band (`Shell_TrayWnd` 도 topmost) 재정렬 trigger → `Shell_TrayWnd` 가 약 1초 후 foreground 변경 → detection thread `SystemFilter` 매칭 → 메인 인디 hide. 주기 재적용은 이 trigger 를 **매 5초 반복할 위험**이 있으므로 두 겹 방어:

1. **동일 `SWP_NOSENDCHANGING` 플래그 세트** — `ApplyTopmost()` 가 첫 표시와 똑같은 플래그로 `SetWindowPos` 호출. `SWP_NOSENDCHANGING` 이 다른 윈도우에 `WM_WINDOWPOSCHANGING` 알림을 차단하므로 `Shell_TrayWnd` 재정렬 trigger 자체가 발생하지 않음 (가설 CC 의 근본 차단 메커니즘 = 첫 표시와 동일).
2. **5초 빈도 제어** — 설령 잔존 trigger 가 있더라도 5초당 1회로 제한 (`CursorForceTopmostIntervalMs`). 메인 인디 `TopmostWatchdog` 와 같은 빈도 정신 ("매 프레임 topmost 호출 지양").
3. **생성 시 `WS_EX_TOPMOST` 재도입 안 함** — `Program.Bootstrap.CreateCursorOverlayWindow` 무변경. 부팅 sequence 동안 일반 z-order 시작은 그대로 유지 (가설 CC 의 부팅 시점 회귀 차단 = "사후 정리" 결정 보존).

### 옵션 A vs 옵션 B (P4 예외 정당화)

| | 옵션 A (채택) | 옵션 B (거부) |
|---|---|---|
| 방식 | 기존 `WM_TIMER` 모션 폴링 핫 경로에 `TickCount64` 게이트 1줄 | cursor 전용 `TopmostWatchdog` 인스턴스 신규 |
| 타이머 | 추가 0 (모션 타이머 재사용) | 새 `SetTimer` 트랙 추가 |
| SWP 플래그 | cursor 전용 (`SWP_NOSENDCHANGING` 포함 — 가설 CC) | `TopmostWatchdog` 는 메인 인디용 플래그 세트 → cursor 와 다름 |

**옵션 B 거부 사유** (메인 `TopmostWatchdog` 미재사용 = P4 예외):
- cursor 인디는 별도 엔진 (`LayeredCursorBase`) 으로 `OverlayAnimator` 의 5-트랙 인프라 (`TopmostWatchdog` 포함) 가 없음 — 이미 본 dev-note "왜 별도 엔진" 의 P4 예외 영역.
- `TopmostWatchdog` 의 `SetWindowPos` 플래그가 cursor 와 다름 — cursor 만 `SWP_NOSENDCHANGING` 이 필수 (가설 CC). `TopmostWatchdog` 에 cursor 전용 플래그 분기를 추가하면 메인 인디 핫 경로에 변경면 추가 (= 본 dev-note "왜 별도 엔진" 1차 근거 위배).
- 모션 폴링이 이미 `WM_TIMER` 핫 경로 (16ms/50ms) 라 기존 tick 에 게이트 1줄이 새 타이머보다 합리적 (steady-state wakeup 추가 0).

### Update 트리거 (재검토 조건)

본 dev-note "왜 별도 엔진 → Update 트리거" 의 조건 2 ("양 엔진 모두에 동일 회귀 3회 이상") 에 topmost 재적용이 카운트 1 추가. 향후 메인/cursor 양쪽에 동일한 topmost 관련 회귀가 누적되면 `TopmostWatchdog` 에 cursor 변형 (플래그 파라미터화) 통합 후보로 재검토.

## 관련

- 메인 인디 알파 race fix (선행 PR-A): [dev-notes/2026-05-27-snap-fade-killtimer.md](2026-05-27-snap-fade-killtimer.md)
- 메인 인디 엔진: [Core/Windowing/LayeredOverlayBase.cs](../../Core/Windowing/LayeredOverlayBase.cs)
- 본 PR-B-1 의 파일: `Core/Windowing/{CursorStyle,LayeredCursorBase}.cs` + `App/UI/CursorRenderer.cs`
- PR-C 파일: `App/UI/Dialogs/SettingsDialog.Fields.cs` (+39 라인) + `App/UI/Dialogs/SettingsDialog.cs` (docstring 1 라인)
