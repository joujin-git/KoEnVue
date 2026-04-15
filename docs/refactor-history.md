# Refactor history — Stages 1–6

Chronological record of the structural refactors the codebase has been through. Each stage has a goal, a set of concrete changes, a byte-size delta on the NativeAOT publish, and a verification gate. This is the load-bearing "why does the code look this way" document — most of the non-obvious structural choices in [implementation-notes.md](implementation-notes.md) trace back to one of these stages.

Entry point: [CLAUDE.md](../CLAUDE.md). Conventions and invariants: [conventions.md](conventions.md).

---

## Stage 1 — Deduplication & initial split

**Goal**: Resolve P4 (no duplicate implementations) by centralizing shared Win32 plumbing into single modules, and split the 1169-line `Tray.cs` god-file into dialog-specific units.

Specific duplications resolved:

1. **`ColorHelper.TryNormalizeHex`** centralized — `SettingsDialog` had its own hex parser, now both dialogs and overlay rendering route through `Core/Color/ColorHelper`
2. **`SafeFontHandle`** adopted across all 3 dialogs — replaced manual `CreateFontW` / `DeleteObject` pairs. `using var hFont = ...` in each dialog's `Show` method frame, scope covers the full modal loop + `DestroyWindow`
3. **`CleanupDialog` modal loop** wrapped with `IsDialogMessageW` for Tab/ESC — replaced the ad-hoc inline message loop
4. **`Tray.cs` 1169-line god-file** split into dialogs — `CleanupDialog.cs`, `ScaleInputDialog.cs`, `SettingsDialog.cs` each become their own file with `[UnmanagedCallersOnly]` WndProc function pointers private to the file
5. **`Config → Detector` layer violation** fixed by extracting `WindowProcessInfo` — `App/Config/Settings.cs` needed HWND → process name lookups but was reaching into `App/Detector/`, violating the soon-to-be Core layer rule. `WindowProcessInfo.GetClassName/GetProcessName` was extracted so `App/Config/` no longer imports `App/Detector/`
6. **`AppConfig` god-object parameter narrowing** across Overlay/Animation/Logger/ImeStatus — too many methods took the full config record when they only read 1–3 fields

Wave 1-D also absorbed **`Win32DialogHelper`** as a new file because splitting `CleanupDialog`/`ScaleInputDialog` out of `Tray.cs` left the metric-calculation helpers orphaned — neither dialog file could "own" it without the other depending on the owner. All three dialogs now compute their non-client size via `CalculateNonClientHeight/Width`.

---

## Stage 2 — Core / App reorg

**Goal**: Physically separate reusable infrastructure from KoEnVue-specific application logic. This is the big P6 enforcement refactor.

Files were split into `Core/` (reusable Win32/.NET infrastructure, `KoEnVue.Core.*` namespace) and `App/` (KoEnVue-specific application logic, `KoEnVue.App.*` namespace). `App/` may import `Core/`; the reverse is forbidden.

Specific moves:

- `Native/*` → `Core/Native/*`
- `ColorHelper` → `Core/Color/`
- `DpiHelper` → `Core/Dpi/` — `BASE_DPI = 96` inlined as a `const int` so the module has no `Config` dependency (would otherwise require `using KoEnVue.App.Config;`)
- `Logger` + `LogLevel` → `Core/Logging/`
- `Win32DialogHelper` + `ModalDialogLoop` + `WindowProcessInfo` → `Core/Windowing/`
- Everything else stays in `App/` (models, detectors, settings, dialogs, UI facades)

`Program.cs` stays at the repo root in `namespace KoEnVue;` as the entry point — it is neither in `Core/` nor `App/` because it composes both layers.

**Verification**: `git grep "KoEnVue\.App" Core/` must return 0. Stage 2 left **one temporary exception** in `Core/Logging/Logger.cs` where `Initialize` still took `AppConfig` — this was documented and removed in Stage 3.

