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
| **외부 패키지 제로** | .NET 10 BCL + Windows API만 사용. NativeAOT 단일 exe (~4.4MB) |
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
- 기본 위치: 포그라운드 앱이 있는 모니터의 작업 영역 우상단 (멀티 모니터 대응)
- 드래그로 모니터 간 이동 시 `WM_MOVING`에서 DPI 실시간 재계산

### 2.3 표시 모드 (config)
- **Always** (기본): 항상 표시, 유휴 시 반투명
- **OnEvent**: 포커스/IME 변경 시 일정 시간 표시 후 페이드아웃

### 2.4 숨김 조건
- 바탕화면 / 작업표시줄 / 잠금 화면 (SystemFilter)
- 전체화면 앱
- 비한국어 IME + NonKoreanIme=Hide 설정
- 포커스 없는 창 (config: `hide_when_no_focus`)

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
- 툴팁: "KoEnVue [포터블/설치] - 한글 모드"

### 4.2 메뉴
```
투명도 ▸ (진하게 / 보통 / 연하게)
───
☑ 시작 프로그램 등록
───
미사용 위치 데이터 정리
───
종료
```

### 4.4 미사용 위치 데이터 정리
- 현재 실행 중이 아닌 프로세스의 `indicator_positions` 항목을 체크박스 다이얼로그로 선택 삭제
- 전체 선택/해제 토글 지원
- 정리할 항목 없으면 안내 메시지 표시
- DPI 스케일 대응, 시스템 폰트(맑은 고딕) 적용, 설명 라벨 + 구분선 포함

### 4.3 시작 프로그램
- `schtasks` 기반 등록/해제 (ONLOGON, HIGHEST 권한)

---

## 5. 핫키

| 핫키 | 기능 |
|------|------|
| `Ctrl+Alt+H` | 인디케이터 표시/숨기기 토글 |

---

## 6. 설정 (config.json)

### 6.1 파일 탐색 순서
1. exe 디렉토리 (포터블 모드)
2. `%APPDATA%\KoEnVue\` (설치 모드)
3. 기본값 사용

### 6.2 주요 설정 카테고리

| 카테고리 | 주요 키 |
|----------|---------|
| 표시 모드 | `display_mode`, `event_display_duration_ms` |
| 외관 | `label_width`, `label_height`, `label_border_radius`, `font_family`, `font_size` |
| 색상 | `hangul_bg`, `hangul_fg`, `english_bg`, `english_fg`, `opacity` |
| 애니메이션 | `animation_enabled`, `fade_in_ms`, `fade_out_ms`, `slide_animation` |
| 감지 | `poll_interval_ms`, `detection_method` |
| 시스템 | `start_with_windows`, `language`, `log_level` |
| 테마 | `theme` (6종 프리셋 + Custom) |
| 앱별 프로필 | `app_profiles`, `app_filter_mode`, `app_filter_list` |

### 6.3 핫 리로드
- 감지 스레드가 ~5초마다 config.json mtime 체크
- 변경 감지 시 WM_CONFIG_CHANGED → 자동 리로드

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
  1. 포그라운드 변경 또는 윈도우 이동 → WM_POSITION_UPDATED(hwndForeground)
  2. IME 상태 변경 → WM_IME_STATE_CHANGED(ImeState)
  3. 포커스 변경 → WM_FOCUS_CHANGED(hwndFocus)

메인 스레드:
  WM_POSITION_UPDATED → 포그라운드 변경 시 앱별 위치 조회 + TriggerShow
  WM_IME_STATE_CHANGED → 트레이 갱신 + TriggerShow
  WM_FOCUS_CHANGED → TriggerShow
  WM_MOVING → 드래그 중 모니터 변경 시 DPI 재계산
```

---

## 9. 빌드

```bash
dotnet build                          # 디버그 빌드
dotnet publish -r win-x64 -c Release  # NativeAOT 릴리스 퍼블리시
```

- NativeAOT 단일 exe (~4.4MB)
- .NET 런타임 설치 불필요
- `app.manifest`: UAC requireAdministrator

---

## 10. 완전 삭제

1. 트레이 메뉴에서 "시작 프로그램 등록" 해제 (이미 해제 상태면 생략)
2. KoEnVue 종료
3. 포터블 모드: exe 폴더 삭제
   설치 모드: exe 삭제 + `%APPDATA%\KoEnVue\` 폴더 삭제
