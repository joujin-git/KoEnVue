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
| **외부 패키지 제로** | .NET 10 BCL + Windows API 만 사용. NativeAOT 단일 exe (~4.9 MB) |
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

- **2단계 위치 기억**
  - **런타임**: hwnd 별 위치 (세션 내 창별 구분 — 여러 개의 메모장·크롬 창 각각 다른 위치 유지)
  - **영구**: 프로세스 이름별 위치 (`config.json → indicator_positions`)
  - 포그라운드 전환 시 조회 순서는 hwnd 런타임 → 프로세스명 config → 기본 위치

- **화면 밖 위치 방어**: 저장된 좌표(런타임·영구)는 반환 직전에 현재 살아있는 모니터의 작업 영역 안으로 클램프된다. 인디 중심점 기준 `MONITOR_DEFAULTTONEAREST` 로 모니터를 라우팅하므로 모니터 제거 후 해당 좌표는 잔존 모니터 중 가장 가까운 쪽으로 재매핑된다. 저장 값 자체는 덮어쓰지 않아서 원 모니터로 복귀 시 원 위치가 정확히 복원된다. 해상도/DPI 변경으로 화면 밖이 되는 경우도 같은 경로로 방어된다

- **기본 위치 (저장 안 된 앱)**: `config.json → default_indicator_position` 이 설정되어 있으면 해당 모서리(Corner) anchor + delta 를 포그라운드 앱이 있는 모니터의 작업 영역에 적용. 미설정 시 작업 영역 우상단 (`workArea.Right - 200, workArea.Top + 10`) 으로 폴백. 모서리 anchor 방식이라 멀티 모니터·해상도 변경에 안정적이다(절대 좌표가 아니므로 해상도가 달라져도 의도한 모서리 근처에 유지)

- **Shift 드래그 축 잠금**: 드래그 중 Shift 키를 누르면 시작 좌표 기준 우세한 축(가로/세로) 한 쪽으로만 이동하도록 제한. Shift 는 드래그 도중 언제든 누르거나 떼도 즉시 반영되며, 유지한 채 반대 방향으로 충분히 끌면 축이 뒤집힌다. 멀티 모니터·DPI 경계 교차는 풀린 축에서 정상 동작한다

- **창 엣지 자석 스냅**: `config.snap_to_windows` (기본 켜짐, 트레이 메뉴 "창에 자석처럼 붙이기" 로 토글). 드래그 중 다른 최상위 창의 시각 프레임 엣지와 현재 모니터 작업 영역 엣지에 임계값(기본 10 px, DPI 스케일) 이내로 접근하면 자석처럼 스냅. 드래그 시작 시 창 목록을 한 번 캐싱하며, 최소 크기 80 px 미만 창, 숨김·최소화 창, DWM cloaked 창(UWP 유령 창)은 후보에서 제외. 후보 rect 는 `DWMWA_EXTENDED_FRAME_BOUNDS` 로 얻은 시각 프레임을 사용해 비가시 리사이즈 테두리를 배제. Shift 축 잠금과 공존 — 잠긴 축은 스냅 대상에서 제외

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
| 슬라이드 | 이전 위치 → 새 위치 이동 (ease-out cubic) | 100 ms (기본 꺼짐) |
| 타이머 | `WM_TIMER` 기반, ~16 ms 프레임 (60 fps) | — |

트레이 메뉴 "애니메이션 사용" 으로 전체 on/off, "변경 시 강조" 로 IME 전환 강조만 on/off. 기본은 모두 켜짐. 페이드 지속 시간·하이라이트 배율·슬라이드 속도는 "상세 설정" 에서 조정.

---

## 4. 시스템 트레이

### 4.1 아이콘
- IME 상태별 색상 변경 (CaretDot 스타일 또는 Static)
- 툴팁: "KoEnVue - 한글 모드" (호버 시 표시). `NOTIFYICON_VERSION_4` 환경에서는 `NIF_SHOWTIP` 플래그를 함께 설정해야 쉘이 툴팁을 렌더링한다
- `config.tray_tooltip = false` 로 툴팁 숨김 가능

