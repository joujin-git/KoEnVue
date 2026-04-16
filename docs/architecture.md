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
  • Hotkey handling (WM_HOTKEY)

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
│                            ModalDialogLoop + Win32DialogHelper + WindowProcessInfo
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
├── Program.Bootstrap.cs     partial class: mutex, window classes, hotkeys, teardown
└── KoEnVue.csproj
```

Every file in `Core/` is reusable in another Windows desktop project; every file in `App/` is product-specific.

---

## 3. Reusable Core modules

| Module | Purpose | Public surface |
|--------|---------|----------------|
| [Core/Native/*](../Core/Native/) | Raw P/Invoke surface. `Win32Types.cs` centralizes every struct + the `Win32Constants` class (SM/WS/DWMWA/etc.). `SafeGdiHandles.cs` hosts `SafeFontHandle`, `SafeIconHandle`, etc. `WinHttp.cs` hosts `SafeWinHttpHandle : SafeHandleZeroOrMinusOneIsInvalid` | `[LibraryImport]` only, no `[DllImport]` |
| [Core/Color/ColorHelper](../Core/Color/ColorHelper.cs) | Hex ↔ COLORREF ↔ RGB conversion. Malformed hex returns 0 / `(0,0,0)` instead of throwing, so a bad `config.json` doesn't leak GDI handles on the render hot path | `TryNormalizeHex`, `HexToColorRef`, `HexToRgb`, `ColorRefToRgb`, `RgbToHex` |
| [Core/Dpi/DpiHelper](../Core/Dpi/DpiHelper.cs) | Per-monitor DPI queries. `BASE_DPI = 96` is inlined as a `const int` so the module has no `Config` dependency | `GetScale`, `GetWorkArea`, `GetRawDpi`, `GetMonitorFromPoint` |
| [Core/Http/HttpClientLite](../Core/Http/HttpClientLite.cs) | Synchronous HTTPS GET wrapper backed by WinHTTP. NativeAOT publish impact ~40 KB (vs ~2.5 MB for `System.Net.Http.HttpClient`). Response body cap 256 KB, all failure paths return `null` | `GetString(userAgent, host, path, extraHeaders?, timeoutMs = 10_000) → string?` |
| [Core/Logging/Logger](../Core/Logging/Logger.cs) + `LogLevel` | Async file logger. `ConcurrentQueue` + `ManualResetEventSlim` + dedicated drain thread. Single `.log → .log.old` rotation. **No `AppConfig` parameter** — Stage 3-A narrowed `Initialize` to primitives | `Initialize(bool enabled, string? logFilePath, int maxSizeMb)`, `SetLevel(LogLevel)`, `Debug`/`Info`/`Warning`/`Error`, `Shutdown` |
| [Core/Config/JsonSettingsManager\<T\>](../Core/Config/JsonSettingsManager.cs) + [JsonSettingsFile](../Core/Config/JsonSettingsFile.cs) | Generic JSON-backed settings pipeline. `JsonTypeInfo<T>` injection is mandatory under NativeAOT + `JsonSerializerIsReflectionEnabledByDefault=false`. Five `protected virtual` hooks run in fixed order during `Load`: `ApplyNullSafetyNet` → `PostDeserializeFixup` → `Migrate` → `Validate` → `ApplyTheme`. Delete-safe hot reload (`File.Exists` pre-check) and corrupted-file spam prevention (mtime cache update inside `catch`) are baked in | `Load() → T`, `Save(T)`, `CheckReload() → bool`, `FilePath` |
| [Core/Animation/OverlayAnimator](../Core/Animation/OverlayAnimator.cs) + [AnimationConfig](../Core/Animation/AnimationConfig.cs) + `AnimationTimerIds` | 5-state machine (Hidden / FadingIn / Holding / FadingOut / Idle) + highlight/slide sub-phases, driven by `WM_TIMER`. 6 callbacks injected via constructor. `AnimationConfig` is a 17-field `record struct` where `AlwaysMode : bool` replaces `DisplayMode.Always`. `SetDimMode(bool)` replaces `NonKoreanImeMode.Dim` check — **Core never imports either enum** | `UpdateConfig`, `SetDimMode`, `TriggerShow → bool wasHidden`, `TriggerHide(forceHidden)`, `HandleTimer(timerId)` |
| [Core/Tray/NotifyIconManager](../Core/Tray/NotifyIconManager.cs) | `Shell_NotifyIconW` wrapper. Captures `(hwndOwner, callbackMessage, iconGuid)` once at construction. Preserves `NIF_SHOWTIP` on `NIM_ADD`/`NIM_MODIFY` (Win7+ under `NOTIFYICON_VERSION_4` silently discards the tooltip without it). `Add`는 `NIM_ADD` 반환값을 확인하여 실패 시 `_added = false` 유지 + 즉시 반환. **`hIcon` ownership stays with the caller** | `Add(hIcon, tip)`, `UpdateIcon`, `UpdateTooltip`, `UpdateIconAndTooltip`, `Remove() → bool` |
| [Core/Windowing/LayeredOverlayBase](../Core/Windowing/LayeredOverlayBase.cs) | Layered window + DIB + DPI + drag/snap engine. `IDisposable` instance constructed as `(IntPtr hwnd, Func<hdc, style, metrics, (w, h)> renderToDib)`. 생성자에서 `CreateCompatibleDC` 실패 시 `InvalidOperationException`. `EnsureDib`는 `CreateDIBSection` 실패 시 `_ppvBits`를 보존하여 해제된 메모리 참조 방지. Engine owns **DPI multiplication** internally via `Kernel32.MulDiv(fontSize, dpiY, 72)` (preferred over `Math.Round` because 64-bit precision matters at fractional DPI ratios). Holds `_fixedLabelWidth` / `_lastStyle` / `_currentDpiScale` / drag state / snap rects internally | `Render`/`Show`/`Hide`/`UpdateAlpha`/`UpdatePosition`/`UpdateScaledSize`/`HandleDpiChanged`/`ForceTopmost`/`BeginDrag(snapToWindows)`/`EndDrag() → (x, y)`/`HandleMoving(ref RECT, style, snapToWindows, snapThresholdPx)`/`GetBaseSize`/`GetLastPosition`/`Hwnd`/`IsVisible` |
| [Core/Windowing/OverlayStyle + OverlayMetrics](../Core/Windowing/OverlayStyle.cs) | `internal readonly record struct` pair forming the engine's **primitive-only boundary**. `OverlayStyle` (engine **input**, 14 fields): `LabelText : string`, `MeasureLabels : (string, string, string)` tuple, `IsBold : bool` (NOT `FontWeight` enum), `CapsLockOn : bool`, `*LogicalPx` size fields (IndicatorScale applied, DPI not yet), color hex strings. `OverlayMetrics` (engine → callback **output**, 9 fields): DPI-scaled pixel values + `TextVCenterOffsetPx` (per-font asymmetric-cell correction) | — |
| [Core/Windowing/ModalDialogLoop](../Core/Windowing/ModalDialogLoop.cs) | Static `Run(hwndDialog, hwndOwner, ref bool isClosedFlag)` replacing the duplicated `EnableWindow(owner, false) + while(GetMessageW... IsDialogMessageW(...)) { Translate/Dispatch } + EnableWindow(owner, true)` boilerplate in all three dialogs. Re-posts `WM_QUIT` when consumed by the nested loop so the outer message loop also terminates | `Run` |
| [Core/Windowing/Win32DialogHelper](../Core/Windowing/Win32DialogHelper.cs) | DPI-aware dialog non-client metrics + 9 pt system font helper + dialog position calculator. `CreateDialogFont(dpiY) → SafeFontHandle` wraps the `CreateFontW` + `SafeFontHandle` boilerplate used by all three dialogs. `CalculateDialogPosition` unifies the monitor-centered (Cleanup/Settings) and cursor-anchored (ScaleInput) patterns | `CalculateNonClientHeight/Width`, `CalculateFontHeightPx`, `ApplyFont`, `CreateDialogFont`, `CalculateDialogPosition` |
| [Core/Windowing/WindowProcessInfo](../Core/Windowing/WindowProcessInfo.cs) | HWND → class name / process name lookups. UWP apps hosted by `ApplicationFrameHost` are resolved to the actual child app process via `EnumChildWindows` (`[ThreadStatic]` bridge + `[UnmanagedCallersOnly]` callback for thread safety). Lives in `Core/` so `App/Config/Settings.cs` can resolve match keys without importing `App/Detector/` | `GetClassName`, `GetProcessName(IntPtr)`, `GetProcessName(uint processId)` |

---

## 4. App-specific modules

| Module | Purpose |
|--------|---------|
| [App/Models/](../App/Models/) | `AppConfig` immutable record + all enums (`DisplayMode`, `DetectionMethod`, `ImeState`, `FontWeight`, `Theme`, `NonKoreanImeMode`, `AppProfileMatch`, `AppFilterMode`, `TrayIconStyle`, `TrayClickAction`, `Corner`, `PositionMode`) |
| [App/Config/DefaultConfig.cs](../App/Config/DefaultConfig.cs) | Magic-number constants: animation timings, pixel offsets, `AppVersion = "0.9.1.0"`, `UpdateRepoOwner/Name`, system-input process list |
| [App/Config/Settings.cs](../App/Config/Settings.cs) | Static facade delegating to `AppSettingsManager : JsonSettingsManager<AppConfig>`. Handles `Load`/`Save`/`CheckReload`/`ResolveForApp` (per-app profile merge) |
| [App/Config/ThemePresets.cs](../App/Config/ThemePresets.cs) | 6 theme presets: `Custom`, `Minimal`, `Vivid`, `Pastel`, `Dark`, `System` (Windows accent color). 프리셋 전환 시 커스텀 색상 자동 백업/복원 (`custom_backup_*` 필드) |
| [App/Detector/ImeStatus.cs](../App/Detector/ImeStatus.cs) | `WM_IME_CONTROL` + `GetKeyboardLayout` IME state detection + `EVENT_OBJECT_IME_CHANGE` WinEvent hook |
| [App/Detector/SystemFilter.cs](../App/Detector/SystemFilter.cs) | 9-condition hide logic (lock screen, virtual desktop, fullscreen, class/owner-chain blacklist, app filter list, etc.) |
| [App/Localization/I18n.cs](../App/Localization/I18n.cs) | Korean default, English fallback. Bool flag + ternary pattern (NativeAOT-friendly, zero allocation) |
| [App/Update/](../App/Update/) | `UpdateChecker` (background thread, fire-once-per-boot GitHub Releases poll) + `GitHubRelease` (JSON DTO + source-gen context) + `UpdateInfo` (callback payload) |
| [App/UI/Overlay.cs](../App/UI/Overlay.cs) | Static facade over `LayeredOverlayBase`. Holds `private static AppConfig _config` + engine instance. **`BuildStyle(config, state)` is the sole `ImeState` → `OverlayStyle` conversion point** in the codebase |
| [App/UI/Animation.cs](../App/UI/Animation.cs) | Static facade over `OverlayAnimator`. `BuildAnimationConfig(config)` extracts primitives; `SetDimMode` routes `NonKoreanImeMode.Dim && state == NonKorean` into a `bool` |
| [App/UI/Tray.cs](../App/UI/Tray.cs) | Static facade over `NotifyIconManager`. Menu construction, schtasks startup registration, `_pendingUpdate` storage |
| [App/UI/TrayIcon.cs](../App/UI/TrayIcon.cs) | GDI-based dynamic icon bitmap (CaretDot / Static styles, per-state color) |
| [App/UI/AppMessages.cs](../App/UI/AppMessages.cs) | `WM_APP + N` message constants for cross-thread signalling |
| [App/UI/Dialogs/CleanupDialog.cs](../App/UI/Dialogs/CleanupDialog.cs) | `indicator_positions` management dialog (checkbox list of all entries, scrollable viewport when >15 items) |
| [App/UI/Dialogs/ScaleInputDialog.cs](../App/UI/Dialogs/ScaleInputDialog.cs) | Custom scale entry dialog (1.0–5.0, spawned at cursor position) |
| [App/UI/Dialogs/SettingsDialog.cs](../App/UI/Dialogs/SettingsDialog.cs) (+ `.Fields.cs` + `.Scroll.cs`) | 60-field scrollable settings dialog split across 3 partial class files |

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
