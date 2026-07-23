# KoEnVue — PRD

## 1. 개요

### 1.1 제품명
**KoEnVue** — Windows IME 한/영 상태 인디케이터

### 1.2 문제 정의
Windows 에서 텍스트를 입력할 때 현재 한글/영문 모드를 직관적으로 알기 어렵다. 기본 작업 표시줄의 IME 표시기는 화면 우측 하단에 위치해 타이핑 중 시선 이동이 크고, 게임·전체 화면 애플리케이션에서는 아예 보이지 않는다.

### 1.3 솔루션
**드래그 가능한 플로팅 오버레이**로 현재 입력 상태를 라벨로 띄워 주는 경량 유틸리티. 앱별로 위치를 기억하고, 포커스 전환 또는 한/영 전환 시 입력 모드를 즉시 확인할 수 있다. CAPS LOCK 상태도 라벨 가장자리 막대로 함께 보조 표시한다.

### 1.4 대상 사용자
- Windows 한국어 사용자
- 텍스트 입력이 많은 사용자 (개발자, 문서 작업자, 번역가)

### 1.5 핵심 설계 원칙

| 원칙 | 설명 |
|------|------|
| **외부 패키지 제로** | .NET 10 BCL + Windows API 만 사용. NativeAOT 단일 exe (~4.7 MB) |
| **한글 우선 표시** | UI 텍스트는 한글 기본, 영어 시스템에서는 영어 fallback. 로그/config 키는 영문 |
| **최소 기능 집합** | 필요한 기능만 구현. 인디케이터 스타일/도형 선택 없음 |
| **완전 포터블** | exe 한 개로 동작. 첫 실행 시 같은 폴더에 `config.json` 과 `koenvue.log` 자동 생성. 지울 때는 폴더 통째로 삭제 |

---

## 2. 인디케이터

### 2.1 표시 형태
- **텍스트 라벨**: "한" (한글), "En" (영문), "EN" (비한국어 IME)
- **도형**: RoundedRect 고정 (설정 불가)
- **색상**: `config.json` 으로 배경/전경 색상 지정 가능. 상태별 (한글/영문/비한국어) 로 따로 지정
- **CAPS LOCK 보조 표시**: CAPS LOCK 이 켜져 있으면 라벨 좌우 수직 가장자리에 현재 상태의 `fg` 색상과 동일한 얇은 세로 막대가 함께 그려져, 타이핑 중 시선 이동 없이 한/영 상태와 CAPS LOCK 상태를 동시에 확인할 수 있다. 꺼지면 막대도 자동으로 사라진다. 상태별 색상과 무관하게 동작하므로 한/영/비한국어 어느 모드에서도 유지된다

### 2.2 위치

**드래그 가능한 플로팅 윈도우** — 마우스로 끌어 원하는 위치에 배치한다.

- **위치 모드** (`config.position_mode`, 트레이 메뉴 "위치 모드" 서브메뉴로 전환)
  - **고정 위치** (`"fixed"`) — 화면 절대 좌표에 인디케이터를 고정. 앱별 위치를 `config.indicator_positions` (프로세스명 → `[x, y]`) 에 저장
  - **창 기준** (`"window"`, 기본) — 포그라운드 창의 DWM 시각 프레임 모서리 기준 상대 오프셋으로 배치. 앱별 위치를 `config.indicator_positions_relative` (프로세스명 → `[(int)Corner, DeltaX, DeltaY]`) 에 저장. 같은 프로세스의 창을 여러 개 열어도 각 창의 실제 위치에 따라 인디케이터가 서로 다른 절대 좌표에 배치됨
  - 모드와 무관하게 시스템 입력 프로세스(시작 메뉴, 검색 창)는 기존 방식(창 시각적 좌상단 바로 위) 유지

- **2단계 위치 기억 (고정 모드)**
  - **런타임**: hwnd 별 위치 (세션 내 창별 구분 — 여러 개의 메모장·크롬 창 각각 다른 위치 유지)
  - **영구**: 프로세스 이름별 위치 (`config.json → indicator_positions`). UWP 앱(설정, Microsoft Store 등)은 `ApplicationFrameHost.exe` 가 윈도우 프레임을 소유하므로 `EnumChildWindows` 로 자식 윈도우를 탐색해 실제 앱 프로세스 이름을 식별한다
  - 포그라운드 전환 시 조회 순서는 hwnd 런타임 → 프로세스명 config → 기본 위치

- **창 기준 위치 기억 (창 기준 모드)**
  - **영구만**: `config.indicator_positions_relative` 에 `Corner` anchor + delta 로 저장. 런타임 hwnd 캐시 없이 매 표시마다 창 rect + 상대 오프셋에서 절대 좌표를 재계산하므로 창이 움직여도 정확
  - 드래그 종료 시 현재 인디 위치에서 가장 가까운 창 모서리를 자동 선정해 상대 오프셋으로 환산
  - **창 이동 감지**: 감지 스레드가 80 ms 간격으로 DWM 프레임 변화를 추적. 이동 중에는 인디케이터를 숨기고, 이동이 멈추면(1 틱 안정화) 새 위치에서 재표시

- **화면 밖 위치 방어**: 표시용 절대좌표는 작업 영역 안으로 클램프된다 — (a) Fixed/Window **저장** 좌표의 읽기 반환, (b) Window **기본** 상대 resolve, (c) Window/Fixed 드래그 종료 직후 `Show`. 시스템 입력 프로세스의 `HandleOverlayDragEnd` early-return `Show` 는 의도적 비범위(저장 스킵·위치 일시적). 인디 중심점 기준 `MONITOR_DEFAULTTONEAREST` 로 모니터를 라우팅하므로 모니터 제거 후 해당 좌표는 잔존 모니터 중 가장 가까운 쪽으로 재매핑된다. Fixed 저장 값 자체는 덮어쓰지 않아서 원 모니터로 복귀 시 원 위치가 복원된다. Window 상대 오프셋은 창 프레임 기준이라 저장 시 클램프하지 않는다(표시만 클램프). 해상도/DPI 변경으로 화면 밖이 되는 경우도 같은 경로로 방어된다
- **기본 위치 모니터 선택**: `GetDefaultPosition`(Fixed default·시스템 입력)과 Window resolve 모두 `MonitorFromWindow(..., MONITOR_DEFAULTTONEAREST)` — 창이 어떤 모니터와도 겹치지 않을 때 가장 가까운 모니터의 work area를 쓴다

- **기본 위치 (저장 안 된 앱)**: 위치 모드에 따라 별도 설정. Delta 는 논리 픽셀(96 DPI 기준) 로 저장되어 적용 시 타겟 모니터의 DPI 스케일로 승산된다 — 서로 다른 DPI 모니터 간 이동 시 시각적 inset 이 보존된다
  - **고정 모드**: `config.default_indicator_position` — Corner anchor + delta(논리 px) 를 포그라운드 앱이 있는 모니터의 작업 영역에 적용. 미설정 시 작업 영역 정중앙으로 폴백
  - **창 기준 모드**: `config.default_indicator_position_relative` — Corner anchor + delta(논리 px) 를 포그라운드 창의 DWM 프레임에 적용. 미설정 시 창 우하단 (`BottomRight, -69, -58` 논리 px) 으로 폴백
  - 모서리 anchor + 논리 px 방식이라 멀티 모니터·해상도·DPI 변경에 안정적이다(절대 좌표가 아니므로 해상도가 달라져도 의도한 모서리 근처에 유지, DPI 가 달라도 모서리로부터의 시각적 거리가 유지)

- **Shift 드래그 축 잠금**: 드래그 중 Shift 키를 누르면 시작 좌표 기준 우세한 축(가로/세로) 한 쪽으로만 이동하도록 제한. Shift 는 드래그 도중 언제든 누르거나 떼도 즉시 반영되며, 유지한 채 반대 방향으로 충분히 끌면 축이 뒤집힌다. 멀티 모니터·DPI 경계 교차는 풀린 축에서 정상 동작한다