### 4.2 메뉴

```
새 버전 있음 (v0.9.0) — 다운로드       ← 업데이트 감지 시에만 조건부로 최상단 삽입
───
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

- **투명도·크기·자석 스냅·애니메이션·변경 시 강조**는 모두 드래그·렌더링·피드백 동작에 영향을 주는 항목이라 상단에 묶어 배치. 모두 토글형 체크 항목이며 자석 스냅·애니메이션·변경 시 강조는 기본 켜짐
- **상세 설정**은 구분선으로 묶어 종료 바로 위에 배치 — 트레이 메뉴로 노출되지 않는 나머지 필드를 편집하는 진입점
- **새 버전 있음**은 부팅 시 GitHub Releases 조회가 성공해 새 버전이 발견된 경우에만 메뉴 최상단에 조건부로 삽입된다. 라벨은 `I18n.FormatMenuUpdateAvailable(version)` (예: "새 버전 있음 (v0.9.0) — 다운로드")이며, 클릭하면 `ShellExecuteW("open", info.HtmlUrl, ...)` 로 기본 브라우저에서 릴리스 페이지를 연다. 자동 다운로드/설치가 아니라 사용자가 직접 새 exe 를 받아 교체하는 방식이다. `config.update_check_enabled` 로 끌 수 있다

### 4.3 시작 프로그램 등록
- `schtasks` 기반 등록/해제 (ONLOGON 트리거, HIGHEST 권한 — P5 requireAdministrator 와 일치)
- **exe 경로 자동 동기화**: 앱 시작 시 백그라운드 스레드로 등록된 schtasks 항목의 `<Command>` 경로를 조회해 현재 `Environment.ProcessPath` 와 비교. 다르면 현재 경로로 재등록(`/create /f`). exe 폴더를 옮겨도 다음 수동 실행부터는 복구된다(이사 직후 첫 자동 부팅은 구 경로를 찌르므로 한 번은 실패할 수 있음)

### 4.4 기본 위치 설정
- 저장 위치가 없는 앱을 열 때 인디케이터가 나타날 기본 위치를 사용자가 지정
- **현재 위치로 설정**: 현재 인디케이터 위치(`_lastX, _lastY`) 에서 가장 가까운 작업 영역 모서리를 맨해튼 거리로 자동 선정해 `Corner` anchor + delta 로 `config.default_indicator_position` 에 저장. 사용자는 모서리 개념을 의식할 필요 없음
- **초기화**: 필드를 null 로 되돌려 하드코딩 폴백(작업 영역 우상단)을 복원. 이미 null 이면 메뉴 항목 비활성화(grayed)
- 시스템 입력 프로세스(시작 메뉴·검색 창)에는 적용되지 않음 — 기존 규칙(창 시각적 좌상단 바로 위) 유지

### 4.5 미사용 위치 데이터 정리
- 현재 실행 중이 아닌 프로세스의 `indicator_positions` 항목을 체크박스 다이얼로그로 선택 삭제
- 전체 선택/해제 토글 지원
- 정리할 항목이 없으면 안내 메시지 표시
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
"상세 설정" 메뉴 클릭 시 스크롤 가능한 모달 대화상자가 열려, 트레이 메뉴로는 노출되지 않는 **59 개 설정 필드를 13 개 섹션**(① 표시 모드, ② 외관 — 크기·테두리, ③ 외관 — 색상·투명도, ④ 외관 — 텍스트, ⑤ 외관 — 테마, ⑥ 애니메이션, ⑦ 감지 및 숨김, ⑧ 앱별 프로필, ⑨ 핫키, ⑩ 트레이, ⑪ 시스템, ⑫ 다중 모니터, ⑬ 고급)으로 묶어 `설명 | 입력 상자` 2 컬럼 테이블 형태로 노출한다.

- 입력 컨트롤 종류: 불리언 = 체크박스, 정수·실수·문자열·색상 = EDIT, 열거형 = COMBOBOX
- 수직 스크롤바 + 마우스 휠 지원, 입력 상자 너비는 뷰포트 가용 폭에 맞춰 자동 축소되어 스크롤바에 가려지지 않음
- 검증 실패(숫자 형식·범위·색상 형식·빈 값) 시 오류 메시지 박스 → 해당 행으로 자동 스크롤 → 입력 상자 포커스 + 텍스트 전체 선택
- 모든 필드 검증 통과 후 "확인" 시 일괄 적용(config 한 번 저장), "취소" 는 변경 사항 폐기
- 룩앤필은 "미사용 위치 데이터 정리" / "직접 지정" 대화상자와 동일 (맑은 고딕 9 pt, 시스템 버튼 배경, DPI 스케일, 중첩 메시지 루프 모달, Tab/Enter/ESC 키 처리)
- **제외 항목**: 트레이 메뉴로 이미 조작 가능한 항목(투명도, 크기 배율, 기본 위치, 시작 프로그램 등록, 자석 스냅, 애니메이션 사용, 변경 시 강조, 위치 데이터, 트레이 활성화)과 구조가 복잡한 컬렉션 필드(앱 프로필 맵, 앱 필터 리스트, 시스템 숨김 클래스 목록, IME 폴백 체인), 내부 전용 필드(`config_version`, `overlay_class_name`), 그리고 부팅 시 1회만 동작하는 저빈도 토글(`update_check_enabled` — `config.json` 직접 편집으로만 on/off)

### 4.8 업데이트 알림

부팅 시 GitHub Releases API 를 1 회 조회해 새 버전이 있으면 트레이 메뉴 최상단에 "새 버전 있음 (v0.9.0) — 다운로드" 항목을 조건부로 삽입한다. 클릭하면 `ShellExecuteW` 로 기본 브라우저에서 릴리스 페이지를 연다. 자동 다운로드 / 자동 설치는 제공하지 않는다. 이 기능은 **v0.8.9.0** (2026-04-14, 첫 공개 릴리스) 에서 처음 배포되었다.

- **버전 소스**: `DefaultConfig.AppVersion` 상수 (수동 편집, 4-part `major.minor.build.revision`). `Assembly.GetName().Version` 사용 시 NativeAOT 트리밍 문제와 `.csproj` `<Version>` 동기화 번거로움이 있어 의도적으로 상수로 관리한다. 릴리스를 내릴 때 이 상수를 `KoEnVue.csproj` 의 `<Version>` 요소와 **반드시 함께** bump 해야 한다 — csproj 값은 PE 헤더의 `AssemblyVersion` / `FileVersion` / `InformationalVersion` 3종에 박히고, `AppVersion` 은 `UpdateChecker` 가 `tag_name` 과 비교하는 값이라, 둘 중 하나만 올리면 파일 속성과 런타임 비교 값이 불일치한다. 구체적 릴리스 순서는 [README.md](../README.md) → 릴리즈 (Releasing) 절 참고
- **레포 소스**: `DefaultConfig.UpdateRepoOwner` / `UpdateRepoName` 상수. 포크 배포자는 두 상수만 바꾸면 자신의 릴리스 채널을 가리키게 할 수 있다
- **조회 경로**: `https://api.github.com/repos/{owner}/{name}/releases/latest`. User-Agent 헤더 필수(누락 시 403), `Accept: application/vnd.github+json` 권장
- **HTTP 스택**: `Core/Http/HttpClientLite` → `Core/Native/WinHttp.cs` P/Invoke. `System.Net.Http.HttpClient` 는 NativeAOT 퍼블리시에 ~2.5 MB 를 추가하지만 WinHTTP 경로는 ~40 KB 로 끝나므로 (약 60× 차이) 트레이 앱 크기 예산(P1 정신)에 맞춰 선택
- **빈도**: 앱 시작 시 백그라운드 스레드 1회만 실행. 주기 폴링·재시도·레이트 리밋 관리 없음. 재확인은 앱 재시작으로 대체
- **silent 실패**: 네트워크 오류, HTTP 비-200, 빈 응답, JSON 파싱 실패, draft/prerelease skip, `current >= latest` 실패 — 모두 `Logger.Debug` 로만 기록. 사용자에게 팝업/배지 노출 없음
- **버전 비교**: `UpdateChecker.NormalizeVersion` 이 `ReadOnlySpan<char>` 로 앞의 `v`/`V` 접두어와 semver prerelease/build 접미어 (`-beta.1`, `+build.42`) 를 제거한 뒤 `System.Version.TryParse` 로 `N.N.N[.N]` 파싱. `IsNewer(current, latest)` 는 `latestV > currentV`. prerelease 정렬 (`1.0.0-alpha < 1.0.0`) 은 의도적으로 무시 — prerelease 태그는 `release.Prerelease || release.Draft` 체크에서 skip 되므로 알림 경로에 닿지 않는다
- **크로스 스레드 마샬링**: 백그라운드 스레드의 `onUpdateFound` 콜백 → `Program.OnUpdateCheckResult` 에서 `Program._pendingUpdate` (`private static volatile UpdateInfo?`) 에 쓰고 `User32.PostMessageW(hwndMain, WM_APP_UPDATE_FOUND, 0, 0)` 호출. 메인 스레드의 WndProc 가 메시지를 받아 `HandleUpdateFound` → `Tray.OnUpdateFound(info)` 를 호출. 감지 스레드와 동일한 `WM_APP+N` 패턴을 재사용해 크로스 스레드 신호 경로를 일관되게 유지
- **트레이 메뉴 삽입**: `Tray._pendingUpdate` (비 volatile — WM_APP_UPDATE_FOUND 처리 이후는 메인 스레드 단독 접근) 가 non-null 이면 `ShowMenu` 가 `IDM_UPDATE_DOWNLOAD = 4008` 으로 `MF_STRING` 항목 + `MF_SEPARATOR` 를 기존 투명도 서브 메뉴 위에 조건부 삽입. null 이면 평소와 동일한 메뉴
- **다른 알림 수단을 의도적으로 선택 안 함**: (a) balloon `NIIF_INFO` — 침해적, (b) Windows Toast — `AppUserModelID` + Start 메뉴 바로가기 필요해 포터블 단일 exe 배포 모델과 충돌, (c) 트레이 툴팁 접두 "⚡ Update available — ..." — 너무 은은해서 발견성 낮음. 트레이 메뉴 항목은 사용자가 우클릭으로 종료/설정을 다룰 때 자연스럽게 발견되면서도 침해적이지 않은 균형점
- **토글**: `config.update_check_enabled` (기본 `true`). Program 부팅 시 `if (_config.UpdateCheckEnabled)` 체크로 감싸진다. 트레이 메뉴·상세 설정 다이얼로그 어느 쪽에도 토글 항목이 없고(부팅 시 1회만 동작하는 저빈도 설정), `config.json` 을 직접 편집해야 on/off 가능

