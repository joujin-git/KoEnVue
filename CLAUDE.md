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
├── Native/      P/Invoke (DLL별 1파일) + Win32Types.cs + SafeGdiHandles.cs + AppMessages.cs + VirtualDesktop.cs
├── Models/      AppConfig (record) + 13개 enum (ImeState, IndicatorStyle, Placement, DisplayMode, PositionMode, CaretPlacement, LabelShape, FontWeight, NonKoreanImeMode, DetectionMethod, CaretMethod, AppFilterMode, LogLevel)
├── Detector/    ImeStatus, CaretTracker, SystemFilter, UiaClient
├── UI/          Overlay (GDI 렌더링), Animation (WM_TIMER 상태 머신), Tray (시스템 트레이), TrayIcon (GDI 아이콘 생성)
├── Config/      DefaultConfig
├── Utils/       DpiHelper, ColorHelper, Logger
└── Program.cs   메인 루프 (3-스레드 관리)
```

## Build & Run

```bash
dotnet build                          # 빌드
dotnet publish -r win-x64 -c Release  # NativeAOT 배포
```

csproj에 `NoWarn: SYSLIB1051` 설정됨 (.NET 10 LibraryImport IntPtr 진단 억제).

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
- [ ] Phase 06: Config (Settings + I18n)
- [ ] Phase 07: Final (UIA + Advanced + Build)

## Spec Files

단계별 구현 명세는 `prompts/` 폴더에 있다:
- `prompts/00_TEAM_ION.md` — 팀 구성 + 워크플로우 + 제약 조건
- `prompts/01_FOUNDATION.md` ~ `07_FINAL.md` — 단계별 상세 명세