**Byte-size gate**: Stage 2 commit is **byte-identical to the pre-relocation publish output (4,891,136 bytes)**, proving the reorg is mechanical and ILC tree-shaking is unaffected. Any drift would have indicated an unintended behavior change.

---

## Stage 3 — Signature narrowing

**Goal**: Remove `AppConfig` from public APIs in the Overlay/Animation/Logger/Detector layers. Unblocks Stage 4 (Core extraction) by guaranteeing reusable code paths can be invoked without the god-object record.

### Stage 3-A — Logger

`Logger.Initialize(AppConfig)` narrowed to `Logger.Initialize(bool enabled, string? logFilePath, int maxSizeMb)`. `enabled = false` still short-circuits to `StopDrainThread`; `logFilePath` null-or-empty still falls back to `Path.Combine(AppContext.BaseDirectory, "koenvue.log")`.

`LogLevel` continues to be set independently via `Logger.SetLevel(LogLevel)` and is not part of `Initialize`'s signature. This removes the last Stage 2 layer violation in `Core/Logging/`.

### Stage 3-B — Overlay

`Overlay` holds a `private static AppConfig _config` populated by the two designated injection points (`Initialize` and `HandleConfigChanged`, which both still take `AppConfig` because they ARE the injection sites). All other public methods drop the parameter:

- `Show(int, int, ImeState)`
- `UpdateColor(ImeState)`
- `HandleDpiChanged()`
- `GetDefaultPosition(IntPtr, string)`

The drag helpers use **primitive injection** instead of `_config` because they're the path Stage 4 will lift into a reusable `LayeredOverlayBase`:

- `BeginDrag(bool snapToWindows)`
- `HandleMoving(ref RECT, ImeState, bool snapToWindows, int snapThresholdPx)`

Private renderers (`EnsureResources`, `EnsureFont`, `CalculateFixedLabelWidth`, `RenderIndicator`, `HandleDragDpiChange`) intentionally still take `AppConfig` and are refactored together in Stage 4-A via `OverlayStyle`.

### Stage 3-C — Animation

Largely unchanged — only seven internal `Overlay.*` call sites updated to the narrower signatures. `Animation.Initialize(hwndMain, hwndOverlay, config)` and the internal `_config` field stay because Stage 4-B refactors them via `OverlayAnimator` + `AnimationConfig` record together with the `NonKoreanImeMode` dimming logic.

### Stage 3-D — ImeStatus

`Detect(IntPtr hwnd, uint threadId, DetectionMethod method)` replaces the `AppConfig`-taking overload — the body only ever read `config.DetectionMethod`. The zero-config `Detect(hwnd, threadId)` overload (Auto-mode internal fallback used by the `EVENT_OBJECT_IME_CHANGE` hook) is unchanged.

`NonKoreanImeMode` is intentionally left in `AppConfig` because it only affects `Animation.GetTargetAlpha` dimming, not detection — Stage 4-B handles it.

### Stage 3-E — Settings

`Settings.cs` private helpers were surveyed for narrowing candidates (helpers taking `AppConfig` whole but using ≤3 fields). None qualified — `LoadFromFile`/`EnsureIndicatorPositions`/`EnsureSubObjects` are spec-protected hot-reload pipeline members, `MergeProfile` serializes the full record so it genuinely needs `AppConfig`, and `ResolveMatchKey`/`MatchProfile` chain into it. Leaving `Settings.cs` unchanged is the correct Stage 3 outcome.

### Byte-size gate

After C7 + C8, `dotnet publish -r win-x64 -c Release` produces a **byte-identical 4,891,136-byte exe** versus Stage 2 baseline. Smoke gate matrix exercised the narrowed paths: boot, `LogLevel` hot-reload INFO→DEBUG (proves `Logger.SetLevel` survives ILC + narrowed `ImeStatus.Detect` emits new Debug lines), corrupted-config injection (1 warning, no spam, exe alive), recovery + color hot-reload.

---

## Stage 4 — Core extraction (6 reusable modules)