- **짧은 좌클릭 = 일시 숨김 / 드래그 = 위치 이동**
  - 인디 위 짧은 좌클릭 → 일시 숨김(`_clickDismissed`). 오버레이가 사라져 그 자리 클릭이 아래 창으로 통과. 포커스 변경 또는 한/영(IME) 변경 시 재표시. 트레이「메인 인디케이터 숨김」(`user_hidden`)과는 별개(영속·트레이 취소선 없음)
  - 마우스 이동이 시스템 드래그 임계(`SM_CXDRAG`/`SM_CYDRAG`)를 넘으면 `WM_NCLBUTTONDOWN`/`HTCAPTION` 네이티브 드래그로 승격. hit-test 는 `HTCLIENT` + 캡처로 클릭/드래그를 구분

- **드래그 활성 키 (드래그 승격 게이트)** (`config.drag_modifier`, 트레이 "드래그 활성 키" 서브메뉴로 전환)
  - **`"none"`** (기본) — 임계 초과 시 항상 드래그 승격. 짧은 클릭은 일시 숨김
  - **`"ctrl"` / `"alt"` / `"ctrl_alt"`** — 해당 키를 정확히 누른 채 임계 초과 시에만 드래그 승격. 아니면 짧은 클릭으로 일시 숨김. `Ctrl` 모드는 `Ctrl ∧ ¬Alt` 엄격 판정이라 `Ctrl+Alt` 조합에 우발 트리거되지 않음. 게이트는 캡처 중 `WM_MOUSEMOVE` 에서 `GetAsyncKeyState` 로 판정. 드래그가 시작되면 `WM_ENTERSIZEMOVE` 모달 루프가 마우스 캡처를 잡으므로 도중에 모디파이어를 놓아도 드래그는 끊기지 않음
  - 용례: 실수로 드래그 승격되는 것을 방지하고 의도적으로 모디파이어 + 드래그일 때만 이동시키고 싶을 때. `Shift` 는 드래그 중 축 고정에 선점되어 있어 선택지에서 제외

- **창 엣지 자석 스냅**: `config.snap_to_windows` (기본 켜짐, 트레이 메뉴 "창에 자석처럼 붙이기" 로 토글). 드래그 중 다른 최상위 창의 시각 프레임 엣지와 현재 모니터 작업 영역 엣지에 임계값(기본 10 px, DPI 스케일) 이내로 접근하면 자석처럼 스냅. `config.snap_gap_px` (기본 10 px, DPI 스케일)만큼 창 경계선과 간격을 두어 겹침을 방지하며, 화면(work area) 엣지에는 간격 없이 밀착. 드래그 시작 시 창 목록을 한 번 캐싱하며, 최소 크기 80 px 미만 창, 숨김·최소화 창, DWM cloaked 창(UWP 유령 창)은 후보에서 제외. 후보 rect 는 `DWMWA_EXTENDED_FRAME_BOUNDS` 로 얻은 시각 프레임을 사용해 비가시 리사이즈 테두리를 배제. Shift 축 잠금과 공존 — 잠긴 축은 스냅 대상에서 제외

