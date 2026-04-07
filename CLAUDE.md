# CLAUDE.md — KoEnVue Project Guide

## What is this?

Windows Korean/English IME state indicator. Shows current input mode (한/En/EN) next to the caret.
C# 14 / .NET 10 + NativeAOT single exe (~3MB). Zero external NuGet packages.

## Tech Stack

- **Target**: `net10.0-windows`, PublishAot, AllowUnsafeBlocks
- **Source Generators**: `[LibraryImport]`, `[GeneratedComInterface]`, `[JsonSerializable]`
- **P/Invoke**: User32, Imm32, Shell32, Gdi32, Kernel32, Shcore, Ole32, OleAut32
- **`[DllImport]` is banned** — always use `[LibraryImport]`

## Hard Constraints (P1-P5)

| Rule | Description |
|------|-------------|
| **P1** | Zero external NuGet packages. .NET 10 BCL + Windows API only |
| **P2** | UI text defaults to Korean. Log messages and config keys in English |
| **P3** | No magic numbers → const/enum/config. No string comparisons → enum |
| **P4** | Shared modules enforced: DPI→DpiHelper, Color→ColorHelper, GDI handles→SafeGdiHandles, P/Invoke→Native/, structs/constants→Win32Types.cs |
| **P5** | app.manifest UAC requireAdministrator |

## Architecture

3-thread model:
```
Main thread (UI):       Message loop + rendering + tray + WM_TIMER animation
Detection thread (BG):  80ms polling → PostMessage to main
UIA thread (BG):        COM STA + IUIAutomation dedicated
```

## Project Structure

```
KoEnVue/
├── Native/      P/Invoke (one file per DLL) + Win32Types.cs + SafeGdiHandles.cs + AppMessages.cs + VirtualDesktop.cs + UiaInterfaces.cs
├── Models/      AppConfig (record) + DebugInfo (record) + 21 enums
├── Detector/    ImeStatus, CaretTracker (4-tier), SystemFilter (8-condition), UiaClient (STA COM)
├── UI/          Overlay (GDI rendering + LabelStyle + DebugOverlay), Animation (WM_TIMER state machine), Tray (system tray + schtasks), TrayIcon (GDI icon)
├── Config/      DefaultConfig, Settings (load/save/validate/migrate/hot-reload/app-profiles), ThemePresets (6 themes)
├── Utils/       DpiHelper, ColorHelper (Hex↔ColorRef↔RGB), Logger, I18n (Ko/En UI text)
└── Program.cs   Main loop (3-thread management + lifecycle)
```

## Build & Run

```bash
dotnet build                          # build
dotnet publish -r win-x64 -c Release  # NativeAOT publish
```

csproj has `NoWarn: SYSLIB1051` (.NET 10 LibraryImport IntPtr diagnostic suppression).

## .NET 10 Compatibility Notes

| Issue | Resolution |
|-------|------------|
| `ImplicitUsings` | Not enabled by default in .NET 10 → explicit in csproj |
| `Nullable` | Explicit `enable` in csproj for nullable reference warnings |
| `SYSLIB1051` | `IntPtr` promoted to error in `[LibraryImport]` source gen → `NoWarn` |
| `uint → nint` cast | Explicit `(nint)` cast required for `uint` constants passed to `IntPtr` params |
| `int & uint` mixed ops | `GetWindowLongW` (int) + `WS_CAPTION` (uint) → CS0034. Use `unchecked((int)WS_CAPTION)` local const |
| NativeAOT COM | `Marshal.GetObjectForIUnknown` unavailable → use `StrategyBasedComWrappers.GetOrCreateObjectForComInstance` |

## Key Implementation Decisions

Corrections and deviations from the original spec (`prompts/`) applied during implementation. These are already reflected in the code — listed here for context.