---

## 5. 핫키

| 핫키 | 기능 |
|------|------|
| `Ctrl+Alt+H` | 인디케이터 표시/숨기기 토글 |

단축키는 "상세 설정" 에서 변경 가능. 조합 수식키는 `Ctrl`/`Alt`/`Shift`/`Win` 중 1개 이상, 기본 키는 `A`~`Z` / `0`~`9` / `F1`~`F12` 지원.

---

## 6. 설정 (config.json)

### 6.1 파일 위치
- **exe 디렉토리의 `config.json`** — 완전 포터블 정책. exe 만 있으면 첫 실행 시 자동 생성된다
- APPDATA 등 외부 경로를 일체 사용하지 않는다. `app.manifest requireAdministrator` (P5) 덕분에 exe 폴더는 항상 쓰기 가능
- **첫 실행**: 파일이 없으면 기본 `AppConfig` 를 즉시 디스크에 저장해 사용자 눈에 바로 보이게 한다
- **파싱 실패**: 기본값을 메모리에서만 사용하고 디스크는 덮어쓰지 않는다(수동 복구 여지 보존). 동시에 손상된 파일의 mtime 을 캐시에 반영해 5 초 폴링이 `WM_CONFIG_CHANGED` 를 무한 재발송하지 않도록 한다
- **완전 삭제**: exe 폴더만 지우면 `config.json` + `koenvue.log` 가 함께 제거된다 (§ 10 참고)