- **시스템 입력 창 예외** (시작 메뉴, 작업 표시줄 검색): TOPMOST 창도 z-밴드 한계로 이들 위에 뜰 수 없어, 드래그로 가려지면 복구 불가. 드래그해도 위치를 저장하지 않으며, 기본 위치를 창의 시각적 왼쪽 위 모서리 바로 위(`frame.Left`, `frame.Top - labelH - 4 px`)로 고정해 항상 보이도록 한다. 시각 프레임은 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` 로 얻어 `GetWindowRect` 의 비가시 리사이즈 테두리를 배제한다. 단, CoreWindow 호스트(`StartMenuExperienceHost` 등)는 DWM extended frame bounds 가 화면 전체를 덮어 시각적 패널 위치를 반영하지 않으므로, 프레임이 작업 영역 전체를 포함하면 직전에 캐시된 유효 프레임(통상 `SearchHost` 의 패널 rect)을 재사용해 동일 위치에 인디케이터를 배치한다. 캐시가 없는 경우에만 일반 기본 위치로 폴스루한다. Win11 은 시작 메뉴와 검색 창이 하나의 HWND 를 공유하고 rect 만 바뀌므로, 감지 스레드가 시각 프레임의 변화를 포그라운드 전환과 동일하게 취급해 인디 위치를 재계산한다. ESC 등으로 시스템 입력 UI가 닫히면 인디를 즉시 숨긴다: 시작 메뉴(`StartMenuExperienceHost`)는 DWM cloaked 전환을 감지하고, 검색 창(`SearchHost`/`SearchApp`)은 포그라운드가 즉시 다른 앱으로 이동하는 것을 감지한다

### 2.3 표시 모드
- **Always** (기본) — 항상 표시, 유휴 시 반투명
- **OnEvent** — 포커스/IME 변경 시 일정 시간 표시 후 페이드 아웃

### 2.4 숨김 조건
- 바탕화면 / 작업 표시줄 / 잠금 화면 (SystemFilter)
- 전체 화면 앱 (`hide_in_fullscreen`)
- 비한국어 IME + `NonKoreanIme = Hide` 설정
- 포커스 없는 창 (`hide_when_no_focus`)
- 앱별 블랙/화이트 리스트 (`app_filter_mode` + `app_filter_list`)

### 2.5 크기 배율
- **범위**: 1.0 배 ~ 5.0 배, 소수점 첫째 자리 (0.1 단위)
- **적용 대상**: `label_width`, `label_height`, `font_size`, `label_border_radius`, `border_width`, 라벨 좌우 패딩
- **DPI 와의 관계**: DPI 스케일링과 독립적(선-곱). 예: 2배 × 150 % DPI = 3 배 픽셀
- **트레이 설정**: "크기 ▸" 서브 메뉴에 1~5배 정수 프리셋 5개 + "직접 지정" 항목. 정수 배율이면 해당 프리셋에 라디오 체크, 비정수(예: 2.3배) 면 "직접 지정 (2.3배)" 로 라벨이 동적으로 바뀌고 라디오 체크가 이쪽으로 이동
- **직접 지정 대화상자**: 마우스 커서 위치에 모달 창으로 띄우고, 현재 배율을 EDIT 에 미리 채움. 1.0 미만 / 5.0 초과 / 숫자 아님은 오류 메시지 박스 후 재입력. Enter = 확인, ESC = 취소
- 배율 변경은 즉시 렌더링에 반영되고 `config.indicator_scale` 에 저장

### 2.6 CAPS LOCK 표시
- **목적**: 한/영 상태와 별개로 CAPS LOCK 토글 상태를 타이핑 중 시선 이동 없이 인디케이터 위에서 바로 확인
- **형태**: 라벨 좌우 수직 가장자리에 세로 막대 두 개. 막대 색상은 현재 상태(한/영/비한국어)의 `fg` 를 그대로 재사용해 테마와 자연스럽게 어울림
- **동작**: 켜지면 즉시 막대 그리기, 꺼지면 즉시 사라짐. 한/영 상태 변경과 독립적으로 동작하므로 어떤 IME 모드에서도 토글 표시가 유지된다
- **감지 방식**: `GetKeyState(VK_CAPITAL)` 은 호출 스레드의 입력 상태를 읽으므로 감지 스레드(80 ms 폴러)에서는 신뢰할 수 없다. 대신 메인 스레드 `WM_TIMER` (주기 200 ms, `DefaultConfig.CapsLockPollMs`) 로 폴링한다. 부팅 시 초기값은 `Overlay.Initialize` 와 `Program.Main` 에서 같은 함수를 호출해 주입해 — 사용자가 CAPS LOCK 이 켜진 상태로 앱을 시작해도 첫 렌더부터 올바르게 표시된다
- **설정 필드 없음**: 토글 표시는 시스템 CAPS LOCK 상태를 그대로 반영하므로 사용자가 별도로 켜고 끌 필요가 없다(원하지 않으면 CAPS LOCK 을 끄면 된다). `config.json` 에 관련 키 없음

### 2.7 텍스트 수직 정렬 보정
`DT_VCENTER` 는 폰트 셀(`tmAscent + tmDescent`) 중앙을 사각형 중앙에 맞춘다. 한글 폰트(맑은 고딕 등)는 라틴 액센트용 상단 reserved 영역(`tmInternalLeading`) 이 있는데 한글·대문자 영문은 이 영역을 사용하지 않으므로, `tmInternalLeading > tmDescent` 인 폰트에서는 글리프 시각 중심이 셀 중심보다 `(tmInternalLeading - tmDescent) / 2` 픽셀만큼 아래쪽에 위치한다. 결과적으로 라벨이 라운드 배경 안에서 살짝 아래로 치우쳐 보인다.

- **측정**: `LayeredOverlayBase.EnsureFont` 가 `Gdi32.GetTextMetricsW` 로 폰트 셀 메트릭을 한 번 측정해 오프셋을 캐시한다. 폰트 캐시 키(family + size + bold + DPI)에 편승하므로 부팅 1회 + 폰트/크기/굵기/DPI 변경 시에만 재실행된다
- **적용**: `OverlayMetrics.TextVCenterOffsetPx` 로 노출. `Overlay.OnRenderToDib` 가 textRect 를 `{ Top = -vOffset, Bottom = h - vOffset }` 로 구성해 높이를 보존한 채 rect 자체를 위로 이동 — `DT_VCENTER` 자체는 정상 동작하고, 글리프 시각 중심이 정확히 `h/2` 에 위치한다
- **한계**: 현재 라벨은 디센더가 없는 글자(`한`/`En`/`EN`)만 사용하므로 단일 보정값으로 충분. 향후 라벨에 디센더가 있는 글자를 도입하면 과보정이 되어 글리프별 메트릭 기반 재유도 필요

---

## 3. 애니메이션

| 종류 | 설명 | 기본값 |
|------|------|--------|
| 페이드인 | 표시 직후 알파 0 → 활성 알파 | 150 ms |
| 페이드아웃 | 유지 시간 종료 후 알파 → 0 (유휴) | 400 ms |
| IME 전환 강조 | 한/영 전환 시 1.3 × 스케일 → 원래 크기 | 300 ms |
| 슬라이드 | 같은 모니터 내 이전 위치 → 새 위치 이동 (ease-out cubic; 모니터 간 이동은 즉시) | 500 ms |
| 타이머 | `WM_TIMER` 기반, `AnimationFrameMs=15` (실배달 ~15.6ms≈64fps; `SetTimer` ~15.625ms 격자 양자화 — 이전 16은 실효 ~31ms≈32fps) | — |

트레이 메뉴 "애니메이션 사용" 으로 전체 on/off, "변경 시 강조" 로 IME 전환 강조만 on/off. 기본은 모두 켜짐. 페이드 지속 시간·하이라이트 배율·슬라이드 속도는 "상세 설정" 에서 조정.

---

## 4. 시스템 트레이

### 4.1 아이콘
- 캐럿+점(caret+dot) 디자인 고정. IME 상태별 배경색 변경 (한글 / 영문 / 비한국어)
- 툴팁: "KoEnVue - 한글 모드" (호버 시 표시). `NOTIFYICON_VERSION_4` 환경에서는 `NIF_SHOWTIP` 플래그를 함께 설정해야 쉘이 툴팁을 렌더링한다
- `config.tray_tooltip = false` 로 툴팁 숨김 가능
- **좌클릭 토글 = 숨김 상태 시각 표시**: 사용자가 좌클릭으로 인디를 숨긴 상태(`config.user_hidden = true`) 에서는 캐럿+점 위에 수평 취소선 1줄을 중앙에 굵게 중첩 렌더링한다. 두께는 `iconH / 4` (16 px 아이콘에서 4 px, 20 px 고DPI 에서 5 px) — 캐럿+점 도형을 가로지르면서도 형체는 읽히는 적정 두께. 색상은 캐럿+점과 동일한 상태별 Fg(`HangulFg` / `EnglishFg` / `NonKoreanFg`) 를 공유한다. 다시 좌클릭해 보임으로 전환하면 취소선이 사라진다
- **쉘 재시작 자동 복구**: `explorer.exe` 가 죽고 재시작되거나 쉘 업데이트로 트레이가 초기화되면 `TaskbarCreated` 브로드캐스트 메시지를 받아 트레이 아이콘을 자동 재등록한다. 사용자가 앱을 재시작할 필요 없음

### 4.1a 좌클릭 동작 (`tray_click_action`)

세 가지 값을 지원하며 SettingsDialog "좌클릭 동작" 콤보박스에서 선택하거나 `config.json` 에서 직접 지정한다.

| 값 | 동작 |
|----|------|
| `"toggle"` (기본) | 인디케이터 전체 보임/숨김 토글 (`config.user_hidden` 반전) |
| `"settings"` | `config.json` 을 메모장(`notepad.exe`)으로 연다 |
| `"none"` | 좌클릭 무시 |

**토글 동작 세부 (`"toggle"`)** — 상태는 `config.user_hidden` 필드(bool, 기본 `false`) 로 `config.json` 에 즉시 영구 저장되므로 앱 재기동·포그라운드 전환·세션 종료에도 유지된다. `user_hidden = true` 일 때는 감지 스레드의 IME/포커스/포그라운드 이벤트 5종이 인디를 다시 띄우지 않는다 — 사용자가 명시적으로 숨겼다는 의도를 모든 자동 복원 경로보다 우선 적용.

**설정 파일 열기 (`"settings"`)** — 메모장 고정 (시스템 기본 `.json` 핸들러가 없어 "앱 선택" 다이얼로그가 뜨는 일반 사용자 환경을 회피). 경로에 공백이 있어도 안전하도록 `lpParameters` 를 따옴표로 감싸 `ShellExecuteW(0, "open", "notepad.exe", "\"{path}\"", ...)` 로 호출. 반환값 ≤ 32 는 `Logger.Warning` 으로만 기록 (사용자에게 알려도 대응 방법이 없음).

**`user_hidden` 리셋 경로** — `"toggle"` 이 아닌 값을 쓰는 환경에서도 dead-end 에 빠지지 않도록 트레이 우클릭 메뉴에 체크 토글 항목 **"인디케이터 숨김"** 을 상세 설정 바로 위에 배치한다 (4.2 참조). 그 외 리셋 경로로 `config.json` 삭제 시 STJ 의 기본 unmapped-member handling 이 `user_hidden = false` 를 자연 복원하며, 필드를 수동 편집해도 hot reload + 다음 IME/포커스 이벤트로 표시가 돌아온다.

### 4.2 메뉴

```
KoEnVue v0.9.2.5 — GitHub              ← 항상 최상단 헤더 (MF_DEFAULT 볼드)
   또는 KoEnVue v0.9.2.5 → v0.9.3.0 — 다운로드   ← 새 버전 가용 시 라벨 변경
───
투명도 ▸ (진하게 / 보통 / 연하게)
크기 ▸ (1배 / 2배 / 3배 / 4배 / 5배 / 직접 지정...)
☑ 창에 자석처럼 붙이기
☑ 애니메이션 사용
☑ 변경 시 강조
───
☑ 시작 프로그램 등록
☐ 관리자 권한으로 실행
───
기본 위치 ▸ (현재 위치로 설정 / 초기화)
위치 모드 ▸ (○ 고정 위치 / ● 창 기준)
드래그 활성 키 ▸ (● 없음 / ○ Ctrl / ○ Alt / ○ Ctrl + Alt)
위치 기록 정리...
───
☐ 인디케이터 숨김
───
상세 설정...
───
종료
```

- **헤더 라인 (KoEnVue v… — GitHub / → v… — 다운로드)** 은 항상 메뉴 최상단에 노출되는 단일 라인이며 두 모드로 동작한다. 평소엔 `KoEnVue v{DefaultConfig.AppVersion} — GitHub` 라벨에 `MF_DEFAULT` 플래그를 부여해 시스템이 자동 볼드로 그리고 (팝업 메뉴당 1개만 가능), 바로 아래 separator 가 메뉴 구조를 분할 — 위치(최상단) + 볼드 + 분리선 3종이 합쳐져 "메뉴 헤더" 역할을 명시한다. `_pendingUpdate is not null` 일 때 라벨이 `KoEnVue v{cur} → {newTag} — 다운로드` 로 자동 전환되어 같은 헤더 위치에서 새 버전 가용을 알린다 (별도 알림 블록 없음). 클릭 시 `IDM_HOMEPAGE` 단일 진입점이 `_pendingUpdate` 유무로 분기 — `OpenUpdatePage()` (`info.HtmlUrl` prefix 검증 후 릴리스 페이지) 또는 `OpenHomepage()` (`https://github.com/{UpdateRepoOwner}/{UpdateRepoName}` 컴파일 타임 합성, 외부 입력 검증 불필요). 라벨의 한·영 분기는 마지막 행위 단어(`I18n.MenuDownload` = "다운로드"/"Download") 만, 브랜드명·버전 숫자·화살표 부분은 공통. `_pendingUpdate.Version` 이 GitHub release `tag_name` 형식("v1.0.1") 이라 `→ {tag}` 로 합성해야 v 가 중복되지 않음. 자동 업데이트는 아니며 사용자가 직접 새 exe 를 받아 교체. `config.update_check_enabled = false` 로 GitHub 조회 자체를 비활성화 가능 (이때 헤더는 평소 모드만 노출)
- **투명도·크기·자석 스냅·애니메이션·변경 시 강조**는 모두 드래그·렌더링·피드백 동작에 영향을 주는 항목이라 상단에 묶어 배치. 모두 토글형 체크 항목이며 자석 스냅·애니메이션·변경 시 강조는 기본 켜짐
- **인디케이터 숨김**은 `config.user_hidden` 체크 토글 — 좌클릭 동작을 `"settings"`/`"none"` 으로 바꾸어 좌클릭 토글 경로가 막힌 경우에도 우클릭 메뉴로 항상 숨김 해제가 가능하도록 4.1a 의 dead-end 방지용으로 단독 블록 배치. 내부적으로 `HandleTrayToggle` 과 동일한 `ApplyUserHiddenTransition` 헬퍼로 오버레이/트레이 아이콘 상태를 즉시 동기화하며, 토글 즉시 `Settings.Save` 로 config.json 에 영구 저장
- **상세 설정**은 구분선으로 묶어 종료 바로 위에 배치 — 트레이 메뉴로 노출되지 않는 나머지 필드를 편집하는 진입점