- **NativeAOT COM pattern**: `[GeneratedComInterface]` + `StrategyBasedComWrappers` throughout (spec assumed `Marshal.GetObjectForIUnknown`)
- **Delegate GC prevention**: Static field retention for P/Invoke callbacks (e.g. `_imeChangeCallback` in ImeStatus.cs)
- **COM init ordering**: Main thread pre-initializes COM STA + forces `SystemFilter` static constructor before detection thread starts
- **Overlay window class**: Separately registered (spec only registered main window class)
- **WM_DESTROY guard**: `hwnd == _hwndMain` check prevents app exit when overlay is destroyed (shared WndProc)
- **CaretTracker tier-1 retry**: Up to 3 retries at 50ms intervals when `rcCaret == (0,0,0,0)`, immediate null on API failure
- **GDI NULL_PEN**: Required for RoundRect/Ellipse to avoid 1px black border. GetStockObject handles are system-owned — never DeleteObject
- **Premultiplied alpha**: Post-processing needed for GDI output (non-premultiplied) with DrawTextW antialiasing edges
- **DIB top-down**: Negative biHeight so (0,0) is top-left
- **Tray callback routing**: Handled in Program.cs (not Tray.cs) because it needs `_indicatorVisible` access
- **Settings.cs**: Static class, record `with` expressions (no builder). LoadFromFile pipeline: Deserialize → Migrate → Validate → ThemePresets.Apply
- **I18n.cs**: Bool flag + ternary pattern (NativeAOT-friendly, zero allocation). Uses `GetUserDefaultUILanguage()` P/Invoke since `InvariantGlobalization: true` disables CultureInfo
- **UIA COM interfaces**: Vtable-layout placeholder methods instead of interface inheritance (Native/UiaInterfaces.cs)
- **UiaClient.cs**: `ConcurrentQueue<UiaRequest>` + `ManualResetEventSlim` for STA thread communication. COM objects released in finally blocks
- **Config hot-reload**: `_lastConfigMtime` updated after `Settings.Save()` to prevent self-triggered reload
- **Volatile field workaround**: `Action<AppConfig>` callback pattern since `ref` cannot be used with volatile `_config`
- **LabelStyle**: 3 modes — "text" (한/En/EN), "dot" (colored dot), "icon" (ㄱ/A)
- **File logging**: Async queue (`ConcurrentQueue` + `ManualResetEventSlim` + dedicated `LogDrain` thread). Non-blocking enqueue from caller threads. Single rotation (.log → .log.old). `Logger.Initialize(config)` / `Logger.Shutdown()` lifecycle
- **Label border**: GDI `CreatePen(PS_SOLID)` + `NULL_BRUSH` overlay stroke. Pen-width inset (`borderW/2`) to keep border inside DIB
- **Slide animation**: Ease-out cubic `1-(1-t)³` interpolation via `TIMER_ID_SLIDE`. All animation timers use `DefaultConfig.AnimationFrameMs` (16ms ≈ 60fps). DWM VSync ensures Show→UpdatePosition same-frame has no visual flash
- **Fixed position anchor/monitor**: `MonitorFromWindow`/`MonitorFromPoint` to resolve `primary`/`mouse`/`active` monitor. 6 anchor types with offset from anchor point
- **NonKoreanImeMode Dim**: `GetTargetAlpha` applies `DefaultConfig.DimOpacityFactor` (0.5) for both active/idle states when `_currentState == NonKorean && Dim`
- **F-key hotkeys**: `ParseHotkey` supports F1-F12 via pattern match → `VK_F1 + (fNum - 1)`. Modifier/F-key strings are `private const` (P3)
- **String→Enum P3 completion**: 8 string config fields (LabelStyle, Theme, TrayClickAction, AppProfileMatch, MultiMonitor, TrayIconStyle, Anchor, Monitor) converted to `[JsonStringEnumConverter]` enums. All const-string comparisons replaced with enum pattern matching

## Spec Files

- `docs/KoEnVue_PRD.md` — Product requirements document (full spec)
