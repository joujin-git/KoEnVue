# KoEnVue — PRD

## 1. 개요

### 1.1 제품명
**KoEnVue** — Windows IME 한/영 상태 인디케이터

### 1.2 문제 정의
Windows에서 텍스트를 입력할 때, 현재 한글/영문 모드를 직관적으로 알기 어렵다. 기존 작업표시줄의 IME 표시기는 화면 우측 하단에 위치하여 타이핑 중 시선 이동이 크다.

### 1.3 솔루션
**드래그 가능한 플로팅 오버레이**로 한/영 상태를 표시하는 경량 유틸리티. 앱별로 위치를 기억하며, 포커스 진입 또는 한/영 전환 시 입력 모드를 즉시 확인할 수 있다.

### 1.4 대상 사용자
- Windows 한국어 사용자
- 텍스트 입력이 많은 사용자 (개발자, 문서 작업자)

### 1.5 핵심 설계 원칙

| 원칙 | 설명 |
|------|------|
| **외부 패키지 제로** | .NET 10 BCL + Windows API만 사용. NativeAOT 단일 exe (~4.7MB) |
| **한글 우선 표시** | 모든 UI 텍스트 한글 기본. 로그/config 키는 영문 |
| **최소 기능 집합** | 필요한 기능만 구현. 인디케이터 스타일/위치 선택 없음 |

---

## 2. 인디케이터

### 2.1 표시 형태
- **텍스트 라벨**: "한" (한글), "En" (영문), "EN" (비한국어 IME)
- **도형**: RoundedRect (고정, 설정 불가)
- **색상**: config.json으로 배경/전경 색상 지정 가능

### 2.2 위치
- **드래그 가능한 플로팅 윈도우**: 마우스로 원하는 위치에 배치
- **2단계 위치 기억**:
  - 런타임: hwnd별 위치 (세션 내 창별 구분 — 메모장/크롬 여러 창 각각 다른 위치)
  - 영구: 프로세스 이름별 위치 (`config.json` → `indicator_positions`)