### 4.3 시작 프로그램 등록
- `schtasks /xml` 기반 등록/해제 (ONLOGON 트리거 + `<Delay>PT15S</Delay>`, 기본 `LeastPrivilege` — P5 asInvoker 와 일치, v0.9.3.0 부터 UAC 프롬프트 없음)
- **로그온 15초 지연**: 부팅 자동 실행 시 explorer 트레이가 초기화되기 전에 앱이 떠서 `Shell_NotifyIconW NIM_ADD` 가 실패하는 레이스를 회피. (NIM_ADD 재시도로 복구되긴 하지만 매 부팅 warn 로그가 남는 문제 해소 목적)
- **자동 동기화**: 앱 시작 시 백그라운드 스레드로 등록된 schtasks 항목의 `<Command>` 경로 / `<Delay>` 값 / `<RunLevel>` 을 조회해 현재 `Environment.ProcessPath` · `PT15S` · `expectedRunLevel`(`config.AdminElevation` 에서 derive) 와 비교. 경로가 바뀌었거나 `<Delay>` 가 없거나(구 버전 `/tr` 방식) `<RunLevel>` 이 기대값과 다르면 XML 방식으로 재등록. v0.9.2.x → v0.9.3.x 업그레이드 시 첫 부팅 후 자동 재등록되어 다음 부팅부터 UAC 프롬프트가 사라진다. exe 폴더를 옮겨도 다음 수동 실행부터는 복구된다(이사 직후 첫 자동 부팅은 구 경로를 찌르므로 한 번은 실패할 수 있음)
- **v0.9.4.0 — 관리자 권한 실행 옵션 (PR-15)**: `config.admin_elevation: true` (트레이 메뉴 "관리자 권한으로 실행" 또는 상세 설정 → 시스템 섹션) 시 (1) 단일 실행 경로 — [App/Bootstrap/AdminElevation](../App/Bootstrap/AdminElevation.cs) 가 mutex 획득 전 `ShellExecuteW("runas")` 로 자기 재실행 (UAC 1회). (2) 부팅 자동 시작 경로 — schtasks `<RunLevel>HighestAvailable</RunLevel>` 분기로 등록 (등록 시 UAC 1회, 부팅마다 UAC 0). 옵션 토글 시 등록된 schtasks 가 즉시 자동 재등록 (`ReregisterIfAdminChanged`). 매니페스트는 `asInvoker` 유지 (P5 invariant). UIPI (User Interface Privilege Isolation — Medium IL → High IL `WM_IME_CONTROL` 차단) 가 일반 권한 KoEnVue 의 관리자 콘솔 IME 감지를 막는 문제를 사용자 선택적으로 우회하기 위함. UAC 거부 시 안내 `MessageBoxW` 후 일반 권한으로 계속 진행

### 4.4 기본 위치 설정
- 저장 위치가 없는 앱을 열 때 인디케이터가 나타날 기본 위치를 사용자가 지정. 현재 위치 모드에 따라 저장 대상이 달라짐
- **현재 위치로 설정**: 현재 인디케이터 위치에서 가장 가까운 모서리를 맨해튼 거리로 자동 선정해 `Corner` anchor + delta 로 저장
  - **고정 모드** → `config.default_indicator_position` (작업 영역 기준)
  - **창 기준 모드** → `config.default_indicator_position_relative` (포그라운드 창 기준)
- **초기화**: 현재 모드에 해당하는 필드를 null 로 되돌려 하드코딩 폴백을 복원. 이미 null 이면 메뉴 항목 비활성화(grayed)
- 시스템 입력 프로세스(시작 메뉴·검색 창)에는 적용되지 않음 — 기존 규칙(창 시각적 좌상단 바로 위) 유지

### 4.5 위치 기록 정리
- 현재 `position_mode` 설정과 무관하게 동작 — `indicator_positions`(고정) 와 `indicator_positions_relative`(창 기준) 양쪽의 키 합집합을 체크박스 다이얼로그로 표시하여 선택 삭제. 삭제 시 양쪽 dict 에서 동시에 제거하므로 모드를 전환해도 삭제한 앱의 위치 데이터가 남지 않음
- 설명 문구로 합집합·동시 삭제 규칙을 안내. 각 항목에 모드 태그 `(고정)` / `(창)` / `(고정·창)` 표시 (영문 Fixed / Window)
- 현재 실행 중인 프로세스에는 태그 안에 `, 실행 중` / `, running` 추가
- 전체 선택/해제 토글 지원
- 저장된 위치 기록이 없으면 안내 메시지 표시
- 항목이 15개를 초과하면 스크롤 가능한 뷰포트 + 마우스 휠 지원
- DPI 스케일 대응, 시스템 폰트(맑은 고딕 9 pt), 설명 라벨 + 구분선
- Tab 키로 체크박스/버튼 사이 포커스 순환, ESC 로 취소

### 4.6 크기 배율 직접 지정 대화상자
- "크기 ▸ 직접 지정" 메뉴 클릭 시 마우스 커서 위치에 모달 다이얼로그 생성 (작업 영역 밖으로 나가지 않도록 클램프)
- 안내 라벨("배율 (1.0 ~ 5.0):") + EDIT(현재 배율을 `0.#` 포맷으로 미리 채움 — 2.0 → "2", 2.3 → "2.3") + 힌트 라벨 + 확인/취소 버튼
- 확인 버튼은 `BS_DEFPUSHBUTTON` 이라 Enter 키로 트리거, ESC 는 취소로 매핑
- 검증 실패(숫자 아님 / 범위 밖) 시 오류 메시지 박스 → EDIT 재포커스 + 텍스트 전체 선택(재입력 편의)
- 파싱은 `InvariantCulture` 기준이라 OS 로캘과 무관하게 `.` 소수점 수용
- DPI 스케일, 시스템 폰트, 메인 창 `EnableWindow(false)` + 중첩 메시지 루프 모달

### 4.7 상세 설정 대화상자
"상세 설정" 메뉴 클릭 시 스크롤 가능한 모달 대화상자가 열려, 트레이 메뉴로는 노출되지 않는 설정 필드를 **12 개 섹션**(① 표시 모드, ② 외관 — 크기·테두리, ③ 외관 — 색상·투명도, ④ 외관 — 텍스트, ⑤ 외관 — 테마, ⑥ 애니메이션, ⑦ 감지 및 숨김, ⑧ 앱별 프로필, ⑨ 트레이, ⑩ 시스템, ⑪ 인디케이터 조작, ⑫ 고급)으로 묶어 `설명 | 입력 상자` 2 컬럼 테이블 형태로 노출한다.

