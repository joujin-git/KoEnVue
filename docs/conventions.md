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
- `TrayQuickOpacityPresets` 배열 디폴트 `[0.95, 0.85, 0.6]` 도 `DefaultConfig` 단일 진실원으로 통일 (감사 High ④, 2026-06-01) — `TrayQuickOpacity1/2/3` const 3개 + `static double[] TrayQuickOpacityPresets => [...]` property. property(`=>`) 라 호출마다 새 배열을 반환해 `static readonly` 배열 공유로 인한 의도치 않은 변형 위험 0. 이전엔 같은 리터럴이 5곳(`AppConfig` init / `Settings.EnsureSubObjects` 폴백 / `SettingsDialog.Fields` getter fallback 3 + `SetPresetAt` 확장 기본값 2)에 흩어져 있었음. getter fallback 은 개별 const(`TrayQuickOpacity1/2/3`) 를, 배열 폴백은 property 를 참조. **위 array `];` 제외 규칙이 이 배열 디폴트를 자동으로는 못 잡으므로** `AppConfig.TrayQuickOpacityPresets` 가 `DefaultConfig.TrayQuickOpacityPresets` 를 참조하는지 수동 확인 필요.
- `Math.Clamp` 류 표현식 인자의 리터럴도 const 화 대상 (감사 High ⑤, 2026-06-01). `Tray.ApplyQuickOpacity` 의 `Math.Clamp(preset * idleRatio, 0.1, 1.0)` → `Math.Clamp(..., DefaultConfig.MinOpacity, DefaultConfig.MaxOpacity)` (SettingsDialog opacity 필드가 이미 쓰던 const 재사용). **invariant grep 한계 메모**: 위 `\}\s*=\s*리터럴\s*;` 가드는 *init 디폴트* 만 잡고 **(a)** `Math.Clamp(expr, 리터럴, 리터럴)` 처럼 식 인자에 박힌 매직 넘버와 **(b)** getter fallback 의 디폴트 리터럴(`GetPresetAt(..., 0.95)`) 은 못 잡는다 — 감사가 ④⑤를 수동 리뷰로 발견한 이유. 새 clamp/fallback 작성 시 리터럴 금지를 코드 리뷰에서 직접 확인.

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
git grep "Win32Constants.MB_OK"        App/             # PR-15 후속 fix #2 (2026-05-28 admin → 일반 down-grade) → fix #2 cleanup 트레일 (2026-05-29): 7 (Tray.cs ×3 — fix #3 의 AdminElevationChangeNotice + fix #2 cleanup 트레일의 ShowPositionError + CleanupPositions empty 분기 / Dialogs/ScaleInputDialog.cs ×2 — invalid input + out of range / Dialogs/SettingsDialog.cs ×1 — 필드 commit 에러 / AdminElevation.cs ×1 — ShowDeniedMessage. fix #2 시점 2건 (Tray.cs 의 down-grade 안내 + AdminElevation.cs) 에 fix #3 의 AdminElevationChangeNotice 1건이 baseline 으로 박혀 6건이 됐고, fix #2 cleanup 트레일이 잔재 매직 넘버 5 spot 을 named argument 로 일괄 교체해 7건으로 통일 — App/UI 의 `MessageBoxW` 단일 OK 버튼 호출 site 100% named `Win32Constants.MB_OK` 패턴)
git grep -E "uType:\s*0\b"             App/             # PR-15 후속 fix #2: 0 (named argument 형태의 매직 넘버 0 잔존 없음)
git grep -nE "User32\.MessageBoxW\([^)]*,\s*0\s*\)" App/  # PR-15 후속 fix #2 cleanup 트레일 (2026-05-29): 0 매치 — positional uType=0 금지 (named argument 강제). 위 `uType:\s*0\b` 가드는 named argument 만 잡기 때문에 positional 형태는 별도 가드 필요
git grep "AdminElevationChangeNotice"  App/           # PR-15 후속 fix #3 (2026-05-29, 4 case 통일): 4 (I18n.cs 3 — enum 항목 + _table 항목 + public surface property + Tray.cs 1 — IDM_ADMIN_ELEVATION 분기 호출). fix #2 의 `AdminElevationDowngradeNotice` + `AdminElevationRestartPrompt` 2 키는 fix #3 에서 통합 제거 (단일 메시지 + MB_OK + 자동 종료 로 4 case 통일)
git grep -E "ClearReentryGuard|SetRelaunchParentPidForTrayRestart" App/   # PR-15 후속 fix #3 (2026-05-29): 0 (트레이 자동 spawn 흐름 폐기로 사용처 0 — 두 메서드 자체 제거). `TryRelaunchAsAdmin` + `WaitForRelaunchParentIfAny` 는 부팅 시점 self-elevation 인프라 (옵션 효력 발생) 로 유지
git grep "IsCurrentProcessElevated()" App/UI/   # PR-15 후속 fix #4 (2026-05-29, 메뉴 체크 OR): 1 (Tray.Menu.cs 의 `bool isAdminEffective = config.AdminElevation || AdminElevation.IsCurrentProcessElevated()` — admin 환경 외부 spawn case 2 의 정직한 시각 노출). fix #2 시점 호출처 (Tray.cs 의 `isDowngrade` 분기) 는 fix #3 에서 폐기 → fix #4 가 같은 partial class 의 다른 파일 (`Tray.Menu.cs`) 에 재추가. `Program.cs` 의 부팅 시점 호출처와 합쳐 총 2 호출처. fix #5 가 `Tray.Menu.cs` 안에서 `isCurrentlyElevated` 변수 분리 (한 우클릭 안에서 1회 호출 + 두 분기 공유 = `isAdminEffective` OR + `isExternalElevation` AND) — `IsCurrentProcessElevated()` 텍스트 매치 카운트는 1 그대로 (변수 분리는 grep 패턴 영향 0)
git grep "MenuAdminElevationExternal" App/   # PR-15 후속 fix #5 (2026-05-29, case 2 라벨 hint): 4 (I18n.cs 3 — I18nKey enum 항목 + _table 항목 + public surface property + Tray.Menu.cs 1 — case 2 분기 호출 `adminElevationLabel = isExternalElevation ? I18n.MenuAdminElevationExternal : I18n.MenuAdminElevation`). 단일 정의 (I18n 3-spot 패턴 정합) + 단일 호출 site (Tray.Menu.cs 의 case 2 라벨 hint 단독 노출). ko/en 영문 mix 라벨 "관리자 권한으로 실행, Config = User" / "Run as administrator, Config = User" — IT 통용어 + 사용자 직접 표현 + 길이 trade-off 균형 정당
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

