# Architecture — Core/App layer split & reuse contract

This document is the source of truth for how `Core/` and `App/` are wired together, which modules live where, and how to extract `Core/` into another project.

Top-level entry point for the project is **[CLAUDE.md](../CLAUDE.md)**. Product spec is **[KoEnVue_PRD.md](KoEnVue_PRD.md)**.

---

## 1. Two-thread model

```
Main thread (UI):
  • Message loop (GetMessageW / DispatchMessageW)
  • Layered window rendering (LayeredOverlayBase + GDI DIB)
  • Tray icon (NotifyIconManager → Shell_NotifyIconW)
  • Animation (WM_TIMER — 5-state machine + highlight/slide sub-phases)
  • CAPS LOCK poll (WM_TIMER, 200 ms)

Detection thread (BG):
  • 80 ms IME polling (ImeStatus.Detect)
  • Foreground window / focus tracking
  • SystemFilter evaluation (9 hide conditions)
  • ConfigFile mtime check (every ~62 polls ≈ 5 s)
  • Cross-thread signalling via User32.PostMessageW(hwndMain, WM_APP + N)
```

See **[implementation-notes.md § Detection](implementation-notes.md#detection)** for the full message pipeline.

---

## 2. Source tree

```
KoEnVue/
├── Core/                    Reusable infrastructure — namespace KoEnVue.Core.*
│   ├── Native/              P/Invoke surfaces + Win32Types.cs + SafeGdiHandles.cs +
│   │                        Advapi32.cs (process token / SID → integrity level RID)
│   ├── Color/               ColorHelper (Hex ↔ COLORREF ↔ RGB)
│   ├── Dpi/                 DpiHelper (per-monitor DPI queries, work area)
│   ├── Http/                HttpClientLite (WinHTTP-backed sync GET — ~40 KB)
│   ├── Logging/             Logger + LogLevel
│   ├── Config/              JsonSettingsManager<T> + JsonSettingsFile
│   ├── Animation/           OverlayAnimator + AnimationConfig + AnimationTimerIds
│   ├── Tray/                NotifyIconManager (Shell_NotifyIconW wrapper)
│   └── Windowing/           LayeredOverlayBase + OverlayStyle/OverlayMetrics +
│                            LayeredCursorBase + CursorStyle/CursorMetrics +
│                            DibSectionFactory + LayeredWindowBlit +
│                            ModalDialogLoop + DialogShell + Win32DialogHelper +
│                            ScrollableDialogHelper + WindowProcessInfo +
│                            WindowSnapHelper + TopmostWatchdog
│
├── App/                     KoEnVue-specific layer — namespace KoEnVue.App.*
│   ├── Models/              AppConfig record + all enums (ImeState, Theme, ...)
│   ├── Bootstrap/           AdminElevation (admin_elevation self-relaunch, UIPI 우회)
│   ├── Config/              DefaultConfig, Settings facade, ThemePresets,
│   │                        AppSettingsManager : JsonSettingsManager<AppConfig>
│   ├── Detector/            ImeStatus + SystemFilter + ImeConstants
│   ├── Localization/        I18n (Ko/En UI text, GetUserDefaultUILanguage)
│   ├── Update/              UpdateChecker + GitHubRelease + UpdateInfo
│   └── UI/                  Overlay facade + Animation facade + Tray + TrayIcon +
│       │                    CursorRenderer (analytical AA pixel shader) +
│       └── Dialogs/         CleanupDialog + ScaleInputDialog + SettingsDialog(×3)
│
├── Program.cs               Main message loop + WndProc + detection thread.
│                            MainImpl bootstrap 순서: 0 LogProvider.Sink → 0a
│                            RegisterCrashHandlers → 0b Settings.Load → 0b-1
│                            AdminElevation.WaitForRelaunchParentIfAny (PR-15
│                            후속 fix — 트레이 토글 재시작 경로 race 차단,
│                            정상 부팅 noop) → 0c AdminElevation.TryRelaunchAsAdmin
│                            (PR-15) → 1 mutex → 2 cleanup tray →
│                            4 Logger.Initialize → ... (mutex 전 self-elevation
│                            으로 race 0). cross-thread hwnd 3종
│                            (`_hwndMain` / `_hwndOverlay` / `_hwndCursorOverlay`) 은
│                            `volatile` 마킹 (PR-18 5/5) — `_config` / `_lastImeState` /
│                            `_indicatorVisible` 와 비대칭이던 ARM64 weak memory model
│                            회귀 표면을 회복.
├── Program.Bootstrap.cs     partial class: mutex, window classes, teardown,
│                            second-instance activation, TaskbarCreated tray recovery
├── KoEnVue.csproj
│
└── tests/KoEnVue.Tests/     xUnit project (dev-only, P1 예외). InternalsVisibleTo
    └── Unit/                — KoEnVue.csproj 가 KoEnVue.Tests 에 internal 노출 (PR-10).
        ├── ColorHelperTests.cs        (PR-10)  Hex ↔ COLORREF 5 메서드
        ├── DpiHelperTests.cs          (PR-10)  Scale 2 오버로드 + BASE_DPI
        ├── SettingsValidateTests.cs   (PR-10)  Validate clamp 12 케이스
        ├── StartupTaskXmlTests.cs     (PR-20)  schtasks XML 6 PASS — PR-03 D
        │                                       (LogonTrigger.UserId) + PR-15 RunLevel
        │                                       분기 + Command escape 박제
        ├── XmlEntityCodecTests.cs     (PR-20)  XML 1.0 5 entity + 순서 invariant
        │                                       9 PASS (Theory InlineData 5 분리)
        └── SanitizeLogPathTests.cs    (PR-20)  4 거부 + 4 허용/폴백 10 PASS
                                                (PR-03 B1 보안 표면, NUL 문자 invalid
                                                 char 채택 — .NET 8+ throw 보장)
```

Every file in `Core/` is reusable in another Windows desktop project; every file in `App/` is product-specific. `tests/` 는 release exe 에 포함되지 않는 dev-only 예외 (P1).

---

## 3. Reusable Core modules

| Module | Purpose | Public surface |
|--------|---------|----------------|
| [Core/Native/*](../Core/Native/) | Raw P/Invoke surface. `Win32Types.cs` centralizes every struct + the `Win32Constants` class (SM/WS/DWMWA/MB_*/etc. — `MB_OK = 0x00000000` 은 PR-15 후속 fix #2 (admin → 일반 down-grade) 가 `User32.MessageBoxW` 의 `uType: 0` hard-code 정리 + 신규 down-grade 안내 호출 site 통일을 위해 추가). `SafeGdiHandles.cs` hosts `SafeFontHandle`, `SafeIconHandle`, etc. `WinHttp.cs` hosts `SafeWinHttpHandle : SafeHandleZeroOrMinusOneIsInvalid` | `[LibraryImport]` only, no `[DllImport]` |
| [Core/Native/Wtsapi32.cs](../Core/Native/Wtsapi32.cs) | WTS session notification for polling-free lock/unlock/logoff detection via `WM_WTSSESSION_CHANGE` — backbone of `HideOnLockScreen` | `WTSRegisterSessionNotification`, `WTSUnRegisterSessionNotification` |
| [Core/Native/Advapi32.cs](../Core/Native/Advapi32.cs) | Process token + SID 조회로 mandatory integrity level RID 추출 (PR-15). `OpenProcessToken` + `GetTokenInformation(TokenIntegrityLevel)` + `GetSidSubAuthority`/`Count` 4 P/Invoke 를 묶어 `GetCurrentProcessIntegrityLevelRid()` 한 메서드로 노출 — caller 가 `Win32Constants.SECURITY_MANDATORY_HIGH_RID` 등과 직접 비교. UIPI (Medium → High `WM_IME_CONTROL` 차단) 우회 필요 시 `App/Bootstrap/AdminElevation` 이 사용 | `OpenProcessToken`, `GetTokenInformation`, `GetSidSubAuthority`, `GetSidSubAuthorityCount`, `GetCurrentProcessIntegrityLevelRid` |
| [Core/Color/ColorHelper](../Core/Color/ColorHelper.cs) | Hex ↔ COLORREF ↔ RGB ↔ ARGB conversion. `HexToArgb` 는 `0xFFRRGGBB` 불투명 ARGB (커서 인디 셰이더가 픽셀 채널을 직접 쓸 때 — `HexToColorRef` 의 BGR 형제). Malformed hex returns 0 / `(0,0,0)` / `0xFF000000` instead of throwing, so a bad `config.json` doesn't leak GDI handles on the render hot path | `TryNormalizeHex`, `HexToColorRef`, `HexToArgb`, `HexToRgb`, `ColorRefToRgb`, `RgbToHex` |
| [Core/Dpi/DpiHelper](../Core/Dpi/DpiHelper.cs) | Per-monitor DPI queries. `BASE_DPI = 96` is inlined as a `const int` so the module has no `Config` dependency | `GetScale`, `GetWorkArea`, `GetRawDpi`, `GetMonitorFromPoint` |
| [Core/Http/HttpClientLite](../Core/Http/HttpClientLite.cs) | Synchronous HTTPS GET wrapper backed by WinHTTP. NativeAOT publish impact ~40 KB (vs ~2.5 MB for `System.Net.Http.HttpClient`). Response body cap 256 KB, all failure paths return `null` | `GetString(userAgent, host, path, extraHeaders?, timeoutMs = 10_000) → string?` |
| [Core/Logging/Logger](../Core/Logging/Logger.cs) + `LogLevel` | Async file logger. `ConcurrentQueue` + `ManualResetEventSlim` + dedicated drain thread. Single `.log → .log.old` rotation. **No `AppConfig` parameter** — Stage 3-A narrowed `Initialize` to primitives. **Queue cap** `MaxQueueSize = 10_000`: 회전 실패로 `_fileWriter = null` 상태가 지속되면 최고령부터 드롭하고 복구 시 누적 드롭 건수를 1회 경고로 기록 (무제한 성장 방지) | `Initialize(bool enabled, string? logFilePath, int maxSizeMb)`, `SetLevel(LogLevel)`, `Debug`/`Info`/`Warning`/`Error`, `Shutdown` |
| [Core/Config/JsonSettingsManager\<T\>](../Core/Config/JsonSettingsManager.cs) + [JsonSettingsFile](../Core/Config/JsonSettingsFile.cs) | Generic JSON-backed settings pipeline. `JsonTypeInfo<T>` injection is mandatory under NativeAOT + `JsonSerializerIsReflectionEnabledByDefault=false`. Five `protected virtual` hooks run in fixed order during `Load`: `ApplyNullSafetyNet` → `PostDeserializeFixup` → `Migrate` → `Validate` → `ApplyTheme`. `MergeWithDefaults` (PostDeserializeFixup 구현 — STJ source-gen 이 `init` 기본값을 reflection-off 에서 드롭하는 우회책) 은 **재귀 머지** (P0 fix, 2026-06-01): 양쪽 모두 객체인 키는 `MergeObjects` 로 내려가 사용자가 중첩 객체 (`event_triggers` / `advanced` 등) 를 **부분만** 지정해도 누락 형제 필드가 기본값을 보존 (이전 최상위-키-only 머지는 중첩 객체를 통째 교체 → 누락 형제를 `default(T)` 로 드롭하던 회귀). 배열·Dictionary 는 의도적으로 통째 교체 (사용자의 항목 축소 의도 보존). 사용자 JSON 은 `JsonDocumentOptions{ CommentHandling=Skip, AllowTrailingCommas=true }` 로 파싱해 소스 생성 컨텍스트와 일치 — 주석/트레일링 콤마가 든 **정상** config 가 머지 전처리에서 `JsonException` 으로 "손상" 오판되어 전체 디폴트 묵음 무시되던 회귀 차단. 가시성 `internal` (테스트 노출). Delete-safe hot reload (`File.Exists` pre-check) and corrupted-file spam prevention (mtime cache update inside `catch`) are baked in. **Atomic save**: `WriteAllText` writes to `path + ".tmp"` then `File.Move(tmp, path, overwrite: true)` — `MoveFileExW(MOVEFILE_REPLACE_EXISTING)` guarantees atomic rename on the same volume, so a crash mid-save cannot leave a truncated config file | `Load() → T`, `Save(T)`, `CheckReload() → bool`, `FilePath` |
| [Core/Animation/OverlayAnimator](../Core/Animation/OverlayAnimator.cs) + [AnimationConfig](../Core/Animation/AnimationConfig.cs) + `AnimationTimerIds` | 4-state machine (Hidden / FadingIn / Holding / FadingOut / Idle) + highlight/slide sub-phases, driven by `WM_TIMER`. 6 callbacks injected via constructor. `AnimationConfig` is a 17-field `record struct` where `AlwaysMode : bool` replaces `DisplayMode.Always`. `SetDimMode(bool)` replaces `NonKoreanImeMode.Dim` check — **Core never imports either enum**. TOPMOST 재적용은 `TopmostWatchdog` 인스턴스로 위임 (PR-08 C5) — `OnWmTimer` 첫 줄에서 `TryHandleTimer` 로 short-circuit | `UpdateConfig`, `SetDimMode`, `TriggerShow → bool wasHidden`, `TriggerHide(forceHidden)`, `HandleTimer(timerId)` |
| [Core/Windowing/TopmostWatchdog](../Core/Windowing/TopmostWatchdog.cs) | 주기적 `SetWindowPos(HWND_TOPMOST)` 재적용 단일 책임 워치독. 호출자가 (hwndTimer, timerId, intervalMs, onTick) 을 주입; `SetInterval(int)` 으로 동적 주기 변경, `TryHandleTimer(nuint)` 가 자기 ID 만 골라 onTick 호출. OverlayAnimator 의 5 트랙 중 시간 / 상태 의존성 없는 단일 트랙이라 분리해도 회귀 위험 최소 (PR-08 C5, [dev-notes](dev-notes/2026-05-21-animator-decomposition-deferral.md)) | `Start`, `Stop`, `SetInterval`, `TryHandleTimer`, `IntervalMs` |
| [Core/Tray/NotifyIconManager](../Core/Tray/NotifyIconManager.cs) | `Shell_NotifyIconW` wrapper. Captures `(hwndOwner, callbackMessage, iconGuid)` once at construction. Preserves `NIF_SHOWTIP` on `NIM_ADD`/`NIM_MODIFY` (Win7+ under `NOTIFYICON_VERSION_4` silently discards the tooltip without it). `Add`는 `NIM_ADD` 반환값을 확인하여 실패 시 `_added = false` 유지 + 즉시 반환. `UpdateIcon`/`UpdateTooltip`/`UpdateIconAndTooltip` 도 `NIM_MODIFY` 반환값을 확인해 실패 시 `Logger.Debug` 로 기록 (트레이 쉘 재시작 직후 등 일시적 실패 진단용). **`hIcon` ownership stays with the caller** | `Add(hIcon, tip)`, `UpdateIcon`, `UpdateTooltip`, `UpdateIconAndTooltip`, `Remove() → bool` |
| [Core/Windowing/LayeredOverlayBase](../Core/Windowing/LayeredOverlayBase.cs) | Layered window + DIB + DPI + drag engine. `IDisposable` instance constructed as `(IntPtr hwnd, Func<hdc, style, metrics, (w, h)> renderToDib)`. 생성자에서 `CreateCompatibleDC` 실패 시 `InvalidOperationException`. `EnsureDib`는 `CreateDIBSection` 실패 시 `_ppvBits`를 보존하여 해제된 메모리 참조 방지. `EnsureFont`는 `CreateFontW` 가 `IntPtr.Zero` 를 반환하면 `Logger.Warning` + 조기 반환으로 기존 폰트/캐시 키를 보존해 다음 호출에서 재시도를 유도한다 (빈 HFONT 가 캐시에 고착되는 회귀 방지). Engine owns **DPI multiplication** internally via `Kernel32.MulDiv(fontSize, dpiY, 72)`. 창 엣지 스냅은 [WindowSnapHelper](../Core/Windowing/WindowSnapHelper.cs) 위임 (PR-08 C6). DIB section 생성은 [DibSectionFactory.TryCreate](../Core/Windowing/DibSectionFactory.cs) 위임 + `UpdateLayeredWindow` 블리트 2 호출 site (`UpdateOverlayDuringDrag` / `UpdateOverlay`) 는 [LayeredWindowBlit.Blit](../Core/Windowing/LayeredWindowBlit.cs) 위임 (PR-18). Holds `_fixedLabelWidth` / `_lastStyle` / `_currentDpiScale` / drag state internally | `Render`/`Show`/`Hide`/`UpdateAlpha`/`UpdatePosition`/`UpdateScaledSize`/`HandleDpiChanged`/`ForceTopmost`/`BeginDrag(snapToWindows)`/`EndDrag() → (x, y)`/`HandleMoving(ref RECT, style, snapToWindows, snapThresholdPx)`/`GetBaseSize`/`GetLastPosition`/`Hwnd`/`IsVisible` |
| [Core/Windowing/WindowSnapHelper](../Core/Windowing/WindowSnapHelper.cs) | 드래그 중 윈도우 엣지 스냅 단일 책임 모듈 (PR-08 C6). `CollectTargets(IntPtr ownerHwnd)` 이 `EnumWindows` 로 가시 / non-cloaked / non-iconic / `>= 80×80` 인 다른 창의 시각 프레임 RECT 를 정적 캐시 (`s_targets` + `s_ownerHwnd`) 에 수집. `ApplySnap(ref RECT, xLocked, yLocked, dpiScale, snapThresholdPx, snapGapPx)` 가 현재 위치 모니터 work area 포함 4 엣지 쌍 (L↔L/L↔R/R↔L/R↔R, T↔T/T↔B/B↔T/B↔B) 을 검사해 최단 거리 스냅을 적용. `[UnmanagedCallersOnly] EnumWindowsCallback` 의 인스턴스 필드 미접근 제약을 처리하기 위한 정적 브리지 — 콜백 본문은 `try/catch` 로 감싸 관리 예외가 unmanaged 경계를 넘어 NativeAOT 프로세스를 종료시키지 않도록 방어하고 해당 창만 스킵 후 열거 계속 (감사 High ②, conventions §11) | `CollectTargets`, `ClearTargets`, `ApplySnap` |
| [Core/Windowing/LayeredCursorBase](../Core/Windowing/LayeredCursorBase.cs) | 커서 추종 인디 (동심원 3개 + 헤일로) 전용 레이어드 윈도우 + DIB 엔진. `LayeredOverlayBase` 의 형제 — 폰트 / 드래그 / 라벨 측정 / WindowSnapHelper 일체 제거하고 DIB 생성 + premultiply + UpdateLayeredWindow 만 책임. 콜백 시그니처는 `Func<IntPtr ppvBits, CursorStyle, CursorMetrics, (int w, int h)>` — main 의 Gdi32 헤더 0 변경 (GetCurrentObject / GetObjectDibSection 미사용, DrawTextW / RoundRect 사용 없이 픽셀 셰이딩만). flip-flop 가드 (`_lastRenderedStyle == style`) + DPI 변경 시 캐시 무효화 (`HandleDpiChanged`). DIB section 생성은 [DibSectionFactory.TryCreate](../Core/Windowing/DibSectionFactory.cs) 위임 + `UpdateOverlay` 의 `UpdateLayeredWindow` 블리트는 [LayeredWindowBlit.Blit](../Core/Windowing/LayeredWindowBlit.cs) 위임 (PR-18). `ApplyPremultipliedAlpha` 의 `a==0 && RGB!=0` 가드는 메인 `LayeredOverlayBase` 동명 가드 (GDI AA 엣지 보존 — a=255 복구) 와 **의미가 다르다** — cursor 셰이더는 alpha 를 명시적으로 쓰므로 그 픽셀은 `ShadeDib` 의 round-down 부산물 (avgAlpha × 255 < 0.5), RGB 까지 0 으로 정리해 외곽 잡티 차단. **P4 예외 정당화**: 메인 인디 알파 race 미해결 영역에 변경면 추가를 차단하기 위한 의도적 엔진 분리 — PR-18 의 두 helper 가 DIB 생성 + UpdateLayeredWindow 블리트 ~50 LOC 를 공유했고 남은 ~40 LOC `ApplyPremultipliedAlpha` 는 의미 차이로 의도적 분기 보존. [docs/dev-notes/2026-05-27-cursor-indicator.md](dev-notes/2026-05-27-cursor-indicator.md) + [docs/dev-notes/2026-05-28-pr-18-core-windowing.md](dev-notes/2026-05-28-pr-18-core-windowing.md) | `PrepareResources`, `Render`, `Show`, `Hide`, `UpdateAlpha`, `UpdatePosition`, `HandleDpiChanged`, `GetBaseSize`, `GetLastPosition`, `Hwnd`, `IsVisible` |
| [Core/Windowing/DibSectionFactory](../Core/Windowing/DibSectionFactory.cs) | top-down 32bpp BI_RGB DIB section 생성 + memory DC `SelectObject` 단일 책임 helper (PR-18). `LayeredOverlayBase.EnsureDib` 와 `LayeredCursorBase.EnsureDib` 가 동일 형식의 DIB section 생성 분기를 각자 들고 있던 ~25 LOC × 2 = 50 LOC 중복을 한 곳으로 합침. `TryCreate(memDC, width, height, out SafeBitmapHandle? bitmap, out IntPtr ppvBits)` 가 `CreateDIBSection` 실패 시 `bitmap=null` + `ppvBits=IntPtr.Zero` + `false` 반환 — caller 가 실패 래치 (`_dibFailureLogged`) / 이전 비트맵 dispose / dimension 캐시 / `_lastRenderedStyle = null` 갱신을 책임. premultiply 후처리는 두 엔진의 의미 차이로 본 helper 범위 외 | `TryCreate` |
| [Core/Windowing/LayeredWindowBlit](../Core/Windowing/LayeredWindowBlit.cs) | `AC_SRC_OVER + AC_SRC_ALPHA` premultiplied 모드의 `User32.UpdateLayeredWindow` 호출 + `BLENDFUNCTION` / `POINT` / `SIZE` 인라인 구성 단일 책임 helper (PR-18). overlay 엔진의 2 호출 site (`UpdateOverlayDuringDrag` / `UpdateOverlay`) + cursor 엔진의 1 호출 site (`UpdateOverlay`) 에서 동일하던 9 줄 보일러를 `Blit(IntPtr hwnd, IntPtr memDC, int x, int y, int width, int height, byte alpha)` 한 줄 호출로 치환. `BLENDFUNCTION` 은 helper 내부 인라인 (caller 는 alpha 만 전달) — 현재 3 호출 site 가 BlendFlags=0 + AlphaFormat=AC_SRC_ALPHA 로 100% 동일. 미래에 분기 필요 시 `BLENDFUNCTION` 직접 받는 오버로드 추가 패턴 | `Blit` |
| [Core/Windowing/CursorStyle + CursorMetrics](../Core/Windowing/CursorStyle.cs) | `internal readonly record struct` pair — `LayeredCursorBase` 의 primitive-only 입력 / 출력 경계. `CursorStyle` (engine **input**, 11 fields): Outer/Middle/Inner `*RadiusLogicalPx`, `CoreThicknessLogicalPx` / `HaloThicknessLogicalPx`, `HaloOpacity : double`, 3 색상 `*ColorArgb : uint`, `CapsLockOn : bool`, `HighlightScale : double = 1.0` (PR-21 — IME 전환 스케일 팝의 매 프레임 배율). `BoundingBoxLogicalPx` 헬퍼가 외측 반지름 + 헤일로 외측 확장 + AA 여유 1px 에 `MaxHighlightScale` 을 곱해 DIB 정사각형 한 변 길이 계산 — CAPS 토글·팝 진행 양쪽에서 DIB 재생성 없이 같은 bbox 안에서 픽셀만 재계산. **`public const double MaxHighlightScale = 2.0`** (PR-21) = 팝 상한 = bbox 고정 기준 — 매 프레임 변동하는 `HighlightScale` 이 아닌 본 상수 기준으로 bbox 를 고정 확대해 팝 중 DIB 재생성 0. App 측 `DefaultConfig.MaxCursorHighlightScale` 이 본 Core const 를 참조 (App→Core 단일 진실원, P6 정방향 — clamp 상한과 bbox 상한 동기화). `CursorMetrics` (engine → callback **output**, 3 fields): `DpiScale` + `ScaledWidth` / `ScaledHeight` | — |
| [Core/Windowing/OverlayStyle + OverlayMetrics](../Core/Windowing/OverlayStyle.cs) | `internal readonly record struct` pair forming the engine's **primitive-only boundary**. `OverlayStyle` (engine **input**, 14 fields): `LabelText : string`, `MeasureLabels : string[]` (PR-08 E1 — 항목 수/의미 일반화, `SequenceEqual` 기반 명시적 `Equals`/`GetHashCode` 오버라이드로 flip-flop 가드 유지), `IsBold : bool` (NOT `FontWeight` enum), `CapsLockOn : bool`, `*LogicalPx` size fields (IndicatorScale applied, DPI not yet), color hex strings. `OverlayMetrics` (engine → callback **output**, 9 fields): DPI-scaled pixel values + `TextVCenterOffsetPx` (per-font asymmetric-cell correction) | — |
| [Core/Windowing/ModalDialogLoop](../Core/Windowing/ModalDialogLoop.cs) | Static `Run(hwndDialog, hwndOwner, ref bool isClosedFlag)` replacing the duplicated `EnableWindow(owner, false) + while(GetMessageW... IsDialogMessageW(...)) { Translate/Dispatch } + EnableWindow(owner, true)` boilerplate in all three dialogs. Re-posts `WM_QUIT` when consumed by the nested loop so the outer message loop also terminates. `IsActive` / `ActiveDialog` expose the current modal HWND so non-UI-thread consumers (e.g. `DetectionLoop`) can gate their own side effects while a dialog is modal — 내부 `s_activeDialog` 필드는 `IntPtr` 라 `volatile` 을 받지 못하므로 모든 접근에 `Volatile.Read` / `Volatile.Write` 를 명시해 교차 스레드 가시성을 보장한다. `RunExternal(hwndSentinel, action)` 는 `MessageBoxW` 처럼 Win32 가 자체 메시지 루프를 돌리는 외부 모달 구간에 `IsActive` 가드만 씌우는 경량 변형 (메시지 펌프/`EnableWindow` 미조작, 중첩 시 이전 값 스택 저장/복원) — see [implementation-notes → Detection → Modal dialog gate](implementation-notes.md#modal-dialog-gate) | `Run`, `RunExternal`, `IsActive`, `ActiveDialog` |
| [Core/Windowing/DialogShell](../Core/Windowing/DialogShell.cs) | 모달 다이얼로그 라이프사이클 통합 진입점 — reentry guard (`ModalDialogLoop.IsActive` ?) → `GetCursorPos` → `MonitorFromPoint` → DPI/font/class 등록 (`Win32DialogHelper.CreateDialogFont` + `RegisterStandardClass`) → `CalculateDialogPosition` → `CreateWindowExW` outer dialog → `ShowWindow` → 선택적 `SetForegroundWindow` → `onAfterShow` (옵셔널) → `ModalDialogLoop.Run` → `DestroyWindow`. `DialogShellMetrics` (record struct: DPI/non-client/pad/dlgWidth) 가 `measureDlgHeight` 콜백으로 전달돼 다이얼로그별 가변 높이 계산을 호출자가 책임. `DialogShellContext` (sealed: + HwndDialog/HFont/DlgHeight + ClientW/ClientH 파생) 가 `buildChildren` / `onAfterShow` 콜백에 전달돼 자식 컨트롤 생성 좌표 + 폰트 적용 + 모달 종료 플래그 배선. WndProc 와 다이얼로그-고유 정적 상태(`_hwndDialog`, `_hwndViewport` 등) 는 호출자 소유. `HandleStandardCommands(wmCommandId, idOk, idCancel, ref result, ref closed, tryCommit?)` 헬퍼는 IDOK/IDCANCEL + 다이얼로그-고유 OK/Cancel ID 동시 수락 패턴을 흡수 — IsDialogMessageW 가 Enter→IDOK / ESC→IDCANCEL 변환해 보내는 경로와 사용자 클릭 경로 양쪽 동일 분기 | `Run`, `HandleStandardCommands` |
| [Core/Windowing/Win32DialogHelper](../Core/Windowing/Win32DialogHelper.cs) | DPI-aware dialog non-client metrics + 9 pt system font helper + dialog position calculator + `WNDCLASSEXW` registration helper. `CreateDialogFont(dpiY, fontFamily) → SafeFontHandle` (PR-08 E3 — 폰트 패밀리는 App 측 [DefaultConfig.DefaultDialogFontFamily](../App/Config/DefaultConfig.cs) 가 주입; Core 는 한국어 폰트 어휘를 알지 않음) 가 `CreateFontW` + `SafeFontHandle` 보일러플레이트를 흡수. `CalculateDialogPosition` unifies the monitor-centered (Cleanup/Settings) and cursor-anchored (ScaleInput) patterns — work area 는 `GetMonitorInfoW` 직접 호출 대신 [DpiHelper.GetWorkArea](../Core/Dpi/DpiHelper.cs) (primary 모니터 폴백 내장) 를 재사용해 hMonitor 무효/조회 실패 시 rcWork 가 `(0,0,0,0)` 으로 떨어져 다이얼로그가 화면 좌상단 (0,0) 에 박히던 결함을 방지 (감사 High ③, 2026-06-01 — P4 폴백 로직 단일 구현 재사용). `RegisterStandardClass(className, wndProc, hbrBackground?)` is the **single entry point** for all `WNDCLASSEXW` registration — internally enforces `hCursor=IDC_ARROW` so no caller can omit the class cursor (defends against `IDC_APPSTARTING` cursor fallback on first-launch hover) | `CalculateNonClientHeight/Width`, `CalculateFontHeightPx`, `ApplyFont`, `CreateDialogFont`, `CalculateDialogPosition`, `RegisterStandardClass` |
| [Core/Windowing/ScrollableDialogHelper](../Core/Windowing/ScrollableDialogHelper.cs) | 세로 스크롤 다이얼로그(`SettingsDialog` / `CleanupDialog`) 공용 스크롤 계산·WinAPI 호출. `ScrollTo(hwndViewport, ref scrollPos, scrollMax, newPos)` 는 `SIF_POS` 갱신 + `ScrollWindowEx(SW_SCROLLCHILDREN \| SW_INVALIDATE \| SW_ERASE)` 로 자식 일괄 이동, `ResolveVScrollPosition` 은 SB_* 코드 → 목표 위치 해석, `CalculateWheelScrollPos` 는 `delta / WHEEL_DELTA × WheelLineStep × lineHeight` 계산. 상태(`scrollPos` 등)는 호출자 소유 — `ref` 로 동기화. `WheelLineStep = 3` 상수 공유 | `ScrollTo`, `ResolveVScrollPosition`, `CalculateWheelScrollPos`, `WheelLineStep` |
| [Core/Windowing/WindowProcessInfo](../Core/Windowing/WindowProcessInfo.cs) | HWND → class name / process name lookups. UWP apps hosted by `ApplicationFrameHost` are resolved to the actual child app process via `EnumChildWindows` (`[ThreadStatic]` bridge + `[UnmanagedCallersOnly]` callback for thread safety). 콜백 본문은 `try/catch` 로 감싸 관리 예외가 unmanaged 경계를 넘어 NativeAOT 프로세스를 종료시키지 않도록 방어하고 해당 자식만 스킵 후 열거 계속 (감사 High ②, conventions §11). Lives in `Core/` so `App/Config/Settings.cs` can resolve match keys without importing `App/Detector/` | `GetClassName`, `GetProcessName(IntPtr)`, `GetProcessName(uint processId)` |
| [Core/Shell/UriLauncher](../Core/Shell/UriLauncher.cs) | `Shell32.ShellExecuteW` `open` verb 단일 진입점. URL/파일 경로 모두 `rc <= 32` 실패 검출 + Warning 로깅 통일. 이름이 `Open` (not `OpenAsync`) — ShellExecuteW 는 동기 반환이라 호출자가 await 하지 않도록 명명. PR-04 분해 후 `Tray.cs` 의 3개 거의 동일한 ShellExecute 블록 (`OpenUpdatePage` / `OpenHomepage` / `OpenConfigFile`) 을 본 모듈에 위임 | `Open(string uriOrPath)`, `Open(string file, string parameters)` |
| [Core/Xml/XmlEntityCodec](../Core/Xml/XmlEntityCodec.cs) | XML 5 predefined entities (`&amp;` / `&lt;` / `&gt;` / `&quot;` / `&apos;`) escape/unescape. `Escape` 는 `&` 를 가장 먼저 처리해 다른 entity 안의 `&` 중복 인코딩 방지, `Unescape` 는 `&amp;` 를 마지막에 처리해 원본 `&` 가 다른 entity 의 앰퍼샌드를 잡아채는 것 방지. schtasks XML 조립 + 역파싱처럼 의존성 없이 1.0 spec 만 필요한 케이스용 (본격 XML 은 `XmlReader` 등) | `Escape`, `Unescape` |

---

## 4. App-specific modules

| Module | Purpose |
|--------|---------|
| [App/Models/](../App/Models/) | `AppConfig` immutable record + all enums (`DisplayMode`, `DetectionMethod`, `ImeState`, `FontWeight`, `Theme`, `NonKoreanImeMode`, `AppProfileMatch`, `AppFilterMode`, `TrayClickAction`, `Corner`, `PositionMode`, `DragModifier`, `AppLanguage`) |
| [App/Bootstrap/AdminElevation.cs](../App/Bootstrap/AdminElevation.cs) | PR-15 — `admin_elevation` 옵션 처리. 자기 IL (`Advapi32.GetCurrentProcessIntegrityLevelRid()`) + 환경 변수 재진입 가드 (`KOENVUE_ELEVATED=1`) 검사 후 `Shell32.ShellExecuteW(verb="runas")` 로 자기 재실행. `Result` enum (`Continue` / `ExitForChild` / `ContinueAfterDenied`) 으로 caller 분기. UAC 거부 시 `User32.MessageBoxW` 안내 후 일반 권한 계속 (fallback c). 매니페스트는 `asInvoker` 유지 (P5 invariant) — 단지 사용자 선택 시 런타임 self-relaunch. `Program.MainImpl` 의 mutex 획득 **전** (step 0c) 호출 — 원본이 mutex 안 잡은 상태라 자식 (High IL) 이 깨끗하게 createdNew=true 획득 (race 0). Logger.Initialize 전 단계라 로그는 `LogProvider.Sink` pre-Init 버퍼 + `Program.AppendCrashFile` 의 `ELEVATION` / `ELEVATION-ERR` 태그 양쪽 기록 ([dev-notes/2026-05-27-admin-elevation.md](dev-notes/2026-05-27-admin-elevation.md)). **PR-15 후속 fix (2026-05-28)** — 트레이 메뉴 토글 재시작 경로의 mutex / trayicon GUID / WTS / IME hook / log file lock race 차단: 신규 환경변수 `KOENVUE_RELAUNCH_PARENT_PID=<PID>` + `Process.WaitForExit(5000)` 패턴. 신규 메서드 (a) `SetRelaunchParentPidForTrayRestart()` — [Tray.cs](../App/UI/Tray.cs) `IDM_ADMIN_ELEVATION` YES 분기에서 자식 spawn 직전 호출, (b) `WaitForRelaunchParentIfAny()` — `Program.MainImpl` step 0b-1 (Settings.Load 직후, TryRelaunchAsAdmin 직전) 호출 — 환경변수에 부모 PID 있으면 wait + 클리어, 정상 부팅 noop. `TryRelaunchAsAdmin` 의 ShellExecuteW("runas") 직전에도 환경변수 재설정으로 손자 generation 전파. PID 재사용 paranoid + 부모 hang 두 케이스는 5초 timeout 으로 영구 hang 회피 ([dev-notes/2026-05-28-pr-15-relaunch-race.md](dev-notes/2026-05-28-pr-15-relaunch-race.md)). **PR-15 후속 fix #2 (2026-05-28)** — admin → 일반 권한 down-grade 케이스 회귀 차단: [Tray.cs](../App/UI/Tray.cs) `IDM_ADMIN_ELEVATION` 핸들러가 자동 spawn 분기 직전에 `isDowngrade = !newAdminConfig.AdminElevation && AdminElevation.IsCurrentProcessElevated()` 검사 → `true` 면 `User32.MessageBoxW(...AdminElevationDowngradeNotice, Win32Constants.MB_OK) + break` 로 자동 spawn 자체를 skip. 원인은 Windows token 모델 — `Shell32.ShellExecuteW("open")` 가 부모 토큰 상속 (admin 부모 → admin 자식). 다른 3 케이스 (일반→admin / 일반→일반 / admin→admin) 는 기존 자동 spawn 흐름 유지 — 4-case 분기 매트릭스 + Option A/B/C (`SaferCreateLevel` 우회 / explorer 위임 / 본 fix 의 명시 안내) 비교 + 미래 우회 진입 조건: [dev-notes/2026-05-28-pr-15-admin-downgrade.md](dev-notes/2026-05-28-pr-15-admin-downgrade.md) + [improvement-plan/PR-15-admin-elevation.md §7.2](improvement-plan/PR-15-admin-elevation.md). `IsCurrentProcessElevated()` 는 본 PR 시점부터 `Program.cs` (부팅 시점 분기) 만의 호출처가 [`App/UI/Tray.cs`](../App/UI/Tray.cs) 의 down-grade 분기에서도 호출됨. 같은 PR 의 [Core/Native/Win32Types.cs](../Core/Native/Win32Types.cs) `MB_OK` const 화로 `ShowDeniedMessage` 의 `uType: 0` → `uType: Win32Constants.MB_OK` 도 정리 (P3 일관성). **PR-15 후속 fix #3 (2026-05-29)** — 트레이 토글 4 case 통일 흐름으로 단순화: fix #2 의 `isDowngrade` 분기 + `MB_YESNO` "예/아니오" mental model 충돌 (메시지박스 표시 **전** 에 `updateConfig` + `ReregisterIfAdminChanged` 이미 disk 반영 완료 — Yes/No 컨벤션의 "취소" 직관과 불일치) + 메인 인디 잔존 회귀 (MB_OK + break 흐름의 자연 결과 — `WM_CLOSE` 미발화) 동시 보고를 사용자 직접 제안 (4 case 단일 메시지 + MB_OK + 자동 종료) 으로 통합 해결. `IDM_ADMIN_ELEVATION` 분기 ~40 LOC → ~14 LOC — `updateConfig(newAdminConfig)` + `ReregisterIfAdminChanged` + `User32.MessageBoxW(...AdminElevationChangeNotice, MB_OK)` + `User32.PostMessageW(hwndMain, WM_CLOSE)` 4 단계로 단순화. 자동 spawn 안 함 (Windows token 모델 admin→일반 down-grade 한계 자연 회피 — 사용자 수동 재실행으로 처리). 트레이 토글 = **옵션 변경** (config 디스크 저장) / 부팅 self-elevation = **옵션 효력 발생** (사용자 일반 권한 재실행 → UAC 1회 자동 admin 진입) 책임 분담 명료화. 자연 제거된 메서드: [AdminElevation.cs](../App/Bootstrap/AdminElevation.cs) 의 `ClearReentryGuard()` + `SetRelaunchParentPidForTrayRestart()` 2개 (사용처 0). 유지: `TryRelaunchAsAdmin` + `WaitForRelaunchParentIfAny` + 환경변수 2종 — 부팅 시점 self-elevation 인프라 (옵션 효력 발생) 는 PR-15 UIPI 우회 가치 자체. I18n 키 정리: `AdminElevationRestartPrompt` + `AdminElevationDowngradeNotice` 2 키 제거 + 신규 단일 키 `AdminElevationChangeNotice` ("관리자 권한 옵션이 변경되어 KoEnVue를 종료합니다. KoEnVue를 다시 실행해 주세요." — fix #4 단순화 반영, fix #3 시점 원본은 "관리자 권한 옵션은 다음 실행부터 적용됩니다." redundant). 시계열 + 사용자 직접 제안 채택 + 트레이드오프 정직 + 분담 명료화: [dev-notes/2026-05-29-pr-15-tray-toggle-unified.md](dev-notes/2026-05-29-pr-15-tray-toggle-unified.md) + [improvement-plan/PR-15-admin-elevation.md §7.3](improvement-plan/PR-15-admin-elevation.md). **PR-15 후속 fix #4 (2026-05-29)** — 트레이 메뉴 "관리자 권한으로 실행" 체크 표시 OR 로직 + 안내 메시지 단순화. (a) [Tray.Menu.cs](../App/UI/Tray.Menu.cs) 의 메뉴 체크 분기를 `config.AdminElevation ? MF_CHECKED : MF_UNCHECKED` → `bool isAdminEffective = config.AdminElevation || AdminElevation.IsCurrentProcessElevated()` OR 로직 — admin 환경 외부 spawn (예: admin Total Commander 가 KoEnVue 실행 시 admin 토큰 상속) 케이스에서 `config=false` 여도 실 권한이 admin 이면 사용자에게 시각 노출 (4-case 매트릭스 case 2 해결). 토글 클릭 의미는 여전히 config 만 변경 (Windows token 모델 한계 — 실 권한은 다음 부팅까지 영향 없음). `IsCurrentProcessElevated()` 호출처가 본 fix 이후 `Program.cs` (부팅 시점) + `App/UI/Tray.Menu.cs` (메뉴 체크) 2 곳 — Tray 의 `using KoEnVue.App.Bootstrap;` import 가 fix #3 에서 `Tray.cs` 제거됐다가 `Tray.Menu.cs` 에 재추가됨. (b) `I18n.AdminElevationChangeNotice` 메시지 단순화 — "다음 실행부터 적용됩니다" 표현 제거 (사용자 종료 → 수동 재실행 흐름에서 "다음 실행" 시점 자명, redundant). 시계열 + 토글 의미 보존 검증 + AOT ±0 bytes 페이지 경계 흡수: [dev-notes/2026-05-29-pr-15-tray-menu-or-logic.md](dev-notes/2026-05-29-pr-15-tray-menu-or-logic.md) + [improvement-plan/PR-15-admin-elevation.md §7.4](improvement-plan/PR-15-admin-elevation.md). **PR-15 후속 fix #5 (2026-05-29)** — 트레이 메뉴 라벨 — case 2 (admin 환경 외부 spawn) 전용 `Config = User` hint suffix. fix #4 의 OR 가 case 2 의 실 권한 시각 노출은 해결했지만 같은 case 의 `config.AdminElevation` 값 (사용자 의도 = User) 은 여전히 invisible. 사용자 질문 ("설정값도 같이 알 수 있으면 제일 좋을 것 같아") + ultrathink 자동 동기화 거절 (시나리오 2/3 — 일회성 admin 환경 사용자에게 schtasks `<RunLevel>HighestAvailable</RunLevel>` 자동 재등록 = 부팅마다 admin 자동 시작으로 변환 = 사용자 명시 의도 0; 사용자 명시 토글 직후 case 2 진입 시 의도 reset) + 사용자 직접 제안 채택 (괄호 → 쉼표 정정: "관리자 권한으로 실행, Config = User"). 변경 2 파일: (a) [I18n.cs](../App/Localization/I18n.cs) `I18nKey` enum + `_table` ko/en + public surface property `MenuAdminElevationExternal` 신규 (3-spot 패턴, public surface 41 → 42 속성). (b) [Tray.Menu.cs](../App/UI/Tray.Menu.cs) `isCurrentlyElevated` 분리 + `isExternalElevation = !config.AdminElevation && isCurrentlyElevated` 분기 + `adminElevationLabel = isExternalElevation ? MenuAdminElevationExternal : MenuAdminElevation` 라벨 동적 선택 — fix #4 의 `isAdminEffective` (OR 로직) 한 줄 변경 0, 라벨만 case 2 hint 단독. case 1/3/4 = 기본 라벨 (사용자 명시 의도 케이스 noise 회피). ko/en 영문 mix ("Config" / "User") 정당성 — IT 통용어 (Windows 표준 어휘) + admin 콘솔 사용자 친화 + 직설성 + 길이 trade-off 균형 (P2 정합 — 메인 동사구 한국어 + suffix 영문 mix 허용 영역 case 한정). AOT publish 4,864,512 → 4,865,024 bytes (+512 — 신규 i18n 키 ko/en 문자열 + 분기 로직, 페이지 경계 흡수 없음), 65/65 PASS. 시계열 + 자동 동기화 ultrathink 거절 분석 + ko/en 영문 mix 정당성 + 4-case 매트릭스 fix #4 → fix #5 변화 + 라벨 표기 정정: [dev-notes/2026-05-29-pr-15-tray-menu-config-hint.md](dev-notes/2026-05-29-pr-15-tray-menu-config-hint.md) + [improvement-plan/PR-15-admin-elevation.md §7.5](improvement-plan/PR-15-admin-elevation.md). **PR-15 후속 fix #2 cleanup 트레일 (2026-05-29)** — fix #2 가 `Win32Constants.MB_OK` const 도입 후 2 곳 (Tray.cs down-grade 안내 + AdminElevation.cs ShowDeniedMessage) 만 정리하고 잔재 5 spot 이 positional `0` 인 채로 잔존하던 P3 부분 일관성을 100% 회복: [`App/UI/Tray.cs`](../App/UI/Tray.cs) ×2 (`ShowPositionError` + `CleanupPositions` empty 분기) + [`App/UI/Dialogs/ScaleInputDialog.cs`](../App/UI/Dialogs/ScaleInputDialog.cs) ×2 (invalid input + out of range) + [`App/UI/Dialogs/SettingsDialog.cs`](../App/UI/Dialogs/SettingsDialog.cs) ×1 (필드 commit 에러). 각각 마지막 positional `0` → `uType: Win32Constants.MB_OK`, 의미 변화 0. `Win32Constants.MB_OK` App/ 카운트: fix #2 시점 2 → fix #3 의 `AdminElevationChangeNotice` 누적 6 → 본 cleanup 으로 **7 통일** (Tray.cs ×3 + ScaleInputDialog.cs ×2 + SettingsDialog.cs ×1 + AdminElevation.cs ×1). 신규 invariant grep ([conventions.md](conventions.md)): `User32\.MessageBoxW\([^)]*,\s*0\s*\)` 0 매치 — positional uType=0 금지 가드 |
| [App/Config/DefaultConfig.cs](../App/Config/DefaultConfig.cs) | 수치/색상 디폴트 + Validate clamp 범위 + SettingsDialog min/max 의 단일 진실원. `AppConfig` 의 모든 numeric init 디폴트가 본 파일의 동명 const 를 참조 — PR-05 D7 가 도입한 6쌍 (`FadeInMs`/`FadeOutMs`/`HighlightScale`/`HighlightDurationMs`/`AlwaysIdleTimeoutMs`/`PollIntervalMs`) 에 PR-17 (v0.9.5.0) 이 14 필드 추가: 외관 스타일/크기 6 (`LabelWidth`/`LabelHeight`/`LabelBorderRadius`/`BorderWidth`/`IndicatorScale`/`FontSize`) + 투명도 3 + 슬라이드 1 (`Opacity`/`IdleOpacity`/`ActiveOpacity`/`SlideSpeedMs`) + 동작/시스템/고급 4 (`EventDisplayDurationMs`/`SnapGapPx`/`LogMaxSizeMb`/`ForceTopmostIntervalMs`). `Settings.Validate` 의 clamp 18개 인자 + `SettingsDialog.Fields.cs` 의 Int/Dbl min/max 13개 인자가 모두 본 파일의 `Min/MaxX` const 16쌍 참조. animation timings, pixel offsets, `DetectionBackoff{Step,Max}Ms`, `UpdateRepoOwner/Name`, system-input process list 포함. `AppVersion` 는 본 파일의 `partial` 다른 절에서 [Directory.Build.targets](../Directory.Build.targets) 의 `GenerateVersionConstants` Target 가 `obj/.../Version.g.cs` 로 emit — csproj `<Version>` (현재 `0.9.5.0`) 단일 진실원에서 derive (PR-11 D6). PR-15 의 `AdminElevation : bool` (default `false`) const 도 본 파일에 위치 — `AppConfig.AdminElevation` init 디폴트가 참조. 트레이 빠른 투명도 프리셋 배열 디폴트도 본 파일 단일화 (감사 High ④, 2026-06-01): `TrayQuickOpacity1/2/3` const 3개 + `static double[] TrayQuickOpacityPresets => [...]` property (호출마다 새 배열 — 공유 변형 위험 0). `AppConfig` init / `Settings.EnsureSubObjects` 폴백 / `SettingsDialog.Fields` getter fallback + `SetPresetAt` 가 모두 본 const/property 참조 (이전 5곳 인라인 `[0.95, 0.85, 0.6]` 중복 제거) |
| [App/Config/Settings.cs](../App/Config/Settings.cs) | Static facade delegating to `AppSettingsManager : JsonSettingsManager<AppConfig>`. Handles `Load`/`Save`/`CheckReload`/`ResolveForApp` (per-app profile merge) |
| [App/Config/PositionCleanupService.cs](../App/Config/PositionCleanupService.cs) | `indicator_positions` 정리 작업의 비-UI 비즈니스 로직. `Compute(config)` 가 양쪽 dict (`IndicatorPositions` + `IndicatorPositionsRelative`) 키 합집합 + 실행 중 프로세스명 접미사 라벨링까지, `RemoveSelected(...)` 가 displayItems → originalNames 매핑 후 두 dict 제거한 새 AppConfig 반환. 다이얼로그 렌더링은 호출자(Tray) 가 담당. PR-04 분해 산물 |
| [App/Config/ThemePresets.cs](../App/Config/ThemePresets.cs) | 6 theme presets: `Custom`, `Minimal`, `Vivid`, `Pastel`, `Dark`, `System`. 4 정적 preset (Minimal/Vivid/Pastel/Dark) 은 `record ThemeColors(HBg,HFg,EBg,EFg,NBg,NFg)` + `Dictionary<Theme, ThemeColors>` 정적 사전으로 표현 (PR-05 D2). `Theme.System` 분기는 (a) 고대비 모드 ON 이면 `HIGHLIGHT/HIGHLIGHTTEXT/WINDOW/WINDOWTEXT` contrast-safe 팔레트 (PR-05 H4-c), (b) 그 외엔 `DwmGetColorizationColor` 우선 + `GetSysColor(COLOR_HIGHLIGHT)` 폴백으로 accent 추적 (PR-14) 후 영문 배경은 보색. 프리셋 전환 시 커스텀 색상 자동 백업/복원 (`custom_backup_*` 필드) |
| [App/Detector/ImeStatus.cs](../App/Detector/ImeStatus.cs) | `WM_IME_CONTROL` + `GetKeyboardLayout` IME state detection + `EVENT_OBJECT_IME_CHANGE` WinEvent hook |
| [App/Detector/ImeConstants.cs](../App/Detector/ImeConstants.cs) | IME 메시지 / WinEvent / HKL 파싱 9 상수 — `WM_IME_CONTROL` / `IMC_GETOPENSTATUS` / `IMC_GETCONVERSIONMODE` / `IME_CMODE_HANGUL` / `EVENT_OBJECT_IME_CHANGE` / `LANGID_KOREAN` / `HKL_LANGID_MASK` / `HKL_IME_DEVICE_MASK` / `HKL_IME_DEVICE_SIG`. P6 게이트 (Core 가 IME 어휘를 모름) 를 지키기 위해 `Core/Native/Win32Constants` 에서 본 위치로 이전 (PR-08 E2). `ImeStatus` 와 `I18n.IsSystemKorean` 이 참조 |
| [App/Detector/SystemFilter.cs](../App/Detector/SystemFilter.cs) | 8-condition short-circuit hide logic (secure desktop / minimized / virtual desktop / class blacklist (+ owner chain) / process blacklist / no focus / fullscreen / app filter list). 자세한 enumerate 는 [docs/implementation-notes.md#system-filter](implementation-notes.md). `ShouldHide` 내부에 `ResolveHwndProcess()` 로컬 캐시(`??=`) 로 동일 틱 내 `WindowProcessInfo.GetProcessName(hwnd)` 중복 호출 제거. `MatchesAny(name, baseList, userList)` (2-리스트 OrdinalIgnoreCase 매칭) 는 `internal` — 커서 인디 [CursorOverlay](../App/UI/CursorOverlay.cs) 의 셸 UI 호버 판정이 같은 헬퍼를 재사용 (P4 단일 구현, 메인/커서 인디 공유) |
| [App/Localization/I18n.cs](../App/Localization/I18n.cs) | Korean default, English fallback. `Dictionary<I18nKey, (Ko, En)>` 테이블 + `Get(key) => _isKorean ? Ko : En` 단일 dispatcher (PR-06 D3). public surface 의 42 속성 (PR-15 후속 fix #3 가 `AdminElevationRestartPrompt` + `AdminElevationDowngradeNotice` 2 키 제거 + 신규 단일 `AdminElevationChangeNotice` 1 키 추가 — net -1, 4 case 통일 흐름 / **PR-15 후속 fix #5 (2026-05-29) 가 `MenuAdminElevationExternal` 1 키 추가** — net +1, case 2 전용 라벨 hint "관리자 권한으로 실행, Config = User" / "Run as administrator, Config = User", ko/en 영문 mix 정당성 = IT 통용어 + 사용자 직접 표현 + 길이 trade-off — fix #4 의 OR 로직과 함께 작동 / **PR-21 (2026-06-01) 가 `MenuCursorHighlight` 1 키 추가** — net +1 → 43 속성, 커서 IME 전환 스케일 팝 토글 "커서 변경 시 강조" / "Cursor highlight on change") 은 모두 `=> Get(I18nKey.X)` 한 줄. 3번째 언어 추가는 튜플을 3-tuple 로 확장하거나 언어 차원을 enum/사전으로 추가. `Load(AppLanguage)` 가 [AppConfig.Language](../App/Models/AppConfig.cs) (PR-06 D4 — string → enum) 를 받아 `_isKorean` 결정 |
| [App/Update/](../App/Update/) | `UpdateChecker` (background thread, fire-once-per-boot GitHub Releases poll) + `GitHubRelease` (JSON DTO + source-gen context) + `UpdateInfo` (callback payload). HTTP 전송 실패는 `Logger.Debug`, 200 응답 후 JSON 파싱 실패는 `Logger.Warning` (API 스키마 변동 가시화) |
| [App/UI/Overlay.cs](../App/UI/Overlay.cs) | Static facade over `LayeredOverlayBase`. Holds `private static AppConfig _config` + engine instance. **`BuildStyle(config, state)` is the sole `ImeState` → `OverlayStyle` conversion point** in the codebase. `TrackPosition(x, y)` delegates to the engine's slide+highlight 합성 position-tracking (blit 없이 `_lastX/Y` 만 갱신 — [implementation-notes.md § Slide animation](implementation-notes.md#slide-animation)) |
| [App/UI/Animation.cs](../App/UI/Animation.cs) | Static facade over `OverlayAnimator`. **`internal BuildAnimationConfig(config)`** extracts primitives and composes the "애니메이션 사용" master gate — `ChangeHighlight`/`SlideAnimation` are AND-composed with `config.AnimationEnabled` so `animation_enabled` gates fade (engine-side) plus highlight·slide as the single source of truth (PR-22; Core unchanged — the engine consumes the composed flags as-is; `internal` for `AnimationFacadeTests`). `SetDimMode` routes `NonKoreanImeMode.Dim && state == NonKorean` into a `bool`. 콜백 배선에 `onTrackPosition: Overlay.TrackPosition` 포함 (slide+highlight 합성 시 slide 가 위치만 추적, highlight 가 blit 전담) |
| [App/UI/CursorRenderer.cs](../App/UI/CursorRenderer.cs) | 커서 추종 인디의 distance-field 분석적 AA 픽셀 셰이더. `LayeredCursorBase` 의 `renderToDib` 콜백으로 주입되어 `CursorStyle` + `CursorMetrics` 를 받아 DIB BGRA32 픽셀에 직접 쓴다. 각 동심원 = 코어 (사용자 색상, alpha 1.0, 양옆 0.5px AA) + 헤일로 (흰색 × `HaloOpacity`, 코어 영역 제외 영역만). 코어 vs 헤일로 winner 는 alpha 비교로 결정. CAPS OFF 시 외측 원 skip — distance 계산 자체를 건너뜀. **PR-21** — 반지름 3종 (Outer/Middle/Inner) + 두께 2종 (core/halo half) 에 `style.HighlightScale` 을 곱해 IME 전환 스케일 팝 시 동심원을 통째로 확대 (평상시 1.0 → 무변화, 팝 중 1.0~`CursorHighlightScale` 보간값) |
| [App/UI/CursorOverlay.cs](../App/UI/CursorOverlay.cs) | `LayeredCursorBase` 위의 정적 파사드 — `Initialize(hwnd, hwndTimer, config, state, caps)` / `HandleConfigChanged` / `HandleCursorMotionTimer` / `HandleCursorPopTimer` / `SetImeState` / `SetCapsLock` / `Dispose`. **PR-21 IME 전환 스케일 팝 상태머신** — `TriggerPop` / `HandleCursorPopTimer` / `StopPop` (`private`/`public`/`private`) + 경량 상태 `_hwndTimer` (팝 `TIMER_ID_CURSOR_POP` 등록 대상 = 메인 윈도우, `Initialize` 두 번째 인자로 주입) / `_popActive` / `_popStartTick`. `SetImeState` 가 가시 + `CursorChangeHighlight` 면 `TriggerPop` (첫 프레임 즉시 렌더로 16ms 지연 없이 개시), 아니면 색만 즉시 `Render`. 보간식 `scale = start + (1.0 - start) * ratio` (메인 인디 Highlight 와 동형, 선형) → `CursorStyle.HighlightScale` 반영 후 재렌더 (bbox 고정이라 DIB 재생성 0). 숨김/이동/config 변경/Dispose 에서 `StopPop` (타이머 정리 + `HighlightScale` 1.0 복원, `_popActive` 가드 멱등). 메인 `OverlayAnimator` 미재사용 (커서 별도 엔진 P4 예외). **`BuildStyle(config, state, capsOn)` 가 `ImeState` + CAPS → `CursorStyle` 합성 단일 진입점** (한글/비한글을 같은 카테고리로 묶어 CAPS Outer 색상이 영문일 때 한글, 그 외엔 영문 — 사용자 인터뷰 결정). 마우스 정지 검출 FSM (`_idleStartTick` + `CursorIdleDelayMs`) 또는 항상 표시 모드 (`CursorAlwaysShow` = 16ms 위치 추종) 분기. 본 파사드 전용 HWND `_hwndCursorOverlay` 는 메인 `_hwndOverlay` 와 별개 윈도우이며 `WS_EX_TRANSPARENT` 영구 ON 으로 클릭 통과 ([dev-notes/2026-05-15-click-through-attempts.md](dev-notes/2026-05-15-click-through-attempts.md) F2 패턴 재사용). `config.CursorIndicatorEnabled = false` 면 lazy 생성 대기 — 트레이 메뉴 토글 시 `Program.EnableCursorOverlay()` 가 윈도우 + 파사드 + 폴링 타이머 일괄 생성. **셸 UI 호버 숨김** — `HandleCursorMotionTimer` 가 매 틱 `IsOverShellUi(cursor)` (`User32.WindowFromPoint` → `GetAncestor(GA_ROOT)` 루트 → 클래스 `SystemFilter.MatchesAny(SystemHideClasses)` + 프로세스 `DefaultConfig.IsSystemInputProcess`=StartMenu/Search) 판정 → 작업 표시줄/시작/검색/트레이 위면 `Hide()` (시작/검색은 immersive z-band 라 일반 topmost 로 못 덮어 일관 숨김; 루트 hwnd 캐시로 `GetProcessName` 반복 회피) |
| [App/UI/Tray.cs](../App/UI/Tray.cs) + [Tray.Menu.cs](../App/UI/Tray.Menu.cs) | Static facade over `NotifyIconManager`. 라이프사이클 (Initialize/HandleAddRetryTimer/UpdateState/OnUpdateFound/Recreate/Remove) + WM_COMMAND 디스패치 + `_pendingUpdate` 저장 + helpers (CleanupPositions/SetDefaultPositionToCurrent/BuildTooltip/ApplyQuickOpacity). `Tray.Menu.cs` partial 는 `ShowMenu` 만 분리 (메뉴 빌더). PR-04 분해 — schtasks 는 [App/Startup/StartupTaskManager](../App/Startup/StartupTaskManager.cs), 위치 정리 비즈니스 로직은 `PositionCleanupService`, URL/파일 열기는 `UriLauncher` 로 위임 |
| [App/Startup/StartupTaskManager.cs](../App/Startup/StartupTaskManager.cs) | Windows Task Scheduler (`schtasks.exe`) 기반 "시작 시 자동 실행" 작업 등록/조회/동기화. UI 와 무관한 XML 조립 + CLI 호출 + 결과 검증 책임. 외부 의존성은 `Logger` + `XmlEntityCodec` 만 — 메뉴 ID 는 모르고 호출자(Tray) 가 ID 와 핸들러를 mapping. PR-04 분해 산물. **PR-15** — `BuildStartupTaskXml(exePath, adminElevation)` 시그니처 확장으로 `<RunLevel>` 을 `LeastPrivilege` (default) vs `HighestAvailable` (admin_elevation=true) 분기 + `SyncStartupPathAsync` 의 `expectedRunLevel` 도 config 에서 derive 해 admin 토글 시 자동 재등록 + 신규 `ReregisterIfAdminChanged(config)` 헬퍼 (Tray/SettingsDialog 토글 직후 호출) |
| [App/UI/TrayIcon.cs](../App/UI/TrayIcon.cs) | GDI-based dynamic icon bitmap — 캐럿+점(caret+dot) 디자인 고정. IME 상태별 배경색을 `CreateIcon`이 즉석에서 그려 `SafeIconHandle`로 반환 |
| [App/UI/AppMessages.cs](../App/UI/AppMessages.cs) | `WM_APP + N` message constants for cross-thread signalling |
| [App/UI/Dialogs/CleanupDialog.cs](../App/UI/Dialogs/CleanupDialog.cs) | `indicator_positions` management dialog (checkbox list of all entries, scrollable viewport when >15 items) |
| [App/UI/Dialogs/ScaleInputDialog.cs](../App/UI/Dialogs/ScaleInputDialog.cs) | Custom scale entry dialog (1.0–5.0, spawned at cursor position) |
| [App/UI/Dialogs/SettingsDialog.cs](../App/UI/Dialogs/SettingsDialog.cs) (+ `.Fields.cs` + `.Scroll.cs`) | Scrollable settings dialog (PR-21 14 sections / 72 필드 — 일반 / 메인 인디 / 커서 인디 3 대분류 재배치. 커서 인디 = "커서 인디 — 동심원" 10 필드 + "커서 인디 — 전환 효과" 2 필드, 모두 DefaultConfig MinCursor*/MaxCursor* const 참조. `cursor_change_highlight` on/off 는 트레이 메뉴 전용이라 미노출) split across 3 partial class files |

---

## 5. Facade pattern — engine-instance composition

Stage 4 extracted 6 reusable modules into `Core/` while keeping every call site in `Program.cs` / `Animation.cs` / dialogs **byte-identical at the source level**. The trick is that each App facade holds the Core engine as a `private static` field and delegates to it:

```csharp
// App/UI/Overlay.cs
internal static class Overlay
{
    private static AppConfig _config = null!;
    private static LayeredOverlayBase _engine = null!;

    public static void Initialize(IntPtr hwnd, AppConfig config)
    {
        _config = config;
        _engine = new LayeredOverlayBase(hwnd, OnRenderToDib);
    }

    public static void Show(int x, int y, ImeState state)
    {
        var style = BuildStyle(_config, state);   // ← sole ImeState → OverlayStyle conversion point
        _engine.Render(style);
        _engine.Show(x, y);
    }

    private static (int w, int h) OnRenderToDib(IntPtr hdc, OverlayStyle style, OverlayMetrics metrics)
    {
        // Raw GDI: RoundRect + DrawTextW + premultiplied alpha
        // Reads metrics.TextVCenterOffsetPx for DT_VCENTER glyph correction
    }
}
```

Key properties of this pattern:

- **Core sees only primitives / `record struct`** — `OverlayStyle.LabelText : string`, `OverlayStyle.IsBold : bool`, `OverlayStyle.CapsLockOn : bool`, `OverlayStyle.MeasureLabels : (string, string, string)`. The enum → primitive conversion happens exactly once, inside `BuildStyle`
- **Facades stay compatible with existing call sites** — `Overlay.Show(x, y, state)` looks the same to `Program.cs` as it did pre-Stage-4
- **DPI ownership lives inside the engine** — the facade only pre-multiplies `IndicatorScale` into the `*LogicalPx` fields; the engine multiplies again by DPI inside its private resource setup
- **Flip-flop guard is automatic** — `OverlayStyle` is a `record struct`, so `newStyle == _lastStyle` value equality in `Render` skips re-render on no-op state changes. `CapsLockOn` is a field inside the record so toggling it naturally breaks equality and forces a re-render

Same pattern applies to `Animation` (wraps `OverlayAnimator`), `Settings` (wraps `AppSettingsManager : JsonSettingsManager<AppConfig>`), and `Tray` (wraps `NotifyIconManager`).

---

## 6. Layer dependency rule (P6)

**`App/` may import `Core/`, but `Core/` must not import `App/`.** This is the P6 hard constraint and it is verified mechanically:

```bash
git grep "KoEnVue\.App"      Core/   # P6 namespace gate              → 0
git grep "ImeState"          Core/   # Risk 4 enum gate               → 0
git grep "NonKoreanImeMode"  Core/   # Risk 4 enum gate               → 0
git grep -E "Hangul|English|NonKorean" Core/   # PR-08 vocabulary gate → 0
git grep "맑은 고딕"          Core/   # PR-08 한국어 폰트 어휘 gate    → 0
```

Additionally, `App/Config/` must not import `App/Detector/`:

```bash
git grep "using KoEnVue\.App\.Detector" App/Config/                   → 0
```

Risk 4 is the critical failure mode: letting `ImeState` leak into `Core/` would couple the generic layered-overlay engine to KoEnVue's IME problem domain and break reuse. The `OverlayStyle.LabelText : string` + `MeasureLabels : string[]` primitive boundary is the defense — PR-08 일반화 (이전 3-tuple `(Hangul, English, NonKorean)` 는 record struct 안에 IME 상태 이름을 박아 둠).

---

## 7. Reuse instructions

`Core/` is designed to drop into another Windows desktop project as-is. Two integration paths:

### A. Folder copy

Copy the `Core/` directory under another project root and reference its files from that project's `.csproj`. Adjust `using KoEnVue.Core.*;` to the consuming namespace if desired — the namespace is the only KoEnVue-named identifier inside `Core/`.

### B. `<Compile Include>` link

From a sibling `.csproj`, add:

```xml
<ItemGroup>
  <Compile Include="..\KoEnVue\Core\**\*.cs"
           Link="Core\%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

to share the source tree without duplicating files. Ensure the consuming project also enables `AllowUnsafeBlocks` and targets .NET 7+ (for the `[LibraryImport]` source generator).

### `JsonSettingsManager<T>` consumers must construct their own `JsonTypeInfo<T>`

Under NativeAOT + `JsonSerializerIsReflectionEnabledByDefault=false` there is no reflection fallback. Define a `[JsonSerializable(typeof(MyType))]` source-gen context and pass `MyContext.Default.MyType` into the constructor:

```csharp
var settings = new JsonSettingsManager<MyType>(
    filePath: Path.Combine(AppContext.BaseDirectory, "settings.json"),
    typeInfo: MyJsonContext.Default.MyType
);
```

### Post-integration verification

After integrating, re-run the three `git grep` invariants above against the consuming project's `Core/` copy. All must remain 0.

---

## 8. Non-reusable `App/` modules

`App/` holds product-specific IME indicator logic and is **not** a reuse target. Its modules depend on `Core/` but also on each other, on `AppConfig`, and on enums like `ImeState`/`DisplayMode`. Cherry-picking a single `App/` file into another project will not work without dragging most of the layer with it.