- 입력 컨트롤 종류: 불리언 = 체크박스, 정수·실수·문자열·색상 = EDIT, 열거형 = COMBOBOX
- 수직 스크롤바 + 마우스 휠 지원, 입력 상자 너비는 뷰포트 가용 폭에 맞춰 자동 축소되어 스크롤바에 가려지지 않음
- 검증 실패(숫자 형식·범위·색상 형식·빈 값) 시 오류 메시지 박스 → 해당 행으로 자동 스크롤 → 입력 상자 포커스 + 텍스트 전체 선택
- 모든 필드 검증 통과 후 "확인" 시 일괄 적용(config 한 번 저장), "취소" 는 변경 사항 폐기
- 룩앤필은 "위치 기록 정리" / "직접 지정" 대화상자와 동일 (맑은 고딕 9 pt, 시스템 버튼 배경, DPI 스케일, 중첩 메시지 루프 모달, Tab/Enter/ESC 키 처리)
- **제외 항목**
  - 트레이 메뉴로 이미 조작 가능한 항목: 투명도, 크기 배율, 기본 위치, 시작 프로그램 등록, 자석 스냅, 애니메이션 사용, 변경 시 강조, 위치 데이터, 위치 모드, 드래그 활성 키
  - **`config.json` 직접 편집 전용 컬렉션 필드**: `app_profiles` (앱 프로필 맵 — 사용 예시는 [User_Guide.md](User_Guide.md)), `app_filter_mode` + `app_filter_list` (앱 화이트/블랙리스트), `system_hide_classes_user` / `system_hide_processes_user` (시스템 숨김 목록 사용자 확장)
  - **`config.json` 직접 편집 전용 hidden 옵션**: `tray_enabled` (트레이 아이콘 자체 켜고 끔 — OFF 시 우클릭 메뉴가 사라지므로 이후 변경은 `config.json` 재편집 또는 앱 재시작 필요), `advanced.overlay_class_name` (윈도우 클래스명, 내부 디버깅용)

### 4.8 업데이트 알림

부팅 시 GitHub Releases API 를 1 회 조회해 새 버전이 있으면 트레이 메뉴 최상단의 항상 노출되는 헤더 라인의 라벨이 평소 "KoEnVue v{ver} — GitHub" 에서 "KoEnVue v{cur} → {newTag} — 다운로드" 로 자동 전환된다. 클릭하면 `ShellExecuteW` 로 기본 브라우저에서 릴리스 페이지(평소엔 레포 루트)를 연다. 자동 다운로드 / 자동 설치는 제공하지 않는다. 이 기능은 **v0.8.9.0** (2026-04-14, 첫 공개 릴리스) 에서 첫 배포된 후 v0.9.2.6 에서 별도 알림 블록이 헤더 라인 라벨 변형으로 통합되었다 (4.2 메뉴 헤더 라인 참조).

- **버전 소스**: `DefaultConfig.AppVersion` 상수 (수동 편집, 4-part `major.minor.build.revision`). `Assembly.GetName().Version` 사용 시 NativeAOT 트리밍 문제와 `.csproj` `<Version>` 동기화 번거로움이 있어 의도적으로 상수로 관리한다. 릴리스를 내릴 때 이 상수를 `KoEnVue.csproj` 의 `<Version>` 요소와 **반드시 함께** bump 해야 한다 — csproj 값은 PE 헤더의 `AssemblyVersion` / `FileVersion` / `InformationalVersion` 3종에 박히고, `AppVersion` 은 `UpdateChecker` 가 `tag_name` 과 비교하는 값이라, 둘 중 하나만 올리면 파일 속성과 런타임 비교 값이 불일치한다. 구체적 릴리스 순서는 [README.md](../README.md) → 릴리즈 (Releasing) 절 참고
- **레포 소스**: `DefaultConfig.UpdateRepoOwner` / `UpdateRepoName` 상수. 포크 배포자는 두 상수만 바꾸면 자신의 릴리스 채널을 가리키게 할 수 있다
- **조회 경로**: `https://api.github.com/repos/{owner}/{name}/releases/latest`. User-Agent 헤더 필수(누락 시 403), `Accept: application/vnd.github+json` 권장
- **HTTP 스택**: `Core/Http/HttpClientLite` → `Core/Native/WinHttp.cs` P/Invoke. `System.Net.Http.HttpClient` 는 NativeAOT 퍼블리시에 ~2.5 MB 를 추가하지만 WinHTTP 경로는 ~40 KB 로 끝나므로 (약 60× 차이) 트레이 앱 크기 예산(P1 정신)에 맞춰 선택
- **빈도**: 앱 시작 시 백그라운드 스레드 1회만 실행. 주기 폴링·재시도·레이트 리밋 관리 없음. 재확인은 앱 재시작으로 대체
- **silent 실패**: 네트워크 오류, HTTP 비-200, 빈 응답, JSON 파싱 실패, draft/prerelease skip, `current >= latest` 실패 — 모두 `Logger.Debug` 로만 기록. 사용자에게 팝업/배지 노출 없음
- **URL 스킴 화이트리스트**: `Tray.OpenUpdatePage` 는 `ShellExecuteW` 호출 전에 GitHub API 응답의 `html_url` 이 `https://github.com/{UpdateRepoOwner}/{UpdateRepoName}/` 프리픽스로 시작하는지 `OrdinalIgnoreCase` 비교한다. PR-03 후 `asInvoker` 라 Admin 토큰 EoP 표면은 사라졌지만, 외부 응답을 그대로 `ShellExecute` 에 넘기면 신뢰된 CA MITM 이 조작한 `file:///`·`javascript:`·`ms-settings:` 등의 임의 핸들러가 사용자 컨텍스트에서 기동될 수 있어 방어를 유지한다. 불일치 시 `Logger.Warning` 후 즉시 반환
- **버전 비교**: `UpdateChecker.NormalizeVersion` 이 `ReadOnlySpan<char>` 로 앞의 `v`/`V` 접두어와 semver prerelease/build 접미어 (`-beta.1`, `+build.42`) 를 제거한 뒤 `System.Version.TryParse` 로 `N.N.N[.N]` 파싱. `IsNewer(current, latest)` 는 `latestV > currentV`. prerelease 정렬 (`1.0.0-alpha < 1.0.0`) 은 의도적으로 무시 — prerelease 태그는 `release.Prerelease || release.Draft` 체크에서 skip 되므로 알림 경로에 닿지 않는다
- **크로스 스레드 마샬링**: 백그라운드 스레드의 `onUpdateFound` 콜백 → `Program.OnUpdateCheckResult` 에서 `Program._pendingUpdate` (`private static volatile UpdateInfo?`) 에 쓰고 `User32.PostMessageW(hwndMain, WM_APP_UPDATE_FOUND, 0, 0)` 호출. 메인 스레드의 WndProc 가 메시지를 받아 `HandleUpdateFound` → `Tray.OnUpdateFound(info)` 를 호출. 감지 스레드와 동일한 `WM_APP+N` 패턴을 재사용해 크로스 스레드 신호 경로를 일관되게 유지
- **트레이 메뉴 헤더 라인 라벨 전환**: `Tray._pendingUpdate` (비 volatile — WM_APP_UPDATE_FOUND 처리 이후는 메인 스레드 단독 접근) 가 non-null 이면 `ShowMenu` 가 헤더 라인의 라벨을 `KoEnVue v{cur} → {newTag} — 다운로드` 로 합성, null 이면 평소 라벨 `KoEnVue v{cur} — GitHub`. 별도 메뉴 항목·구분선 추가 없이 같은 `IDM_HOMEPAGE = 4010` 항목 한 줄에서 라벨만 전환되므로 메뉴 시각 구조가 두 모드 사이에 변하지 않는다 (v0.9.2.6 에서 v0.8.9.0 의 별도 `IDM_UPDATE_DOWNLOAD = 4008` 블록을 통합)
- **다른 알림 수단을 의도적으로 선택 안 함**: (a) balloon `NIIF_INFO` — 침해적, (b) Windows Toast — `AppUserModelID` + Start 메뉴 바로가기 필요해 포터블 단일 exe 배포 모델과 충돌, (c) 트레이 툴팁 접두 "⚡ Update available — ..." — 너무 은은해서 발견성 낮음. 통합 헤더 라인은 메뉴 최상단 + `MF_DEFAULT` 볼드 + 라벨 변형(`→ {tag} — 다운로드`) 으로 발견성·침해성 균형. 우클릭만 하면 첫 줄에서 즉시 인지
- **토글**: `config.update_check_enabled` (기본 `true`). Program 부팅 시 `if (_config.UpdateCheckEnabled)` 체크로 감싸진다. 상세 설정 다이얼로그 "[시스템]" 섹션의 **"부팅 시 업데이트 확인"** 체크박스로 on/off 가능(`config.json` 직접 편집도 지원). OFF 로 두면 `WinHttpOpen` 자체가 호출되지 않아 WPAD 탐지·DNS 질의·TCP 연결 등 어떠한 네트워크 트래픽도 발생하지 않는다 — 폐쇄망 친화

