# 인디케이터 클릭 통과 — 시도 기록 (2026-05-15)

> **결과**: 모든 시도 실패. v0.9.2.8 로 롤백. 본 문서는 다음에 같은 함정을 피하기 위한 기록.

## 목표

- 인디케이터 위 마우스 클릭(좌/우/휠/더블/NC 버튼/X1/X2)이 아래 창으로 자연 통과
- 인디 위에서 마우스 드래그(SM_CXDRAG 임계 이상 이동)는 인디 이동
- `drag_modifier` 는 드래그 개시 게이트로 의미 재정의 (통과는 항상)
- WinUI 3 (메모장 새 탭 추가 버튼 등) 포함 호환

## 핵심 결론 (TL;DR)

**클릭 통과를 사용자 레벨에서 합성하는 어떤 방식도 만족스럽게 동작하지 않음.** `WS_EX_TRANSPARENT` 만이 OS 라우팅을 100% 신뢰 가능. 그러나 `WS_EX_TRANSPARENT` 와 "인디 위에서 드래그 시작" 은 본질적으로 충돌 (드래그 개시 검출이 OS 입력 큐 검사에 의존하면 OS 가 인디를 건너뛰어 좌버튼 상태를 인디 위치 기준으로 못 잡음).

가장 가까웠던 방식조차 다음 둘 중 하나에서 막혔다:
1. **NC 버튼 (최소화/최대화/닫기) / WinUI 3 컨트롤** — `PostMessage(WM_NCLBUTTONDOWN, HTMINBUTTON, ...)` 합성이 DefWindowProc 의 NC 모달 루프가 시스템 입력 큐를 직접 watch 하므로 인식 안 됨 (정확히는 `WM_SYSCOMMAND(SC_MINIMIZE)` 직접 보내면 동작하지만 Cursor IDE 처럼 OS 가 즉시 처리 안 하고 다른 입력 후 처리되는 케이스 존재. WinUI 3 의 컴포지션 트리는 합성 WM_LBUTTONDOWN 자체를 무시).
2. **드래그 중 아래 창에도 동작 누설** — 인디가 캡처를 잡지 않는 (또는 WS_EX_TRANSPARENT ON 상태) 경우 OS hit-test 가 아래 창에도 LBUTTONDOWN 을 전달.

---

## 시도한 5 가지 접근과 실패 모드

### 1) PostMessage + WS_EX_TRANSPARENT 동적 토글
**모델**: 평상시 ON (통과). DragPoller 가 WM_TIMER 30ms 로 `GetAsyncKeyState(VK_LBUTTON)` watch. down edge 시 anchor 저장, 4px 초과 시 `Disable` → `PostMessage(WM_NCLBUTTONDOWN, HTCAPTION, current_cursor)`.

**실패**:
- `mouse_event(MOUSEEVENTF_LEFTUP)` 으로 아래 창 mouseDown 잔재 정리하려 했더니 시스템 입력 큐의 LBUTTON 상태가 false 로 corrupting → `GetAsyncKeyState(VK_LBUTTON)` 가 사용자가 누른 상태에서도 false 반환 → sizemove 모달이 즉시 종료 → "드래그가 4px 후 멈춤".
- LEFTUP 합성 제거하면 → sizemove 의 SC_MOVE 핸들러가 시스템 입력 큐 LBUTTON 검사 → 사용자의 진짜 LBUTTONDOWN 이 아래 창으로 라우팅된 상태라 큐에 LBUTTON down 없음 → Alt+Space "이동" **키보드 모드** 폴백 (사용자가 LBUTTON 떼고 마우스 움직이면 인디 따라옴, 한 번 더 클릭해야 종료).

**OS-level 발견**: `WS_EX_TRANSPARENT` 가 ON 인 동안은 OS 가 인디로 향한 마우스 메시지를 큐에 큐잉하지 않음. PostMessage 로 NC 메시지를 합성해도 OS 의 sizemove 모달이 사용하는 시스템 입력 큐와 별개 — 모달은 큐를 보지 PostMessage 결과를 안 봄.