`Logger.FlushQueue` and `Logger.Initialize` cannot recursively use `Logger.*` for their own file I/O failures, so they avoid `Logger.*`, but narrow to `IOException or UnauthorizedAccessException` so logic bugs in the drain loop / init path crash the drain thread and surface. Both write a single `Trace.WriteLine` breadcrumb (the only safe sink mid-drain) so the debugger has a hint — `Initialize` on init failure, `FlushQueue` on write/rotate failure (감사 Medium ⑥, 2026-06-01). `FlushQueue` no longer stays permanently silent after a failed rotation: it calls `TryReopenWriter()` each cycle to recover (see Logging conventions above).

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

### 11. `[UnmanagedCallersOnly]` 콜백 경계 가드

`WindowSnapHelper.EnumWindowsCallback` (드래그 스냅 후보 수집) 과 `WindowProcessInfo.EnumChildCallback` (UWP 자식 프로세스 해석) 의 본문 전체를 `try { ... return 1; } catch (Exception ex) { LogProvider.Sink?.Debug("...skipped a window: {ex.Message}"); return 1; }` 로 감싼다 (감사 High ②, 2026-06-01).

- **이유 (Rule 2 의 변형)**: 관리 예외가 `[UnmanagedCallersOnly]` 콜백을 빠져나가 unmanaged 열거 함수(`EnumWindows`/`EnumChildWindows`)의 경계를 넘으면 NativeAOT 런타임이 예외를 전파할 수 없어 **프로세스 전체를 종료**시킨다. 한 창(예: 권한 상승된 프로세스의 HWND, 전환 중인 UWP host)이 던진 예외가 전체 열거 + 앱을 죽이는 대신, 그 창만 누락하고 열거를 계속하는 것이 안전한 복구다 — 드래그 스냅·UWP 이름 해석 모두 best-effort 기능. 본문이 P/Invoke + BCL 혼합이라 narrowing 이 불완전(§10 과 동형)하지만, 경계 가드는 "런타임 종료 회피" 가 목적이라 wide catch 가 정당.
- **`return 1`** = Win32 `BOOL TRUE` = 열거 계속. 콜백 시그니처상 `1` 만 정상 흐름과 동일.
- **§9 정합**: "skipped" 단어로 Debug 레벨 "failed" 회피.
- **WndProc 는 미적용 (한정 조건)**: 다이얼로그 / 윈도우 클래스 `WndProc` 콜백은 OS 메시지 펌프가 호출하고 본문이 자체 좁은 분기라 예외 표면이 다르며, 일괄 `try/catch` 는 정상 메시지 처리의 로직 버그까지 삼킨다. 본 가드는 **임의 HWND 를 순회하며 외부 상태에 의존하는 `Enum*` 류 콜백** 에만 한정한다 (전수 가드 아님).

