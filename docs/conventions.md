# Conventions

Enforcement rules, code style policies, and .NET 10 / NativeAOT compatibility notes. Companion to [CLAUDE.md](../CLAUDE.md) — this file is the normative source for "what gets rejected in review".

Related: [architecture.md](architecture.md) (structural rules), [implementation-notes.md](implementation-notes.md) (non-obvious implementation choices).

---

## Hard constraints (P1–P6)

| Rule | Description |
|------|-------------|
| **P1** | Zero external NuGet packages. .NET 10 BCL + Windows API only |
| **P2** | UI text defaults to Korean. Log messages and config keys in English |
| **P3** | No magic numbers → `const`/`enum`/config. No string comparisons → `enum` |
| **P4** | No duplicate implementations — the same functionality must never be re-implemented in a second location; always reach for the shared module |
| **P5** | `app.manifest` UAC `requireAdministrator` — guarantees exe folder is writable (single source of truth for portable config) |
| **P6** | One-way layer dependency: `App/` may import `Core/`, but `Core/` must not import `App/` |

### P4 in practice

Centralized modules enforced across the codebase:

| Concern | Authoritative module |
|---------|----------------------|
| DPI | [Core/Dpi/DpiHelper](../Core/Dpi/DpiHelper.cs) |
| Color conversion | [Core/Color/ColorHelper](../Core/Color/ColorHelper.cs) |
| GDI handles | [Core/Native/SafeGdiHandles](../Core/Native/SafeGdiHandles.cs) (`SafeFontHandle`, `SafeIconHandle`, ...) |
| P/Invoke | [Core/Native/*](../Core/Native/) |
| Structs / constants | [Core/Native/Win32Types.cs](../Core/Native/Win32Types.cs) (`Win32Constants` class — SM/WS/DWMWA/etc.) |
| Win32 dialog metrics | [Core/Windowing/Win32DialogHelper](../Core/Windowing/Win32DialogHelper.cs) |
| Modal dialog loop | [Core/Windowing/ModalDialogLoop](../Core/Windowing/ModalDialogLoop.cs) |
| Dialog scroll helpers | [Core/Windowing/ScrollableDialogHelper](../Core/Windowing/ScrollableDialogHelper.cs) |
| Layered overlay engine | [Core/Windowing/LayeredOverlayBase](../Core/Windowing/LayeredOverlayBase.cs) |
| Overlay animation | [Core/Animation/OverlayAnimator](../Core/Animation/OverlayAnimator.cs) |
| JSON settings pipeline | [Core/Config/JsonSettingsManager\<T\>](../Core/Config/JsonSettingsManager.cs) |
| Tray icon | [Core/Tray/NotifyIconManager](../Core/Tray/NotifyIconManager.cs) |
| HTTP(S) GET (lightweight) | [Core/Http/HttpClientLite](../Core/Http/HttpClientLite.cs) |
| Async logging | [Core/Logging/Logger](../Core/Logging/Logger.cs) |
| HWND → class/process | [Core/Windowing/WindowProcessInfo](../Core/Windowing/WindowProcessInfo.cs) |

Before adding a new helper: **grep Core/ first**.

### P6 verification invariants

All must return **0 matches** at the repo root:

```bash
git grep "KoEnVue\.App"      Core/   # P6 namespace gate
git grep "ImeState"          Core/   # Risk 4 enum gate
git grep "NonKoreanImeMode"  Core/   # Risk 4 enum gate
git grep "DllImport"                 # banned, use [LibraryImport]
```

Additional sub-rule — `App/Config/` must not import `App/Detector/`:

```bash
git grep "using KoEnVue\.App\.Detector" App/Config/                   → 0
```

**Risk 4** (the critical failure mode): letting `ImeState` leak into `Core/` would couple the generic layered-overlay engine to KoEnVue's IME problem domain and break reuse. The `OverlayStyle.LabelText : string` + `MeasureLabels : (string, string, string)` primitive boundary is the defense. Similarly `OverlayStyle.IsBold : bool` keeps `FontWeight` out, `AnimationConfig.AlwaysMode : bool` keeps `DisplayMode` out, and `OverlayAnimator.SetDimMode(bool)` keeps `NonKoreanImeMode` out.

---

## Silent catch policy

Policy for `catch` blocks in this codebase:

### 1. Type narrowing over bare `catch`

Replace `catch { }` with `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` (or whichever specific types the `try` body can actually throw). Logic bugs (`NullReferenceException`, `IndexOutOfRangeException`) propagate instead of hiding.

### 2. Wide catch allowed when narrowing is impossible

If the `try` body is a single P/Invoke or COM call and expected exception types can't be listed (e.g., `[PreserveSig]` COM path in `SystemFilter.IsOnCurrentVirtualDesktop`, or WinHTTP marshalling edge cases in `HttpClientLite.GetString`), keep `catch (Exception ex)` + logging. A single-line `try` means a wide catch can't mask logic bugs.

### 3. Log level

- Hot-path / modal-internal swallowing → `Logger.Debug`
- Rare catastrophic paths (`CleanupPositions` `Process.GetProcesses` failure, `Tray.Remove` `NIM_DELETE` failure on shutdown) → `Logger.Warning`
- Silent failures that will propagate to users if ignored → `Logger.Error`

### 4. Intentionally empty catches

These are allowed without logging because they have no recovery path:

- `Program.Main`'s crash writer fallback — Logger may be uninitialized, can't log anything anyway
- `JsonSettingsManager.Load`'s nested mtime catch inside the outer catch that already logs

Both have single-line try bodies so wide catch poses no masking risk.

### 5. Logger self-catches stay silent + narrowed

`Logger.FlushQueue` and `Logger.Initialize` cannot recursively use `Logger.*` for their own file I/O failures, so they stay silent, but narrow to `IOException or UnauthorizedAccessException` so logic bugs in the drain loop / init path crash the drain thread and surface. `Initialize` also writes a single `Trace.WriteLine` fallback so the debugger has a hint.

`StopDrainThread` uses `_fileWriter?.WriteLine(...)` + `Console.Error.WriteLine` when `Join` times out, bypassing the already-closed queue.

### 6. Not every ignored Win32 return is a catch

`Program.Bootstrap.CleanupPreviousTrayIcon`'s `Shell_NotifyIconW(NIM_DELETE)` is a P/Invoke `bool` return value ignored, not an exception swallow. Missing icon (normal path for clean startup) returns false, so logging would spam on every boot.

### 7. Stage 5 catch narrowing wave

`JsonSettingsManager.Load`'s outer catch and all four `Tray.cs` schtasks-related catches (`IsStartupRegistered` had a *bare* catch violating rule 1, `ToggleStartupRegistration`, `SyncStartupPathCore`) were narrowed to:

- **`Load` outer catch**: `IOException or UnauthorizedAccessException or JsonException or NotSupportedException`
- **schtasks catches**: `Win32Exception or InvalidOperationException or PlatformNotSupportedException or FileNotFoundException`

Logic bugs in pipeline hooks (`Migrate`/`Validate`/`ApplyTheme`) and in path normalization helpers now propagate instead of being absorbed.

### 8. Detection loop catch

`DetectionLoop`의 while 본문은 `catch (Exception ex)` + `Logger.Warning`으로 래핑된다. 단일 폴링 예외(예: `WindowProcessInfo.GetProcessName` 내 `Process.GetProcessById` 실패)가 감지 스레드 전체를 종료시키지 않도록 보호하며, 다음 폴링 주기에서 정상 재개한다. `Thread.Sleep`은 try 바깥에 위치하여 예외 후에도 폴링 간격이 유지된다. Rule 2의 변형으로, P/Invoke + BCL 호출이 혼합된 긴 본문이므로 wide catch가 정당하며 `Logger.Warning` 레벨은 Rule 3의 "rare catastrophic" 기준에 해당한다.

---

## .NET 10 compatibility notes

| Issue | Resolution |
|-------|------------|
| `ImplicitUsings` | `<ImplicitUsings>enable</ImplicitUsings>` in csproj — the default `Microsoft.NET.Sdk` global `using`s are active, but files still list their non-default `using` directives explicitly (consistency with `App/` ↔ `Core/` cross-namespace imports) |
| `Nullable` | Explicit `<Nullable>enable</Nullable>` for nullable reference warnings |
| `SYSLIB1051` | `IntPtr` promoted to error in `[LibraryImport]` source gen under .NET 10 → `<NoWarn>SYSLIB1051</NoWarn>` |
| `uint → nint` cast | Explicit `(nint)` cast required for `uint` constants passed to `IntPtr` params (e.g., `(nint)WS_CAPTION`) |
| `int & uint` mixed ops | `GetWindowLongW` returns `int` but Win32 constants like `WS_CAPTION` are `uint` → CS0034. Use `unchecked((int)...)` |
| STJ record `init` defaults | Source gen drops `init` defaults for properties absent from JSON under `JsonSerializerIsReflectionEnabledByDefault=false`. **Workaround**: `MergeWithDefaults()` in `Settings.cs` — serializes a freshly constructed default record to JSON, overlays user keys, deserializes back |
| `CultureInfo` absent | `InvariantGlobalization=true` strips ICU. Use `CultureInfo.InvariantCulture` only; detect system language via `Kernel32.GetUserDefaultUILanguage` P/Invoke |
| `volatile` + `ref` | `ref` cannot be used with `volatile` fields. Use `Action<T>` callback pattern for config updates (`_config` is `volatile`) |

---

## NativeAOT specifics

### Reflection is disabled

`<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>` is set in [KoEnVue.csproj](../KoEnVue.csproj). Every `JsonSerializer.Serialize` / `Deserialize` call **must** go through a `JsonTypeInfo<T>` from a source-gen context (`[JsonSerializable(typeof(T))]`).

`JsonSettingsManager<T>` takes `JsonTypeInfo<T>` as a constructor parameter for this reason — there is no reflection fallback path.

### `[LibraryImport]` only, `[DllImport]` banned

`[LibraryImport]` uses a source generator to emit marshalling code at compile time, which is NativeAOT-compatible. `[DllImport]` falls back to reflection-based marshalling at runtime and will break under ILC trimming.

Verification: `git grep "DllImport"` must return 0.

### `[UnmanagedCallersOnly]` + `delegate*` for Win32 callbacks

Prefer function pointer + `[UnmanagedCallersOnly]` over delegate marshaling for Win32 callbacks. Used by `EnumWindows`, `EnumChildWindows`, dialog `WndProc`, window class `WndProc`, etc.

Example (from drag snap):

```csharp
[UnmanagedCallersOnly]
private static int EnumWindowsCallback(IntPtr hwnd, IntPtr lParam)
{
    // ...
    return 1; // Win32 BOOL = 4 bytes, not 1
}

// Registration:
unsafe
{
    delegate* unmanaged<IntPtr, IntPtr, int> callback = &EnumWindowsCallback;
    User32.EnumWindows(callback, IntPtr.Zero);
}
```

Return type is `int` (not `bool`) because Win32 `BOOL` is 4 bytes.

### Delegate GC prevention

For callbacks that *must* use a managed delegate (e.g., `SetWinEventHook`), retain the delegate in a `private static` field. Without the static reference, the GC collects the delegate mid-flight and the Win32 call crashes with `AccessViolation`.

Example: `_imeChangeCallback` in [ImeStatus.cs](../App/Detector/ImeStatus.cs).

### ILC byte-size discipline

Every structural refactor tracks the NativeAOT publish exe size before and after. The informal gate is **≤+100 KB per stage**.

---

## Dialog patterns

All three dialogs (`CleanupDialog`, `ScaleInputDialog`, `SettingsDialog`) share:

- **`using var hFont = Win32DialogHelper.CreateDialogFont(dpiY);`** at the top of `Show`, scope covers the full modal loop + `DestroyWindow`
- **`ModalDialogLoop.Run(hwndDialog, hwndOwner, ref _isClosed);`** for the modal loop
- **`Win32DialogHelper.ApplyFont(child, hFont.DangerousGetHandle())`** via `WM_SETFONT` on each child control
- **`Win32DialogHelper.CalculateDialogPosition(hMonitor, w, h, anchor?)`** for positioning (null = centered, POINT = anchored)
- **`[UnmanagedCallersOnly]` `WndProc`** function pointer private to each file (no NativeAOT export name collision)
- **Tab/Enter/ESC** routed through `IsDialogMessageW` in `ModalDialogLoop`
- **Detection-thread gate** via `ModalDialogLoop.IsActive` — `DetectionLoop` suppresses its per-tick foreground processing while any of the three dialogs is modal, so polling-side effects (indicator jumping to the dialog HWND, focus-interfering `TriggerShow` renders) never reach the UI thread. New modal dialogs using `ModalDialogLoop.Run` inherit this behavior without any call-site hide logic. 외부 모달(`User32.MessageBoxW` 등 Win32 가 자체 메시지 루프를 돌리는 경우)은 `ModalDialogLoop.RunExternal(hwndSentinel, action)` 으로 감싸 동일한 감지 스레드 가드를 적용한다 (메시지 펌프/`EnableWindow` 건드리지 않고 `IsActive` 센티넬만 세팅)

The `SafeFontHandle` `using` pattern is critical — early release would crash `DrawTextW` while child controls still reference the HFONT.

---

## Logging conventions

- Log messages in English (P2). Config keys in English (P2)
- UI text in Korean default + English fallback via [I18n.cs](../App/Localization/I18n.cs) — bool flag + ternary pattern (NativeAOT-friendly, zero allocation)
- Logger is async — `ConcurrentQueue` + `ManualResetEventSlim` + dedicated drain thread. Single `.log → .log.old` rotation at `LogMaxSizeMb`. Queue is capped at `MaxQueueSize = 10_000` to defend against rotation failures that could otherwise grow the backing queue indefinitely; oldest messages are dropped and a single summary warning is written once writing resumes
- Initialize with primitives: `Logger.Initialize(enabled, path, maxSizeMb)` (since Stage 3-A). `LogLevel` set separately via `SetLevel`
- Default log path: `Path.Combine(AppContext.BaseDirectory, "koenvue.log")` — next to the exe, matching the portable config policy

---

## Testing

No unit test project. Verification is:

- **Debug + Release build both clean** (0 warnings, 0 errors). A debug-only build leaves the release exe outdated
- **Smoke gate matrix** exercised manually: boot → tray icon appears → indicator follows foreground → IME toggle changes color → drag works → drag with Shift locks axis → drag with snap sticks to edges → CAPS LOCK toggles bars → config hot-reload → corrupted config spam check → update check (both branches: no update / new version) → Start Menu ESC dismissal hides indicator → Search bar ESC dismissal hides indicator
- **`git grep` invariants** (listed above) all return 0
- **Byte-size tracking** against the previous stage's baseline

The feedback loop is short enough that unit tests haven't earned their keep — the entire app is essentially a single Win32 message pump with a handful of pure helper functions, and most of the interesting behavior is in the interaction between GDI / user32 / DWM / the IME stack, which mocking can't simulate usefully.