### 6.2 주요 설정 카테고리

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
| 핫키 | `hotkeys_enabled`, `hotkey_toggle_visibility` |
| 트레이 | `tray_enabled`, `tray_icon_style`, `tray_tooltip`, `tray_click_action`, `tray_show_notification`, `tray_quick_opacity_presets` |
| 시스템 | `startup_with_windows`, `startup_minimized`, `single_instance`, `log_level`, `log_to_file`, `log_file_path`, `log_max_size_mb`, `language` |
| 업데이트 | `update_check_enabled` |
| 다중 모니터 | `per_monitor_scale`, `clamp_to_work_area` |
| 인디 위치 | `indicator_positions` (프로세스명별 영구), `default_indicator_position` (저장 없는 앱의 기본 위치: `{corner, delta_x, delta_y}`), `snap_to_windows` |
| 고급 | `advanced.force_topmost_interval_ms`, `advanced.ime_fallback_chain`, `advanced.overlay_class_name`, `advanced.prevent_sleep` |
| 버전 | `config_version` (스키마 버전, 마이그레이션용) |

### 6.3 테마 프리셋

`theme` 필드는 6 종 프리셋 중 선택한다. `custom` 외의 값은 `hangul_bg` / `hangul_fg` / `english_bg` / `english_fg` / `non_korean_bg` / `non_korean_fg` 를 덮어쓴다.