---

## 5. 설정 (config.json)

### 5.1 파일 위치
- **기본은 exe 디렉토리의 `config.json`** — 포터블 정책. exe 만 있으면 첫 실행 시 자동 생성된다
- **v0.9.3.0 (PR-03) 부터 `%LOCALAPPDATA%\KoEnVue\` 로 자동 fallback** — `app.manifest` 를 `asInvoker` 로 전환하면서 exe 폴더가 user-non-writable 한 경우(예: `Program Files` 설치)를 [App/Config/PortablePath](../App/Config/PortablePath.cs) 가 결정. 결정 우선순위: `BaseDirectory\config.json` 이 이미 있으면 그 경로(v0.9.2.x → v0.9.3.x 마이그레이션) → BaseDirectory writable 이면 BaseDirectory → `%LOCALAPPDATA%\KoEnVue\config.json`
- `koenvue.log` 도 동일 fallback. `config.json:log_file_path` 사용자 지정 값은 `PortablePath.SanitizeLogPath` 가 허용 루트(BaseDirectory / `%LOCALAPPDATA%\KoEnVue`) 하위인지 검증해 위반 시 기본 경로로 폴백 + `Logger.Warning`
- **첫 실행**: 파일이 없으면 기본 `AppConfig` 를 즉시 디스크에 저장해 사용자 눈에 바로 보이게 한다
- **원자적 저장**: `JsonSettingsFile.WriteAllText` 는 `path + ".tmp"` 로 먼저 쓴 뒤 `File.Move(tmp, path, overwrite: true)` — 내부적으로 `MoveFileExW(MOVEFILE_REPLACE_EXISTING)` 이므로 같은 볼륨에서 원자적 교체가 보장된다. 저장 중 크래시 / 전원 차단이 일어나도 잘린 `config.json` 이 남지 않으며, 핫 리로드의 "삭제 감지" 가드와 호환된다
- **파싱 실패**: 기본값을 메모리에서만 사용하고 디스크는 덮어쓰지 않는다(수동 복구 여지 보존). 동시에 손상된 파일의 mtime 을 캐시에 반영해 5 초 폴링이 `WM_CONFIG_CHANGED` 를 무한 재발송하지 않도록 한다
- **완전 삭제**: 포터블 경로면 exe 폴더만 지우면 끝. fallback 경로를 쓴 경우 `%LOCALAPPDATA%\KoEnVue\` 도 함께 지워야 `config.json` + `koenvue.log` 가 모두 제거된다 (§ 9 참고)

### 5.2 주요 설정 카테고리

| 카테고리 | 주요 키 |
|----------|---------|
| 표시 모드 | `display_mode`, `event_display_duration_ms`, `always_idle_timeout_ms` |
| 외관 — 크기 | `label_width`, `label_height`, `label_border_radius`, `border_width`, `indicator_scale` |
| 외관 — 텍스트 | `font_family`, `font_size`, `font_weight`, `hangul_label`, `english_label`, `non_korean_label` |
| 외관 — 색상 | `hangul_bg`, `hangul_fg`, `english_bg`, `english_fg`, `non_korean_bg`, `non_korean_fg`, `opacity`, `idle_opacity`, `active_opacity` |
| 외관 — 테마 | `theme` (6 종 프리셋: `custom` / `minimal` / `vivid` / `pastel` / `dark` / `system`) |
| 애니메이션 | `animation_enabled`, `fade_in_ms`, `fade_out_ms`, `change_highlight`, `highlight_scale`, `highlight_duration_ms`, `slide_animation`, `slide_speed_ms` |
| 감지 | `poll_interval_ms`, `detection_method`, `non_korean_ime`, `hide_in_fullscreen`, `hide_when_no_focus`, `hide_on_lock_screen`, `system_hide_classes`, `system_hide_classes_user`, `system_hide_processes`, `system_hide_processes_user` |
| 앱별 프로필 | `app_profiles`, `app_profile_match`, `app_filter_mode`, `app_filter_list` |
| 트레이 | `tray_enabled`, `tray_tooltip`, `tray_click_action`, `tray_quick_opacity_presets`, `user_hidden` |
| 시스템 | `log_level`, `log_to_file`, `log_file_path`, `log_max_size_mb`, `language` |
| 업데이트 | `update_check_enabled` |
| 인디 위치 | `position_mode` (`fixed` / `window`), `indicator_positions` (고정 모드 프로세스명별 영구), `indicator_positions_relative` (창 기준 모드 프로세스명별 영구), `default_indicator_position` (고정 모드 기본 위치), `default_indicator_position_relative` (창 기준 모드 기본 위치), `snap_to_windows` |
| 인디케이터 조작 | `snap_gap_px`, `drag_modifier` |
| 고급 | `advanced.force_topmost_interval_ms`, `advanced.overlay_class_name` |

### 5.3 테마 프리셋

`theme` 필드는 6 종 프리셋 중 선택한다. `custom` 외의 값은 `hangul_bg` / `hangul_fg` / `english_bg` / `english_fg` / `non_korean_bg` / `non_korean_fg` 를 덮어쓴다. 프리셋 적용 시 기존 커스텀 색상은 `custom_backup_*` 필드 6 개에 자동 백업되며, `custom` 으로 복귀하면 원래 커스텀 색상이 복원된다.

| 테마 | 설명 |
|------|------|
| `custom` | 사용자 색상 그대로 통과(기본값). 백업이 존재하면 복원 후 백업 소멸 |
| `minimal` | 미니멀 — 무채색 계열 |
| `vivid` | 비비드 — 강한 원색 대비 (녹/적/청) |
| `pastel` | 파스텔 — 부드러운 연두/노랑/보라 |
| `dark` | 다크 — 진한 에메랄드/앰버/회색 |
| `system` | 시스템 강조색 기반 — Win11 의 `DwmGetColorizationColor` (personalization accent 의 source-of-truth) 우선, DWM 비활성/실패 시 `COLOR_HIGHLIGHT` 폴백. 한글 배경에 직접 적용, 영문 배경은 보색 계산. `WM_DWMCOLORIZATIONCOLORCHANGED` (0x0320) / `WM_THEMECHANGED` (0x031A) / `WM_SETTINGCHANGE` (0x001A) 가 모두 캐시 무효화 + 재렌더를 트리거 |

### 5.4 앱별 프로필 머지

`app_profiles` 는 포그라운드 앱에 따라 일부 키만 오버라이드하는 맵이다. 매칭 키는 `app_profile_match` 에 따라 프로세스명(`process`) / 윈도우 클래스명(`class`) / 윈도우 타이틀 정규식(`title`) 중 하나로 결정되며, 매칭된 프로필 객체에 명시된 키만 글로벌 설정을 덮어쓰고 나머지는 상속한다. `"enabled": false` 프로필은 해당 앱에서 인디케이터를 완전히 끈다.

- **머지 후처리 파이프라인** — 매칭된 프로필을 JSON 레벨에서 글로벌과 합친 결과는 디스크 로드 경로(`JsonSettingsManager.Load`)와 동일한 후처리를 거친다: `EnsureSubObjects` (null 보정) → `Validate` (범위 클램핑, enum 검증, `advanced.overlay_class_name` 폴백) → `ApplyTheme` (프리셋 색상 적용 + Custom 백업/복원). `Migrate` 는 App 레벨 override 가 없어(JsonSettingsManager identity) 현재 효과가 없지만 추후 스키마 진화 시 같은 진입점에서 호출된다
- **실효 범위 — 감지 측 키** — 머지된 `resolved` AppConfig 는 감지 스레드의 `Settings.ResolveForApp` 가 반환하며, 같은 틱 안에서 (a) `SystemFilter.ShouldHide` 의 모든 파라미터(`system_hide_classes` / `system_hide_processes` / `hide_in_fullscreen` / `hide_when_no_focus` / `app_filter_mode` / `app_filter_list`), (b) `PositionMode` (`position_mode` 의 fixed/window 결정 + `TrackWindowMove` 의 활성 여부), (c) `ImeStatus.Detect` 의 `detection_method` 분기에 즉시 반영된다
- **실효 범위 — 시각/표시 측 키 (PR-13 이후)** — 메인 스레드에 `ResolveCurrent()` 헬퍼가 추가되어 `_lastForegroundHwnd` 기반으로 `Settings.ResolveForApp` 를 LRU 캐시 hit 로 재호출한다. `HandleImeStateChanged` / `HandleFocusChanged` 의 `DisplayMode` + `EventTriggers` 평가, 모든 `Animation.TriggerShow` / `Overlay.Show` / `Overlay.UpdateColor` 호출(총 18 곳) 이 글로벌 `_config` 대신 `ResolveCurrent()` 결과를 사용. 따라서 프로필이 `theme` / 색 6쌍(`hangul_bg` / `english_bg` / `non_korean_bg` 의 fg/bg) / `opacity` / `idle_opacity` / `active_opacity` / `label_width` / `label_height` / `label_border_radius` / `border_width` / `border_color` / `indicator_scale` / `font_*` / `*_label` / `animation_*` / `*_duration_ms` / `slide_*` / `change_highlight` / `non_korean_ime` / `display_mode` / `event_triggers` 등 시각·표시 키를 override 하면 화면 색·크기·투명도·폰트·라벨·애니메이션·표시 모드가 즉시 해당 프로필 값으로 전환된다
- **글로벌-only 키** — `Tray.*` / `update_check_enabled` / `language` / `log_*` / `default_indicator_position*` / `snap_to_windows` / `snap_gap_px` / `drag_modifier` / `poll_interval_ms` 등은 프로세스-단위 의미가 약하거나(트레이 아이콘 1개, 언어 1개) 시스템-단위 인터랙션(드래그 모디파이어) 이라 현재 구조상 글로벌만 사용한다. 프로필에서 override 해도 효과 없음
- **PollIntervalMs 의 한계** — 감지 루프(`DetectionLoop`) 는 `_config.PollIntervalMs` 글로벌 값만 사용하므로 프로필에서 이 키를 override 해도 실효 변화 없다
- **LRU 캐시** — 매칭 결과는 프로세스명/클래스명/타이틀 키 기준 LRU(최대 50개)로 캐시되어 80ms 폴링 핫패스의 JSON roundtrip 비용을 흡수한다. 글로벌 인스턴스가 교체(핫 리로드 / 트레이 저장) 되면 자동 무효화되고, 시스템 비주얼 스타일 변경(`WM_SETTINGCHANGE` · `WM_THEMECHANGED`) 시에도 캐시가 클리어되어 `Theme.System` 을 상속한 프로필이 옛 강조색을 박제하지 않는다
- **타이틀 모드의 ReDoS 가드** — `title` 모드는 각 프로필 키를 정규식으로 평가하며 100ms 매칭 타임아웃을 적용한다. `config.json` 이 user-writable 이라 악의적 패턴의 지수 백트래킹이 들어와도 한 틱 안에서 컷오프된다

### 5.5 핫 리로드
- 감지 스레드가 ~5 초마다 `config.json` mtime 체크
- 변경 감지 시 `WM_CONFIG_CHANGED` → 자동 리로드
- **삭제 안전**: mtime 체크 전에 `File.Exists` 가드를 통과해야 한다. 파일이 삭제된 상태에서는 `GetLastWriteTimeUtc` 가 `1601-01-01` 센티널을 반환해 "변경됨" 으로 오인되는데, 이를 통과시키면 `Load()` 가 기본값으로 리셋하고 다음 `Save()` 가 파일을 재생성하면서 사용자 설정을 덮어써 버린다. 에디터의 원자적 교체(`delete → rename`) 저장 방식과도 호환되어야 하므로 파일 잠금이 아닌 읽기 측 가드로 해결

---

## 6. 감지

### 6.1 IME 상태 감지
- `WM_IME_CONTROL` + `IMC_GETOPENSTATUS` / `IMC_GETCONVERSIONMODE`
- `GetKeyboardLayout` LANGID 확인 (비한국어 IME 판별)
- `WinEvent` 훅 (`EVENT_OBJECT_IME_CHANGE`) 보조

### 6.2 시스템 필터 (9 조건)
1. 보안 데스크톱 (hwnd 없음)
2. 비가시/최소화 창
3. 다른 가상 데스크톱 (`IVirtualDesktopManager`)
4. 클래스명 블랙리스트 (`Progman`, `WorkerW`, `Shell_TrayWnd` 등 + 사용자 지정)
4-b. 소유자 창 체인 블랙리스트 — 소유자 클래스가 hide list에 있고, 대화상자와 소유자가 같은 프로세스일 때 숨김 (바탕화면 시스템 대화상자 vs 앱 Common File Dialog 구분)
5. 프로세스명 블랙리스트 (`ShellExperienceHost` 등 + 사용자 지정 — 리스트 비어 있으면 조회 스킵)
6. 포커스 없음 (`hide_when_no_focus`)
7. 전체 화면 독점 (모니터 커버 + `WS_CAPTION` 없음)
8. 앱별 블랙/화이트 리스트 (`app_filter_mode` + `app_filter_list`)

---

## 7. 아키텍처

### 7.1 2-스레드 모델

```
메인 스레드 (UI):    메시지 루프 + 렌더링 + 트레이 + 애니메이션 + CAPS LOCK 폴링 (200 ms)
감지 스레드 (BG):    80 ms IME 폴링 → PostMessageW
```

### 7.2 메시지 파이프라인

```
감지 스레드:
  1. 매 폴링마다 ResolveForApp + SystemFilter.ShouldHide 평가
     - 필터 진입 시(이전 미필터 → 현재 필터):   WM_HIDE_INDICATOR
     - 필터 해소 또는 포그라운드 변경:          WM_POSITION_UPDATED(hwndForeground)
  2. IME 상태 변경 → WM_IME_STATE_CHANGED(ImeState)
  3. 포커스 변경   → WM_FOCUS_CHANGED(hwndFocus)

