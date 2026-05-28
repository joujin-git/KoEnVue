# Conventions

Enforcement rules, code style policies, and .NET 10 / NativeAOT compatibility notes. Companion to [CLAUDE.md](../CLAUDE.md) — this file is the normative source for "what gets rejected in review".

Related: [architecture.md](architecture.md) (structural rules), [implementation-notes.md](implementation-notes.md) (non-obvious implementation choices).

---

## Hard constraints (P1–P6)

| Rule | Description |
|------|-------------|
| **P1** | Zero external NuGet packages. .NET 10 BCL + Windows API only |
| **P2** | UI text defaults to Korean. Log messages and config keys in English |
| **P3** | No magic numbers → `const`/`enum`/config. No string comparisons → `enum`. config.json 의 3-상태 이상 키는 enum + `[JsonStringEnumMemberName]` (PR-06 D4) |
| **P4** | No duplicate implementations — the same functionality must never be re-implemented in a second location; always reach for the shared module |
| **P5** | `app.manifest` UAC `asInvoker` (PR-03, v0.9.3.0) — **invariant 유지** (런타임/매니페스트 둘 다 admin 전제 금지). exe 폴더가 user-non-writable 인 경우 `%LOCALAPPDATA%\KoEnVue\` 로 config.json/koenvue.log 자동 fallback ([App/Config/PortablePath](../App/Config/PortablePath.cs)). schtasks 시작 등록은 기본 `LeastPrivilege`. **PR-15 (v0.9.4.0)** — 사용자 선택적 `admin_elevation: bool` (default `false`) 옵션 도입: ON 시 (a) 단일 실행은 [App/Bootstrap/AdminElevation](../App/Bootstrap/AdminElevation.cs) 가 `ShellExecuteW("runas")` 로 자기 재실행 (UAC 1회), (b) 부팅 자동 시작은 schtasks `<RunLevel>HighestAvailable</RunLevel>` 분기 (UAC 등록 시 1회, 부팅마다 0). 매니페스트는 `asInvoker` 그대로 — PR-03 의 default UAC 0 정책 보존 (옵션 비활성이 default) |
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
| Win32 dialog metrics + `WNDCLASSEXW` 등록 | [Core/Windowing/Win32DialogHelper](../Core/Windowing/Win32DialogHelper.cs) |
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

#### P4 sub-rule — 수치/색상 디폴트는 `DefaultConfig` 단일 진실원 (PR-05)

`AppConfig` 의 init 디폴트 / `Settings.Validate` 의 `Math.Clamp` 인자 / `SettingsDialog.Fields.cs` 의 `Int/Dbl` min/max 인자 / `ScaleInputDialog.Scale{Min,Max}Value` 의 4-축이 모두 [App/Config/DefaultConfig.cs](../App/Config/DefaultConfig.cs) 의 const 를 참조한다. **한 곳에서 값을 변경하면 자동으로 네 곳이 따라가야 한다** — 리터럴을 손으로 동기화하면 다이얼로그 입력 → Validate 사이에서 silent 보정이 발생할 수 있다.

규약:

- 새 `AppConfig` 수치 필드 디폴트: `public int X { get; init; } = DefaultConfig.X;` 형태로 const 참조 — 인라인 리터럴 금지.
- 새 `Settings.Validate` clamp 범위: `Math.Clamp(value, DefaultConfig.MinX, DefaultConfig.MaxX)` — 0/100 등 리터럴 인자 금지.
- 새 다이얼로그 입력 필드: `Int("...", DefaultConfig.MinX, DefaultConfig.MaxX, getter, setter)` — 다이얼로그 hand-sync 금지.
- 4-색 preset 추가 시 [App/Config/ThemePresets.cs](../App/Config/ThemePresets.cs) 의 `ThemeColors` record + `_presets` Dictionary 항목 추가 — `Theme.X => backed with { HangulBg=..., ..., NonKoreanFg=... }` 의 6-필드 반복 금지.
- `AppConfig.cs` 의 모든 numeric init 디폴트가 `DefaultConfig.*` const 를 참조한다 (PR-17, v0.9.5.0). 동일 값을 두 곳에 두면 `fd4373c` 의 "빌드 디폴트 동기화" 류 작업이 누락 누적을 못 잡는다. 검증: `git grep -nE "\}\s*=\s*-?[0-9]+(\.[0-9]+)?\s*;" App/Models/AppConfig.cs` → 0 매치 (array `];` / enum cast `(X)0` / nested record nullable 디폴트는 의도적으로 제외).

### P6 verification invariants

All must return **0 matches** at the repo root:

```bash
git grep "KoEnVue\.App"      Core/   # P6 namespace gate
git grep "ImeState"          Core/   # Risk 4 enum gate
git grep "NonKoreanImeMode"  Core/   # Risk 4 enum gate
git grep "DllImport"                 # banned, use [LibraryImport]
git grep -E "Hangul|English|NonKorean" Core/   # PR-08 IME 어휘 게이트
git grep "맑은 고딕"          Core/   # PR-08 한국어 폰트 어휘 게이트
git grep "requireAdministrator"     app.manifest   # P5: asInvoker only (manifest invariant)
git grep "RunAsVerb"                App/Bootstrap/ # PR-15: 1+ (AdminElevation 의 verb="runas" const 추출 후 실 호출 site 검증 — PR-15 도입 당시 "ShellExecuteW.*runas" grep 은 doc comment 만 매치하고 ShellExecuteW(..., RunAsVerb, ...) 실 호출은 미매치되는 stale 상태)
git grep "GetTokenInformation"      Core/Native/   # PR-15: 1 (Advapi32 RID 추출)
git grep "RunLevelHighestAvailable" App/Startup/   # PR-15: 1 (StartupTaskManager admin 분기 const)
git grep "RelaunchParentPidEnvVarName" App/Bootstrap/   # PR-15 후속 fix (2026-05-28): 1 (AdminElevation 의 KOENVUE_RELAUNCH_PARENT_PID env var 명 단일 const — 트레이 토글 재시작 race 차단 핵심 패턴, KOENVUE_ELEVATED 와 동일 명명 prefix `KOENVUE_*`)
git grep "Win32Constants.MB_OK"        App/             # PR-15 후속 fix #2 (2026-05-28 admin → 일반 down-grade): 2 (Tray.cs 의 down-grade 안내 + AdminElevation.cs 의 ShowDeniedMessage — `uType: 0` hard-code 정리, `MessageBoxW` 의 단일 OK 버튼 호출 site 통일)
git grep -E "uType:\s*0\b"             App/             # PR-15 후속 fix #2: 0 (이전 AdminElevation.cs 한 곳을 const 화한 후 매직 넘버 0 잔존 없음)
git grep "AdminElevationDowngradeNotice" App/           # PR-15 후속 fix #2: 4 (I18n.cs 3 — enum 항목 + _table 항목 + public surface property + Tray.cs 1 — IDM_ADMIN_ELEVATION 분기 호출) — 메시지 안 '종료' / 'Exit' 단어가 `MenuExit` 라벨과 정확 일치해야 사용자 다음 단계 인지 (manual 검토, 본 PR 시점에는 자동 grep 미도입)
git grep -n "User32.UpdateLayeredWindow" Core/Windowing/LayeredOverlayBase.cs   # PR-18: 0 (LayeredWindowBlit 위임)
git grep -n "User32.UpdateLayeredWindow" Core/Windowing/LayeredCursorBase.cs    # PR-18: 0 (LayeredWindowBlit 위임)
git grep -n "Gdi32.CreateDIBSection"     Core/Windowing/LayeredOverlayBase.cs   # PR-18: 0 (DibSectionFactory 위임)
git grep -n "Gdi32.CreateDIBSection"     Core/Windowing/LayeredCursorBase.cs    # PR-18: 0 (DibSectionFactory 위임)
git grep -nE "private static volatile IntPtr (_hwndMain|_hwndOverlay|_hwndCursorOverlay)" Program.cs   # PR-18 5/5: 3
```

> `RunLevel.*HighestAvailable` 의 기존 0-매치 가드는 PR-15 에서 무효화됨 — `BuildStartupTaskXml` 가 config 분기로 `HighestAvailable` 을 정당하게 emit. 위 4종 grep 이 새로운 invariant (각 1매치). PR-18 의 4 매치 가드는 overlay/cursor 두 엔진이 `UpdateLayeredWindow` / `CreateDIBSection` 을 직접 호출하지 않고 `LayeredWindowBlit` / `DibSectionFactory` helper 에 위임함을 검증 (호출 단일화) — `ApplyPremultipliedAlpha` 는 의미 차이로 의도적 분기 보존이라 동일 가드 미적용.

Additional sub-rule — `App/Config/` must not import `App/Detector/`:

```bash
git grep "using KoEnVue\.App\.Detector" App/Config/                   → 0
```

**Risk 4** (the critical failure mode): letting `ImeState` leak into `Core/` would couple the generic layered-overlay engine to KoEnVue's IME problem domain and break reuse. The `OverlayStyle.LabelText : string` + `MeasureLabels : string[]` primitive boundary is the defense (PR-08 E1 일반화 — 이전 3-tuple `(Hangul, English, NonKorean)` 가 record struct 안에 IME 상태 이름을 박아 두었음). Similarly `OverlayStyle.IsBold : bool` keeps `FontWeight` out, `AnimationConfig.AlwaysMode : bool` keeps `DisplayMode` out, and `OverlayAnimator.SetDimMode(bool)` keeps `NonKoreanImeMode` out. IME 메시지 / WinEvent / HKL 파싱 9 상수는 [`App/Detector/ImeConstants.cs`](../App/Detector/ImeConstants.cs) 에 위치 (PR-08 E2). 다이얼로그 폰트 패밀리는 호출자가 주입 — `Win32DialogHelper.CreateDialogFont(uint dpiY, string fontFamily)` 시그니처에 명시, App 측 [`DefaultConfig.DefaultDialogFontFamily`](../App/Config/DefaultConfig.cs) 가 `"맑은 고딕"` 단일 진실원 (PR-08 E3).

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
- **schtasks catches**: `Win32Exception or InvalidOperationException or PlatformNotSupportedException or FileNotFoundException`. `ToggleStartupRegistration` and `SyncStartupPathCore` additionally include `IOException or UnauthorizedAccessException` because the XML-based registration path writes a temp file (`%TEMP%\koenvue-task-{pid}.xml`) before handing it to `schtasks /xml`.

Logic bugs in pipeline hooks (`Migrate`/`Validate`/`ApplyTheme`) and in path normalization helpers now propagate instead of being absorbed.

### 8. Core ↔ Logger 단방향 추상화 (PR-09)

Core 레이어 코드는 정적 `Logger.X(...)` 를 직접 호출하지 않는다 — 대신 `LogProvider.Sink?.X(...)` 를 사용한다.

- **이유**: Core 모듈을 다른 Windows 데스크톱 프로젝트로 lift-out 할 때, 거기서 채택한 다른 로깅 백엔드(또는 no-op) 와 결합 가능해야 한다. `Logger` 의 drain thread / 파일 로테이션을 전부 끌고 갈 필요가 없도록 인터페이스 한 점에서만 외부 의존을 받는다.
- **배선**: `Program.MainImpl` 의 첫 라인(Mutex 획득 전)에서 `LogProvider.Sink = new LoggerSink();`. `LoggerSink` 는 `Logger.X` 로 passthrough.
- **부트 순서 보장**: `LogProvider.Sink` 가 `Logger.Initialize` 이전에 set 되어도 무방 — `Logger` 내부에 pre-Initialize 큐가 있어 `Settings.Load` 단계(JsonSettingsManager 의 Info/Warning) 로그는 버퍼링됐다가 `Initialize` 직후 일괄 flush 된다. PR-06 Tier-3 ④ 의 "Settings.Load Warning 이 koenvue.log 에 안 남는다" 한계가 본 절차로 해소됨.
- **App 레이어**: App 코드는 `Logger.X` 직접 호출 그대로 OK — Core 만 단방향 추상화 대상.
- **검증**: `git grep "Logger\.\(Debug\|Info\|Warning\|Error\)" Core/` 가 Logger.cs(LoggerSink 정의) 외 0 매치 + `git grep "LogProvider\." Core/` 가 1+ 매치.
- **`LogLevel` STJ 의존**: Core `LogLevel` enum 은 `[JsonStringEnumMemberName]` 도 부착하지 않는 순수 enum. JSON 매핑(`"DEBUG"/"INFO"/"WARNING"/"ERROR"`) 은 App 레이어의 `LogLevelJsonConverter` 가 담당 — `AppConfig.LogLevel` 속성에 `[JsonConverter(typeof(LogLevelJsonConverter))]` 만 부착.

### 9. Debug 레벨 로그의 "failed" 단어 회피

Debug 레벨에서 "failed" 단어는 사용자가 로그 파일을 봤을 때 불필요한 불안을 유발한다 (Debug 레벨까지 켜는 사용자는 보통 진단 중이라 더 민감). 호출 자체는 정상 흐름인데 단발성 idempotent / silent fallthrough / 핫 패스 silent-drop 경로의 의도 노출이 깨진다.

- **대체 표현**: "skipped" / "rejected" / "already X" / "absent" 등 — 동작의 의미를 표현.
- **Warning/Error 레벨은 "failed" 허용**: 사용자가 봐야 할 진짜 실패는 "failed" 의 명확한 신호가 더 가치 있다.
- **검증**: `git grep "Sink?\.Debug.*failed" Core/` 0 매치 (Win32DialogHelper 의 `error=` 포함 Error 레벨 라인은 무관). `App/` 도 같은 정신을 적용 — PR-16 (v0.9.5.0) 에서 잔존 3건 (`SystemFilter` / `PositionCleanupService` / `UpdateChecker`) 정리 후 `git grep "Logger\.Debug.*failed" App/` 도 0 매치 baseline.

### 10. Detection loop catch

`DetectionLoop`의 while 본문은 `catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or COMException or ArgumentException)`으로 래핑된다. 단일 폴링 예외(예: `WindowProcessInfo.GetProcessName` 내 `OpenProcess` 실패, UAC 전환 중 `COMException`)가 감지 스레드 전체를 종료시키지 않도록 보호하며, 다음 폴링 주기에서 정상 재개한다. `Thread.Sleep`은 try 바깥에 위치하여 예외 후에도 폴링 간격이 유지된다. Rule 2의 변형으로, P/Invoke + BCL 호출이 혼합된 긴 본문이므로 4-타입 narrowing 이 정당하며 로직 버그(`NullReferenceException` 등)는 Rule 1 에 따라 propagate 된다.

**지수 백오프 + 중복 로그 스팸 억제 (0.9.2.2 이후)**: 예외가 반복되면 `Thread.Sleep(PollIntervalMs + backoffMs)` 의 `backoffMs` 를 매 실패마다 `DefaultConfig.DetectionBackoffStepMs = 200` 씩 가산(`DetectionBackoffMaxMs = 2000` 상한, `_stopping` 신호 응답성 2초 이내 보장). 동일 예외 메시지가 연속 발생하면 첫 발생만 `Logger.Warning` 으로 기록하고 이후는 `Logger.Debug` 로 강등 — 드문 COM apartment 과도기 상황에서 초당 12건의 Warning 이 누적되어 로그 파일을 오염시키던 시나리오 차단. 성공 tick 이 돌아오면 `backoffMs = 0` 리셋 + "Detection loop recovered after backoff (prev=Nms)" Info 로그 1회.

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

### AOT/Trim/SingleFile 분석기 정책

`KoEnVue.csproj` 에는 `PublishAot=true` 와 함께 분석기 3종이 빌드 시점에 활성화되어 있다:

```xml
<EnableAotAnalyzer>true</EnableAotAnalyzer>
<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
<EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
```

`PublishAot=true` 자체는 ILC publish 시점에 trim/dynamic-code 위반을 최종 검출하지만, 그건 피드백 루프가 길다 (Release publish 가 필요). 분석기 3종은 Roslyn 단계에서 동일 분류의 위반을 `dotnet build` 즉시 노출 — 후속 PR 들이 reflection API(`Type.GetType(string)`, `Assembly.GetType`, `Activator.CreateInstance` 등) 를 새로 추가할 때 회귀를 IDE 의 빨간 줄로 즉시 발견.

정책:

- 새 trim/AOT/single-file 경고는 **같은 PR 안에서 fix** 또는 명시적 `<NoWarn>NNNNN</NoWarn>` + 사유 주석 + DECISIONS.md 항목.
- 묵음 누적 금지. 경고 0개가 baseline.
- `<NoWarn>` 으로 일시 처리한 경고는 `// TODO: PR-NN` 주석으로 정리 PR 을 명시.

