# CLAUDE.md — KoEnVue Project Guide

## What is this?

Windows용 한/영 IME 상태 인디케이터. 캐럿 옆에 현재 입력 상태(한/En/EN)를 표시한다.
C# 14 / .NET 10 + NativeAOT 단일 exe (~3MB). 외부 NuGet 패키지 없음.

## Tech Stack

- **Target**: `net10.0-windows`, PublishAot, AllowUnsafeBlocks
- **Source Generators**: `[LibraryImport]`, `[GeneratedComInterface]`, `[JsonSerializable]`
- **P/Invoke**: User32, Imm32, Shell32, Gdi32, Kernel32, Shcore, Ole32, OleAut32
- **`[DllImport]` 사용 금지** — 반드시 `[LibraryImport]` 사용

## Hard Constraints (P1-P5)

| 규칙 | 내용 |
|------|------|
| **P1** | NuGet 외부 패키지 제로. .NET 10 기본 라이브러리 + Windows API만 |
| **P2** | UI 텍스트 한글 기본. 로그/config 키 영문 |
| **P3** | 매직 넘버 금지 → const/enum/config. 문자열 비교 금지 → enum |
| **P4** | 공통모듈 강제: DPI→DpiHelper, 색상→ColorHelper, GDI핸들→SafeGdiHandles, P/Invoke→Native/, 구조체/상수→Win32Types.cs |
| **P5** | app.manifest UAC requireAdministrator |

## Architecture

3-스레드 모델:
```
메인 스레드 (UI):     메시지 루프 + 렌더링 + 트레이 + WM_TIMER 애니메이션
감지 스레드 (BG):     80ms 폴링 → PostMessage로 메인에 통보
UIA 스레드 (BG):      COM STA + IUIAutomation 전용
```

## Project Structure

```
KoEnVue/
├── Native/      P/Invoke (DLL별 1파일) + Win32Types.cs + SafeGdiHandles.cs + AppMessages.cs + VirtualDesktop.cs + UiaInterfaces.cs
├── Models/      AppConfig (record) + DebugInfo (record) + 13개 enum
├── Detector/    ImeStatus, CaretTracker (4-tier), SystemFilter (8-조건), UiaClient (STA COM)
├── UI/          Overlay (GDI 렌더링 + LabelStyle + DebugOverlay), Animation (WM_TIMER 상태 머신), Tray (시스템 트레이 + schtasks), TrayIcon (GDI 아이콘)
├── Config/      DefaultConfig, Settings (로드/저장/검증/마이그레이션/핫리로드/앱프로필), ThemePresets (6개 테마)
├── Utils/       DpiHelper, ColorHelper, Logger, I18n (한/영 UI 텍스트)
└── Program.cs   메인 루프 (3-스레드 관리 + 라이프사이클)
```

## Build & Run

```bash
dotnet build                          # 빌드
dotnet publish -r win-x64 -c Release  # NativeAOT 배포
```

csproj에 `NoWarn: SYSLIB1051` 설정됨 (.NET 10 LibraryImport IntPtr 진단 억제).

## .NET 10 Compatibility Notes

Phase 01~02 구현 시 발견된 .NET 10 호환성 이슈와 적용한 수정:

| 항목 | 내용 |
|------|------|
| `ImplicitUsings` | .NET 10에서 기본 `enable`이 아님 → csproj에 명시 필요 |
| `Nullable` | nullable 참조 경고 해소 위해 csproj에 `enable` 명시 |
| `SYSLIB1051` | .NET 10에서 `IntPtr`이 `[LibraryImport]` source generator에서 에러로 승격 → `NoWarn` 억제 |
| `uint → nint 캐스트` | `uint` 상수를 `IntPtr` 매개변수에 전달 시 `(nint)` 명시적 캐스트 필요 (예: `(nint)IMC_GETOPENSTATUS`) |
| `int & uint 연산` | `GetWindowLongW`(int 반환) + `WS_CAPTION`(uint) 혼합 → CS0034 에러. `unchecked((int)WS_CAPTION)` 로컬 상수로 해결 |
| NativeAOT COM | `Marshal.GetObjectForIUnknown` 사용 불가 → `StrategyBasedComWrappers.GetOrCreateObjectForComInstance` 사용 |

