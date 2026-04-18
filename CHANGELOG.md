# Changelog

이 프로젝트의 주요 변경 사항을 기록합니다.
형식은 [Keep a Changelog](https://keepachangelog.com/ko/)를 따릅니다.

## [Unreleased]

### 수정

- **트레이 아이콘 캐럿+점 시각 중심 보정** — `TrayIcon.DrawCaretDot` 의 `caretY = (iconH - caretH) / 2` 는 `iconH - caretH` 가 홀수일 때 정수 나눗셈 절사로 상단 1px 편중이 발생했음 (24×24 @ 150% DPI: `(24-15)/2=4` → top 4 / bottom 5 — 기하 중심에서 0.5px 위). 추가로 캐럿 하단에 점이 붙는 구도는 시각 무게중심이 기하 중심보다 아래라 사용자 지각 상으로도 아이콘이 떠 보이는 현상이 누적. `(iconH - caretH + 1) / 2 + CaretYOffsetPx` (신규 상수 `CaretYOffsetPx = 1`) 로 변경해 홀수 차이에서는 하단 방향 반올림, 모든 사이즈에서 1px 아래로 시각 보정. 결과: 16×16 top3/bot3→top4/bot2, 24×24 top4/bot5→top6/bot3, 32×32 top6/bot6→top7/bot5 — 전 DPI 범위에서 하단 편중 일관화. `dotY = caretY + caretH - dotSize` 는 업데이트된 `caretY` 를 그대로 참조하므로 점 상대 위치 불변
- **상세 설정 `position_mode` 핫 리로드 torn read** — `Program.MainLoop` 에서 디텍션 스레드가 관측한 `AppConfig` 스냅샷을 `appConfig` 지역변수로 잡은 뒤에도 동일 틱에서 `_config.PositionMode` 를 다시 읽어 비교하던 분기가 있었음. 동일 틱 중 핫 리로드가 `_config` 필드를 교체하면 같은 로직 안에서 이전 스냅샷(PositionMode=Fixed)과 새 스냅샷(Window)이 섞여 인디 위치 산출이 튈 수 있었음. `appConfig.PositionMode` 로 교체해 한 틱 내 단일 스냅샷 규율 복원
- **`Tray.Recreate` 미초기화 상태 가드** — `TaskbarCreated` 브로드캐스트 메시지가 `Initialize` 보다 먼저 도달할 때 (예: 부팅 초기 레이스) 기존 가드 `_hwndMain == IntPtr.Zero` 만으로는 걸러지지 않아 `Remove` 단계가 skip 되고 `Initialize` 만 실행되어 중복 등록 또는 미초기화 상태에서 `NIM_ADD` 가 호출될 수 있었음. `|| !_initialized` 조건 추가
- **`LayeredOverlayBase.EnsureDib` 실패 로그 부재** — `CreateDIBSection` 이 `0` 을 돌려주면 아무 로그 없이 조기 반환되어 렌더 공백의 원인 추적이 어려웠음. `_dibFailureLogged` 래치로 최초 1회만 `Logger.Warning` 출력 후 후속 실패는 스팸 회피를 위해 silent 유지
- **드래그 진입/종료 시 `GetWindowRect` 실패 무시** — `BeginDrag`/`EndDrag` 의 `GetWindowRect` 반환값을 검사하지 않고 `rc=default(RECT)` 로 진행하던 흐름. 실패 시 `_dragStartX/Y=0` 으로 드래그가 좌상단 (0,0) 에서 시작되는 시각 튐. 반환값이 `false` 이면 조기 반환
- **`Program.HandleConfigChanged` 핫 리로드 이외 경로의 `UpdateConfigAndNotify` 사각지대** — `Program.UpdateConfigAndNotify` 호출처가 0건(dead code). 삭제로 동일 의도의 재진입 경로 혼선 제거
- **`Core/Native/Kernel32.GetLastError` P/Invoke 미사용** — 호출처 0건. 선언 삭제로 `[LibraryImport]` surface 축소
- **`ModalDialogLoop.s_activeDialog` 스레드 가시성 공백** — `IsActive`/`ActiveDialog` 가 감지 스레드(`Program.MainLoop` / `DetectionLoop` 의 모달 게이트, [Program.cs:820](Program.cs#L820))와 UI 스레드(재진입 가드) 양쪽에서 접근되지만 필드는 `IntPtr` 로 `volatile` 을 받지 못하는 상태였음. 접근 4곳(getter 2 + `Run` write 진입·finally + `RunExternal` prev/write/restore)을 `Volatile.Read`/`Volatile.Write` 로 교체해 모달 진입/종료 타이밍 직후 감지 스레드가 구값을 관측하지 않도록 메모리 배리어 명시. 기존 주석 "UI 스레드에서만 호출" 은 사실 오독이었음 — 교차 스레드 접근이 설계에 포함된다는 점을 주석에 명시
- **`Logger` 비동기 큐 무제한 성장 방어** — 로그 회전 중 `File.Move` 가 다른 프로세스의 파일 잠금 등으로 실패하면 `_fileWriter = null` 상태가 지속되며 `FlushQueue` 가 조기 반환, 그동안 `EnqueueToFile` 은 `_drainThread is not null` 만 체크하므로 큐가 무제한 성장할 수 있었음. `MaxQueueSize = 10_000` 상한 추가 + `Interlocked.Increment` 기반 드롭 카운터로 최고령 메시지부터 드롭. 파일 쓰기가 복구되는 첫 `FlushQueue` 에서 누적 드롭 건수를 1회 요약 경고로 기록

### 개선

- **`LayeredOverlayBase.PaintDib` 의 `_ppvBits` 방어 가드** — `Span<byte>((void*)_ppvBits, ...)` 진입 전 `_ppvBits == IntPtr.Zero` 방어적 조기 반환. `EnsureDib` 실패와 `PaintDib` 호출 순서 이상 시 null 포인터 접근 차단
- **`Logger` 레벨별 메서드 중복 제거** — `Debug`/`Info`/`Warning`/`Error` 4종이 `level < _logLevel` 가드 + 포맷팅을 각자 복사하던 구조를 `Write(LogLevel, string prefix, string message)` private 헬퍼 1곳으로 통합. 공개 메서드 4종은 expression-bodied delegator. "[WARN]" 약자 형식을 유지하기 위해 prefix 를 `ToString` 이 아닌 명시 파라미터로 전달
- **`JsonSettingsManager` 예외 필터 패턴 추출** — `IsExpectedLoadException`/`IsExpectedSaveException`/`IsExpectedIoException` 3종 private static 헬퍼로 추출. Load/Save/CheckReload 가 동일한 `IOException or UnauthorizedAccessException [or JsonException] [or NotSupportedException]` 조합을 재사용. 훅(Validate/Migrate/PostDeserializeFixup) 의 로직 버그는 여전히 전파되는 정책 유지
- **`Win32DialogHelper.ApplyFont` AggressiveInlining** — 1-liner `SendMessageW(WM_SETFONT)` wrapper 에 `[MethodImpl(MethodImplOptions.AggressiveInlining)]` 어트리뷰트 추가로 NativeAOT 호출 오버헤드 제거
- **`Win32Constants.S_OK` HRESULT 상수 도입** — `DpiHelper.GetScale`/`GetRawDpi` 의 `hr != 0` 비교를 `hr != Win32Constants.S_OK` 로 교체해 HRESULT 의미 명시화. 기존 raw 0 비교는 winerror.h 의미와 동일하나 가독성/일관성 향상
- **`SystemFilter.MatchesAny` 2-리스트 조회 헬퍼** — 클래스명·프로세스명 블랙리스트 조회가 `config.SystemHideClasses` + `SystemHideClassesUser` (소유자 탐색까지 포함해 4쌍) 의 8회 `foreach` + `Equals(OrdinalIgnoreCase)` 로 중복되어 있었음. `MatchesAny(name, baseList, userList)` private 헬퍼 1곳으로 통합해 소유자 탐색 시 `ownerInHideList` 임시 플래그도 제거. 호출부가 단일 표현식 1줄로 축약
- **`ScrollableDialogHelper` 공용 추출** — `SettingsDialog.Scroll.ScrollTo`/`ResolveVScrollPosition`/WM_MOUSEWHEEL 핸들러와 `CleanupDialog` 의 동일 3조합(SB_* → `SIF_POS` + `ScrollWindowEx(SW_SCROLLCHILDREN|SW_INVALIDATE|SW_ERASE)`, SB_*→목표 위치 해석, `delta / WHEEL_DELTA × WheelLineStep × lineHeight`) 을 `Core/Windowing/ScrollableDialogHelper.cs` 의 `ScrollTo(ref scrollPos, ...)`/`ResolveVScrollPosition(...)`/`CalculateWheelScrollPos(...)` 3 메서드로 추출. 호출부는 expression-bodied 1-라이너로 축약되고 `WheelLineStep = 3` 상수도 헬퍼로 이동 (P4 공통 모듈 규칙 준수)

### 제거

- **핫키 기능** — `hotkeys_enabled` / `hotkey_toggle_visibility` config 키, 트레이 메뉴 등록 없이 오직 `Ctrl+Alt+H`(또는 사용자 지정) 으로만 인디 표시/숨김 토글하던 기능 전체 제거. 트레이 좌클릭이 `tray_click_action` 으로 동일 토글을 제공하므로 기능 중복. 관련 삭제: `Program.Bootstrap.RegisterHotkeys`/`UnregisterHotkeys`/`ParseHotkey` (~75 줄), `Program.HandleHotkey` + `WM_HOTKEY` 분기, `Core/Native/User32.RegisterHotKey`/`UnregisterHotKey` P/Invoke, `Win32Constants.WM_HOTKEY`/`MOD_*`/`VK_F1..F12` 상수, `AppConfig.HotkeysEnabled`/`HotkeyToggleVisibility`, 상세 설정 다이얼로그 "핫키" 섹션 2필드
- **`TrayIconStyle` enum 및 `tray_icon_style` config 키** — 캐럿+점(`caret_dot`) 디자인 고정으로 단순화. `Static` 변종은 트레이 아이콘으로 IME 상태를 보여주지 않는 옵션이었으나 사용 사례가 불분명. `App/Models/TrayIconStyle.cs` 파일 삭제, `AppConfig.TrayIconStyle` 필드, `TrayIcon.CreateIcon` 의 Static 정규화 분기, `Tray.UpdateState` 의 스타일 전환 설명, 상세 설정 다이얼로그 "트레이 > 아이콘 스타일" 콤보 제거. `TrayIcon.CreateIcon` 은 항상 IME 상태에 따라 `HangulBg`/`EnglishBg`/`NonKoreanBg` 로 캐럿+점을 그린다
- **`Win32Constants` 미사용 상수 6건** — `CB_RESETCONTENT`, `SWP_NOZORDER`, `SWP_NOREDRAW`, `UIA_TextPattern2Id`, `CS_HREDRAW`, `CS_VREDRAW` 및 이에 딸린 "// --- UIA ---", "// --- 기타 ---" 주석 헤더. 프로젝트 전역 grep 결과 호출처 0건. `Core/Native/Win32Types.cs` 상수 표 축소

### 문서

- **사용자 가이드 "단축키" 섹션 삭제** — `Ctrl+Alt+H` 핫키 제거 (a6f9ac7) 에 맞춰 `docs/User_Guide.md` 의 단축키 표·관련 문단 제거. 트레이 좌클릭 `tray_click_action` 으로 동일 기능 안내는 잔여 섹션에 유지
- **README 상세 설정 필드 수 서술 수정** — "전체 62개 설정 필드" 라는 실측과 어긋나는 숫자 고정 문구 대신, 13개 섹션 이름만 열거하는 서술로 교체. 필드 수 변동마다 문서 업데이트 누락 리스크 제거
- **`.gitignore` 에 `.claude/` 추가** — 로컬 개발 설정(플랜/메모리) 이 `git status` 에 뜨지 않도록 차단
- **2차 리팩터 반영 문서 정합** — `docs/architecture.md` Core 트리·모듈 표에 `ScrollableDialogHelper` 신규 행 추가, `Logger` 에 `MaxQueueSize` 동작, `ModalDialogLoop` 에 `Volatile.Read/Write` 교차 스레드 가시성 언급 추가. `docs/conventions.md` P4 권위 모듈 표에 `ScrollableDialogHelper`, Logging 섹션에 큐 cap 설명 추가. `docs/implementation-notes.md` 의 `SettingsDialog` 스크롤 섹션 끝에 "3 메서드는 `CleanupDialog` 와 공유되어 `ScrollableDialogHelper` 로 추출" 문단 추가. 실측 불일치였던 "62 fields"/"64 → 62 fields" 하드코드 숫자는 `BuildRowDefs` 참조/"3 rows dropped" 설명으로 교체 (필드 수 변동마다 문서 업데이트 누락 리스크 제거)

## [0.9.1.6] — 2026-04-18

### 수정

- **모달 대화상자 활성 중 외부 앱 포커스에서는 인디 표시** — 기존 `ModalDialogLoop.IsActive` 게이트는 대화상자가 떠 있는 동안 포그라운드가 어느 앱이든 무조건 인디를 숨겼음. Win32 다이얼로그는 **소유자 기준 모달**일 뿐 시스템 전체 모달이 아니어서, 사용자가 Alt+Tab 으로 다른 앱에 포커스를 옮기면 해당 앱에서는 인디가 표시돼야 정상. `DetectionLoop` 의 모달 게이트를 `User32.GetWindowThreadProcessId(hwndForeground, out fgPid); if (ModalDialogLoop.IsActive && fgPid == (uint)Environment.ProcessId)` 조건으로 축소 — 자기 프로세스 윈도우 (대화상자, `MessageBoxW`) 가 포그라운드일 때만 숨김 메시지 전송, 외부 앱 포커스 시에는 fall-through 로 정상 감지 경로 진입. `ModalDialogLoop.ActiveDialog` HWND 비교만으로는 `MessageBoxW` (HWND 가 `user32` 내부 소유라 우리가 알 수 없음) 를 커버할 수 없어 PID 비교가 유일한 견고 해법. `Environment.ProcessId` 는 .NET BCL 속성이라 P/Invoke 추가 없음. `GetWindowThreadProcessId` 호출을 게이트 상단으로 hoist 해 후속 `GUITHREADINFO` 용 `threadId` 와 한 번에 확보 — 중복 호출 없음
- `lastFiltered` 상태 기계는 기존대로 유지되어 자기-프로세스 모달 종료 후 원 앱 포그라운드 복귀 첫 틱에서 `foregroundChanged=true` 자연 재표시가 그대로 작동 — 외부 앱 ↔ 대화상자 ↔ 다른 외부 앱 반복 전환에도 중복 hide 없이 정합

## [0.9.1.5] — 2026-04-18

### 수정

- **`config.json` 저장 원자화** — `JsonSettingsFile.WriteAllText` 가 기존에는 `File.WriteAllText(path, json)` 단일 호출이라 쓰기 도중 전원 차단·프로세스 강제 종료·OS 크래시가 발생하면 config 파일이 절단된 반쪽 상태로 남을 수 있었음. `path + ".tmp"` 에 전체를 먼저 기록한 뒤 `File.Move(tmpPath, path, overwrite: true)` 로 교체하도록 변경 — Windows 동일 볼륨에서 `MoveFileExW(MOVEFILE_REPLACE_EXISTING)` 는 원자적 rename 을 보장하므로 쓰기 중 크래시가 와도 원본 파일 또는 새 파일 중 하나는 항상 온전한 상태로 남는다 (truncate 된 반쪽 파일 불가능). `Settings.CheckConfigFileChange` 의 mtime 폴링은 타겟 경로만 관찰하므로 `.tmp` 파일이 핫 리로드를 트리거하지 않음
- **`LayeredOverlayBase.EnsureFont` 의 `CreateFontW` 실패 가드** — 기존 흐름은 `_currentFont.Dispose() → CreateFontW → new SafeFontHandle(hFont)` 를 무조건 순차 실행하고 캐시 키를 갱신했기 때문에, GDI 핸들 고갈·잘못된 폰트 패밀리 등으로 `CreateFontW` 가 `0` 을 돌려주면 (1) 이전 유효 폰트가 이미 해제된 뒤고, (2) 빈 HFONT 가 캐시에 고착되어 동일 파라미터로는 영원히 재진입 없이 렌더가 실패하는 상태에 빠질 수 있었음. `CreateFontW` 결과를 먼저 검사해 `IntPtr.Zero` 이면 `Logger.Warning(family/size/bold)` + 조기 반환 — 이전 `_currentFont` 와 캐시 키를 모두 보존해 다음 `EnsureFont` 호출에서 재시도가 가능하도록 수정. 성공 경로에서만 Dispose → wrap → 캐시 갱신 순서로 진행
- **IME 감지 Tier 1 이 `openResult = 0` 인 경우 `ImeState.English` 대신 `null` 반환** — `ImeStatus.TryTier1` 의 `IMC_GETOPENSTATUS` 결과가 "IME 비활성" 을 의미하는 `0` 일 때 한국어 IME 환경에서는 영문 입력이지만, 비-한국어 로케일(일본어/중국어) 에서도 동일한 결과가 나오므로 여기서 `English` 로 단정하면 Tier 3 의 `GetKeyboardLayout` 기반 `NonKorean` 판별 기회를 잃는다. `null` 을 돌려 Tier 2 → Tier 3 체인에 위임하도록 변경 — 대부분의 비-한국어 IME 윈도우는 `ImmGetContext = 0` 이라 Tier 2 도 자연스럽게 null 로 패스-스루되어 Tier 3 가 `langId` 로 로케일을 확인한다. 한국어 사용자 경로는 Tier 2/3 모두 `English` 를 반환하므로 동작 불변. explicit `DetectionMethod.ImeDefault` 경로는 `TryTier1 ?? ImeState.English` 폴백으로 기존 동작 유지
- **`TrayIconStyle.Static` 팬텀 enum 실체화** — enum 값(`[JsonStringEnumMemberName("static")]`)과 AppConfig 필드, 상세 설정 다이얼로그 "아이콘 스타일" 콤보박스가 모두 이미 존재했지만 실제 렌더링 경로에서 값을 전혀 참조하지 않아 사용자가 "Static / 고정" 을 선택해도 IME 전환마다 색상이 바뀌는 `CaretDot` 동작 그대로였음. `TrayIcon.CreateIcon` 진입점에서 `config.TrayIconStyle == TrayIconStyle.Static` 이면 `state = ImeState.English` 로 정규화해 항상 `EnglishBg` 로 렌더링하도록 수정. `Tray.UpdateState` 는 state/config 변경 구분 없이 항상 재렌더하는 정책을 유지해 CaretDot↔Static 스타일 전환이 즉시 반영됨 (NIM_MODIFY 비용 μs 수준). 툴팁은 `TrayTooltip` 플래그가 별도 제어하므로 영향 없음

## [0.9.1.4] — 2026-04-17

### 추가

- **드래그 활성 키(`drag_modifier`)** — 인디케이터 마우스 클릭을 아래 창으로 투과시킬지 여부를 결정하는 신규 옵션. `"none"`(기본, 기존 동작: 모든 마우스 이벤트를 오버레이가 소비하고 어디든 드래그 가능) / `"ctrl"` / `"alt"` / `"ctrl_alt"` 4종. 비-None 모드에선 `WM_NCHITTEST`가 `GetAsyncKeyState`로 모디파이어 상태를 확인해, 눌려 있으면 `HTCAPTION`(드래그), 안 눌렸으면 `HTTRANSPARENT`(클릭·우클릭·휠이 아래 창으로 투과) 반환. 인디를 창 종료 버튼 위에 둔 채 클릭해 창을 닫는 용도 등에 활용. 드래그 도중 모디파이어를 놓아도 `WM_ENTERSIZEMOVE` 모달 루프가 캡처를 유지해 드래그 끊기지 않음
- 트레이 메뉴 "드래그 활성 키" 서브메뉴(라디오 4항목, `IDM_DRAG_MOD_*`) 및 상세 설정 "다중 모니터" 섹션 콤보박스. 기본값 `None` 이라 기존 사용자 영향 없음
- `App/Models/DragModifier.cs` enum, `AppConfig.DragModifier` 필드, `Win32Constants.VK_CONTROL` / `VK_MENU` 상수 추가. `Program.IsDragModifierPressed` 헬퍼 — `Ctrl` 모드는 `Ctrl && !Alt` 엄격 판정으로 Ctrl+Alt 조합에 우발 트리거되지 않음. `Shift`는 드래그 중 축 고정에 선점되어 있어 선택지에서 제외

### 개선

- **퍼블리시 exe 크기 다이어트 (~407 KB / 7.9 %)** — NativeAOT 진단 메타데이터를 비활성화하는 csproj 빌드 플래그 6 종 추가: `StackTraceSupport=false` (예외 스택트레이스의 메서드명 테이블 제거 — 본 앱은 silent-catch 정책으로 사용자에게 스택을 노출하지 않음), `UseSystemResourceKeys=true` (BCL 예외 메시지를 짧은 키로 — 사용자 대면 예외 텍스트 없음), `DebuggerSupport=false` (릴리스 디버거 어태치 메타데이터), `MetadataUpdaterSupport=false` (Hot Reload — 릴리스 무관), `EventSourceSupport=false` (ETW/EventSource — 자체 `Logger` 사용), `HttpActivityPropagationSupport=false` (DiagnosticSource HTTP 전파 — `WinHTTP` 직접 P/Invoke 사용). 결과: release exe 5,130,752 B → **4,723,200 B**. 코드 동작 변화 없음

### 수정

- **모달 대화상자 활성 중 인디케이터 거동 통일** — 세 대화상자(크기 직접 지정 / 위치 기록 정리 / 상세 설정)의 OK/취소/Esc 경로에 따라 인디 숨김 여부가 달랐고, 특히 Window 위치 모드에선 감지 스레드가 대화상자 HWND 를 일반 포그라운드 앱으로 오인식해 인디가 대화상자 근처로 튀는 부작용이 있었음. `DetectionLoop` 에 `ModalDialogLoop.IsActive` 가드를 추가해 모달 활성 중에는 `WM_HIDE_INDICATOR` 1회 송출 + `lastFiltered=true` 후 틱을 건너뛰도록 수정. 대화상자 종료 후 원 앱 foreground 복귀 첫 틱에서 `foregroundChanged=true` 로 자연 재표시. `PositionMode`(Fixed/Window), `DragModifier`(None/Ctrl/Alt/CtrlAlt) 모든 조합에 동일 적용. 부수 효과로 모달 중 감지 경로의 `WM_POSITION_UPDATED` → `TriggerShow` 렌더 간섭이 사라지면서, 대화상자가 뜨자마자 ESC 로 닫을 때 포커스 지연이 사라짐
- **MessageBoxW 안내 대화상자도 동일 가드 적용** — `Tray.ShowPositionError` / `CleanupPositions` 의 빈-상태 안내("저장된 위치 기록이 없습니다.", "인디케이터 위치를 확인할 수 없습니다.") 두 `MessageBoxW` 호출은 Win32 자체 메시지 루프를 돌려 `ModalDialogLoop.Run` 으로 감쌀 수 없었음. 신규 `ModalDialogLoop.RunExternal(hwndSentinel, action)` 헬퍼로 `IsActive` 가드만 씌워(메시지 펌프·`EnableWindow` 은 Win32 에 위임), 박스가 열린 동안 감지 스레드가 박스 HWND 를 일반 앱으로 오인식해 인디가 근처로 튀는 부작용 제거. 세 커스텀 다이얼로그 내부의 `MessageBoxW` (Settings/ScaleInput 검증 실패 팝업)는 부모 모달이 이미 `Run` 중이라 별도 래핑 불필요
- **상세 설정 · 위치 기록 정리 스크롤 플리커 및 성능 회귀** — 썸 드래그·휠 스크롤 중 뷰포트가 비어 보이거나 렌더가 스크롤바를 못 쫓아오던 문제. 두 축을 함께 적용해 해결:
  - (1) 스크롤 뷰포트 스타일에 `WS_CLIPCHILDREN` + `WS_EX_COMPOSITED` 를 추가 — DWM 이 뷰포트와 모든 자식을 오프스크린 비트맵에 합성 후 한 번에 출력하는 더블버퍼링. 연속 이동 중 중간 상태가 화면에 노출되지 않아 플리커·티어링 제거
  - (2) `ScrollTo` 의 "N개 자식 `SetWindowPos` 루프 + 전체 `InvalidateRect`" 를 단일 `User32.ScrollWindowEx(0, dy, ..., SW_SCROLLCHILDREN | SW_INVALIDATE | SW_ERASE)` 호출로 대체 — OS 가 클라이언트 픽셀을 BitBlt 로 복사하고 자식 HWND 까지 한 번에 이동시키며, 노출된 띠만 무효화해 휠 한 틱당 작업량이 `O(N)` → `O(1)` 로 감소. Settings 다이얼로그(120+ 컨트롤)에서 체감 휠 반응 개선
  - `Win32Constants.SW_SCROLLCHILDREN` / `SW_INVALIDATE` / `SW_ERASE` / `WS_CLIPCHILDREN` / `WS_EX_COMPOSITED` 상수, `User32.ScrollWindowEx` P/Invoke, `ModalDialogLoop.RunExternal` 헬퍼 추가

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