- 포그라운드 전환 시 조회 순서: hwnd 런타임 → 프로세스명 config → 기본 위치
- **화면 밖 위치 방어**: 저장된 좌표(런타임·영구)는 반환 직전에 현재 살아있는 모니터의 작업 영역 안으로 클램프된다. 인디 중심점 기준 `MONITOR_DEFAULTTONEAREST`로 모니터를 라우팅하므로 모니터 제거 후 해당 좌표는 잔존 모니터 중 가장 가까운 쪽으로 재매핑된다. 저장 값 자체는 덮어쓰지 않아서 원 모니터로 복귀 시 원 위치가 정확히 복원된다. 해상도/DPI 변경 후 화면 밖이 되는 경우도 같은 경로로 방어
- **기본 위치 (저장 안 된 앱)**: `config.json` → `default_indicator_position`이 설정되어 있으면 해당 모서리(Corner) anchor + delta를 포그라운드 앱이 있는 모니터의 작업 영역에 적용. 미설정 시 작업 영역 우상단(`workArea.Right - 200, workArea.Top + 10`) 폴백. 모서리 anchor 방식이라 멀티 모니터·해상도 변경에 안정적(절대 좌표가 아니므로 해상도가 달라져도 의도한 모서리 근처에 유지)
- 드래그로 모니터 간 이동 시 `WM_MOVING`에서 DPI 실시간 재계산
- **Shift 드래그 축 잠금**: 드래그 중 Shift 키를 누르면 시작 좌표 기준 우세한 축(가로/세로) 한 쪽으로만 이동하도록 제한. Shift는 드래그 도중 언제든 누르거나 떼도 즉시 반영되며, 유지한 채 반대 방향으로 충분히 끌면 축이 뒤집힘. 멀티 모니터·DPI 경계 교차는 풀린 축 쪽에서 정상 동작
- **창 엣지 자석 스냅**: `config.snap_to_windows` (기본 켜짐, 트레이 메뉴 "창에 자석처럼 붙이기"로 토글). 드래그 중 다른 최상위 창의 시각 프레임 엣지와 현재 모니터 작업 영역 엣지에 임계값(기본 10px, DPI 스케일링) 이내로 접근하면 자석처럼 스냅. 드래그 시작 시 창 목록을 한 번 캐싱하며, 최소 크기(80px) 미만 창, 숨김·최소화 창, DWM cloaked 창(UWP 유령 창)은 후보에서 제외. 후보 rect는 `DWMWA_EXTENDED_FRAME_BOUNDS`로 얻은 시각 프레임을 사용해 비가시 리사이즈 테두리를 배제. Shift 축 잠금과 공존 — 잠긴 축은 스냅 대상에서 제외
- **시스템 입력 창 예외** (시작 메뉴, 작업 표시줄 검색): TOPMOST 창도 z-밴드 한계로 이들 위에 뜰 수 없어 드래그 후 가려지면 복구 불가. 드래그해도 위치를 저장하지 않으며, 기본 위치를 창의 시각적 왼쪽 위 모서리 바로 위(`frame.Left`, `frame.Top - labelH - 4px`)로 고정해 항상 보이도록 한다. 시각 프레임은 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`로 얻어 `GetWindowRect`의 비가시 리사이즈 테두리를 배제한다. Win11은 시작 메뉴와 검색 창이 하나의 HWND를 공유하고 rect만 바뀌므로, 감지 스레드가 시각 프레임의 변화를 포그라운드 전환과 동일하게 취급해 인디 위치를 재계산

### 2.3 표시 모드 (config)
- **Always** (기본): 항상 표시, 유휴 시 반투명
- **OnEvent**: 포커스/IME 변경 시 일정 시간 표시 후 페이드아웃

### 2.4 숨김 조건
- 바탕화면 / 작업표시줄 / 잠금 화면 (SystemFilter)
- 전체화면 앱
- 비한국어 IME + NonKoreanIme=Hide 설정
- 포커스 없는 창 (config: `hide_when_no_focus`)

### 2.5 크기 배율
- **범위**: 1.0배 ~ 5.0배, 소수점 첫째 자리 (0.1 단위)
- **적용 대상**: `label_width`, `label_height`, `font_size`, `label_border_radius`, `border_width`, 라벨 좌우 패딩
- **DPI와 관계**: DPI 스케일링과 독립적(선-곱). 예: 2배 × 150% DPI = 3배 픽셀
- **트레이에서 설정**: "크기 ▸" 서브메뉴에 1배~5배 정수 프리셋 5개와 "직접 지정" 항목. 정수 배율이면 해당 프리셋에 라디오 체크, 비정수(예: 2.3배)이면 "직접 지정 (2.3배)"로 라벨이 동적으로 바뀌고 라디오 체크가 이쪽으로 이동
- **직접 지정 대화상자**: 마우스 커서 위치에 모달 창으로 띄우고, 현재 배율을 EDIT에 미리 채움. 1.0 미만/5.0 초과·숫자 아님은 오류 메시지박스 후 재입력. Enter=확인, ESC=취소
- 배율 변경은 즉시 렌더링에 반영되고 `config.indicator_scale`에 저장

---

## 3. 애니메이션

- **페이드인/아웃**: 설정 가능한 지속 시간 (기본 150ms/400ms)
- **IME 전환 강조**: 1.3x 스케일 → 원래 크기 (300ms)
- **슬라이드**: 이전 위치 → 새 위치 이동 (ease-out cubic)
- **타이머**: WM_TIMER 기반, ~16ms 프레임 (60fps)

---

## 4. 시스템 트레이

### 4.1 아이콘
- IME 상태별 색상 변경 (CaretDot 스타일 또는 Static)
- 툴팁: "KoEnVue - 한글 모드" (호버 시 표시. `NOTIFYICON_VERSION_4` 환경에서는 `NIF_SHOWTIP` 플래그를 함께 설정해야 쉘이 툴팁을 렌더링함)
- `config.tray_tooltip = false` 설정 시 툴팁 숨김

### 4.2 메뉴
```
투명도 ▸ (진하게 / 보통 / 연하게)
크기 ▸ (1배 / 2배 / 3배 / 4배 / 5배 / 직접 지정)
☑ 창에 자석처럼 붙이기
☑ 애니메이션 사용
☑ 변경 시 강조
───
☑ 시작 프로그램 등록
───
기본 위치 ▸ (현재 위치로 설정 / 초기화)
미사용 위치 데이터 정리
───
상세 설정
───
종료
```

- **투명도·크기·자석 스냅·애니메이션·변경 시 강조**는 모두 드래그·렌더링·피드백 동작에 영향을 주는 항목이라 상단에 묶어 배치. 모두 토글형 체크 항목이며 자석 스냅·애니메이션·변경 시 강조는 기본은 켜짐
- **상세 설정**은 구분선으로 묶어 종료 바로 위에 배치 — 트레이 메뉴로 노출되지 않는 전체 설정 필드를 편집하는 진입점

### 4.3 시작 프로그램
- `schtasks` 기반 등록/해제 (ONLOGON, HIGHEST 권한)
- **exe 경로 자동 동기화**: 앱 시작 시 백그라운드 스레드로 등록된 schtasks 항목의 `<Command>` 경로를 조회해 현재 `Environment.ProcessPath`와 비교. 다르면 현재 경로로 재등록(`/create /f`). exe 폴더를 옮겨도 다음 수동 실행부터는 복구됨(이사 직후 첫 자동 부팅은 구 경로를 찌르므로 한 번은 실패할 수 있음)

### 4.4 기본 위치 설정
- 저장 위치가 없는 앱을 열 때 인디케이터가 나타날 기본 위치를 사용자가 지정
- **현재 위치로 설정**: 현재 인디케이터 위치(`_lastX, _lastY`)에서 가장 가까운 작업 영역 모서리를 맨해튼 거리로 자동 선정하여 Corner anchor + delta로 `config.default_indicator_position`에 저장. 사용자는 모서리 개념을 의식할 필요 없음
- **초기화**: 필드를 null로 되돌려 하드코딩 폴백(작업 영역 우상단) 복원. 이미 null이면 메뉴 항목 비활성화(grayed)
- 시스템 입력 프로세스(시작 메뉴·검색 창)에는 적용되지 않음 — 기존 규칙(창 시각적 좌상단 바로 위) 유지

### 4.5 미사용 위치 데이터 정리
- 현재 실행 중이 아닌 프로세스의 `indicator_positions` 항목을 체크박스 다이얼로그로 선택 삭제
- 전체 선택/해제 토글 지원
- 정리할 항목 없으면 안내 메시지 표시
- DPI 스케일 대응, 시스템 폰트(맑은 고딕) 적용, 설명 라벨 + 구분선 포함
- Tab 키로 체크박스/버튼 사이 포커스 순환, ESC 키로 취소 (설정 대화상자·크기 배율 대화상자와 동일한 키 동작)

### 4.6 크기 배율 직접 지정 대화상자
- "크기 ▸ 직접 지정" 메뉴 클릭 시 마우스 커서 위치에 모달 다이얼로그 생성 (작업 영역 밖으로 나가지 않도록 클램프)
- 안내 라벨("배율 (1.0 ~ 5.0):") + EDIT(현재 배율로 미리 채움, `0.#` 포맷 — 2.0 → "2", 2.3 → "2.3") + 힌트 라벨 + 확인/취소 버튼
- 확인 버튼은 기본 버튼(`BS_DEFPUSHBUTTON`)이라 Enter 키로 트리거, ESC는 취소로 매핑
- 검증 실패(숫자 아님 / 범위 밖) 시 오류 메시지박스 → EDIT 재포커스 + 텍스트 전체 선택(재입력 편의)
- 파싱은 `InvariantCulture` 기준이라 OS 로캘과 무관하게 `.` 소수점 수용
- DPI 스케일 적용, 시스템 폰트(맑은 고딕), 메인 창 `EnableWindow(false)` + 중첩 메시지 루프로 모달 동작