검증:

```bash
git grep "EnableAotAnalyzer"        KoEnVue.csproj   → 1
git grep "EnableTrimAnalyzer"       KoEnVue.csproj   → 1
git grep "EnableSingleFileAnalyzer" KoEnVue.csproj   → 1
dotnet build                                          → 경고 0개
```

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

- **`Win32DialogHelper.RegisterStandardClass(className, &DlgProc, (IntPtr)(COLOR_BTNFACE + 1));`** for class registration — single entry point that always loads `IDC_ARROW` (defends `IDC_APPSTARTING` cursor leak on first-launch hover)
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

[tests/KoEnVue.Tests/](../tests/KoEnVue.Tests/) xUnit project (PR-10, dev-only — release exe 미포함 → P1 예외). `InternalsVisibleTo("KoEnVue.Tests")` 가 KoEnVue.csproj 에 박혀 internal API 접근 가능. 검증 매트릭스:

- **Debug + Release build both clean** (0 warnings, 0 errors). A debug-only build leaves the release exe outdated
- **`dotnet test tests/KoEnVue.Tests/`** — 현재 baseline **65 PASS** (PR-10 40 + PR-20 25). Unit/ 디렉토리 6 파일:
  - **PR-10** (G1): `ColorHelperTests` (Hex ↔ COLORREF 5 메서드) / `DpiHelperTests` (Scale 2 + BASE_DPI) / `SettingsValidateTests` (Validate clamp 12 케이스)
  - **PR-20**: `StartupTaskXmlTests` (schtasks XML 6 PASS — PR-03 D LogonTrigger.UserId + PR-15 RunLevel 분기 + Command escape) / `XmlEntityCodecTests` (XML 1.0 5 entity + 순서 invariant 9 PASS) / `SanitizeLogPathTests` (4 거부 + 4 허용/폴백 10 PASS, NUL 문자 invalid char — `|<>*` 는 .NET 8+ throw 안 할 수 있음)
- **Smoke gate matrix** exercised manually: boot → tray icon appears → indicator follows foreground → IME toggle changes color → drag works → drag with Shift locks axis → drag with snap sticks to edges → CAPS LOCK toggles bars → config hot-reload → corrupted config spam check → update check (both branches: no update / new version) → Start Menu ESC dismissal hides indicator → Search bar ESC dismissal hides indicator
- **`git grep` invariants** (listed above) all return 0
- **Byte-size tracking** against the previous stage's baseline

자동화 가능한 표면 — XML 조립, 문자열 codec, 경로 정규화처럼 입출력이 명확하고 외부 의존성 0 — 은 단위 테스트로 박제 (PR-20 의 19 메서드 매트릭스 = 회귀 차단 baseline). 외부 의존성이 큰 표면 — `LayeredOverlayBase` 의 GDI 핫 패스, `OverlayAnimator` 의 WM_TIMER FSM, IME 스택과의 상호작용, schtasks.exe 실 호출 — 은 여전히 수동 smoke 가 정직한 검증 ([dev-notes/2026-05-28-pr-20-unit-tests.md](dev-notes/2026-05-28-pr-20-unit-tests.md)).
