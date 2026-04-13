# 00 — Context & Target Structure

→ Next: [01 — Stage 0: 기준선 고정](01-stage0-baseline.md)

## Context

KoEnVue는 현재 C# 14 / .NET 10 / NativeAOT 단일 exe(~4.7MB)로 동작하는 한글/영문 IME 상태 표시기이며, 코드가 기술 레이어(Native, Models, UI, Detector, Config, Utils) 기준으로 정리되어 있다. 사용자는 이 프로젝트에서 작성한 인프라 코드(P/Invoke 파운데이션, GDI 레이어드 오버레이, Win32 모달 다이얼로그 툴킷, 비동기 파일 로거, JSON 핫-리로드 설정 프레임워크, 시스템 트레이 래퍼, WM_TIMER 기반 애니메이션 스테이트 머신)를 **다른 Windows 데스크톱 프로젝트에서 바로 재사용**하고 싶어한다.

Phase 1 감사(Explore 에이전트 2대 병렬)로 확인한 현재 상태:

- **Core-Generic (20 파일)**: Native/* (AppMessages 제외), Utils/ColorHelper, Utils/Logger, Utils/Win32DialogHelper, Models/LogLevel — 그대로 재사용 가능
- **Core-WithConfig (8 파일)**: Overlay, Animation, Settings, DefaultConfig, ThemePresets, SystemFilter, ImeStatus, I18n — 매개변수 분리 후 재사용 가능
- **App-Specific (18 파일)**: 나머지 Models enum, Tray, TrayIcon, SettingsDialog, Program — KoEnVue 전용
- **Mixed (5 파일)**: AppConfig, SystemFilter, DpiHelper (Config 의존), Tray+TrayIcon, Logger (AppConfig 파라미터)

### 발견된 중복/커플링 핫스팟 6건 + 레이어 위반 1건

1. `SettingsDialog.TryNormalizeHexColor`(UI/SettingsDialog.cs:1003-1019) → ColorHelper로 이동 필요
2. 3개 다이얼로그(CleanupDialog/ScaleDialog/SettingsDialog)가 `SafeFontHandle` 대신 수동 `CreateFontW`/`DeleteObject` 사용
3. `CleanupDialog` 모달 루프(Tray.cs:815-821)가 `IsDialogMessageW` 래핑 누락 — Tab/ESC 미작동
4. Config→Detector 레이어 위반: Settings.cs:425-426이 `SystemFilter.GetProcessName`/`GetClassName` 호출 (+ `using KoEnVue.Detector;` at Settings.cs:4)
5. `Tray.cs`가 1169 라인 단일 파일에 트레이 + CleanupDialog + ScaleInputDialog 혼재
6. AppConfig god-object 과잉 전달: `Overlay.BeginDrag`, `Overlay.HandleMoving`, `Overlay.UpdateColor`, `ImeStatus.Detect`, `Logger.Initialize`가 각각 1-4개 필드만 읽으면서 AppConfig 전체를 받음
7. **추가 레이어 위반 (Stage 2 precondition)**: `Utils/DpiHelper.cs:2`가 `using KoEnVue.Config;`로 `DefaultConfig.BASE_DPI`([DpiHelper.cs:13](../../Utils/DpiHelper.cs#L13)) 를 참조. Stage 2가 DpiHelper를 `Core/Dpi/`로 이동하는 순간 Core→App 의존이 생기므로, 이동 전에 `const int BASE_DPI = 96;` 인라인으로 Config 의존을 끊어야 한다 (Stage 2 첫 substep).

### "KoEnVue 누출" 잔여

- `ImeState` enum이 Animation/Overlay/Tray 분기에 직접 등장 → Core 추출 시 제거 대상
- 윈도우 클래스명 "KoEnVueOverlay"가 `Config/DefaultConfig.cs:88`과 `Models/AppConfig.cs:134`에, "KoEnVueMain"이 `Program.cs:39`에 하드코딩
- 트레이 메뉴 문자열은 I18n.cs에 있음(I18n 자체가 App 전용이므로 누출 아님). Core 측 다이얼로그/트레이 기반은 문자열을 콜백으로 주입받아야 함
- **확인된 반례**: IME 라벨 "한"/"En"/"EN"은 `AppConfig.cs:44-46`의 `HangulLabel`/`EnglishLabel`/`NonKoreanLabel` init 기본값으로 주입되고 Overlay는 `GetLabelText`(Overlay.cs:819-824) 스위치에서 config를 읽음 → 하드코딩 아님

## 이 계획의 목표

1. **폴더/네임스페이스 분리**: 단일 csproj 유지, `Core/` + `App/` 최상위 폴더 구조, 네임스페이스 `KoEnVue.Core.*` / `KoEnVue.App.*`로 이원화. Core는 `KoEnVue.App.*`를 **어떤 경로로도** 참조하지 않도록 강제.
2. **중복 구현 전면 제거 (P4 강화)**: 위 핫스팟 6건 + 추가 레이어 위반 1건 모두 해소. 핫스팟 1~6은 Stage 1에서 in-place 처리, #7 (DpiHelper) 은 Stage 2의 이동 직전 substep에서 처리한다.
3. **재사용 가능한 기반 완전 추출**: `LayeredOverlayBase`, `JsonSettingsManager<T>`, `NotifyIconManager`를 Core로 분리해 KoEnVue-specific 로직 없이도 컴파일되게 만듦.
4. **CLAUDE.md와 docs/KoEnVue_PRD.md 현행화** — 계획의 마지막 단계에 명시적으로 포함.
5. **단계별 검증 + 최종 전체 검증** — 각 단계는 빌드·런타임 게이트를 통과해야 다음 단계로 넘어감.

사용자가 Phase 3에서 확정한 결정:
- 깊이: **LayeredOverlayBase / JsonSettingsManager\<T\> / NotifyIconManager 추출 포함**
- 레이아웃: **단일 csproj + Core/App 폴더 + KoEnVue.Core.* / KoEnVue.App.* 네임스페이스**

## Target Structure

```
KoEnVue/
├── Core/                                    (KoEnVue.Core.* — 재사용 가능)
│   ├── Native/                              KoEnVue.Core.Native
│   │   ├── Dwmapi.cs
│   │   ├── Gdi32.cs
│   │   ├── Imm32.cs
│   │   ├── Kernel32.cs
│   │   ├── Ole32.cs
│   │   ├── OleAut32.cs
│   │   ├── Shcore.cs
│   │   ├── Shell32.cs
│   │   ├── User32.cs
│   │   ├── Win32Types.cs                    (P4 시임 — 모든 struct/constant)
│   │   ├── SafeGdiHandles.cs                (P4 시임 — SafeFontHandle 포함)
│   │   └── VirtualDesktop.cs
│   ├── Color/
│   │   └── ColorHelper.cs                   (+ TryNormalizeHex 신규 추가)
│   ├── Dpi/
│   │   └── DpiHelper.cs                     (BASE_DPI 인라인으로 Config 의존 제거 — Stage 2 선행 substep)
│   ├── Logging/
│   │   ├── LogLevel.cs                      (Models/에서 이동)
│   │   └── Logger.cs                        (Initialize(LogLevel) 시그니처)
│   ├── Config/
│   │   ├── JsonSettingsFile.cs              (파일 I/O + mtime 폴링)
│   │   └── JsonSettingsManager.cs           (제네릭 Merge→Deserialize→Migrate→Validate 파이프라인)
│   ├── Windowing/
│   │   ├── Win32DialogHelper.cs             (기존)
│   │   ├── ModalDialogLoop.cs               (IsDialogMessageW + EnableWindow 패턴 공통화)
│   │   ├── LayeredOverlayBase.cs            (드래그/스냅/WM_MOVING/DPI 변화/UpdateLayeredWindow 추출)
│   │   ├── WindowProcessInfo.cs             (GetProcessName/GetClassName — Detector→Core 이동)
│   │   └── WindowFilter.cs                  (SystemFilter의 제네릭 부분 추출)
│   ├── Tray/
│   │   └── NotifyIconManager.cs             (Shell_NotifyIconW + NIF_SHOWTIP + WM_CONTEXTMENU 래퍼)
│   └── Animation/
│       └── OverlayAnimator.cs               (WM_TIMER 스테이트 머신 — 페이즈/이즈/슬라이드)
│
├── App/                                     (KoEnVue.App.* — IME 전용)
│   ├── Models/
│   │   ├── AppConfig.cs
│   │   ├── AppFilterMode.cs
│   │   ├── AppProfileMatch.cs
│   │   ├── Corner.cs
│   │   ├── DetectionMethod.cs
│   │   ├── DisplayMode.cs
│   │   ├── FontWeight.cs
│   │   ├── ImeState.cs                      (App 내 전용 — Core로 누출 금지)
│   │   ├── NonKoreanImeMode.cs
│   │   ├── Theme.cs
│   │   ├── TrayClickAction.cs
│   │   └── TrayIconStyle.cs
│   ├── Config/
│   │   ├── DefaultConfig.cs
│   │   ├── Settings.cs                      (JsonSettingsManager<AppConfig> 소비)
│   │   └── ThemePresets.cs
│   ├── Detector/
│   │   ├── ImeStatus.cs                     (Detect(DetectionMethod) 시그니처)
│   │   └── SystemFilter.cs                  (WindowFilter 베이스 상속 또는 조립)
│   ├── Localization/
│   │   └── I18n.cs                          (Utils/에서 이동)
│   └── UI/
│       ├── AppMessages.cs                   (Native/에서 이동 — WM_APP+N은 앱 전용)
│       ├── Overlay.cs                       (정적 파사드 + LayeredOverlayBase 컴포지션)
│       ├── Animation.cs                     (OverlayAnimator 소비)
│       ├── Tray.cs                          (NotifyIconManager 소비, 메뉴/스케줄러/툴팁)
│       ├── TrayIcon.cs
│       └── Dialogs/
│           ├── CleanupDialog.cs             (Tray.cs에서 분리)
│           ├── ScaleInputDialog.cs          (Tray.cs에서 분리)
│           └── SettingsDialog.cs            (UI/에서 이동)
│
├── Program.cs                                (App 진입점 — 경로는 루트 유지)
├── app.manifest
├── KoEnVue.csproj
├── CLAUDE.md
└── docs/
    ├── KoEnVue_PRD.md
    └── reorg/                                (이 폴더)
```

**일방 의존 규칙**: `App → Core` 만 허용. `git grep "KoEnVue\.App" Core/`는 0건이어야 함 — Stage 7 검증 게이트에서 grep으로 강제.

## 에이전트 팀 구성 원칙

- **Explore**: 감사/grep/읽기 전용. Phase 1과 Stage 7에서 사용.
- **Plan**: 하위 단계 설계 검증이 필요할 때. Stage 1과 Stage 4에서 사용.
- **general-purpose**: 실제 파일 편집 담당. Stage 1/2/3/4/5/6에서 사용.
- **병렬 실행 원칙**: 파일이 서로 겹치지 않는 작업은 병렬. 동일 파일을 만지는 작업은 직렬.
- **각 에이전트 브리핑 필수 포함**:
  1. 해당 에이전트가 다룰 파일 목록
  2. 만지지 말아야 할 파일 목록
  3. P1-P5 + 중복 금지 규칙
  4. 해당 단계의 검증 명령어

---

← Back to [README](README.md) | → Next: [01 — Stage 0](01-stage0-baseline.md)