### 4.7 상세 설정 대화상자
- "상세 설정" 메뉴 클릭 시 스크롤 가능한 모달 대화상자가 열려, 트레이 메뉴로는 노출되지 않는 59개 설정 필드를 13개 섹션(표시 모드, 외관 — 크기·테두리·색상·투명도·텍스트·테마, 애니메이션, 감지 및 숨김, 앱별 프로필, 핫키, 트레이, 시스템, 다중 모니터, 고급)으로 묶어 테이블(`설명 | 입력 상자`) 형태로 노출
- 입력 컨트롤 종류: 불리언=체크박스, 정수·실수·문자열·색상=EDIT, 열거형=COMBOBOX
- 수직 스크롤바 + 마우스 휠 지원, 입력 상자 너비는 뷰포트 가용 폭에 맞춰 자동 축소되어 스크롤바에 가려지지 않음
- 검증 실패(숫자 형식/범위/색상 형식/빈 값) 시 오류 메시지박스 → 해당 행으로 자동 스크롤 → 입력 상자 포커스 + 텍스트 전체 선택(재입력 편의)
- 모든 필드 검증 통과 후 "확인" 시 일괄 적용(config 한 번 저장), "취소"는 변경 사항 폐기
- 룩앤필은 "미사용 위치 데이터 정리" / "직접 지정" 대화상자와 동일(맑은 고딕 9pt, 시스템 버튼 배경, DPI 스케일, 중첩 메시지 루프 모달, Tab/Enter/ESC 키 처리)
- 제외 항목: 트레이 메뉴로 이미 조작 가능한 항목(투명도, 크기 배율, 기본 위치, 시작 프로그램 등록, 자석 스냅, 애니메이션 사용, 변경 시 강조, 위치 데이터, 트레이 활성화)과 구조가 복잡한 컬렉션 필드(앱 프로필 맵, 앱 필터 리스트, 시스템 숨김 클래스 목록, IME 폴백 체인), 내부 전용 필드(`config_version`, `overlay_class_name`)

