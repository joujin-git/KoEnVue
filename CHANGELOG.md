# Changelog

이 프로젝트의 주요 변경 사항을 기록합니다.
형식은 [Keep a Changelog](https://keepachangelog.com/ko/)를 따릅니다.

## [Unreleased]

### 추가

- **드래그 활성 키(`drag_modifier`)** — 인디케이터 마우스 클릭을 아래 창으로 투과시킬지 여부를 결정하는 신규 옵션. `"none"`(기본, 기존 동작: 모든 마우스 이벤트를 오버레이가 소비하고 어디든 드래그 가능) / `"ctrl"` / `"alt"` / `"ctrl_alt"` 4종. 비-None 모드에선 `WM_NCHITTEST`가 `GetAsyncKeyState`로 모디파이어 상태를 확인해, 눌려 있으면 `HTCAPTION`(드래그), 안 눌렸으면 `HTTRANSPARENT`(클릭·우클릭·휠이 아래 창으로 투과) 반환. 인디를 창 종료 버튼 위에 둔 채 클릭해 창을 닫는 용도 등에 활용. 드래그 도중 모디파이어를 놓아도 `WM_ENTERSIZEMOVE` 모달 루프가 캡처를 유지해 드래그 끊기지 않음
- 트레이 메뉴 "드래그 활성 키" 서브메뉴(라디오 4항목, `IDM_DRAG_MOD_*`) 및 상세 설정 "다중 모니터" 섹션 콤보박스. 기본값 `None` 이라 기존 사용자 영향 없음
- `App/Models/DragModifier.cs` enum, `AppConfig.DragModifier` 필드, `Win32Constants.VK_CONTROL` / `VK_MENU` 상수 추가. `Program.IsDragModifierPressed` 헬퍼 — `Ctrl` 모드는 `Ctrl && !Alt` 엄격 판정으로 Ctrl+Alt 조합에 우발 트리거되지 않음. `Shift`는 드래그 중 축 고정에 선점되어 있어 선택지에서 제외

## [0.9.1.3] — 2026-04-17

### 수정

- **중복 실행 시 기존 인스턴스의 트레이 아이콘이 사라지던 문제** — `Program.MainImpl`에서 `CleanupPreviousTrayIcon`이 `TryAcquireMutex`보다 먼저 호출되어, 두 번째 인스턴스가 이미 실행 중인 정상 인스턴스의 트레이 아이콘을 고정 GUID 기반 `NIM_DELETE` 로 지워버린 뒤 Mutex 실패로 종료하던 부작용. Mutex 체크를 먼저 수행하고, 획득 성공 시에만 Cleanup 을 실행하도록 순서 교체. 크래시 복구 경로(프로세스 사망 시 OS 가 Mutex 자동 해제)는 영향 없음

### 추가

- **중복 실행 시 기존 인스턴스에 활성화 신호 전달** — 두 번째 인스턴스가 조용히 종료되던 이전 동작 대신, `FindWindowW` 로 실행 중인 메인 윈도우를 찾아 `WM_APP_ACTIVATE` (`WM_APP + 7`) 을 `PostMessageW` 로 전송. 기존 인스턴스는 `HandleActivateRequest` 에서 현재 포그라운드 앱 기준으로 인디케이터를 즉시 표시해 "이미 실행 중" 시각 피드백을 제공 (`DisplayMode` / `EventTriggers` 설정과 무관하게 강제 표시)
- **Explorer 재시작 시 트레이 아이콘 자동 복구** — `RegisterWindowMessageW("TaskbarCreated")` 로 셸 브로드캐스트 메시지 ID 를 등록하고 WndProc 에서 수신 → `Tray.Recreate` (내부 상태 초기화 + `NotifyIconManager` 재생성 + `NIM_ADD` + `NIM_SETVERSION`) 로 아이콘 재등록. 셸 업데이트·크래시·수동 `explorer.exe` 재시작 시나리오 모두 커버. 등록 실패 시 `Logger.Warning` + 복구 기능만 비활성화 (앱 자체는 정상 동작)
- `User32.FindWindowW`, `User32.RegisterWindowMessageW` P/Invoke 추가
- `Tray.Recreate(ImeState, AppConfig)` internal API 추가

## [0.9.1.2] — 2026-04-17

### 추가

- **MIT LICENSE** — 저장소 루트에 `LICENSE` 파일 추가. 이전까지는 라이선스 미명시 상태(기본 "All Rights Reserved")였던 문제 해소. `README.md` 라이선스 섹션 + `koenvue.ico` 출처 명시
- `KoEnVue.csproj` 에 `<Copyright>` / `<Company>` / `<Product>` 필드 추가 — PE 헤더에 박혀 Windows 탐색기 "자세히" 탭에 노출 (`LegalCopyright: Copyright (c) 2026 joujin-git`)
- **상세 설정 → 시스템 섹션에 "부팅 시 업데이트 확인" 토글 노출** — `update_check_enabled` 를 UI 에서 즉시 on/off 가능. 이전에는 `config.json` 을 직접 편집해야 했음(폐쇄망 사용자 UX 개선)

### 수정

- `Tray.OpenUpdatePage` 의 `ShellExecuteW` 호출이 GitHub API 응답의 `html_url` 을 스킴 검증 없이 열던 문제 — 신뢰된 CA 를 가진 MITM 프록시가 `file:///`·`javascript:`·`ms-settings:` 등을 주입하면 `requireAdministrator` 프로세스에서 EoP 로 번질 수 있음. `https://github.com/{UpdateRepoOwner}/{UpdateRepoName}/` 프리픽스 일치 검사 추가(`OrdinalIgnoreCase`). 불일치 시 `Logger.Warning` 후 즉시 반환
- `Settings.MatchProfile` 의 `Regex.IsMatch` 가 타임아웃을 지정하지 않아 기본값 `Regex.InfiniteMatchTimeout` 이 적용, 기존 `catch (RegexMatchTimeoutException)` 이 무력화되던 문제 — `RegexMatchTimeout = TimeSpan.FromMilliseconds(100)` 상수 + 4-인자 오버로드로 교체. `app_profiles` 맵(title 모드)에 악의적 지수 백트래킹 패턴이 들어가도 감지 경로가 고착되지 않음

## [0.9.1.1] — 2026-04-17

### 변경

- `position_mode` 기본값을 `"fixed"` → `"window"` 로 변경 — 새 설치 시 창 기준 모드로 시작 (기존 `config.json` 은 영향 없음)

### 수정

- `Logger` 로테이션 시 `StreamWriter` 교체 중 예외 발생하면 `_fileWriter`가 dispose된 인스턴스를 가리켜 이후 쓰기에서 `ObjectDisposedException` 으로 드레인 스레드가 종료되던 문제 — 필드를 null 로 먼저 비운 뒤 dispose 하도록 교정
- `UpdateChecker` 콜백에서 예외 발생 시 백그라운드 스레드가 미처리 예외로 종료되어 프로세스가 죽을 가능성 — 콜백 호출부를 파싱 try 블록 밖으로 분리하고 별도 방어 블록으로 감쌈
- `OnProcessExit`에서 `Logger.Shutdown()` 이후 `Logger.Info()`를 호출하여 종료 로그가 기록되지 않던 문제 — 호출 순서 교정
- `Shell_NotifyIconW(NIM_ADD)` 반환값 미확인 — 실패 시 `_added = false` 유지 + 로그, `NIM_SETVERSION` 실패도 로그
- `CreateDIBSection` 실패 시 `out _ppvBits`가 이전 유효 포인터를 덮어써 해제된 메모리 참조 가능성 — 로컬 변수로 수신 후 성공 시에만 필드 갱신
- `AppConfig.ConfigVersion` 기본값이 `3` 이어서 새 config 파일이 이전 스키마로 기록되던 문제 — `Settings.CurrentVersion` 과 일치하는 `4` 로 정렬

### 개선

- `config.json` 숫자 배열(`tray_quick_opacity_presets`, `indicator_positions*`)을 `[0.95, 0.85, 0.6]` 형태의 1줄로 압축 출력 — 가독성 향상
- 모달 다이얼로그(상세 설정/위치 기록 정리/배율 입력)의 `ModalDialogLoop.Run` + `DestroyWindow` + 정적 상태 초기화를 `try/finally` 로 감싸 도중 예외 발생 시에도 윈도우/핸들이 누수되지 않도록 보장
- 8개 bare catch 지점을 좁은 예외 집합으로 교체(`Settings` Regex/Profile/PostDeserialize, `Tray` 작업 스케줄러/경로 비교, `Program.Main`, `DetectionLoop`) — 로직 버그가 silent 삼켜지지 않고 표면화
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
