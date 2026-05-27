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

## 관련

- 메인 인디 알파 race fix (선행 PR-A): [dev-notes/2026-05-27-snap-fade-killtimer.md](2026-05-27-snap-fade-killtimer.md)
- 메인 인디 엔진: [Core/Windowing/LayeredOverlayBase.cs](../../Core/Windowing/LayeredOverlayBase.cs)
- 본 PR-B-1 의 파일: `Core/Windowing/{CursorStyle,LayeredCursorBase}.cs` + `App/UI/CursorRenderer.cs`