---

## 5. 핫키

| 핫키 | 기능 |
|------|------|
| `Ctrl+Alt+H` | 인디케이터 표시/숨기기 토글 |

---

## 6. 설정 (config.json)

### 6.1 파일 위치
- exe 디렉토리의 `config.json` (완전 포터블 — exe만 있으면 첫 실행 시 자동 생성)
- APPDATA 등 외부 경로를 일체 사용하지 않는다. `app.manifest requireAdministrator` 덕분에 exe 폴더는 항상 쓰기 가능
- **첫 실행**: 파일이 없으면 기본 `AppConfig`를 즉시 디스크에 저장해 사용자 눈에 바로 보이게 한다
- **파싱 실패**: 기본값을 메모리에서만 사용하고 디스크는 덮어쓰지 않는다(수동 복구 여지 보존). 동시에 손상된 파일의 mtime을 캐시에 반영해 5초 폴링이 `WM_CONFIG_CHANGED`를 무한 재발송하지 않도록 한다
- **완전 삭제**: exe 폴더만 지우면 config.json과 koenvue.log가 함께 제거된다

### 6.2 주요 설정 카테고리

| 카테고리 | 주요 키 |
|----------|---------|
| 표시 모드 | `display_mode`, `event_display_duration_ms` |
| 외관 | `label_width`, `label_height`, `label_border_radius`, `font_family`, `font_size`, `indicator_scale` |
| 색상 | `hangul_bg`, `hangul_fg`, `english_bg`, `english_fg`, `opacity` |
| 애니메이션 | `animation_enabled`, `fade_in_ms`, `fade_out_ms`, `slide_animation` |
| 감지 | `poll_interval_ms`, `detection_method` |
| 시스템 | `start_with_windows`, `language`, `log_level` |
| 테마 | `theme` (6종 프리셋 + Custom) |
| 앱별 프로필 | `app_profiles`, `app_filter_mode`, `app_filter_list` |
| 인디 위치 | `indicator_positions` (프로세스명별 영구 저장), `default_indicator_position` (저장 없는 앱의 기본 위치: `{corner, delta_x, delta_y}`), `snap_to_windows` (드래그 중 창 엣지 자석 스냅 on/off) |