### 2) SendMessage 동기 + WS_EX_TRANSPARENT 항상 ON
**모델**: ex-style 토글 없이 ON 유지. PostMessage 대신 `SendMessageTimeoutW`.

**실패**:
- 동일한 sizemove 모달 시스템 큐 의존 문제. 인디로 향한 LBUTTON 이 큐에 없으니 즉시 키보드 모드 폴백.

### 3) SetCapture + 이벤트 기반 (WS_EX_TRANSPARENT 항상 ON)
**모델**: `WS_EX_TRANSPARENT` 유지하면서 폴러가 down 감지 시 `SetCapture(_hwndOverlay)` 호출 → 이후 마우스 이벤트가 인디 WndProc 으로 라우팅되기를 기대.

**실패**:
- **`WS_EX_TRANSPARENT` 는 SetCapture 보다 강함**. OS hit-test 가 인디를 건너뛰면 캡처 보유자가 누구든 메시지가 인디로 안 옴. 인디가 전혀 안 움직임.

### 4) 순수 폴링 (WS_EX_TRANSPARENT 항상 ON, 메시지 사용 안 함)
**모델**: WM_TIMER 30ms 로 LBUTTON 상태 + 커서 위치 → 임계값 초과 시 `Overlay.BeginDrag` 직접 호출 → 매 사이클 `MoveWindow` 로 인디 이동.

**실패**:
- 드래그 자체는 동작했으나 **드래그 시작 시점 hot point 오차** (anchor=LBUTTONDOWN 좌표, BeginDrag 호출=임계 초과 시점 좌표 — 4px 차이) → 인디가 4px 점프.
- `Overlay.BeginDrag(snap, hotX, hotY)` 오버로드 추가로 해결했으나, 동시에 **드래그 중 아래 창에도 마우스 액션 누설** 발견 — `WS_EX_TRANSPARENT` 가 ON 인 동안 OS 가 LBUTTON down/move 를 아래 창으로 라우팅. 드래그 중에만 OFF 하려 했으나 시점 race 가 끝없음.

### 5) Indicator-as-gatekeeper (사용자 마지막 제안: WS_EX_TRANSPARENT 사용 안 함)
**모델**: 인디가 모든 마우스 메시지를 직접 받음. 짧은 클릭 / 휠 / 우클릭 / 더블클릭 / NC 버튼 클릭은 `WindowFromPoint(cursor)` + z-order 사이드스텝(`HWND_BOTTOM`) → `WindowFromPoint` 재실행 → 적절한 메시지 합성 → 인디 복귀.

**실패**:
- 좌클릭 / 휠 / 우클릭 / 더블/트리플 클릭 (CS_DBLCLKS 추가 후) **OK**.
- **NC 버튼 (최소화/최대화/닫기) 실패**: `PostMessage(WM_NCLBUTTONDOWN, HTMINBUTTON)` 합성을 DefWindowProc 의 NC 모달이 인식 안 함. → `WM_SYSCOMMAND(SC_MINIMIZE)` 로 우회 → 일부 앱 OK, **Cursor IDE 는 즉시 처리 안 하고 다른 입력 후 처리** (시스템 큐 동기화 race).
- **WinUI 3 새 탭 추가 버튼 실패**: WinUI 3 컴포지션 트리는 합성 PostMessage 자체를 무시. `SendInput` 필요 (시도 안 함).
- 첫 실행 시 모래시계 커서 (해결: `WM_SETCURSOR` 핸들러).
- 누적 복잡도 + 미해결 호환성 → 사용자 롤백 결정.

---

## OS 레벨 발견 (재현 가능)