**Goal**: Extract the 6 genuinely reusable modules into `Core/` while keeping every call site in `Program.cs` / `Animation.cs` / dialogs **byte-identical at the source level**. This is the biggest structural refactor in the project's history.

Modules extracted:

1. **[Core/Windowing/LayeredOverlayBase](../Core/Windowing/LayeredOverlayBase.cs)** — layered window + DIB + DPI + drag/snap engine
2. **[Core/Windowing/OverlayStyle + OverlayMetrics](../Core/Windowing/OverlayStyle.cs)** — `internal readonly record struct` pair forming the primitive-only engine boundary
3. **[Core/Animation/OverlayAnimator + AnimationConfig + AnimationTimerIds](../Core/Animation/)** — 5-state machine lifted out of `App/UI/Animation.cs`
4. **[Core/Config/JsonSettingsManager\<T\>](../Core/Config/JsonSettingsManager.cs)** + `JsonSettingsFile` — generic JSON settings pipeline
5. **[Core/Tray/NotifyIconManager](../Core/Tray/NotifyIconManager.cs)** — `Shell_NotifyIconW` wrapper
6. **[Core/Windowing/ModalDialogLoop](../Core/Windowing/ModalDialogLoop.cs)** — static `Run` replacing duplicated modal loop boilerplate

### 4-A — LayeredOverlayBase

Instance `IDisposable` engine owning all GDI state (`memDC`, `currentBitmap`, `currentFont`, `nullPen`), DPI cache, drag state (`_isDragging`/`_dragStart`/`_dragHotPoint`), snap rects, `_fixedLabelWidth`.

Constructor: `(IntPtr hwnd, Func<IntPtr, OverlayStyle, OverlayMetrics, (int, int)> renderToDib)`. Engine owns **DPI multiplication** — facade pre-multiplies `IndicatorScale` into `OverlayStyle.*LogicalPx` fields and engine runs `Kernel32.MulDiv(fontSize, dpiY, 72)` internally. The `MulDiv` path was preserved over `Math.Round` because simple rounding regressed label width by 0–1 px at fractional DPI ratios (the 64-bit `MulDiv` precision matters).

`Render(OverlayStyle style)` short-circuits when `newStyle == _lastStyle` via `record struct` value equality — this is the flip-flop guard.

### 4-B — OverlayStyle / OverlayMetrics

Two `internal readonly record struct`. Primitive-only boundary.

`OverlayStyle` (engine **input**, 14 fields):

- `LabelText : string` — current state resolved
- `MeasureLabels : (string, string, string)` — all three state labels for width measurement
- `IsBold : bool` (NOT `FontWeight` enum — deliberate)
- `CapsLockOn : bool` — state-independent, painted bilateral bars
- `*LogicalPx` size fields — `IndicatorScale` applied, DPI not yet
- Color hex strings

`OverlayMetrics` (engine → `renderToDib` callback **output**, 9 fields):

- DPI-scaled pixel values the callback drops directly into GDI APIs
- `TextVCenterOffsetPx` — per-font asymmetric-cell correction (positive = shift textRect up by N physical px)

**No `ImeState` / `FontWeight` import** — the App-side facade resolves the enum once in `BuildStyle` and hands strings/bools across the boundary. This is the Risk 4 gate.

### 4-C — Overlay facade shrinkage

`App/UI/Overlay.cs` shrunk from **842 → ~320 lines**. Holds `private static LayeredOverlayBase _engine` and delegates.

`BuildStyle(_config, state)` is the **sole `ImeState` → `OverlayStyle` conversion point** in the codebase — the only place in the entire facade that maps the enum to `BgHex` / `FgHex` / `LabelText`. `OnRenderToDib(hdc, style, metrics)` does raw GDI (`RoundRect` + `DrawTextW` + premultiplied alpha post-processing), returning the measured DIB size.

Every `Program.cs` / `Animation.cs` call site is unchanged at the source level.