### 6.3 핫 리로드
- 감지 스레드가 ~5초마다 config.json mtime 체크
- 변경 감지 시 WM_CONFIG_CHANGED → 자동 리로드
- **삭제 안전**: mtime 체크 전에 `File.Exists` 가드를 통과해야 함. 파일이 삭제된 상태에서는 `GetLastWriteTimeUtc`가 1601-01-01 센티널을 반환해 "변경됨"으로 오인되는데, 이를 통과시키면 `Load()`가 기본값으로 리셋하고 다음 `Save()`가 파일을 재생성하면서 사용자 설정을 덮어써 버린다. 에디터의 원자적 교체(delete → rename) 저장 방식과도 호환되어야 하므로 파일 잠금이 아닌 읽기 측 가드로 해결

---

## 7. 감지

### 7.1 IME 상태 감지
- `WM_IME_CONTROL` + `IMC_GETOPENSTATUS` / `IMC_GETCONVERSIONMODE`
- `GetKeyboardLayout` LANGID 확인 (비한국어 IME 판별)
- WinEvent 훅 (`EVENT_OBJECT_IME_CHANGE`) 보조

### 7.2 시스템 필터 (7조건)
1. 보안 데스크톱 (hwnd 없음)
2. 비가시/최소화 창
3. 다른 가상 데스크톱
4. 클래스명 블랙리스트 (Progman, WorkerW, Shell_TrayWnd 등 + 사용자 지정)
5. 포커스 없음 (`hide_when_no_focus`)
6. 전체화면 독점 (모니터 커버 + WS_CAPTION 없음)
7. 앱별 블랙리스트/화이트리스트

---

## 8. 아키텍처

### 8.1 2-스레드 모델

```
메인 스레드 (UI):    메시지 루프 + 렌더링 + 트레이 + 애니메이션
감지 스레드 (BG):    80ms 폴링 → PostMessage
```

### 8.2 메시지 파이프라인

```
감지 스레드:
  1. 매 폴링마다 ResolveForApp + SystemFilter.ShouldHide 평가
     - 필터 진입 시(이전 미필터 → 현재 필터): WM_HIDE_INDICATOR
     - 필터 해소 또는 포그라운드 변경: WM_POSITION_UPDATED(hwndForeground)
  2. IME 상태 변경 → WM_IME_STATE_CHANGED(ImeState)
  3. 포커스 변경 → WM_FOCUS_CHANGED(hwndFocus)

메인 스레드:
  WM_POSITION_UPDATED → 포그라운드 변경 또는 이전 숨김 상태였다면 앱별 위치 조회 + TriggerShow
  WM_IME_STATE_CHANGED → 트레이 갱신 + TriggerShow
  WM_FOCUS_CHANGED → TriggerShow
  WM_HIDE_INDICATOR → Animation.TriggerHide(forceHidden: true) — Always 모드에서도 완전 숨김
  WM_MOVING → Shift 축 잠금(`HandleMoving`) + 드래그 중 모니터 변경 시 DPI 재계산
```

- 필터 평가를 매 폴링으로 단순화한 이유: "바탕화면 클릭 → 같은 앱 복귀" 시 `hwndForeground`는 동일하지만 인디케이터는 다시 나타나야 한다. `lastFiltered` 플래그로 중복 메시지는 억제
- 시스템 필터·핫키·트레이 토글 OFF는 모두 "실제 사라짐"을 의미하므로 `forceHidden: true`로 Always 모드의 dim-idle을 우회

### 8.3 Core / App 레이어 분리