---

## .NET 10 compatibility notes

| Issue | Resolution |
|-------|------------|
| `ImplicitUsings` | `<ImplicitUsings>enable</ImplicitUsings>` in csproj — the default `Microsoft.NET.Sdk` global `using`s are active, but files still list their non-default `using` directives explicitly (consistency with `App/` ↔ `Core/` cross-namespace imports) |
| `Nullable` | Explicit `<Nullable>enable</Nullable>` for nullable reference warnings |
| `SYSLIB1051` | `IntPtr` promoted to error in `[LibraryImport]` source gen under .NET 10 → `<NoWarn>SYSLIB1051</NoWarn>` |
| `uint → nint` cast | Explicit `(nint)` cast required for `uint` constants passed to `IntPtr` params (e.g., `(nint)WS_CAPTION`) |
| `int & uint` mixed ops | `GetWindowLongW` returns `int` but Win32 constants like `WS_CAPTION` are `uint` → CS0034. Use `unchecked((int)...)` |
| STJ record `init` defaults | Source gen drops `init` defaults for properties absent from JSON under `JsonSerializerIsReflectionEnabledByDefault=false`. **Workaround**: `MergeWithDefaults()` (`Core/Config/JsonSettingsManager.cs`) — serializes a freshly constructed default record to JSON, overlays user keys via **recursive `MergeObjects`** (양쪽 객체인 키만 내려가 중첩 부분지정 시 형제 기본값 보존; 배열·dict 통째 교체), deserializes back. 사용자 JSON 은 `JsonDocumentOptions{ Skip comments + AllowTrailingCommas }` 로 파싱해 주석/콤마 든 정상 config 의 "손상" 오판 차단 — 둘 다 P0 fix (2026-06-01), [implementation-notes.md § STJ source-gen init default workaround](implementation-notes.md#stj-source-gen-init-default-workaround) |
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

**콜백 본문은 `try/catch (Exception ex)` 로 경계 보호.** 관리 예외가 `[UnmanagedCallersOnly]` 콜백을 빠져나가 unmanaged 경계(`EnumWindows`/`EnumChildWindows` 등)를 넘으면 NativeAOT 런타임이 프로세스를 종료시킨다. 한 창에서 던진 예외가 전체 열거(나아가 앱)를 죽이지 않도록 본문 전체를 wide `catch` 로 감싸고 `LogProvider.Sink?.Debug("...skipped...")` 로 기록한 뒤 `return 1` (열거 계속) 한다 — 스냅 후보 수집·UWP 이름 해석은 best-effort. 자세한 사유는 [Silent catch policy §11](#11-unmanagedcallersonly-콜백-경계-가드). 적용 site: `WindowSnapHelper.EnumWindowsCallback`, `WindowProcessInfo.EnumChildCallback`.

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
- **Rotation-failure recovery (감사 Medium ⑥, 2026-06-01)**: a rotation (`File.Move` → new `StreamWriter`) that throws `IOException` (e.g. `.old` locked by AV / open handle) used to leave `_fileWriter` permanently null, after which `FlushQueue`'s `if (_fileWriter is null) return;` silently dropped every later log line *plus* the drop summary for the rest of the session, retrying 0 times. The guard is now `if (_fileWriter is null && !TryReopenWriter()) return;` — `TryReopenWriter` (`[MemberNotNullWhen(true, …)]`, append mode, `_filePath`-empty guard, narrowed to `IOException or UnauthorizedAccessException`) re-attempts writer creation **every flush cycle**, so logging self-heals once the transient lock clears. The rotate-failure catch also emits a single `Trace.WriteLine` breadcrumb (drain itself, so `Logger.*` recursion is forbidden — see Silent catch policy Rule 5)
- Initialize with primitives: `Logger.Initialize(enabled, path, maxSizeMb)` (since Stage 3-A). `LogLevel` set separately via `SetLevel`
- Default log path: `Path.Combine(AppContext.BaseDirectory, "koenvue.log")` — next to the exe, matching the portable config policy

---

## Testing

[tests/KoEnVue.Tests/](../tests/KoEnVue.Tests/) xUnit project (PR-10, dev-only — release exe 미포함 → P1 예외). `InternalsVisibleTo("KoEnVue.Tests")` 가 KoEnVue.csproj 에 박혀 internal API 접근 가능. 검증 매트릭스:

- **Debug + Release build both clean** (0 warnings, 0 errors). A debug-only build leaves the release exe outdated
- **`dotnet test tests/KoEnVue.Tests/`** — 현재 baseline **82 PASS** (PR-10 40 + PR-20 25 + config 머지 P0 10 + 감사 ⑩ 3 + PR-22 4). Unit/ 디렉토리 9 파일:
  - **PR-10** (G1): `ColorHelperTests` (Hex ↔ COLORREF 5 메서드) / `DpiHelperTests` (Scale 2 + BASE_DPI) / `SettingsValidateTests` (Validate clamp 12 케이스)
  - **PR-20**: `StartupTaskXmlTests` (schtasks XML 6 PASS — PR-03 D LogonTrigger.UserId + PR-15 RunLevel 분기 + Command escape) / `XmlEntityCodecTests` (XML 1.0 5 entity + 순서 invariant 9 PASS) / `SanitizeLogPathTests` (4 거부 + 4 허용/폴백 10 PASS, NUL 문자 invalid char — `|<>*` 는 .NET 8+ throw 안 할 수 있음)
  - **config 머지 P0** (2026-06-01): `JsonSettingsMergeTests` (`JsonSettingsManager<T>.MergeWithDefaults` 머지 매트릭스 10 PASS) — 중첩 부분지정 시 형제 기본값 보존 4 (`event_triggers`/`advanced`/`default_indicator_position_relative` 부분지정 + full override) + 주석/트레일링 콤마 정상 파싱 2 + 기존 동작 보존 4 (스칼라 교체 / 배열 빈 배열로 축소 / 빈 객체 → 전체 디폴트 / unknown 키 통과). 머지가 "사용자가 명시 안 한 필드는 기본값 유지" 의 단일 진실원이라 회귀를 박제 — `MergeWithDefaults` `private→internal` 노출이 본 항목의 입력(JSON 문자열)·출력(머지 JSON)이 명확하고 외부 의존성 0 인 **순수 표면 단위 테스트** 철학 사례 (GDI/WM_TIMER/IME 스택 의존 표면과 대비)
  - **감사 ⑩** (2026-06-02): `OverlayAnimatorTests` (`OverlayAnimator` slide+highlight 트랙 경합 회피 3 PASS) — `TriggerShow` 에 IME 전환 + 위치 변경이 겹칠 때 slide 를 보류해 두 트랙이 같은 윈도우의 위치/크기를 다투지 않게 한 동작을 박제 (slide 보류 / slide 정상 / highlight 생존). FSM 전체가 아니라 **콜백 spy 로 관측 가능한 좁은 경합 규칙만** 단위로 떼어낸 사례 — `AnimationEnabled=false` 로 Hidden→Holding 동기 전이를 만들고 `onPositionOffset` / `onScaledSize` 콜백 호출 유무로 slide/highlight 활성을 판정 (GDI/실 WM_TIMER 펌프 없이). 나머지 FSM (페이드 보간, hold 만료, idle dim) 은 여전히 수동 smoke 영역 (line 아래 참조)
  - **PR-22** (2026-06-02): `AnimationFacadeTests` (`Animation.BuildAnimationConfig` 마스터 게이팅 4 PASS) — `animation_enabled` 가 fade 뿐 아니라 highlight·slide 까지 끄는 마스터임을 **파사드 합성 단계**에서 박제 (마스터 OFF→`ChangeHighlight`/`SlideAnimation` AND 게이팅 / 마스터 ON→개별 토글 보존 / 마스터 ON+개별 OFF→OFF 유지 / `AnimationEnabled` 통과). 순수 입출력(`AppConfig`→`AnimationConfig` record)이라 GDI/타이머 의존 0 — `MergeWithDefaults` 류 순수 표면 단위 테스트. 감사 ⑩ `OverlayAnimatorTests` 는 Core 를 직접 생성해 파사드 합성을 비경유하므로 본 게이팅은 **파사드 쪽에서만** 박제 가능 (`BuildAnimationConfig` 가시성 `private→internal`)
- **Smoke gate matrix** exercised manually: boot → tray icon appears → indicator follows foreground → IME toggle changes color → drag works → drag with Shift locks axis → drag with snap sticks to edges → CAPS LOCK toggles bars → config hot-reload → corrupted config spam check → update check (both branches: no update / new version) → Start Menu ESC dismissal hides indicator → Search bar ESC dismissal hides indicator
- **`git grep` invariants** (listed above) all return 0
- **Byte-size tracking** against the previous stage's baseline

자동화 가능한 표면 — XML 조립, 문자열 codec, 경로 정규화처럼 입출력이 명확하고 외부 의존성 0 — 은 단위 테스트로 박제 (PR-20 의 19 메서드 매트릭스 = 회귀 차단 baseline). 외부 의존성이 큰 표면 — `LayeredOverlayBase` 의 GDI 핫 패스, `OverlayAnimator` FSM 의 실 렌더/타이머 펌프 의존 부분(페이드 보간·hold 만료·idle dim), IME 스택과의 상호작용, schtasks.exe 실 호출 — 은 여전히 수동 smoke 가 정직한 검증 ([dev-notes/2026-05-28-pr-20-unit-tests.md](dev-notes/2026-05-28-pr-20-unit-tests.md)). 단 감사 ⑩ 의 `OverlayAnimatorTests` 처럼 콜백 spy 로 관측 가능한 좁은 분기 규칙(slide↔highlight 경합 회피)은 GDI 없이 단위로 떼어낼 수 있다 — FSM 이라고 통째로 수동 영역인 건 아니고, 외부 의존이 콜백 경계 밖에 있는 조각만 추려 박제한다.