### 4-D — OverlayAnimator + AnimationConfig + AnimationTimerIds

5-state machine (Hidden / FadingIn / Holding / FadingOut / Idle + highlight / slide sub-phases) lifted out of `App/UI/Animation.cs`.

Constructor injects 6 callbacks: `onAlphaChange(byte)`, `onPositionOffset(int, int)`, `onScaledSize(int, int)`, `onHide()`, `onForceTopmost()`, `getBaseSize() → (int, int)`.

`AnimationConfig` is a 17-field `record struct` where `AlwaysMode : bool` replaces the `DisplayMode.Always` enum check. `SetDimMode(bool)` replaces the `NonKoreanImeMode.Dim` enum check. The facade extracts every knob from `AppConfig` and passes primitives in, so **Core never touches `AppConfig` / `DisplayMode` / `NonKoreanImeMode`**.

`AnimationTimerIds` record injects the 5 `WM_TIMER` IDs (`Fade` / `Hold` / `Highlight` / `Topmost` / `Slide`) — Core can't hardcode them because the App's `WM_TIMER` allocation scheme is app-specific.

### 4-E — JsonSettingsManager\<T\>

Generic `where T : class, new()` pipeline for JSON-backed settings. Constructor: `(string filePath, JsonTypeInfo<T> typeInfo)` — `JsonTypeInfo<T>` is **mandatory** under NativeAOT + `JsonSerializerIsReflectionEnabledByDefault=false` (source-gen only).

Public API: `Load() → T`, `Save(T)`, `CheckReload() → bool`, `FilePath` property.

Five `protected virtual` hooks run in fixed order during `Load`:

1. `ApplyNullSafetyNet` — EnsureSubObjects equivalent
2. `PostDeserializeFixup` — MergeWithDefaults equivalent (STJ source-gen init-default workaround)
3. `Migrate` — version upgrades
4. `Validate` — clamping / normalization
5. `ApplyTheme` — preset application

Delete-safe hot reload (`File.Exists` pre-check) and corrupted-file spam prevention (mtime update inside catch) are baked into the Core pipeline. `JsonSettingsFile` is a static helper with `ReadAllTextStripBom` / `WriteAllText` / `GetLastWriteTimeUtc` — BOM-safe I/O wrapping `File.*`.

`AppSettingsManager : JsonSettingsManager<AppConfig>` is an `internal sealed` class at the bottom of `App/Config/Settings.cs` with 5 hook overrides wiring existing Settings logic into the generic pipeline. `Settings.Load()` / `Save()` / `CheckConfigFileChange()` remain as static facade methods delegating to a single `AppSettingsManager` instance — call sites in `Program.cs` are unchanged.

### 4-F — NotifyIconManager

`Shell_NotifyIconW` wrapper. Constructor `(IntPtr hwndOwner, uint callbackMessage, Guid iconGuid)` captures the fields that appear on every `NOTIFYICONDATAW` payload, so later calls only need the mutating values.

Five methods: `Add(hIcon, tip)`, `UpdateIcon(hIcon)`, `UpdateTooltip(tip)`, `UpdateIconAndTooltip(hIcon, tip)`, `Remove() → bool`.

Preserves `NIF_SHOWTIP` on both `NIM_ADD` and `NIM_MODIFY` (Win7+ under `NOTIFYICON_VERSION_4` silently discards the tooltip without it). **`hIcon` ownership stays with the caller** — the manager does not `DestroyIcon` on removal because the facade owns the `SafeIconHandle`.

`Tray.cs` holds a `private static NotifyIconManager _notifyIcon` field; `Initialize` / `UpdateState` / `Remove` collapse to 1–3 line delegations.

### 4-G — ModalDialogLoop

Single `static void Run(IntPtr hwndDialog, IntPtr hwndOwner, ref bool isClosedFlag)` replacing the duplicated 15–20 line boilerplate in `CleanupDialog` / `ScaleInputDialog` / `SettingsDialog`. The `ref bool isClosedFlag` lets each dialog's WndProc signal close from inside `WM_COMMAND` / `WM_CLOSE` without the loop helper knowing the close semantics.

