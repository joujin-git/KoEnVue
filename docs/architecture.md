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
│   ├── Native/              P/Invoke surfaces + Win32Types.cs + SafeGdiHandles.cs
│   ├── Color/               ColorHelper (Hex ↔ COLORREF ↔ RGB)
│   ├── Dpi/                 DpiHelper (per-monitor DPI queries, work area)
│   ├── Http/                HttpClientLite (WinHTTP-backed sync GET — ~40 KB)
│   ├── Logging/             Logger + LogLevel
│   ├── Config/              JsonSettingsManager<T> + JsonSettingsFile
│   ├── Animation/           OverlayAnimator + AnimationConfig + AnimationTimerIds
│   ├── Tray/                NotifyIconManager (Shell_NotifyIconW wrapper)
│   └── Windowing/           LayeredOverlayBase + OverlayStyle/OverlayMetrics +
│                            ModalDialogLoop + Win32DialogHelper +
│                            ScrollableDialogHelper + WindowProcessInfo
│
├── App/                     KoEnVue-specific layer — namespace KoEnVue.App.*
│   ├── Models/              AppConfig record + all enums (ImeState, Theme, ...)
│   ├── Config/              DefaultConfig, Settings facade, ThemePresets,
│   │                        AppSettingsManager : JsonSettingsManager<AppConfig>
│   ├── Detector/            ImeStatus + SystemFilter
│   ├── Localization/        I18n (Ko/En UI text, GetUserDefaultUILanguage)
│   ├── Update/              UpdateChecker + GitHubRelease + UpdateInfo
│   └── UI/                  Overlay facade + Animation facade + Tray + TrayIcon +
│       └── Dialogs/         CleanupDialog + ScaleInputDialog + SettingsDialog(×3)
│
├── Program.cs               Main message loop + WndProc + detection thread
├── Program.Bootstrap.cs     partial class: mutex, window classes, teardown,
│                            second-instance activation, TaskbarCreated tray recovery
└── KoEnVue.csproj
```

Every file in `Core/` is reusable in another Windows desktop project; every file in `App/` is product-specific.

---

## 3. Reusable Core modules

| Module | Purpose | Public surface |
|--------|---------|----------------|
| [Core/Native/*](../Core/Native/) | Raw P/Invoke surface. `Win32Types.cs` centralizes every struct + the `Win32Constants` class (SM/WS/DWMWA/etc.). `SafeGdiHandles.cs` hosts `SafeFontHandle`, `SafeIconHandle`, etc. `WinHttp.cs` hosts `SafeWinHttpHandle : SafeHandleZeroOrMinusOneIsInvalid` | `[LibraryImport]` only, no `[DllImport]` |
| [Core/Native/Wtsapi32.cs](../Core/Native/Wtsapi32.cs) | WTS session notification for polling-free lock/unlock/logoff detection via `WM_WTSSESSION_CHANGE` — backbone of `HideOnLockScreen` | `WTSRegisterSessionNotification`, `WTSUnRegisterSessionNotification` |
| [Core/Color/ColorHelper](../Core/Color/ColorHelper.cs) | Hex ↔ COLORREF ↔ RGB conversion. Malformed hex returns 0 / `(0,0,0)` instead of throwing, so a bad `config.json` doesn't leak GDI handles on the render hot path | `TryNormalizeHex`, `HexToColorRef`, `HexToRgb`, `ColorRefToRgb`, `RgbToHex` |
| [Core/Dpi/DpiHelper](../Core/Dpi/DpiHelper.cs) | Per-monitor DPI queries. `BASE_DPI = 96` is inlined as a `const int` so the module has no `Config` dependency | `GetScale`, `GetWorkArea`, `GetRawDpi`, `GetMonitorFromPoint` |
| [Core/Http/HttpClientLite](../Core/Http/HttpClientLite.cs) | Synchronous HTTPS GET wrapper backed by WinHTTP. NativeAOT publish impact ~40 KB (vs ~2.5 MB for `System.Net.Http.HttpClient`). Response body cap 256 KB, all failure paths return `null` | `GetString(userAgent, host, path, extraHeaders?, timeoutMs = 10_000) → string?` |
| [Core/Logging/Logger](../Core/Logging/Logger.cs) + `LogLevel` | Async file logger. `ConcurrentQueue` + `ManualResetEventSlim` + dedicated drain thread. Single `.log → .log.old` rotation. **No `AppConfig` parameter** — Stage 3-A narrowed `Initialize` to primitives. **Queue cap** `MaxQueueSize = 10_000`: 회전 실패로 `_fileWriter = null` 상태가 지속되면 최고령부터 드롭하고 복구 시 누적 드롭 건수를 1회 경고로 기록 (무제한 성장 방지) | `Initialize(bool enabled, string? logFilePath, int maxSizeMb)`, `SetLevel(LogLevel)`, `Debug`/`Info`/`Warning`/`Error`, `Shutdown` |
| [Core/Config/JsonSettingsManager\<T\>](../Core/Config/JsonSettingsManager.cs) + [JsonSettingsFile](../Core/Config/JsonSettingsFile.cs) | Generic JSON-backed settings pipeline. `JsonTypeInfo<T>` injection is mandatory under NativeAOT + `JsonSerializerIsReflectionEnabledByDefault=false`. Five `protected virtual` hooks run in fixed order during `Load`: `ApplyNullSafetyNet` → `PostDeserializeFixup` → `Migrate` → `Validate` → `ApplyTheme`. Delete-safe hot reload (`File.Exists` pre-check) and corrupted-file spam prevention (mtime cache update inside `catch`) are baked in. **Atomic save**: `WriteAllText` writes to `path + ".tmp"` then `File.Move(tmp, path, overwrite: true)` — `MoveFileExW(MOVEFILE_REPLACE_EXISTING)` guarantees atomic rename on the same volume, so a crash mid-save cannot leave a truncated config file | `Load() → T`, `Save(T)`, `CheckReload() → bool`, `FilePath` |
| [Core/Animation/OverlayAnimator](../Core/Animation/OverlayAnimator.cs) + [AnimationConfig](../Core/Animation/AnimationConfig.cs) + `AnimationTimerIds` | 5-state machine (Hidden / FadingIn / Holding / FadingOut / Idle) + highlight/slide sub-phases, driven by `WM_TIMER`. 6 callbacks injected via constructor. `AnimationConfig` is a 17-field `record struct` where `AlwaysMode : bool` replaces `DisplayMode.Always`. `SetDimMode(bool)` replaces `NonKoreanImeMode.Dim` check — **Core never imports either enum** | `UpdateConfig`, `SetDimMode`, `TriggerShow → bool wasHidden`, `TriggerHide(forceHidden)`, `HandleTimer(timerId)` |
| [Core/Tray/NotifyIconManager](../Core/Tray/NotifyIconManager.cs) | `Shell_NotifyIconW` wrapper. Captures `(hwndOwner, callbackMessage, iconGuid)` once at construction. Preserves `NIF_SHOWTIP` on `NIM_ADD`/`NIM_MODIFY` (Win7+ under `NOTIFYICON_VERSION_4` silently discards the tooltip without it). `Add`는 `NIM_ADD` 반환값을 확인하여 실패 시 `_added = false` 유지 + 즉시 반환. `UpdateIcon`/`UpdateTooltip`/`UpdateIconAndTooltip` 도 `NIM_MODIFY` 반환값을 확인해 실패 시 `Logger.Debug` 로 기록 (트레이 쉘 재시작 직후 등 일시적 실패 진단용). **`hIcon` ownership stays with the caller** | `Add(hIcon, tip)`, `UpdateIcon`, `UpdateTooltip`, `UpdateIconAndTooltip`, `Remove() → bool` |
| [Core/Windowing/LayeredOverlayBase](../Core/Windowing/LayeredOverlayBase.cs) | Layered window + DIB + DPI + drag/snap engine. `IDisposable` instance constructed as `(IntPtr hwnd, Func<hdc, style, metrics, (w, h)> renderToDib)`. 생성자에서 `CreateCompatibleDC` 실패 시 `InvalidOperationException`. `EnsureDib`는 `CreateDIBSection` 실패 시 `_ppvBits`를 보존하여 해제된 메모리 참조 방지. `EnsureFont`는 `CreateFontW` 가 `IntPtr.Zero` 를 반환하면 `Logger.Warning` + 조기 반환으로 기존 폰트/캐시 키를 보존해 다음 호출에서 재시도를 유도한다 (빈 HFONT 가 캐시에 고착되는 회귀 방지). Engine owns **DPI multiplication** internally via `Kernel32.MulDiv(fontSize, dpiY, 72)` (preferred over `Math.Round` because 64-bit precision matters at fractional DPI ratios). Holds `_fixedLabelWidth` / `_lastStyle` / `_currentDpiScale` / drag state / snap rects internally | `Render`/`Show`/`Hide`/`UpdateAlpha`/`UpdatePosition`/`UpdateScaledSize`/`HandleDpiChanged`/`ForceTopmost`/`BeginDrag(snapToWindows)`/`EndDrag() → (x, y)`/`HandleMoving(ref RECT, style, snapToWindows, snapThresholdPx)`/`GetBaseSize`/`GetLastPosition`/`Hwnd`/`IsVisible` |
| [Core/Windowing/OverlayStyle + OverlayMetrics](../Core/Windowing/OverlayStyle.cs) | `internal readonly record struct` pair forming the engine's **primitive-only boundary**. `OverlayStyle` (engine **input**, 14 fields): `LabelText : string`, `MeasureLabels : (string, string, string)` tuple, `IsBold : bool` (NOT `FontWeight` enum), `CapsLockOn : bool`, `*LogicalPx` size fields (IndicatorScale applied, DPI not yet), color hex strings. `OverlayMetrics` (engine → callback **output**, 9 fields): DPI-scaled pixel values + `TextVCenterOffsetPx` (per-font asymmetric-cell correction) | — |
| [Core/Windowing/ModalDialogLoop](../Core/Windowing/ModalDialogLoop.cs) | Static `Run(hwndDialog, hwndOwner, ref bool isClosedFlag)` replacing the duplicated `EnableWindow(owner, false) + while(GetMessageW... IsDialogMessageW(...)) { Translate/Dispatch } + EnableWindow(owner, true)` boilerplate in all three dialogs. Re-posts `WM_QUIT` when consumed by the nested loop so the outer message loop also terminates. `IsActive` / `ActiveDialog` expose the current modal HWND so non-UI-thread consumers (e.g. `DetectionLoop`) can gate their own side effects while a dialog is modal — 내부 `s_activeDialog` 필드는 `IntPtr` 라 `volatile` 을 받지 못하므로 모든 접근에 `Volatile.Read` / `Volatile.Write` 를 명시해 교차 스레드 가시성을 보장한다. `RunExternal(hwndSentinel, action)` 는 `MessageBoxW` 처럼 Win32 가 자체 메시지 루프를 돌리는 외부 모달 구간에 `IsActive` 가드만 씌우는 경량 변형 (메시지 펌프/`EnableWindow` 미조작, 중첩 시 이전 값 스택 저장/복원) — see [implementation-notes → Detection → Modal dialog gate](implementation-notes.md#modal-dialog-gate) | `Run`, `RunExternal`, `IsActive`, `ActiveDialog` |
| [Core/Windowing/Win32DialogHelper](../Core/Windowing/Win32DialogHelper.cs) | DPI-aware dialog non-client metrics + 9 pt system font helper + dialog position calculator + `WNDCLASSEXW` registration helper. `CreateDialogFont(dpiY) → SafeFontHandle` wraps the `CreateFontW` + `SafeFontHandle` boilerplate used by all three dialogs. `CalculateDialogPosition` unifies the monitor-centered (Cleanup/Settings) and cursor-anchored (ScaleInput) patterns. `RegisterStandardClass(className, wndProc, hbrBackground?)` is the **single entry point** for all `WNDCLASSEXW` registration — internally enforces `hCursor=IDC_ARROW` so no caller can omit the class cursor (defends against `IDC_APPSTARTING` cursor fallback on first-launch hover) | `CalculateNonClientHeight/Width`, `CalculateFontHeightPx`, `ApplyFont`, `CreateDialogFont`, `CalculateDialogPosition`, `RegisterStandardClass` |
| [Core/Windowing/ScrollableDialogHelper](../Core/Windowing/ScrollableDialogHelper.cs) | 세로 스크롤 다이얼로그(`SettingsDialog` / `CleanupDialog`) 공용 스크롤 계산·WinAPI 호출. `ScrollTo(hwndViewport, ref scrollPos, scrollMax, newPos)` 는 `SIF_POS` 갱신 + `ScrollWindowEx(SW_SCROLLCHILDREN \| SW_INVALIDATE \| SW_ERASE)` 로 자식 일괄 이동, `ResolveVScrollPosition` 은 SB_* 코드 → 목표 위치 해석, `CalculateWheelScrollPos` 는 `delta / WHEEL_DELTA × WheelLineStep × lineHeight` 계산. 상태(`scrollPos` 등)는 호출자 소유 — `ref` 로 동기화. `WheelLineStep = 3` 상수 공유 | `ScrollTo`, `ResolveVScrollPosition`, `CalculateWheelScrollPos`, `WheelLineStep` |
| [Core/Windowing/WindowProcessInfo](../Core/Windowing/WindowProcessInfo.cs) | HWND → class name / process name lookups. UWP apps hosted by `ApplicationFrameHost` are resolved to the actual child app process via `EnumChildWindows` (`[ThreadStatic]` bridge + `[UnmanagedCallersOnly]` callback for thread safety). Lives in `Core/` so `App/Config/Settings.cs` can resolve match keys without importing `App/Detector/` | `GetClassName`, `GetProcessName(IntPtr)`, `GetProcessName(uint processId)` |
| [Core/Shell/UriLauncher](../Core/Shell/UriLauncher.cs) | `Shell32.ShellExecuteW` `open` verb 단일 진입점. URL/파일 경로 모두 `rc <= 32` 실패 검출 + Warning 로깅 통일. 이름이 `Open` (not `OpenAsync`) — ShellExecuteW 는 동기 반환이라 호출자가 await 하지 않도록 명명. PR-04 분해 후 `Tray.cs` 의 3개 거의 동일한 ShellExecute 블록 (`OpenUpdatePage` / `OpenHomepage` / `OpenConfigFile`) 을 본 모듈에 위임 | `Open(string uriOrPath)`, `Open(string file, string parameters)` |
| [Core/Xml/XmlEntityCodec](../Core/Xml/XmlEntityCodec.cs) | XML 5 predefined entities (`&amp;` / `&lt;` / `&gt;` / `&quot;` / `&apos;`) escape/unescape. `Escape` 는 `&` 를 가장 먼저 처리해 다른 entity 안의 `&` 중복 인코딩 방지, `Unescape` 는 `&amp;` 를 마지막에 처리해 원본 `&` 가 다른 entity 의 앰퍼샌드를 잡아채는 것 방지. schtasks XML 조립 + 역파싱처럼 의존성 없이 1.0 spec 만 필요한 케이스용 (본격 XML 은 `XmlReader` 등) | `Escape`, `Unescape` |

---

## 4. App-specific modules

| Module | Purpose |
|--------|---------|
| [App/Models/](../App/Models/) | `AppConfig` immutable record + all enums (`DisplayMode`, `DetectionMethod`, `ImeState`, `FontWeight`, `Theme`, `NonKoreanImeMode`, `AppProfileMatch`, `AppFilterMode`, `TrayClickAction`, `Corner`, `PositionMode`, `DragModifier`) |
| [App/Config/DefaultConfig.cs](../App/Config/DefaultConfig.cs) | Magic-number constants: animation timings, pixel offsets, `DetectionBackoff{Step,Max}Ms`, `AppVersion = "0.9.2.8"`, `UpdateRepoOwner/Name`, system-input process list |
| [App/Config/Settings.cs](../App/Config/Settings.cs) | Static facade delegating to `AppSettingsManager : JsonSettingsManager<AppConfig>`. Handles `Load`/`Save`/`CheckReload`/`ResolveForApp` (per-app profile merge) |
| [App/Config/PositionCleanupService.cs](../App/Config/PositionCleanupService.cs) | `indicator_positions` 정리 작업의 비-UI 비즈니스 로직. `Compute(config)` 가 양쪽 dict (`IndicatorPositions` + `IndicatorPositionsRelative`) 키 합집합 + 실행 중 프로세스명 접미사 라벨링까지, `RemoveSelected(...)` 가 displayItems → originalNames 매핑 후 두 dict 제거한 새 AppConfig 반환. 다이얼로그 렌더링은 호출자(Tray) 가 담당. PR-04 분해 산물 |
| [App/Config/ThemePresets.cs](../App/Config/ThemePresets.cs) | 6 theme presets: `Custom`, `Minimal`, `Vivid`, `Pastel`, `Dark`, `System` (Windows accent color). 프리셋 전환 시 커스텀 색상 자동 백업/복원 (`custom_backup_*` 필드) |
| [App/Detector/ImeStatus.cs](../App/Detector/ImeStatus.cs) | `WM_IME_CONTROL` + `GetKeyboardLayout` IME state detection + `EVENT_OBJECT_IME_CHANGE` WinEvent hook |
| [App/Detector/SystemFilter.cs](../App/Detector/SystemFilter.cs) | 9-condition hide logic (lock screen, virtual desktop, fullscreen, class/owner-chain blacklist, app filter list, etc.). `ShouldHide` 내부에 `ResolveHwndProcess()` 로컬 캐시(`??=`) 로 동일 틱 내 `WindowProcessInfo.GetProcessName(hwnd)` 중복 호출 제거 |
| [App/Localization/I18n.cs](../App/Localization/I18n.cs) | Korean default, English fallback. Bool flag + ternary pattern (NativeAOT-friendly, zero allocation) |
| [App/Update/](../App/Update/) | `UpdateChecker` (background thread, fire-once-per-boot GitHub Releases poll) + `GitHubRelease` (JSON DTO + source-gen context) + `UpdateInfo` (callback payload). HTTP 전송 실패는 `Logger.Debug`, 200 응답 후 JSON 파싱 실패는 `Logger.Warning` (API 스키마 변동 가시화) |
| [App/UI/Overlay.cs](../App/UI/Overlay.cs) | Static facade over `LayeredOverlayBase`. Holds `private static AppConfig _config` + engine instance. **`BuildStyle(config, state)` is the sole `ImeState` → `OverlayStyle` conversion point** in the codebase |
| [App/UI/Animation.cs](../App/UI/Animation.cs) | Static facade over `OverlayAnimator`. `BuildAnimationConfig(config)` extracts primitives; `SetDimMode` routes `NonKoreanImeMode.Dim && state == NonKorean` into a `bool` |
| [App/UI/Tray.cs](../App/UI/Tray.cs) + [Tray.Menu.cs](../App/UI/Tray.Menu.cs) | Static facade over `NotifyIconManager`. 라이프사이클 (Initialize/HandleAddRetryTimer/UpdateState/OnUpdateFound/Recreate/Remove) + WM_COMMAND 디스패치 + `_pendingUpdate` 저장 + helpers (CleanupPositions/SetDefaultPositionToCurrent/BuildTooltip/ApplyQuickOpacity). `Tray.Menu.cs` partial 는 `ShowMenu` 만 분리 (메뉴 빌더). PR-04 분해 — schtasks 는 [App/Startup/StartupTaskManager](../App/Startup/StartupTaskManager.cs), 위치 정리 비즈니스 로직은 `PositionCleanupService`, URL/파일 열기는 `UriLauncher` 로 위임 |
| [App/Startup/StartupTaskManager.cs](../App/Startup/StartupTaskManager.cs) | Windows Task Scheduler (`schtasks.exe`) 기반 "시작 시 자동 실행" 작업 등록/조회/동기화. UI 와 무관한 XML 조립 + CLI 호출 + 결과 검증 책임. 외부 의존성은 `Logger` + `XmlEntityCodec` 만 — 메뉴 ID 는 모르고 호출자(Tray) 가 ID 와 핸들러를 mapping. PR-04 분해 산물 |
| [App/UI/TrayIcon.cs](../App/UI/TrayIcon.cs) | GDI-based dynamic icon bitmap — 캐럿+점(caret+dot) 디자인 고정. IME 상태별 배경색을 `CreateIcon`이 즉석에서 그려 `SafeIconHandle`로 반환 |
| [App/UI/AppMessages.cs](../App/UI/AppMessages.cs) | `WM_APP + N` message constants for cross-thread signalling |
| [App/UI/Dialogs/CleanupDialog.cs](../App/UI/Dialogs/CleanupDialog.cs) | `indicator_positions` management dialog (checkbox list of all entries, scrollable viewport when >15 items) |
| [App/UI/Dialogs/ScaleInputDialog.cs](../App/UI/Dialogs/ScaleInputDialog.cs) | Custom scale entry dialog (1.0–5.0, spawned at cursor position) |
| [App/UI/Dialogs/SettingsDialog.cs](../App/UI/Dialogs/SettingsDialog.cs) (+ `.Fields.cs` + `.Scroll.cs`) | Scrollable settings dialog (12 sections) split across 3 partial class files |

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
```

Additionally, `App/Config/` must not import `App/Detector/`:

```bash
git grep "using KoEnVue\.App\.Detector" App/Config/                   → 0
```

Risk 4 is the critical failure mode: letting `ImeState` leak into `Core/` would couple the generic layered-overlay engine to KoEnVue's IME problem domain and break reuse. The `OverlayStyle.LabelText : string` + `MeasureLabels : (string, string, string)` primitive boundary is the defense.

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