소스 트리는 2계층으로 나뉘며, 의존 방향은 단방향이다: `App/`은 `Core/`를 참조할 수 있지만 그 반대는 금지된다(P6). `git grep "KoEnVue\.App" Core/` = 0, `git grep "ImeState" Core/` = 0으로 검증한다.

- **`Core/`** (`namespace KoEnVue.Core.*`): KoEnVue 외에도 재사용 가능한 Win32/.NET 인프라
  - `Core/Native/` — P/Invoke (User32, Imm32, Shell32, Gdi32, Kernel32, Shcore, Ole32, OleAut32, Dwmapi, VirtualDesktop), `Win32Types.cs`, `SafeGdiHandles`
  - `Core/Color/` — `ColorHelper` (Hex ↔ COLORREF ↔ RGB, `TryNormalizeHex`)
  - `Core/Dpi/` — `DpiHelper` (스케일/작업 영역/모니터 조회)
  - `Core/Logging/` — `Logger` (비동기 큐/드레인/회전)
  - `Core/Config/` — `JsonSettingsManager<T>` (5단계 파이프라인 제네릭 로드·저장·핫리로드), `JsonSettingsFile` (BOM 안전 I/O)
  - `Core/Animation/` — `OverlayAnimator` (5상태 머신 + 하이라이트/슬라이드 서브페이즈), `AnimationConfig` 17필드 레코드 구조체
  - `Core/Tray/` — `NotifyIconManager` (`Shell_NotifyIconW` 래퍼, `NIF_SHOWTIP` 유지)
  - `Core/Windowing/` — `LayeredOverlayBase` (레이어드 창 + DIB + DPI + 드래그/스냅 엔진), `OverlayStyle`/`OverlayMetrics` 레코드 구조체, `ModalDialogLoop.Run`, `Win32DialogHelper`, `WindowProcessInfo`

- **`App/`** (`namespace KoEnVue.App.*`): KoEnVue 특화 응용 계층. Core 모듈을 조합해 제품 동작을 정의
  - `App/Models/` — `AppConfig` 레코드 + 모든 enum (`DisplayMode`, `DetectionMethod`, `ImeState`, `FontWeight`, `Theme`, `NonKoreanImeMode` 등)
  - `App/Config/` — `Settings` 정적 파사드, `DefaultConfig`, `ThemePresets`, `AppSettingsManager : JsonSettingsManager<AppConfig>` (5개 훅 오버라이드)
  - `App/Detector/` — `ImeStatus`, `SystemFilter`
  - `App/Localization/` — `I18n`
  - `App/UI/` — `Overlay` (정적 파사드 + `LayeredOverlayBase` 엔진 + `BuildStyle` ImeState→OverlayStyle 변환 유일 지점), `Animation` (정적 파사드 + `OverlayAnimator` 엔진 + `AppConfig`→`AnimationConfig` 변환), `Tray` (`NotifyIconManager` 사용), `TrayIcon`, 세 다이얼로그 (모두 `ModalDialogLoop.Run` 사용)

- **`Program.cs`**: 네임스페이스 `KoEnVue` (루트). Core·App 양쪽을 조합하는 엔트리 포인트라 어느 계층에도 속하지 않음

#### 레이어 분리 핵심 원칙

- **Enum 경계 차단**: `ImeState`/`DisplayMode`/`NonKoreanImeMode`/`FontWeight`는 App 전용 enum이며 Core에 유출되면 안 된다. 대신 Core 인터페이스는 primitive로 표현한다:
  - `OverlayStyle`은 `LabelText : string`과 `MeasureLabels : (string, string, string)` 튜플만 받으며, 현재 상태의 레이블은 파사드의 `Overlay.BuildStyle` 안에서 `ImeState`를 해석해 한 번만 주입된다
  - `OverlayStyle.IsBold : bool`이 `FontWeight.Bold`를 대신하고, `AnimationConfig.AlwaysMode : bool`이 `DisplayMode.Always`를 대신한다
  - `OverlayAnimator.SetDimMode(bool)`이 `NonKoreanImeMode.Dim` 체크를 추상화한다