All three dialogs' modal loop invocation reduces to a single `ModalDialogLoop.Run(_hwndDialog, hwndMain, ref _isClosed);` call.

### Risk 4 hard-fail enum gate

Stage 4's critical constraint: **`git grep "ImeState" Core/` = 0**.

- `OverlayStyle.MeasureLabels` is a primitive 3-string tuple (not `Dictionary<ImeState, string>`) precisely to avoid the enum import
- `OverlayStyle.LabelText` is the facade-resolved label for the current state
- `OverlayAnimator.SetDimMode(bool)` abstracts `NonKoreanImeMode.Dim`
- `AnimationConfig.AlwaysMode : bool` abstracts `DisplayMode.Always`

Verified: `git grep "ImeState" Core/` = 0, `git grep "NonKoreanImeMode" Core/` = 0, `git grep "KoEnVue\.App" Core/` = 0. Doc comments referencing these terms as English words were also scrubbed.

### Risk 2 (NativeAOT ILC regression) + byte gate

Publish exe = **4,911,104 bytes** vs Stage 2 baseline 4,891,136 bytes → **+19,968 bytes (+19.5 KB)**, within the ≤+100 KB gate. Engine-instance pattern + `JsonTypeInfo<T>` constructor injection + delegate-callback state machine all survive ILC tree-shaking without rooting anything new.

Runtime smoke (launch under P5 `requireAdministrator`) confirmed: boot succeeds, `VirtualDesktopManager COM initialized`, `config.json` auto-create via `JsonSettingsManager.Save`, `Tray icon initialized` (`NotifyIconManager` path), `IME change hook registered` (`Detector` path), `Initialization complete, entering message loop`, first `PositionUpdated` emitted after foreground resolve — end-to-end engine path verified.

---

## Stage 5 — Review-driven cleanup

**Goal**: Audit-driven sweep covering 2 minor compliance fixes plus 2 file-size improvements. Not a structural refactor; this is the "tie up loose ends" stage.

### 5-1 — `JsonSettingsManager.Save` catch narrowed