| # | 발견 | 출처 |
|---|------|------|
| F1 | `mouse_event(LEFTUP)` 합성은 `GetAsyncKeyState(VK_LBUTTON)` 의 반환값을 corrupting | 4px 드래그 멈춤 증상. `_syntheticUpTimestamp` 필터로 우회 가능하나 sizemove 큐 검사는 못 우회 |
| F2 | `WS_EX_TRANSPARENT` 는 OS hit-test 단계에서 인디를 건너뛰므로 `SetCapture` 보다 강함 | SetCapture 후에도 인디가 마우스 이벤트 수신 못함 |
| F3 | `GetWindow(hwnd, GW_HWNDNEXT)` 는 z-order 그룹 내에서만 동작. 인디가 topmost 면 다음도 topmost 만 반환 | MSDN 명시. `GetTopWindow(NULL)` 또는 `WindowFromPoint` + `HWND_BOTTOM` 사이드스텝 필요 |
| F4 | `SetWindowPos(hwnd, HWND_NOTOPMOST, ...)` 은 인디를 non-topmost 그룹 **최상단**에 놓음 → 여전히 일반 창들 위 | `HWND_BOTTOM` 사용해야 진짜 바닥 |
| F5 | DefWindowProc 의 sizemove SC_MOVE 핸들러는 시스템 입력 큐의 LBUTTON 상태를 직접 polling. PostMessage / SendMessage 합성된 가짜 down 은 인식 안 함 | "키보드 모드" 폴백 증상의 근본 원인 |
| F6 | DefWindowProc 의 NC 버튼 모달 루프 (HTMINBUTTON 등) 도 동일하게 시스템 입력 큐 의존 | NC 버튼 PostMessage 합성 실패 |
| F7 | `WM_SYSCOMMAND(SC_MINIMIZE/MAXIMIZE/CLOSE)` 는 PostMessage 가 동작하지만 일부 앱(Cursor IDE) 은 다음 입력 이벤트까지 처리 지연 | 시스템 큐 동기화 race |
| F8 | 더블/트리플 클릭은 윈도우 클래스에 `CS_DBLCLKS` 가 박혀 있어야 OS 가 `WM_LBUTTONDBLCLK` 를 생성 | `Win32DialogHelper.RegisterStandardClass` 에 추가 |
| F9 | WinUI 3 컴포지션 컨트롤은 메시지 큐를 우회하므로 PostMessage 합성 무시. `SendInput` (가짜 하드웨어 입력) 만 인식 | 메모장 새 탭 추가 버튼, Win11 시작 메뉴 등 |
| F10 | 첫 윈도우 생성 시 OS 는 `IDC_APPSTARTING` 폴백 (커서 grace period). 명시적 `WM_SETCURSOR` 핸들러로 `IDC_ARROW` 강제 필요 | 모래시계 증상 |
| F11 | `WS_EX_TRANSPARENT` + `WS_EX_LAYERED` 동시 사용은 표준이며 DWM 합성 호환 | (검증됨, 부작용 없음) |
| F12 | UIPI: 관리자 권한 KoEnVue → 일반 권한 메모장 메시지 라우팅은 `requireAdministrator` manifest + UIPI 통과 메시지 화이트리스트 의존 | WM_LBUTTONDOWN/WM_SYSCOMMAND 모두 화이트리스트에 있어 OK |

---

## 미해결 항목

- **WinUI 3 컨트롤 호환** — `SendInput` 필요. 부작용: 입력 큐에 진짜 하드웨어 입력으로 들어가므로 z-order 사이드스텝 race 가 더 정교해야 함 (인디를 z-order 바닥으로 보낸 후 SendInput → WindowFromPoint 가 아래 창 반환하도록 — 짧은 SetTimer 후 복귀).
- **Cursor IDE NC 버튼 지연 처리** — `WM_SYSCOMMAND` 가 시스템 큐 동기화에 의존. `SendInput` 로 진짜 하드웨어 LBUTTONDOWN/UP 합성하면 해결 가능성 있음.
- **인디 위 셀렉션 드래그 vs 인디 드래그 충돌** — 트레이드오프. 사용자 의도 우선순위 결정 필요.

---

## 다음 시도 시 권장 접근

**우선순위 1 — SendInput 기반 (시도 안 한 마지막 방식)**
1. 인디 항상 모든 마우스 메시지 수신 (`WS_EX_TRANSPARENT` 사용 안 함)
2. 짧은 클릭 / 우클릭 / 휠 / 더블/트리플 / NC 버튼 모두:
   - 인디를 `SetWindowPos(HWND_BOTTOM)` 으로 z-order 바닥으로
   - 인디를 일시 `ShowWindow(SW_HIDE)` (또는 `WS_EX_TRANSPARENT` ON 만)
   - `SendInput` 으로 진짜 하드웨어 입력 합성 (현재 커서 위치에서)
   - 짧은 SetTimer (50ms?) 후 인디 복귀