- **DPI 소유권**: Core 엔진이 DPI 곱셈을 전담한다. 파사드는 `IndicatorScale`만 먼저 곱해 `*LogicalPx` 필드로 전달하고, 엔진은 내부에서 `Kernel32.MulDiv(fontSize, dpiY, 72)`로 정밀하게 산출 (`Math.Round` 대체는 일부 DPI 비율에서 레이블 폭 0~1px 회귀가 있어 기각)
- **플립-플롭 가드**: `OverlayStyle`이 `record struct`이므로 값 동등성으로 자동 비교되며, `UpdateStyle(newStyle)`은 `newStyle == _lastStyle`이면 재렌더를 스킵한다. `MeasureLabels`에 세 상태 레이블을 모두 넣는 것도 `_fixedLabelWidth`를 상태 전환 시 churn 없이 max에 고정하기 위함
- **엔진 인스턴스 + 정적 파사드 조합**: `Overlay`/`Animation`/`Tray` 파사드는 `private static` 필드에 Core 엔진 인스턴스를 보관하고 기존 정적 API에서 엔진 메서드로 위임한다. 결과적으로 `Program.cs`·다이얼로그·Tray·Animation 서로 간의 호출 지점은 Stage 4 전후로 **바이트 동일**하며, 재구성의 영향은 파사드 내부에 국한된다
- **`JsonSettingsManager<T>`의 `JsonTypeInfo<T>` 주입**: `JsonSerializerIsReflectionEnabledByDefault=false` 환경에서는 STJ 소스 생성기 컨텍스트만이 유효하다. `AppSettingsManager`는 생성자에 `AppConfigJsonContext.Default.AppConfig`를 넘겨 NativeAOT 트리밍을 통과시킨다
- **크기 예산**: Core 추출 후 퍼블리시 exe = **4,911,104 bytes** (Stage 2 기준 4,891,136 bytes 대비 +19,968 bytes, +19.5 KB — 게이트 +100 KB 이내). ILC 트리밍이 엔진 인스턴스 패턴·델리게이트 콜백·제네릭 `JsonTypeInfo<T>` 주입을 모두 수용한다

#### 재사용 대상 / 비대상 구분

`Core/`는 다른 Windows 데스크톱 프로젝트에 폴더 복사 또는 `<Compile Include>` 링크로 그대로 옮길 수 있도록 설계되었으며, KoEnVue 고유 심볼·`AppConfig`·IME enum·도메인 문자열을 일체 포함하지 않는다. 반면 `App/`은 IME 인디케이터 제품 고유 로직이라 재사용 대상이 아니다.

**재사용 대상 (`Core/`)** — 다른 프로젝트에서 그대로 가져다 쓸 수 있는 인프라

| 모듈 | 한 줄 설명 |
|------|-----------|
| `Core/Native/*` | P/Invoke 파운데이션 + Win32 structs/constants(`Win32Types.cs`) + `SafeGdiHandles` |
| `Core/Color/ColorHelper` | Hex ↔ COLORREF ↔ RGB 변환 + `TryNormalizeHex` |
| `Core/Dpi/DpiHelper` | 모니터 DPI 조회 + 작업 영역 + 스케일 계산 (`BASE_DPI = 96` 인라인 — Config 의존 없음) |
| `Core/Logging/Logger` + `LogLevel` | 비동기 큐/드레인/회전 파일 로거. `Initialize(bool, string?, int)` (primitive 시그니처) |
| `Core/Config/JsonSettingsManager<T>` + `JsonSettingsFile` | 제네릭 JSON 설정 파이프라인 (5단계 훅) + BOM 안전 I/O 헬퍼. NativeAOT를 위한 `JsonTypeInfo<T>` 주입 필수 |
| `Core/Animation/OverlayAnimator` + `AnimationConfig` + `AnimationTimerIds` | 5상태 머신 + 하이라이트/슬라이드 서브페이즈. 6개 콜백 델리게이트 주입, 타이머 ID 외부화 |
| `Core/Tray/NotifyIconManager` | `Shell_NotifyIconW` 래퍼 (`NIF_SHOWTIP` 유지). hIcon 소유권은 호출자 보유 |
| `Core/Windowing/LayeredOverlayBase` + `OverlayStyle`/`OverlayMetrics` | 레이어드 창 + DIB + DPI + 드래그/스냅 엔진. primitive 전용 record struct 경계 |
| `Core/Windowing/ModalDialogLoop` | `IsDialogMessageW` + `EnableWindow` 모달 메시지 루프 헬퍼 (`Run` 단일 진입점) |
| `Core/Windowing/Win32DialogHelper` | DPI 대응 다이얼로그 non-client 메트릭 + 9pt 시스템 폰트 헬퍼 (`ApplyFont`) + `CreateDialogFont(dpiY)` (맑은 고딕 9pt SafeFontHandle 생성) + `CalculateDialogPosition(hMonitor, w, h, anchor?)` (rcWork 센터/커서 앵커 + 경계 클램프) |
| `Core/Windowing/WindowProcessInfo` | HWND → 클래스명/프로세스명 조회 |