| 테마 | 설명 |
|------|------|
| `custom` | 사용자 색상 그대로 통과(기본값). `config.json` 의 색상 필드 그대로 적용 |
| `minimal` | 미니멀 — 무채색 계열 |
| `vivid` | 비비드 — 강한 원색 대비 (녹/적/청) |
| `pastel` | 파스텔 — 부드러운 연두/노랑/보라 |
| `dark` | 다크 — 진한 에메랄드/앰버/회색 |
| `system` | 시스템 강조색 기반 — Windows `COLOR_HIGHLIGHT` 에서 한글 배경을 뽑고 보색으로 영문 배경을 계산 |

### 6.4 핫 리로드
- 감지 스레드가 ~5 초마다 `config.json` mtime 체크
- 변경 감지 시 `WM_CONFIG_CHANGED` → 자동 리로드
- **삭제 안전**: mtime 체크 전에 `File.Exists` 가드를 통과해야 한다. 파일이 삭제된 상태에서는 `GetLastWriteTimeUtc` 가 `1601-01-01` 센티널을 반환해 "변경됨" 으로 오인되는데, 이를 통과시키면 `Load()` 가 기본값으로 리셋하고 다음 `Save()` 가 파일을 재생성하면서 사용자 설정을 덮어써 버린다. 에디터의 원자적 교체(`delete → rename`) 저장 방식과도 호환되어야 하므로 파일 잠금이 아닌 읽기 측 가드로 해결

---

## 7. 감지

### 7.1 IME 상태 감지
- `WM_IME_CONTROL` + `IMC_GETOPENSTATUS` / `IMC_GETCONVERSIONMODE`
- `GetKeyboardLayout` LANGID 확인 (비한국어 IME 판별)
- `WinEvent` 훅 (`EVENT_OBJECT_IME_CHANGE`) 보조

### 7.2 시스템 필터 (9 조건)
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

## 8. 아키텍처

### 8.1 2-스레드 모델

```
메인 스레드 (UI):    메시지 루프 + 렌더링 + 트레이 + 애니메이션 + CAPS LOCK 폴링 (200 ms)
감지 스레드 (BG):    80 ms IME 폴링 → PostMessageW
```

### 8.2 메시지 파이프라인

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
- 시스템 필터·핫키·트레이 토글 OFF 는 모두 "실제 사라짐" 을 의미하므로 `forceHidden: true` 로 Always 모드의 dim-idle 을 우회