`catch (Exception ex)` → `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)`. Pre-Stage-5 the wide catch could absorb logic bugs in source-gen JSON serialization (e.g., a `T` with a property the source-gen context doesn't know about). Now those propagate.

### 5-2 — `SystemFilter` IID extracted to const

`Guid iid = typeof(IVirtualDesktopManager).GUID;` → `private static readonly Guid IID_IVirtualDesktopManager = new("a5cd92ff-29be-454c-8d04-d82879fb3f1b");`. The reflection path `typeof(...).GUID` is technically supported under NativeAOT, but explicit const sidesteps any future trim-mode rooting requirement and makes the IID greppable.

### 5-3 — `SettingsDialog.cs` split into 3 partials

Was 1024 lines. Now:

- **`SettingsDialog.cs`** (426 lines — modal state, `Show`, `TryCommit`, dialog WndProc)
- **`SettingsDialog.Fields.cs`** (489 lines — `FieldType` enum, `FieldDef`/`RowDef` records, `BuildRowDefs` 13-section spec, 6 factory methods, helpers)
- **`SettingsDialog.Scroll.cs`** (156 lines — scroll state, `SetupScrollbar`/`ScrollTo`/`ScrollFieldIntoView`/`ResolveVScrollPosition`, viewport WndProc)

`partial class` shares all static state at compile time. **No call-site changes** — `SettingsDialog.Show(hwndMain, config, updateConfig)` is the same public entry point.

### 5-4 — `Program.cs` split with `Program.Bootstrap.cs`

Was 924 lines. Init/teardown helpers extracted to `Program.Bootstrap.cs` (246 lines):

- `_mutex` field
- Hotkey ID/parsing constants (`HOTKEY_TOGGLE_VISIBILITY`, `ModCtrl`/`ModAlt`/`ModShift`/`ModWin`, `FKeyPrefix`/`FKeyMin`/`FKeyMax`)
- Methods: `TryAcquireMutex`, `CleanupPreviousTrayIcon`, `RegisterWindowClasses`, `CreateMainWindow`, `CreateOverlayWindow`, `RegisterHotkeys`, `RegisterSingleHotkey`, `UnregisterHotkeys`, `ParseHotkey`, `OnProcessExit`

Main message loop, WndProc, detection thread, all event handlers stay in `Program.cs` (709 lines). Both files declare `internal static partial class Program` so the static fields/properties (`_config`, `_hwndMain`, `_hwndOverlay`, `_indicatorVisible`, etc.) compose at compile time.

### 5-5 — Stage 5 catch narrowing wave

Per the audit, `JsonSettingsManager.Load`'s outer catch and 4 `Tray.cs` schtasks-related catches (`IsStartupRegistered` had a *bare* `catch` violating policy item 1) were also narrowed. See the [silent catch policy in conventions.md](conventions.md#silent-catch-policy).

### Byte-size gate

Stage 5 publish exe = **4,903,424 bytes** vs Stage 4 baseline 4,911,104 bytes → **-7,680 bytes (-7.5 KB)**. Refactor reduces ILC output slightly (removed duplicate code metadata). Debug + release builds both clean (0 warnings, 0 errors). All P1–P6 + Risk 4 + layer-separation `git grep` invariants still return 0.

---

## Stage 6 — Update notification

**Goal**: Add GitHub Releases polling + tray menu item. Phase 1 only — detection and user-visible notification. Phase 2 (auto-download/install) is deliberately out of scope.

### Architecture

1. **Version source**: `DefaultConfig.AppVersion = "0.8.9.0"` (4-part `major.minor.build.revision`, accepted by `System.Version.TryParse`) — hand-edited const, no auto-bump from git tag or CI. `DefaultConfig.UpdateRepoOwner = "joujin-git"` + `UpdateRepoName = "KoEnVue"` pin the GitHub repo
2. **WinHTTP over HttpClient** — `Core/Native/WinHttp.cs` + `Core/Http/HttpClientLite.cs`. ~40 KB publish impact vs ~2.5 MB for `System.Net.Http.HttpClient` (60× smaller)
3. **Fire-once-per-boot** — `UpdateChecker.CheckInBackground` called exactly once from `Program.MainImpl`, gated by `config.UpdateCheckEnabled` (default `true`). No periodic polling
4. **Silent failure** — network error, HTTP non-200, unparseable JSON, prerelease skip, or `current >= latest` → `Logger.Debug` only
5. **Version comparison** via `System.Version.TryParse` — `NormalizeVersion` strips optional `v`/`V` prefix and semver prerelease/build suffixes via `ReadOnlySpan<char>`
6. **Thread marshaling** via `PostMessageW` + volatile `_pendingUpdate` field — reuses the existing `WM_APP + N` cross-thread pattern
7. **Tray menu injection** — conditional `MF_STRING` + `MF_SEPARATOR` at the top of the popup menu (ID `IDM_UPDATE_DOWNLOAD = 4008`). Click → `Shell32.ShellExecuteW` opens the GitHub release page

### KoEnVue.csproj version sync rule

`DefaultConfig.AppVersion` must stay in sync with `KoEnVue.csproj <Version>`:

- `AppVersion` drives the runtime `UpdateChecker` compare
- csproj `<Version>` drives the PE header's `AssemblyVersion` / `FileVersion` / `InformationalVersion` triad (shown in Windows File Properties)

Bumping only one creates a mismatch. `KoEnVue.csproj` also sets `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>` so `InformationalVersion` is a clean `0.8.9.0` instead of `0.8.9.0+{gitHash}` — the release tag already identifies the build.

Full release procedure lives in **[README.md → 릴리즈 (Releasing)](../README.md)**.

### P1 compliance

WinHTTP bindings + `SafeHandleZeroOrMinusOneIsInvalid` (from `Microsoft.Win32.SafeHandles`, already in BCL) + `System.Text.Json` source-gen + `System.Version` — every type is BCL + Win32. **No package references added.**

### Byte-size gate

Stage 6 publish exe = **4,943,872 bytes** vs Stage 5 baseline 4,903,424 bytes → **+40,448 bytes (+39.5 KB)**. WinHTTP + HttpClientLite + 3 JSON-source-gen types + UpdateChecker + 1 new P/Invoke in `Shell32` + 1 new message constant + ~50 lines of Program/Tray integration, all for ~40 KB. Debug + release builds clean (0 warnings, 0 errors). All P1–P6 + Risk 4 + layer-separation `git grep` invariants still return 0.

Current publish exe size (post-v0.8.9.0 release): **4,945,408 bytes** (minor drift from Stage 6 commit value due to rebuild metadata variation).

### End-to-end validation against v0.8.9.0 (cut 2026-04-14)

After the release was published, two smoke runs exercised the full pipeline:

1. **"no update" branch** — `AppVersion = 0.8.9.0` matching the release tag, UpdateChecker fires on boot, `HttpClientLite.GetString` hits `https://api.github.com/repos/joujin-git/KoEnVue/releases/latest`, HTTP 200 + JSON in 47 ms round-trip, source-gen deserializer parses `GitHubRelease`, `draft=false / prerelease=false` filter passes, `NormalizeVersion` strips `v` prefix, `Version.TryParse("0.8.9.0")` returns `0.8.9.0`, `IsNewer` returns `false`, `Logger.Debug("UpdateChecker: current=0.8.9.0 latest=v0.8.9.0 (no update)")` — tray menu stays unchanged

2. **"new version found" branch** — `AppVersion` temporarily patched to `0.8.8.0`, same run logs `Logger.Info("UpdateChecker: new version available — current=0.8.8.0 latest=v0.8.9.0")`, `_pendingUpdate = info`, `PostMessageW(WM_APP_UPDATE_FOUND)` on the background thread, main thread's WndProc dispatches to `HandleUpdateFound` → `Tray.OnUpdateFound` → stores in `Tray._pendingUpdate`, right-clicking tray injects the `MF_STRING` update item at the top, clicking routes through `Tray.OpenUpdatePage` → `Shell32.ShellExecuteW(IntPtr.Zero, "open", info.HtmlUrl, null, null, SW_SHOWNORMAL)` → returns > 32 (success), GitHub release page opens in default browser

Every link in the chain — `WinHttpSetTimeouts` inheritance, `SafeWinHttpHandle` RAII cleanup, JSON source gen, `NormalizeVersion` v-prefix handling, 4-part `Version.TryParse`, volatile `_pendingUpdate` + `PostMessageW` cross-thread hop, tray menu dynamic injection, `ShellExecuteW` browser launch — confirmed operational against a real GitHub release.

---

## Cumulative byte-size trajectory

| Stage | Publish exe (bytes) | Delta |
|-------|---------------------|-------|
| Pre-Stage 2 baseline | 4,891,136 | — |
| Stage 2 (Core/App reorg) | 4,891,136 | ±0 (byte-identical) |
| Stage 3 (signature narrowing) | 4,891,136 | ±0 (byte-identical) |
| Stage 4 (Core extraction, 6 modules) | 4,911,104 | +19,968 (+19.5 KB) |
| Stage 5 (cleanup + partial splits) | 4,903,424 | -7,680 (-7.5 KB) |
| Stage 6 (update notification) | 4,943,872 | +40,448 (+39.5 KB) |
| Current (post-v0.8.9.0) | 4,945,408 | +1,536 (rebuild drift) |

**Cumulative delta**: Stage 2 → current = **+54,272 bytes (+53 KB)** for 6 Core modules + full update notification pipeline. Still well within the ≤+100 KB informal gate.
