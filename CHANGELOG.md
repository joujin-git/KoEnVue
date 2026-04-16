# Changelog

이 프로젝트의 주요 변경 사항을 기록합니다.
형식은 [Keep a Changelog](https://keepachangelog.com/ko/)를 따릅니다.

## [Unreleased]

### 수정

- `OnProcessExit`에서 `Logger.Shutdown()` 이후 `Logger.Info()`를 호출하여 종료 로그가 기록되지 않던 문제 — 호출 순서 교정
- `Shell_NotifyIconW(NIM_ADD)` 반환값 미확인 — 실패 시 `_added = false` 유지 + 로그, `NIM_SETVERSION` 실패도 로그
- `CreateDIBSection` 실패 시 `out _ppvBits`가 이전 유효 포인터를 덮어써 해제된 메모리 참조 가능성 — 로컬 변수로 수신 후 성공 시에만 필드 갱신

### 개선

- 윈도우 생성(`CreateMainWindow`/`CreateOverlayWindow`) 실패 시 `IntPtr.Zero` 체크 → 조기 종료 (null 핸들로 후속 초기화 진행 방지)
- `CreateCompatibleDC` 반환값 `Zero` 체크 추가 (`InvalidOperationException`)
- `DetectionLoop` while 본문 `try-catch` 래핑 — 단일 폴링 예외 시 스레드 무음 종료 대신 로그 + 다음 폴링 계속
- `OnProcessExit` 종료 시퀀스 강화: CAPS LOCK 타이머 `KillTimer` 명시적 해제, 메인 윈도우 `DestroyWindow` 명시적 파괴 추가
- `_stopping` 필드에 `volatile` 추가 — 감지 스레드와의 크로스 스레드 가시성 보장 (기존 `_config`/`_lastImeState`/`_indicatorVisible`과 일관성)

## [0.9.1.0] — 2026-04-16

### 추가

- **창 기준 위치 모드** — 인디케이터를 포그라운드 창의 모서리 기준 상대 오프셋으로 배치하는 새 위치 모드. 같은 앱의 창을 여러 개 열어도 각 창의 실제 위치에 따라 인디케이터가 정확히 배치됨
- 트레이 메뉴 "위치 모드" 서브메뉴 (고정 위치 / 창 기준 라디오 선택)
- `PositionMode` enum (`fixed` / `window`)
- `indicator_positions_relative` (프로세스명별 창 기준 상대 위치 저장)
- `default_indicator_position_relative` (창 기준 모드 기본 위치 설정)
- 창 기준 모드에서 창 이동 중 인디케이터 자동 숨김, 이동 완료 시 새 위치에 재표시

## [0.9.0.6] — 2026-04-16

### 수정

- UWP 앱(설정, Microsoft Store 등) 간 인디케이터 위치가 공유되던 문제 — `ApplicationFrameHost` 윈도우의 자식 윈도우를 탐색하여 실제 앱 프로세스 이름으로 위치 저장

### 추가

- `User32.EnumChildWindows` P/Invoke
- `WindowProcessInfo` UWP 프로세스 이름 해석 (`[ThreadStatic]` 브리지 + `[UnmanagedCallersOnly]` 콜백)

## [0.9.0.5] — 2026-04-16

### 수정

- 상세 설정에서 테마 프리셋 선택 시 색상이 즉시 반영되지 않던 문제 — `updateConfig` 콜백에서 `ThemePresets.Apply()` 즉시 실행

### 추가

- 테마 프리셋 전환 시 커스텀 색상 백업/복원 — 프리셋 적용 전 커스텀 색상을 `custom_backup_*` 필드에 저장, `custom` 복귀 시 자동 복원

## [0.9.0.4] — 2026-04-16

### 개선

- 창 엣지 스냅 시 인디케이터와 창 경계 사이에 간격(기본 2 px, DPI 스케일) 적용 — 경계선 겹침 방지
- `snap_gap_px` 설정 추가 (config.json / 상세 설정 → 다중 모니터 섹션, 범위 0–10, 0 = 밀착)

## [0.9.0.3] — 2026-04-16

### 개선

- 트레이 메뉴: 대화상자를 여는 항목에 "..." 접미사 추가 (직접 지정..., 위치 기록 정리..., 상세 설정...)
- "미사용 위치 데이터 정리" → "위치 기록 정리"로 리네임 — 전체 `indicator_positions` 항목 표시, 실행 중인 앱에 "(실행 중)" 접미사
- 위치 기록 정리 다이얼로그: 항목 15개 초과 시 스크롤 뷰포트 + 마우스 휠 지원

### 수정

- 모달 다이얼로그가 열린 상태에서 앱 종료 시 다이얼로그가 남아있던 문제 — ModalDialogLoop에서 WM_QUIT 재전달

## [0.9.0.2] — 2026-04-16

### 수정

- 바탕화면 소유 시스템 대화상자(휴지통 비우기 확인 등)에서 인디케이터 숨김 — 소유자 창 체인 + 동일 프로세스 검증 (SystemFilter 조건 4-b)
- Always 모드에서 일시적 포커스 드롭 후 인디케이터가 Idle로 복귀하지 않고 완전 숨김되던 문제 수정 — `_forceHidden` 플래그 누수 해소

### 추가

- `User32.GetWindow` P/Invoke, `GW_OWNER` 상수

## [0.9.0.1] — 2026-04-16

### 수정

- Always 모드 투명도 수정 — ActiveOpacity → IdleOpacity 페이드 전이, 트레이 프리셋 반영, 핫리로드 즉시 반영
- 시스템 입력 ESC 해제 시 인디케이터 숨김 (시작 메뉴 + 검색)

## [0.9.0.0] — 2026-04-16

### 추가

- 로그 타임스탬프에 날짜 표시 (`[INFO] 12:40:46.172` → `[INFO] 2026.04.16 12:40:46.172`)

### 수정

- Win11 바탕화면·작업 표시줄 우클릭 컨텍스트 메뉴에서 인디케이터 숨김
- StartMenuExperienceHost 인디케이터 위치 보정 (캐시된 프레임 재사용)

## [0.8.9.0] — 2026-04-14

첫 공개 릴리스.

### 추가

- 한/En/EN 라벨로 IME 상태 실시간 표시
- CAPS LOCK 좌우 세로 막대 동시 표시
- 드래그 가능한 TOPMOST 오버레이 (앱별 위치 기억)
- 자석 스냅 + Shift 축 고정, 멀티 모니터 / DPI / 화면 회전 대응
- 트레이 메뉴: 투명도·크기·시작 프로그램·기본 위치·상세 설정
- 59개 필드 상세 설정 다이얼로그
- 6개 프리셋 테마 (Dracula / Nord / Monokai / Solarized Dark / High Contrast / Default)
- 소수점 인디케이터 배율 (1.0~5.0) 커스텀 입력 다이얼로그
- 저장 위치 없는 앱의 기본 인디케이터 위치 설정
- 인디케이터 위치 기록 정리 다이얼로그
- 캐럿 이동 감지 (마우스 클릭 재배치 시 인디케이터 표시)
- `Ctrl+Alt+H` 보이기/숨기기 토글
- GitHub Releases 업데이트 알림 (트레이 메뉴, WinHTTP 경량 구현)
- 완전 포터블 config — exe 폴더 우선, delete-safe 핫 리로드
- 포터블 단일 exe (~4.9 MB), .NET 런타임 설치 불필요
- exe 애플리케이션 아이콘