메인 스레드:
  WM_POSITION_UPDATED  → 포그라운드 변경 또는 이전 숨김 상태였다면 앱별 위치 조회 + TriggerShow
  WM_IME_STATE_CHANGED → 트레이 갱신 + TriggerShow
  WM_FOCUS_CHANGED     → TriggerShow
  WM_HIDE_INDICATOR    → Animation.TriggerHide(forceHidden: true) — Always 모드에서도 완전 숨김
  WM_MOVING            → Shift 축 잠금 + 드래그 중 모니터 변경 시 DPI 재계산
```

- 필터 평가를 매 폴링으로 단순화한 이유: "바탕화면 클릭 → 같은 앱 복귀" 시 `hwndForeground` 는 동일하지만 인디케이터는 다시 나타나야 한다. `lastFiltered` 플래그로 중복 메시지는 억제
- 시스템 필터·트레이 토글 OFF 는 모두 "실제 사라짐" 을 의미하므로 `forceHidden: true` 로 Always 모드의 dim-idle 을 우회

### 7.3 Core / App 레이어 분리

소스 트리는 2 계층으로 나뉘며, 의존 방향은 단방향이다: `App/` 은 `Core/` 를 참조할 수 있지만 그 반대는 금지(P6). `git grep "KoEnVue\.App" Core/` = 0, `git grep "ImeState" Core/` = 0, `git grep "NonKoreanImeMode" Core/` = 0 으로 검증한다.

- **`Core/`** (`namespace KoEnVue.Core.*`) — 재사용 가능한 Win32 / .NET 인프라. 다른 Windows 데스크톱 프로젝트에 폴더 복사 또는 `<Compile Include>` 링크로 그대로 옮길 수 있도록 설계되었으며, KoEnVue 고유 심볼·`AppConfig`·IME enum·도메인 문자열을 일체 포함하지 않는다
- **`App/`** (`namespace KoEnVue.App.*`) — KoEnVue 특화 응용 계층. `Core` 모듈을 조합해 제품 동작을 정의
- **[Program.cs](../Program.cs) + [Program.Bootstrap.cs](../Program.Bootstrap.cs)** — `namespace KoEnVue` (루트). `Core` · `App` 양쪽을 조합하는 엔트리 포인트라 어느 계층에도 속하지 않음. `internal static partial class Program` 으로 두 파일에 분할: 메인 메시지 루프 / WndProc / 감지 스레드 / 이벤트 핸들러는 `Program.cs`, Mutex 가드 / 잔재 트레이 정리 / 윈도우 클래스 등록 / `OnProcessExit` 종료 시퀀스는 `Program.Bootstrap.cs` 에 보관

#### Core / App 모듈 상세

Core / App 각 모듈의 책임·공개 API·재사용 지침은 별도 문서 **[architecture.md](architecture.md)** 에 정리했다. 이 PRD 에서는 제품 관점 개요만 유지한다.

#### 레이어 분리 핵심 원칙

- **Enum 경계 차단**: `ImeState` / `DisplayMode` / `NonKoreanImeMode` / `FontWeight` 는 App 전용 enum 이며 Core 에 유출되면 안 된다. Core 인터페이스는 primitive 로 표현한다:
  - `OverlayStyle` 은 `LabelText : string` 과 `MeasureLabels : (string, string, string)` 튜플만 받으며, 현재 상태의 레이블은 파사드의 `Overlay.BuildStyle` 안에서 `ImeState` 를 해석해 한 번만 주입된다
  - `OverlayStyle.IsBold : bool` 이 `FontWeight.Bold` 를 대신하고, `AnimationConfig.AlwaysMode : bool` 이 `DisplayMode.Always` 를 대신한다
  - `OverlayAnimator.SetDimMode(bool)` 이 `NonKoreanImeMode.Dim` 체크를 추상화한다
- **DPI 소유권**: Core 엔진이 DPI 곱셈을 전담한다. 파사드는 `IndicatorScale` 만 먼저 곱해 `*LogicalPx` 필드로 전달하고, 엔진은 내부에서 `Kernel32.MulDiv(fontSize, dpiY, 72)` 로 정밀하게 산출 (`Math.Round` 대체는 일부 DPI 비율에서 레이블 폭 0~1 px 회귀가 있어 기각)
- **플립-플롭 가드**: `OverlayStyle` 이 `record struct` 이므로 값 동등성으로 자동 비교되며, `Render(newStyle)` 은 `newStyle == _lastStyle` 이면 재렌더를 스킵한다. `MeasureLabels` 에 세 상태 레이블을 모두 넣는 것도 `_fixedLabelWidth` 를 상태 전환 시 churn 없이 max 에 고정하기 위함
- **엔진 인스턴스 + 정적 파사드 조합**: `Overlay` / `Animation` / `Tray` / `Settings` 파사드는 `private static` 필드에 Core 엔진 인스턴스를 보관하고 기존 정적 API 에서 엔진 메서드로 위임한다. 결과적으로 `Program.cs` · 다이얼로그 · Tray · Animation 서로 간의 호출 지점은 Stage 4 전후로 **바이트 동일**하며, 재구성의 영향은 파사드 내부에 국한된다
- **`JsonSettingsManager<T>` 의 `JsonTypeInfo<T>` 주입**: `JsonSerializerIsReflectionEnabledByDefault=false` 환경에서는 STJ 소스 생성기 컨텍스트만이 유효하다. `AppSettingsManager` 는 생성자에 `AppConfigJsonContext.Default.AppConfig` 를 넘겨 NativeAOT 트리밍을 통과시킨다
- **크기 예산**: Stage 6 업데이트 알림 추가 후 퍼블리시 exe 는 v0.8.9.0 기준 약 **4.94 MB** (Stage 2 기준 4,891,136 bytes 대비 +52 KB 내외). 같은 기능을 `System.Net.Http.HttpClient` 로 구현하면 NativeAOT 퍼블리시에 ~2.5 MB 가 추가되므로 WinHTTP P/Invoke 경로가 약 60 배 작다. v0.9.1.4 기준 약 **4.72 MB** — `StackTraceSupport=false` 등 진단 메타데이터 6종을 비활성화해 v0.9.1.3 대비 약 **407 KB (~7.9 %)** 추가 절감

### 7.4 다중 인스턴스 및 트레이 복구

- **단일 인스턴스 보장**: 고정 GUID 기반 Named Mutex (`KoEnVue_{GUID}`) 로 동시 실행 1개만 허용. 두 번째 실행은 즉시 종료
- **활성화 신호 전달**: 두 번째 실행이 감지되면 `FindWindowW` 로 실행 중인 메인 윈도우를 찾아 `WM_APP_ACTIVATE` 를 `PostMessageW` 로 전송한다. 기존 인스턴스는 현재 포그라운드 앱 기준으로 인디케이터를 즉시 표시해 "이미 실행 중" 시각 피드백을 제공 (`DisplayMode` / `EventTriggers` 설정과 무관하게 강제 표시)
- **크래시 잔재 정리**: 이전 실행이 크래시로 트레이 아이콘을 남긴 경우, Mutex 획득 성공 (= 경쟁 인스턴스 없음 확정) 후에만 `Shell_NotifyIconW(NIM_DELETE)` 로 정리한다. 순서가 반대면 두 번째 실행이 실행 중인 첫 번째 인스턴스의 아이콘을 지우는 부작용 발생
- **쉘 재시작 복구**: `RegisterWindowMessageW("TaskbarCreated")` 로 쉘 브로드캐스트 메시지 ID 를 등록하고 WndProc 에서 수신 시 트레이 아이콘을 재등록 (`NIM_ADD` + `NIM_SETVERSION`). `explorer.exe` 재시작·쉘 업데이트·수동 프로세스 종료 모두 커버

구현 세부는 [implementation-notes.md](implementation-notes.md) 의 "Multi-instance and tray recovery" 절 참고.

---

## 8. 빌드

```bash
dotnet build                          # 디버그 빌드
dotnet publish -r win-x64 -c Release  # NativeAOT 릴리스 퍼블리시
```

- NativeAOT 단일 exe (~4.7 MB)
- .NET 런타임 설치 불필요
- `app.manifest` : UAC `asInvoker` (P5, v0.9.3.0~). Program Files 등 user-non-writable 위치 설치 시 `%LOCALAPPDATA%\KoEnVue\` 로 config/log 자동 fallback. v0.9.4.0~ `config.admin_elevation: true` 시 런타임 self-elevation + schtasks `HighestAvailable` 분담 — 매니페스트는 그대로 (PR-15)
- **디버그·릴리스 둘 다 빌드 필수** — 디버그만 돌리면 릴리스 exe 가 낡은 상태로 남음

---

## 9. 완전 삭제

1. 트레이 메뉴에서 **시작 프로그램 등록** 해제 (이미 해제 상태면 생략)
2. KoEnVue 종료
3. exe 폴더 삭제 — 포터블 경로(BaseDirectory)면 `config.json` 과 `koenvue.log` 도 exe 옆에 있으므로 함께 제거된다
4. **fallback 경로를 쓴 경우** (Program Files 등 user-non-writable 위치 설치) `%LOCALAPPDATA%\KoEnVue\` 폴더도 함께 삭제

레지스트리 변경은 전혀 없다. schtasks 시작 등록을 사전에 해제했다면 폴더 삭제로 흔적이 완전히 사라진다.

---

## 10. 관련 문서

| 문서 | 내용 |
|------|------|
| **[../CLAUDE.md](../CLAUDE.md)** | 프로젝트 진입점, 기술 스택, P1–P6, 빌드 |
| **[../README.md](../README.md)** | 다운로드, 빌드, 릴리즈 절차, `config.json` 키 |
| **[User_Guide.md](User_Guide.md)** | 최종 사용자 매뉴얼 |
| **[architecture.md](architecture.md)** | Core / App 모듈 상세, 재사용 계약, 파사드 패턴 |
| **[implementation-notes.md](implementation-notes.md)** | 렌더 파이프라인, 드래그/스냅, 애니메이션, CAPS LOCK, 핫 리로드, 다이얼로그, 업데이트 체크 |
| **[conventions.md](conventions.md)** | P1–P6 세부, silent catch 정책, .NET 10 호환 노트 |