## Spec Corrections Applied

### Phase 01 (`prompts/01_FOUNDATION.md`)

1. **AppConfig.cs**: `using System.Text.Json;` 추가 (JsonElement용), 중복 `using KoEnVue.Models;` 제거
2. **Program.cs**: 빌드용 최소 스텁 추가 (Phase 03에서 교체)
3. **KoEnVue.csproj**: `ImplicitUsings`, `Nullable`, `NoWarn` 추가 (.NET 10 호환)

### Phase 02 (`prompts/02_DETECTION.md`)

1. **Win32Types.cs**: `IME_CMODE_HANGUL`, `HKL_LANGID_MASK`, `MAX_CLASS_NAME` 상수 추가 (P3 매직 넘버 제거)
2. **SystemFilter.cs**: `[GeneratedComInterface]` + `StrategyBasedComWrappers` NativeAOT 패턴 적용 (스펙은 `Marshal.GetObjectForIUnknown` 가정)
3. **SystemFilter.cs**: `WS_CAPTION` int/uint 타입 불일치 → `unchecked((int)WS_CAPTION)` 로컬 상수 패턴 적용
4. **ImeStatus.cs**: `_imeChangeCallback` static 필드 보관 패턴 (delegate GC 방지 — 스펙에 명시되지 않았으나 필수)
5. **SystemFilter.cs**: COM 초기화 순서 이슈 — 감지 스레드에서 최초 접근 시 COM 미초기화로 실패 가능. Phase 03에서 메인 스레드 선제 초기화 필요

### Phase 03 (`prompts/03_CORE_LOOP.md`)

1. **Program.cs**: 메인 스레드 COM STA 초기화 + `SystemFilter.ShouldHide(0,0,_config)` 선제 호출로 static constructor 강제 실행 (Phase 02 보완 5 해결)
2. **Program.cs**: `HandleImeStateChanged`/`HandleFocusChanged`에 `EventTriggers` 가드 추가 (스펙 명시이나 의사코드에서 누락)
3. **Program.cs**: `CleanupPreviousTrayIcon()` unsafe 블록 — `NOTIFYICONDATAW` fixed char 필드 때문에 `sizeof` 사용 필수
4. **Program.cs**: 오버레이 윈도우 클래스 별도 등록 추가 (스펙은 메인 클래스만 등록, `CreateOverlayWindow`의 `OverlayClassName` 미등록 상태)
5. **Program.cs**: `WM_DESTROY`에 `hwnd == _hwndMain` 가드 추가 (오버레이와 WndProc 공유 시 오버레이 파괴로 앱 종료 방지)
6. **Program.cs**: `HandleConfigChanged()`에 `Logger.SetLevel` 재호출 추가
7. **Program.cs**: `OnProcessExit`에 `Ole32.CoUninitialize()` 추가 (메인 스레드 COM 정리)
8. **Program.cs**: `CaretTracker.GetCaretPosition` 반환값 nullable 5-tuple 처리 — `result is { } caret` 패턴 매칭
9. **Program.cs**: `MainClassName` const 선언으로 매직 스트링 제거 (P3)
10. **CaretTracker.cs**: tier-1 `TryTier1WithRetry` 추가 — `rcCaret==(0,0,0,0)` 시 `CaretPollIntervalMs`(50ms) 간격 최대 3회 재시도, API 실패는 즉시 null
11. **Win32Types.cs**: `WM_DPICHANGED = 0x02E0` 상수 추가
12. **DefaultConfig.cs**: `UiaLoopIntervalMs = 100` 상수 추가 (P3 매직 넘버 제거)

### Phase 04 (`prompts/04_RENDERING.md`)