3. 드래그는 4 번 방식(순수 폴링 + BeginDrag hotpoint overload) 그대로
4. `drag_modifier` 의미 재정의 적용

**리스크**:
- SendInput 의 진짜 하드웨어 입력은 다른 앱의 글로벌 훅 (`WH_MOUSE_LL`) 에도 잡힘 → 그룹웨어/스크린리코더 등이 인지
- z-order 사이드스텝 race 가 정교해야 함 (인디가 바닥으로 가기 전에 SendInput 가면 OS 가 다시 인디로 라우팅)
- 인디 깜빡임 가능 (SW_HIDE 사용 시) — `WS_EX_TRANSPARENT` ON 으로 대체 시 race 줄어듦

**우선순위 2 — WH_MOUSE_LL 로 입력 가로채기**
- 글로벌 저수준 마우스 훅으로 인디 영역 클릭을 가로채고 `SendInput` 으로 재합성
- 다른 앱에서 우리 훅이 보임 (보안/AV 회피 의심 받을 수 있음)
- DLL injection 없이 in-process 훅 가능 (`SetWindowsHookExW(WH_MOUSE_LL, ...)`)

**우선순위 3 — WM_POINTER API**
- Win8+ 의 통합 포인터 모델. Touch/Pen/Mouse 통합.
- 본 케이스에는 over-engineered. NativeAOT 호환성 확인 필요.

---

## 절대 다시 시도하지 말 것

| 방법 | 이유 |
|------|------|
| `mouse_event(LEFTUP)` 합성으로 mouseDown 잔재 정리 | F1: `GetAsyncKeyState` corrupting → 폴러 / sizemove 모두 깨짐 |
| `WS_EX_TRANSPARENT` 동적 토글로 "드래그 중에만 OFF" | F2: 토글 시점 race 끝없음. OS hit-test 가 frame-perfect 동기화 안 됨 |
| `PostMessage(WM_NCLBUTTONDOWN, HTCAPTION, anchor)` 로 sizemove 시작 | F5: sizemove SC_MOVE 가 시스템 입력 큐 검사 → 가짜 down 무시 → 키보드 모드 폴백 |
| `PostMessage(WM_NCLBUTTONDOWN, HTMINBUTTON, ...)` 로 NC 버튼 합성 | F6: NC 모달 루프가 시스템 입력 큐 의존 → 무시 |
| `HWND_NOTOPMOST` 로 z-order 사이드스텝 | F4: non-topmost 그룹 최상단에 놓일 뿐. `HWND_BOTTOM` 사용 |
| `GetWindow(GW_HWNDNEXT)` 로 아래 창 찾기 | F3: 같은 z-order 그룹 내에서만 동작 |
| `SetCapture` 후 `WS_EX_TRANSPARENT` ON 유지 | F2: hit-test 가 캡처보다 강함 |

---

## 시간 비용

- 약 12 시간 + 5 차례 아키텍처 피벗 + 다수 빌드/테스트 사이클
- 사용자 검증 부담 (각 빌드마다 EXE 종료/재실행, 다양한 앱에서 시나리오 테스트)
- 최종적으로 v0.9.2.8 롤백 결정 — 누적 복잡도 + 미해결 호환성 (WinUI 3, Cursor IDE)

## 관련 commit (롤백 직전 시점 — 모두 `git restore .` 으로 되돌림)

- 작업 결과는 디스크에 남기지 않았음. v0.9.2.8 (`95b693b release: v0.9.2.8`) 직후 상태와 동일.

---

## 참고 자료

- MSDN: [Window Features — Layered Windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows)
- MSDN: [`WS_EX_TRANSPARENT`](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)
- MSDN: [`GetWindow` — z-order traversal limitations](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindow)
- MSDN: [`SendInput` vs `mouse_event`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- MSDN: [`SetWindowsHookExW` (WH_MOUSE_LL)](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw)
- MSDN: [`CS_DBLCLKS`](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-class-styles)