**비재사용 (`App/`) — KoEnVue 전용** — 제품 고유 IME 로직이라 재사용 대상이 아님

| 모듈 | 한 줄 설명 |
|------|-----------|
| `App/Models/*` | `AppConfig` 레코드 + 모든 enum (`ImeState`, `DisplayMode`, `NonKoreanImeMode`, `FontWeight`, `Theme`, `Corner`, `DetectionMethod`, `AppFilterMode`, `AppProfileMatch`, `TrayIconStyle`, `TrayClickAction`) |
| `App/Config/DefaultConfig` + `ThemePresets` | KoEnVue 기본값 + 6종 색상 테마 프리셋 |
| `App/Config/Settings` + `AppSettingsManager` | `JsonSettingsManager<AppConfig>` 5개 훅 오버라이드 + 정적 파사드 |
| `App/Detector/ImeStatus` | IME 상태 감지 (`WM_IME_CONTROL` + `WinEvent` 훅) |
| `App/Detector/SystemFilter` | 7조건 숨김 로직 (보안 데스크톱·가상 데스크톱·전체화면·블랙리스트 등) |
| `App/Localization/I18n` | 한/영 UI 텍스트 라우팅 (`GetUserDefaultUILanguage`) |
| `App/UI/Overlay`, `App/UI/Animation`, `App/UI/Tray` | Core 엔진을 소비하는 정적 파사드 (enum → primitive 변환 담당) |
| `App/UI/TrayIcon` | GDI 기반 트레이 아이콘 비트맵 생성 |
| `App/UI/AppMessages` | `WM_APP+N` 앱 전용 메시지 상수 |
| `App/UI/Dialogs/{CleanupDialog, ScaleInputDialog, SettingsDialog}` | 제품별 대화상자 — 모두 `Core/Windowing/ModalDialogLoop.Run` 사용 |

다른 프로젝트로 옮기려면 `Core/` 폴더를 통째로 복사하거나, 형제 `.csproj`에서 `<Compile Include="..\KoEnVue\Core\**\*.cs" Link="Core\%(RecursiveDir)%(Filename)%(Extension)" />`로 링크한다. 통합 후 `git grep "KoEnVue\.App" Core/`, `git grep "ImeState" Core/`, `git grep "NonKoreanImeMode" Core/` 세 검증 명령은 모두 0건이어야 한다.

---

## 9. 빌드

```bash
dotnet build                          # 디버그 빌드
dotnet publish -r win-x64 -c Release  # NativeAOT 릴리스 퍼블리시
```

- NativeAOT 단일 exe (~4.7MB)
- .NET 런타임 설치 불필요
- `app.manifest`: UAC requireAdministrator

---

## 10. 완전 삭제

1. 트레이 메뉴에서 "시작 프로그램 등록" 해제 (이미 해제 상태면 생략)
2. KoEnVue 종료
3. exe 폴더 삭제 (config.json + koenvue.log가 exe 옆에 있음)