1. **Gdi32.cs**: `GetTextExtentPoint32W`, `GetStockObject` P/Invoke 추가 (F-S01 라벨 고정 너비 + NULL_PEN)
2. **Win32Types.cs**: `FW_NORMAL/FW_BOLD`, `DEFAULT_CHARSET`, `CLEARTYPE_QUALITY`, `NULL_PEN`, `DT_CALCRECT` 등 GDI 폰트/도형 상수 추가
3. **AppMessages.cs**: `TIMER_ID_FADE/HOLD/HIGHLIGHT/TOPMOST` 타이머 ID 상수 추가
4. **Overlay.cs**: GDI NULL_PEN 필수 — RoundRect/Ellipse는 선택된 펜으로 테두리를 그림. 미선택 시 1px 검은 테두리 발생
5. **Overlay.cs**: GetStockObject 반환 핸들은 시스템 소유 — DeleteObject/SafeHandle 사용 금지
6. **Overlay.cs**: premultiplied alpha 후처리 — GDI 출력은 non-premultiplied, DrawTextW 안티앨리어싱 엣지 포함
7. **Overlay.cs**: DIB biHeight 음수 설정 (top-down) — 0,0이 좌상단이 되도록
8. **Overlay.cs**: `_lastAlpha` 필드 추가 — Show/UpdateColor 시 현재 알파 유지 (페이드 중 깜빡임 방지)
9. **Overlay.cs**: EnsureDib() SelectObject 순서 — 새 비트맵 SelectObject 후 기존 비트맵 Dispose (선택된 GDI 객체 삭제 방지)
10. **DpiHelper.cs**: `GetRawDpi` 메서드 추가 — GetScale은 dpiX만 반환하나 HFONT 생성에 dpiY 필요
11. **Program.cs**: `_indicatorVisible = true` 3개 핸들러에 추가 (HandleImeStateChanged/HandleFocusChanged/HandleCaretUpdated)
12. **Models/**: 9개 enum 파일 신규 — 문자열 비교 → enum 전환 (P3). `JsonStringEnumConverter<T>` + `[JsonStringEnumMemberName]` NativeAOT 패턴
13. **VirtualDesktop.cs**: IVirtualDesktopManager COM 인터페이스를 SystemFilter.cs → Native/ 이동 (P4)

### Phase 05 (`prompts/05_SYSTEM_UI.md`)

1. **Tray.cs**: HandleCallback 분기를 Program.cs에서 처리 — `_indicatorVisible` 접근 필요하므로 Tray.cs가 아닌 Program.cs의 HandleTrayCallback에서 LOWORD(lParam) 분기
2. **Tray.cs**: I18n.cs 미존재 시 tooltip 텍스트를 Tray.cs 내부 private 메서드로 처리 (Phase 06에서 I18n으로 이관)
3. **Tray.cs**: Settings.cs 미존재 시 config 파일 경로 및 JSON 저장을 inline 처리 (Phase 06에서 Settings로 이관)
4. **Tray.cs**: schtasks 시작 프로그램 등록 로직을 Tray.cs 내부에 구현 (별도 Startup.cs 분리 불필요)
5. **AppConfigJsonContext**: `WriteIndented = true` 추가 — 사람이 읽을 수 있는 config.json 출력
6. **Program.cs**: 트레이 메뉴 변경 → config.json 저장 → ~5초 후 mtime 감지 → 중복 리로드 이슈. Phase 06에서 `Settings.Save()` 직후 `_lastConfigMtime` 갱신으로 해결 (자기 저장 감지 방지)
7. **Program.cs**: volatile 필드(`_config`)에 `ref` 사용 불가 → HandleMenuCommand에서 `Action<AppConfig> updateConfig` 콜백 패턴으로 처리
8. **Win32Types.cs**: `TPM_RIGHTBUTTON`, `NIM_SETVERSION`, `LOWORD_MASK` 상수 추가 (스펙에 누락)
9. **DefaultConfig.cs**: `ConfigFileName`, `AppDataFolderName`, `GetDefaultConfigPath()` 중앙화 — Program.cs와 Tray.cs 중복 제거 (P4)

### Phase 06 (`prompts/06_CONFIG.md`)

1. **Settings.cs**: static class 패턴 (프로젝트 전체 static 패턴 일관성)
2. **Settings.cs**: AppConfigBuilder 불필요 — record `with` 표현식으로 충분
3. **Settings.cs**: `ReadCommentHandling = JsonCommentHandling.Skip`, `AllowTrailingCommas = true` 추가 — config.json 내 사용자 주석/후행 쉼표 허용
4. **Settings.cs**: LoadFromFile 파이프라인: Deserialize → Migrate → Validate → ThemePresets.Apply → return
5. **I18n.cs**: Dictionary 대신 `_isKorean` bool 플래그 + 삼항 연산자 패턴 — NativeAOT 친화적, zero allocation
6. **I18n.cs**: 시스템 언어 감지 — `InvariantGlobalization: true`로 CultureInfo 사용 불가 → `GetUserDefaultUILanguage()` P/Invoke 사용
7. **SystemFilter.cs**: `GetProcessName`/`GetClassName` private → internal 접근 수준 변경 (Settings.cs 앱 프로필에서 재사용)
8. **Program.cs**: 포그라운드 윈도우 변경 시 `Settings.ResolveForApp` 호출 → 앱 프로필 적용 (enabled: false면 인디케이터 숨김)
9. **ImeStatus.cs**: Detect 2-param → 3-param 변경 (앱 프로필의 detection_method 오버라이드 반영)
10. **User32.cs**: `GetWindowTextW`, `GetWindowTextLengthW` P/Invoke 추가 (앱 프로필 title 매칭용)
11. **Kernel32.cs**: `GetUserDefaultUILanguage()` P/Invoke 추가 (시스템 언어 감지용)

### Phase 07 (`prompts/07_FINAL.md`)

1. **UiaInterfaces.cs**: UIA COM 인터페이스를 Native/에 배치 (P4). vtable 레이아웃을 위한 placeholder 메서드 사용 (인터페이스 상속 대신)
2. **UiaClient.cs**: `ConcurrentQueue<UiaRequest>` + `ManualResetEventSlim` 큐 패턴 — STA 스레드 통신
3. **UiaClient.cs**: COM 객체 정리 — 중간 객체(element, pattern, range)를 finally 블록에서 `Marshal.Release`
4. **OleAut32.cs**: `SafeArrayAccessData`/`SafeArrayUnaccessData`/`SafeArrayGetUBound`/`SafeArrayGetLBound`/`SafeArrayDestroy` 추가 — GetBoundingRectangles SAFEARRAY(double) 처리
5. **ThemePresets.cs**: Config/ 폴더에 배치 (Settings.cs/DefaultConfig.cs와 동일 카테고리). system 테마는 `GetSysColor(COLOR_HIGHLIGHT)` 사용
6. **Overlay.cs**: DebugOverlay — `SetDebugInfo(DebugInfo?)` static 메서드로 디버그 정보 전달 (Show() 시그니처 변경 회피)
7. **Overlay.cs**: LabelStyle 분기 — "text" (한/En/EN), "dot" (색상 점), "icon" (ㄱ/A) 3종 지원
8. **CaretTracker.cs**: TryTier2 null placeholder → `UiaClient.GetCaretBounds()` 실제 호출로 교체. `SkipUiaForProcesses` 가드 추가
9. **Tray.cs**: 포터블/설치 모드 접두어 — `Settings.IsPortableMode` → "[포터블]"/"[설치]" tooltip 표시
10. **Program.cs**: HandlePowerResume — IME 재감지 + `Overlay.HandleDpiChanged()`. HandleSettingChange — 고대비 감지 + ThemePresets 재적용

## Commit Convention

```
phase-XX: 단계 설명
```

## Phase Status

- [x] Phase 01: Foundation (P/Invoke, Models, Utils, Config)
- [x] Phase 02: Detection (IME 3-tier + Caret 4-tier + SystemFilter)
- [x] Phase 03: Core Loop (Program.cs + 3-thread + event pipeline)
- [x] Phase 04: Rendering (Overlay + Animation)
- [x] Phase 05: System UI (Tray + Hotkey)
- [x] Phase 06: Config (Settings + I18n)
- [x] Phase 07: Final (UIA + Advanced + Build)

## Spec Files

단계별 구현 명세는 `prompts/` 폴더에 있다:
- `prompts/00_TEAM_ION.md` — 팀 구성 + 워크플로우 + 제약 조건
- `prompts/01_FOUNDATION.md` ~ `07_FINAL.md` — 단계별 상세 명세