### 8.3 Core / App 레이어 분리

소스 트리는 2 계층으로 나뉘며, 의존 방향은 단방향이다: `App/` 은 `Core/` 를 참조할 수 있지만 그 반대는 금지(P6). `git grep "KoEnVue\.App" Core/` = 0, `git grep "ImeState" Core/` = 0, `git grep "NonKoreanImeMode" Core/` = 0 으로 검증한다.

- **`Core/`** (`namespace KoEnVue.Core.*`) — 재사용 가능한 Win32 / .NET 인프라. 다른 Windows 데스크톱 프로젝트에 폴더 복사 또는 `<Compile Include>` 링크로 그대로 옮길 수 있도록 설계되었으며, KoEnVue 고유 심볼·`AppConfig`·IME enum·도메인 문자열을 일체 포함하지 않는다
- **`App/`** (`namespace KoEnVue.App.*`) — KoEnVue 특화 응용 계층. `Core` 모듈을 조합해 제품 동작을 정의
- **[Program.cs](../Program.cs) + [Program.Bootstrap.cs](../Program.Bootstrap.cs)** — `namespace KoEnVue` (루트). `Core` · `App` 양쪽을 조합하는 엔트리 포인트라 어느 계층에도 속하지 않음. `internal static partial class Program` 으로 두 파일에 분할: 메인 메시지 루프 / WndProc / 감지 스레드 / 이벤트 핸들러는 `Program.cs`, Mutex 가드 / 잔재 트레이 정리 / 윈도우 클래스 등록 / 핫키 파싱·등록·해제 / `OnProcessExit` 종료 시퀀스는 `Program.Bootstrap.cs` 에 보관

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
- **크기 예산**: Stage 6 업데이트 알림 추가 후 퍼블리시 exe 는 v0.8.9.0 기준 약 **4.94 MB** (Stage 2 기준 4,891,136 bytes 대비 +52 KB 내외). 같은 기능을 `System.Net.Http.HttpClient` 로 구현하면 NativeAOT 퍼블리시에 ~2.5 MB 가 추가되므로 WinHTTP P/Invoke 경로가 약 60 배 작다

---

## 9. 빌드

```bash
dotnet build                          # 디버그 빌드
dotnet publish -r win-x64 -c Release  # NativeAOT 릴리스 퍼블리시
```

- NativeAOT 단일 exe (~4.9 MB)
- .NET 런타임 설치 불필요
- `app.manifest` : UAC `requireAdministrator` (P5)
- **디버그·릴리스 둘 다 빌드 필수** — 디버그만 돌리면 릴리스 exe 가 낡은 상태로 남음

---

## 10. 완전 삭제

1. 트레이 메뉴에서 **시작 프로그램 등록** 해제 (이미 해제 상태면 생략)
2. KoEnVue 종료
3. exe 폴더 삭제 — `config.json` 과 `koenvue.log` 도 exe 옆에 있으므로 함께 제거된다

외부 레지스트리/APPDATA 변경이 전혀 없으므로 폴더 삭제로 흔적이 완전히 사라진다.

---

## 11. 관련 문서

| 문서 | 내용 |
|------|------|
| **[../CLAUDE.md](../CLAUDE.md)** | 프로젝트 진입점, 기술 스택, P1–P6, 빌드 |
| **[../README.md](../README.md)** | 다운로드, 빌드, 릴리즈 절차, `config.json` 키 |
| **[User_Guide.md](User_Guide.md)** | 최종 사용자 매뉴얼 |
| **[architecture.md](architecture.md)** | Core / App 모듈 상세, 재사용 계약, 파사드 패턴 |
| **[implementation-notes.md](implementation-notes.md)** | 렌더 파이프라인, 드래그/스냅, 애니메이션, CAPS LOCK, 핫 리로드, 다이얼로그, 업데이트 체크 |
| **[conventions.md](conventions.md)** | P1–P6 세부, silent catch 정책, .NET 10 호환 노트 |
