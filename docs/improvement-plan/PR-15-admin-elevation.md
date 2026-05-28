# PR-15: admin_elevation — 단일 옵션 + 두 메커니즘 분담 (admin 콘솔 회귀 fix)

**Status**: 📝 draft (planner 산출 — 구현 시작 전 사용자 승인 필요)
**Size**: L (반나절+, ~600 LOC + Tier-3 6종 smoke)
**Risk**: High (런타임 self-elevation 은 OS 상호작용이 크고 회귀 표면 다중)
**Depends on**: PR-03 (asInvoker), PR-04 (StartupTaskManager 분리), PR-09 (Logger pre-Init 버퍼)
**Blocks**: 없음 (independent enhancement)

## 1. 문제

v0.9.3.0 PR-03 의 `app.manifest` `requireAdministrator` → `asInvoker` BREAKING 전환 부작용으로 admin 권한 콘솔 (관리자 cmd, 관리자 WT) 의 한/영 IME 상태 감지가 회귀했다. 메커니즘은 **UIPI** (User Interface Privilege Isolation) — Medium IL (asInvoker) KoEnVue 가 High IL admin 콘솔의 IME 윈도우에 `WM_IME_CONTROL` 메시지를 보내면 OS 가 차단해 `SendMessageTimeoutW(SMTO_ABORTIFHUNG)` 가 즉시 ABORT 한다. 진단 상세 [docs/dev-notes/2026-05-27-cursor-indicator.md L338-L378](../dev-notes/2026-05-27-cursor-indicator.md). 본 PR 는 매니페스트 `asInvoker` (P5 invariant) 를 유지하면서 사용자 선택적으로 admin 권한 실행을 가능케 한다.

## 2. 설계 — 단일 옵션 + 두 메커니즘 분담

**단일 config 키**: `admin_elevation: bool` (default `false`). UI 는 단일 체크박스 **"관리자 권한으로 실행"**.

### 메커니즘 분담

| 메커니즘 | 커버 경로 | UAC 빈도 |
|---------|---------|---------|
| 자체 elevation (self-check) | 단일 실행 / 직접 실행 / 임시 실행 | UAC 1회 (사용자 클릭 시점) |
| schtasks `/RL HIGHEST` 재등록 | 부팅 자동 시작 | UAC 1회 (등록 시점) + 부팅마다 0 |

### 흐름

```
Main() → MainImpl() →
  [0] LogProvider.Sink 배선
  [0a] RegisterCrashHandlers
  [0b] Settings.Load(min) — admin_elevation 만 읽기 위한 lightweight 로드 (또는 풀 Load 후 첫 체크)
  [0c] AdminElevation.TryRelaunchAsAdmin(config):
        if !config.AdminElevation: return Continue
        if AlreadyElevated() (High IL): return Continue (이미 admin — schtasks 경로)
        if EnvSet("KOENVUE_ELEVATED"): return Continue (재진입 방지 — UAC 거부 후 재시작 차단)
        결과 = ShellExecuteW(IntPtr.Zero, "runas", currentExe, null, null, SW_SHOW)
        if 결과 ok: _mutex?.Dispose(); return Exit (원본 종료, 자식이 새 mutex 획득)
        if 결과 == ERROR_CANCELLED (1223): MessageBoxW(...) 후 Continue (fallback c — 일반 진행)
        else: Logger.Error + Continue (fallback c)
  [1] TryAcquireMutex(...) — self-elevation 미적용/거부 후 도달
  ...
```

`AdminElevation.TryRelaunchAsAdmin` 은 **Mutex 획득 전** 에 호출 — 원본이 mutex 안 잡은 상태에서 자식이 한 번에 획득해 race 없음. 단, 부팅 자동 시작 (이미 schtasks `/RL HIGHEST` 로 admin) 은 `AlreadyElevated()` 가 true → 즉시 Continue → mutex 정상 획득.

### schtasks 보정

`StartupTaskManager.BuildStartupTaskXml` 가 `config.AdminElevation` 에 따라 `<RunLevel>` 분기:

```xml
<RunLevel>HighestAvailable</RunLevel>   <!-- admin_elevation = true 시 -->
<!-- vs -->
<RunLevel>LeastPrivilege</RunLevel>     <!-- admin_elevation = false 시 (기존 디폴트) -->
```

`SyncStartupPathAsync` 의 `runLevelMatches` 비교가 `config.AdminElevation` 에서 derive 한 expected runLevel 과 등록값을 비교 → 마이그레이션. **자동 갱신은 사용자 토글 시점에만** 발생 (트레이 메뉴 / Settings 다이얼로그 클릭) — 5초 mtime poll 의 `SyncStartupPathAsync` 는 등록 안 된 상태면 noop 이므로 새 admin 토글이 등록을 트리거하려면 사용자가 "시작 프로그램 등록" 메뉴를 한 번 누르거나, admin_elevation 토글 시점에 자동 재등록 분기 추가.

## 3. 검토 항목 (1–10)

### 1. Fallback 정책 — UAC 거부 시

**권장**: **(c) 1회 알림 후 일반 진행**.

- (a) 일반 권한 계속 (silent) — 사용자가 "왜 admin 안 됐지?" 디버깅 불가. silent 정책 위반.
- (b) 종료 — admin 콘솔만 안 잡혀도 일반 콘솔/메모장은 정상 작동. KoEnVue 전체 봉쇄는 과도.
- (c) MessageBoxW(`"관리자 권한 거부됨. 일반 권한으로 계속 실행합니다. admin 콘솔의 한/영 표시는 작동하지 않습니다."`, OK, ICONWARN) + 진행.

(c) 의 비용 — `MessageBoxW` 한 줄, modal 차단 약 2초 (사용자 OK 클릭). Logger.Warning 도 동시 기록. UAC 거부는 명시적 사용자 행위 (User Account Control 다이얼로그의 "아니요") 라 1회 알림 정당.

### 2. exe path 이동 감지

**권장**: **사용자 토글 시점 자동 갱신**, 부팅마다 silent 갱신 — 기존 `SyncStartupPathAsync` 패턴 그대로.

`SyncStartupPathAsync` 가 path / delay / runLevel 3축 비교 → 불일치 시 `RegisterStartupTaskWithXml` (기존 로직). admin_elevation 변경도 같은 패턴 — runLevel 만 4번째 변수. UAC 빈도 영향: 자체 elevation 으로 부팅된 인스턴스 (High IL) 가 `schtasks /create` 호출 시 UAC 없이 통과 (admin 토큰 보유). 자체 elevation 거부 후 fallback 진행한 Medium IL 인스턴스는 schtasks `/RL HIGHEST` 등록 시 UAC 또 뜸 — 다만 거부 후 fallback 진행한 사용자가 자동 시작 토글 누를 확률 낮음.

### 3. schtasks 등록 실패 graceful

기존 `RunSchtasks` 의 ExitCode + STDERR 로깅 (PR-03 fix) 패턴을 admin_elevation true 케이스에도 적용. silent catch 금지 (KoEnVue 정책).

**시나리오별**:

| 실패 | 진단 | UI 알림 |
|------|-----|--------|
| Group Policy 차단 (`SE_PRIVILEGE_NOT_HELD` 등) | ExitCode + STDERR 로 식별 | MessageBoxW(`"Group Policy 가 작업 등록을 차단했습니다. 시스템 관리자에게 문의하세요."`) |
| Task Scheduler 서비스 비활성 | `Process.Start` `Win32Exception` | MessageBoxW(`"Windows Task Scheduler 서비스가 실행되고 있지 않습니다."`) |
| 권한 부족 (asInvoker + `/RL HIGHEST` 거부) | ExitCode=1 + STDERR "액세스 거부" | MessageBoxW(`"관리자 권한이 필요합니다. '관리자 권한으로 실행' 옵션이 켜져 있는지 확인하세요."`) |
| 디스크 가득 (XML temp file write 실패) | `IOException` | Logger.Error + 메뉴 토글이 자동으로 unchecked 상태로 복귀 |

**구현**: 기존 `RunSchtasks` 의 Warning 한 줄 외에, `ToggleStartupRegistration` 의 호출자에서 stderr 키워드 매칭 (`"denied"` / `"refused"` / `"policy"` 등) → 분기된 MessageBoxW. 정확한 키워드 매칭은 시스템 로케일 의존이라 깔끔하진 않지만, ExitCode 만으로는 시나리오 구분 불가.

### 4. 구현 분담 (실 파일 구조 반영)

dev-note 가 적은 경로의 정정:

| dev-note 표기 | 실제 위치 |
|--------------|----------|
| `App/Autostart/SchtasksHelper.cs` | `App/Startup/StartupTaskManager.cs` (PR-04 분리 결과) |
| `App/Config/AppConfig.cs` | `App/Models/AppConfig.cs` (PR-06 이후) |
| `App/Bootstrap/AdminElevation.cs` | **신규** — `App/Bootstrap/` 폴더 신설 |

**책임 분담**:
- `App/Bootstrap/AdminElevation.cs` (신규, ~150 LOC) — IL 체크 + ShellExecuteW("runas") + 환경 변수 가드 + fallback MessageBox. NuGet 0 (`[LibraryImport]` 만 사용).
- `Core/Native/Advapi32.cs` (신규 또는 기존 확장, ~30 LOC) — `OpenProcessToken` + `GetTokenInformation(TokenIntegrityLevel)` 두 P/Invoke. **Core 에 위치 정당** — IL 체크는 KoEnVue 도메인 무관한 일반 Win32 함수.
- `Core/Native/Win32Types.cs` 에 const 추가 — `TOKEN_QUERY = 0x0008`, `TokenIntegrityLevel = 25`, `SECURITY_MANDATORY_HIGH_RID = 0x3000`, `SECURITY_MANDATORY_SYSTEM_RID = 0x4000`. P3: 매직 넘버 금지.
- `App/Startup/StartupTaskManager.cs` (수정, ~30 LOC 추가) — `BuildStartupTaskXml(string exePath, bool admin)` 시그니처 확장 + `SyncStartupPathAsync` 의 expected runLevel 을 config 에서 derive + 자동 재등록 분기.
- `App/Models/AppConfig.cs` (수정, ~3 LOC 추가) — `public bool AdminElevation { get; init; } = DefaultConfig.AdminElevation;`.
- `App/Config/DefaultConfig.cs` (수정, ~3 LOC 추가) — `public const bool AdminElevation = false;`.
- `App/UI/Tray.Menu.cs` (수정, ~10 LOC 추가) — 메뉴 항목 + 체크 표시.
- `App/UI/Tray.cs` (수정, ~25 LOC 추가) — `IDM_ADMIN_ELEVATION` const + HandleMenuCommand case + admin 변경 시 schtasks 재등록 트리거.
- `App/UI/Dialogs/SettingsDialog.Fields.cs` (수정, ~6 LOC 추가) — Bool 필드 한 행.
- `App/Localization/I18n.cs` (수정, ~10 LOC 추가) — `MenuAdminElevation` + `MenuAdminElevationTooltip` + `AdminElevationDeniedTitle` + `AdminElevationDeniedMessage` 4 키.
- `Program.cs` (수정, ~5 LOC) — MainImpl 의 mutex 획득 전 `AdminElevation.TryRelaunchAsAdmin(_config)` 호출 + 결과 분기.
- `Program.Bootstrap.cs` (수정, ~3 LOC) — TryAcquireMutex 의 self-elevation 시점 mutex Dispose 보조 헬퍼.

**핵심 결정** — `AdminElevation` 클래스가 `App/Bootstrap/` 에 위치한 이유: `Program.cs` 가 진입점이고 elevation 결정은 KoEnVue 정책 (config 키 명, 메시지 키 영어, fallback 정책 UX) 이라 App 도메인. IL 체크 자체는 Core (Advapi32 P/Invoke) 이지만 의사결정은 App.

### 5. UI 위치 (Settings vs TrayMenu)

**권장**: **두 곳 모두**.

- **TrayMenu** — 시작 프로그램 등록 토글 바로 위 또는 아래. UAC 1회 가까운 즉시성 시각화. 사용자가 admin 콘솔에서 한/영 안 잡힐 때 트레이로 손이 가는 패턴.
- **Settings 다이얼로그** — "시스템" 섹션에 Bool 필드 추가. 다른 admin/시스템 옵션과 함께 그룹화.

기존 패턴 — `IDM_STARTUP` (시작 프로그램 등록) 은 트레이만, `LogLevel` 은 Settings 만. admin_elevation 은 두 곳 모두 — 트레이는 단축 + Settings 는 다른 시스템 옵션 옆.

**상태 변경 시 즉시 적용 vs 다음 실행 반영**:
- 자체 elevation 부분 — **다음 실행 반영** (이미 Medium IL 인 인스턴스를 런타임에 High 로 바꿀 수 없음). Tray 메뉴 토글 시 MessageBoxW(`"다음 실행부터 적용됩니다. 지금 재시작하시겠습니까?"` Yes/No) — Yes 면 `PostQuitMessage(0)` + `ShellExecuteW("open", currentExe)` (UAC 없는 normal 재실행 — 그 후 새 인스턴스의 self-check 가 UAC 띄움).
- schtasks 부분 — **즉시 적용** (config 저장 + `SyncStartupPathAsync` 호출). 다음 부팅부터 새 RunLevel 사용.

### 6. 재진입 방지

**권장**: **환경 변수 `KOENVUE_ELEVATED=1`**.

- `ShellExecuteW("runas", exe)` 호출 직전 `Environment.SetEnvironmentVariable("KOENVUE_ELEVATED", "1")` — 자식 프로세스가 이 변수를 상속.
- 자식의 `TryRelaunchAsAdmin` 진입 첫 줄에서 `if Environment.GetEnvironmentVariable("KOENVUE_ELEVATED") == "1": return Continue` — UAC 가 한 번 통과해 High IL 이거나, UAC 거부 후 사용자가 또 똑같이 부른 경우 모두 재시도 차단.
- argv 플래그 (`--elevated`) 대비 이점: 사용자가 KoEnVue.exe 를 cmd 에서 `--debug` 등 다른 플래그와 호출해도 영향 없음. 환경 변수는 process tree 한정.
- 단일 인스턴스 mutex 와 독립 (mutex 는 동시 실행 방지, 환경 변수는 elevation 재시도 방지 — 서로 다른 차원).

**ShellExecuteW 의 lpDirectory + 환경 변수 상속**: `ShellExecuteEx` 의 `SHELLEXECUTEINFOW.lpVerb="runas"` 는 표준 `CreateProcessAsUser` 경로를 거쳐 부모의 환경 변수를 상속. 명시 검증: `Process.Start(ProcessStartInfo { Verb="runas", Environment={"KOENVUE_ELEVATED","1"} })` 변형도 가능 — 단 `ShellExecuteW` 호출이 `Process.Start("runas")` 보다 직접적이라 KoEnVue 의 [Core/Shell/UriLauncher](../../Core/Shell/UriLauncher.cs) 패턴 일관성 유지. 환경 변수 set 은 `Environment.SetEnvironmentVariable` 한 줄로 충분 (ShellExecuteW 호출 직전, in-process global 이 자식에 자동 상속).

### 7. 단일 인스턴스 정책과의 상호작용

기존 mutex 위치: `Program.Bootstrap.cs:23 _mutex` + `TryAcquireMutex` (line 34) — `Mutex(true, DefaultConfig.MutexName, out createdNew)`. `OnProcessExit` 에서 `Dispose`.

**상호작용 시나리오**:

```
원본 인스턴스 (Medium IL) Main()
  → MainImpl()
  → AdminElevation.TryRelaunchAsAdmin(config) = ShellRun + Exit
     (mutex 아직 획득 안 함 — TryAcquireMutex 호출 전)
     ShellExecuteW("runas", exe) 동기 호출 → 자식 프로세스 spawn
     (UAC 다이얼로그 표시 + 자식 부팅 시작)
  → return → Main 종료 (mutex 해제할 게 없음 — 깨끗)

자식 인스턴스 (High IL) Main()
  → MainImpl()
  → AdminElevation.TryRelaunchAsAdmin(config):
        AlreadyElevated() = true → return Continue
  → TryAcquireMutex() — 원본이 mutex 잡은 적 없으므로 createdNew=true
```

**핵심**: `TryRelaunchAsAdmin` 을 **mutex 획득 전** 에 호출하면 원본 인스턴스에 mutex 없음 → 자식이 깨끗하게 새로 획득. dev-note 의 `_mutex?.Dispose();` 권장 — 이 경우 mutex 획득 후 elevation 결정한 경우 대비 안전망이지만, **현 권장 흐름에선 불필요** (mutex 획득 전 호출). 다만 코드 안전을 위해 mutex Dispose 가드는 유지.

**UAC 거부 시 race**: ShellExecuteW("runas") 가 UAC 거부 (1223) 반환 → 원본은 fallback (c) 로 진행 → mutex 획득. 자식은 spawn 안 됨 (UAC 거부 시 자식 프로세스 생성 자체가 일어나지 않음). race 없음.

### 7.1 트레이 토글 재시작 경로 race 차단 (PR-15 후속 fix, 2026-05-28)

위 §7 의 "원본이 mutex 안 잡은 상태" 가정은 **부팅 시점 self-elevation 한정** 으로 valid. 하지만 **트레이 메뉴 토글 재시작 경로** (`Tray.HandleMenuCommand` 의 `IDM_ADMIN_ELEVATION` YES 분기) 에서는 원본이 mutex + trayicon GUID + WTS notification + IME hook + log file lock 등 모든 리소스를 이미 보유한 정상 실행 상태에서 자식을 spawn 한다. `OnProcessExit` cleanup 시퀀스 (PR-19 step 0~7) 가 `_mutex?.Dispose()` 까지 수백 ms 소요되어 자식이 그 사이 mutex `createdNew=false` + 부가 리소스 race 에 빠진다.

진단 시계열 (사용자 보고 → `koenvue.log` + `koenvue_crash.txt` 매핑):

```
T₀     원본 (admin 권한) Tray YES 분기 → ShellExecuteW("open", exe) 자식 spawn
T₀+   자식 부팅 → Mutex(true, MutexName, out createdNew) 시도
       → createdNew=false (원본이 아직 mutex 보유 중)
       → NotifyExistingInstance(원본) → return
       → Logger.Initialize 도달 전 종료 (koenvue.log starting 라인 없음 — crash.txt 만)
T₀+989ms 원본 stopped (cleanup 완료)
사용자: 재시작 안 됨 → 수동 재실행
```

**fix (Option B — 부모 PID + WaitForExit)**:

```csharp
// App/Bootstrap/AdminElevation.cs
private const string RelaunchParentPidEnvVarName = "KOENVUE_RELAUNCH_PARENT_PID";
private const int RelaunchParentWaitMs = 5000;

internal static void SetRelaunchParentPidForTrayRestart()
{
    Environment.SetEnvironmentVariable(
        RelaunchParentPidEnvVarName,
        Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
}

internal static void WaitForRelaunchParentIfAny()
{
    string? pidStr = Environment.GetEnvironmentVariable(RelaunchParentPidEnvVarName);
    if (string.IsNullOrEmpty(pidStr)) return;
    Environment.SetEnvironmentVariable(RelaunchParentPidEnvVarName, null);  // consume
    if (!int.TryParse(pidStr, ..., out int parentPid)) return;
    try
    {
        using var parent = Process.GetProcessById(parentPid);
        parent.WaitForExit(RelaunchParentWaitMs);
    }
    catch (Exception ex) when (ex is ArgumentException
        or InvalidOperationException
        or Win32Exception
        or SystemException)
    {
        // 부모가 이미 종료 / 접근 거부 / detached — "wait 불가, 진행"
    }
}
```

배선:

| 호출 위치 | 호출 메서드 | 의도 |
|----------|------------|------|
| [`App/UI/Tray.cs`](../../App/UI/Tray.cs) IDM_ADMIN_ELEVATION YES 분기 (`ClearReentryGuard` 직후, `UriLauncher.Open` 직전) | `SetRelaunchParentPidForTrayRestart()` | 자식 spawn 직전 본 인스턴스 PID 를 환경변수 set — `ShellExecuteW` 가 자식에 자동 상속 |
| [`Program.cs`](../../Program.cs) `MainImpl` 의 step 0b-1 (Settings.Load 직후, TryRelaunchAsAdmin 직전) | `WaitForRelaunchParentIfAny()` | 자식이 mutex 시도 전 부모 종료 wait (5초 timeout) |
| [`App/Bootstrap/AdminElevation.cs`](../../App/Bootstrap/AdminElevation.cs) `TryRelaunchAsAdmin` 의 ShellExecuteW("runas") 직전 | `SetEnvironmentVariable(RelaunchParentPidEnvVarName, Environment.ProcessId.ToString(...))` | 손자 (High IL) generation 에도 정확한 부모 PID (= 자식 PID) 전달 |

`WaitForRelaunchParentIfAny` 는 환경변수 read 직후 클리어 — 정상 부팅으로 진입한 인스턴스에 stale PID 가 남지 않게. 정상 부팅 (환경변수 없음) 에는 첫 줄 `if (string.IsNullOrEmpty(pidStr)) return;` 로 noop.

**왜 Option B 채택 (vs A/C)**:

| 옵션 | 메커니즘 | 거부 사유 |
|------|---------|----------|
| A | 원본이 spawn 직전 `_mutex?.Dispose()` 명시 호출 | mutex 외 race (trayicon GUID / WTS / IME hook / log file lock) 미해결. cleanup ordering 두 곳 분기 → 회귀 위험 |
| **B** | 자식이 부모 종료를 명시 wait (환경변수 + `WaitForExit(5000)`) | **채택** — 모든 race 근본 차단 (OS 레벨 리소스 ownership 자동 해제). 자식 코드만 변경 |
| C | Mutex Wait + Retry | KoEnVue 의 단일 인스턴스 정책 (createdNew=false → NotifyExistingInstance + 종료) 과 정면 충돌 |

**race 잔존 명시**: 5초 timeout 후 강행 → 부모가 5초 안에 안 죽는 극단 케이스에 race 잔존. 다만 (a) `OnProcessExit` 가 5초 넘는 케이스는 drain thread hang / NIM_DELETE 셸 무한 대기 = OS 강제 종료 시나리오, (b) timeout 5초는 사용자 인내심 (재시작 안 됨 → 수동 재실행) 상한. timeout 도달 시 Log 명시 ("did not exit within 5000ms — proceeding") 후 사용자 진단 가능. **race window 0 에 매우 가까운 차단 — 절대 0 보장은 timeout 무한 대기만 가능**.

PID 재사용 paranoid: 부모 종료 후 OS 가 같은 PID 다른 프로세스에 재할당하면 `Process.GetProcessById` 가 새 프로세스 wait → 최대 5초 후 진행. 자식 spawn ↔ `GetProcessById` 간격이 ms 단위라 재할당 확률 0 에 가깝지만 timeout 이 안전망.

자세한 시계열 + 가설 비교 + 검증 매트릭스: [docs/dev-notes/2026-05-28-pr-15-relaunch-race.md](../dev-notes/2026-05-28-pr-15-relaunch-race.md).

### 8. P 규칙 영향

| 규칙 | 영향 | 대응 |
|------|------|-----|
| P1 (NuGet 0) | 영향 없음 — Advapi32 P/Invoke 만 추가, BCL/Win32 only | — |
| P2 (UI 한국어, 로그 영어) | UI 신규 4 메시지 + 로그 신규 5~10 메시지. I18n.cs 에 4 키 추가, Logger 호출은 영어 fixed string. | I18n + Logger 라인 추가 시 컨벤션 준수 명시 |
| P3 (magic number/string) | `"runas"`, `"/RL HIGHEST"`, `1223` (ERROR_CANCELLED), `0x3000` (HIGH_RID), `25` (TokenIntegrityLevel), `"KOENVUE_ELEVATED"` 등을 모두 const 화. | `AdminElevation` 클래스 안 `private const string RunAsVerb = "runas";` `private const int ErrorCancelled = 1223;` `private const string ElevatedEnvVarName = "KOENVUE_ELEVATED";` `private const string ElevatedEnvVarValue = "1";` Win32Types.cs 에 IL 상수 추가. |
| P4 (단일 구현) | IL 체크가 schtasks 결정 + self-elevation 결정 두 곳에서 필요. **단일 모듈 `AdminElevation.IsElevated()` 헬퍼** 로 통일. | 중복 구현 0 |
| P5 (asInvoker 유지) | app.manifest 변경 없음 — invariant 보존. PR-03 정책 보존 명시. | invariant grep `git grep "requireAdministrator" app.manifest` = 0 유지 |
| P6 (App→Core 단방향) | Advapi32.cs 는 Core (KoEnVue 도메인 무관). AdminElevation.cs 는 App (정책 의사결정). 의존 방향 정상. | invariant grep `git grep "KoEnVue\.App" Core/` = 0 유지 |

### 9. 로그 정책

silent 금지. INFO 이상으로 모든 elevation 결정 + 시도 + 결과 기록. 영문 fixed string (P2).

```
[INFO] AdminElevation: config.AdminElevation=true, current IL=Medium, env KOENVUE_ELEVATED=null — attempting self-elevation
[INFO] AdminElevation: ShellExecuteW(runas) initiated for path={exe}
[INFO] AdminElevation: re-elevation aborted by user (ERROR_CANCELLED), continuing with normal privileges
[ERROR] AdminElevation: ShellExecuteW failed (rc={rc}, error={lastError}), continuing with normal privileges
[INFO] AdminElevation: skipping (already High IL)
[INFO] AdminElevation: skipping (KOENVUE_ELEVATED=1 — re-entry guard)
[INFO] AdminElevation: skipping (config.AdminElevation=false)
[INFO] StartupTask: registering with RunLevel={HighestAvailable|LeastPrivilege} (admin_elevation={true|false})
```

`AppendCrashFile` 패턴 — pre-Init 시점에도 koenvue_crash.txt 에 fallback. Logger pre-Init 버퍼 (PR-09) 가 elevation 로그를 잡아주므로 별도 처리 불필요.

### 10. 테스트 전략

**unit (xUnit / tests/KoEnVue.Tests/)**:
- `AdminElevation.IsEnvFlagSet` — 환경 변수 세팅/해제 후 분기 (Environment.SetEnvironmentVariable 으로 테스트 격리)
- `StartupTaskManager.BuildStartupTaskXml` — 신규 `(exePath, adminElevation)` 시그니처 호출 시 XML 안 `<RunLevel>` 값 검증 (string contains)
- `Settings.Validate` — `AdminElevation` enum 아님 (bool) — Validate 영향 없음 단순 확인

**unit 불가 (manual / integration)**:
- IL 체크 (`OpenProcessToken` + `GetTokenInformation`) — 실 프로세스 토큰 필요. 단, 테스트 프로세스 자체의 IL 을 읽는 sanity check 1 케이스는 가능 (xUnit runner 는 Medium IL 라 Medium 반환 검증).
- `ShellExecuteW("runas")` 자체 — UAC 다이얼로그 띄움. CI 자동화 불가.
- schtasks 실 등록 — CI 의 windows-latest runner 는 service account 라 다름. manual 검증.

**Tier-3 수동 smoke (사용자 검증 매트릭스)**:

| # | 시나리오 | 기대 |
|---|---------|-----|
| A | `admin_elevation: false` (default) + 정상 부팅 | UAC 0, 일반 권한 동작, 기존 PR-03 행동 그대로 |
| B | `admin_elevation: true` + 단일 실행 (직접 클릭) | UAC 1회 → 승인 → 인디 정상 + 관리자 cmd 한/영 표시 정상 |
| C | `admin_elevation: true` + 단일 실행 + UAC 거부 | MessageBox 표시 → OK → 일반 권한 계속 + 관리자 cmd 한/영 미표시 (예상 동작) |
| D | `admin_elevation: true` + 시작 프로그램 등록 후 재부팅 | UAC 0 (schtasks /RL HIGHEST + 자체 elevation 스킵), 관리자 cmd 한/영 정상 |
| E | `admin_elevation: false → true` 토글 + 시작 프로그램 등록 상태 | schtasks 자동 재등록 (RunLevel 갱신), 재부팅 후 UAC 0 + admin 동작 |
| F | `admin_elevation: true → false` 토글 + 시작 프로그램 등록 상태 | schtasks 자동 재등록 (LeastPrivilege 복귀), 재부팅 후 UAC 0 + 일반 동작 (PR-03 디폴트) |

Tier-1 (build) + Tier-2 (invariant grep) 는 모든 PR 공통.

## 4. 파일별 변경

| 파일 | 동작 | 책임 | 라인 추정 |
|------|------|-----|---------|
| `App/Bootstrap/AdminElevation.cs` | **신규** | IL 체크 + ShellExecuteW("runas") + 환경 변수 가드 + fallback MessageBox | ~150 |
| `Core/Native/Advapi32.cs` | **신규** | `OpenProcessToken` + `GetTokenInformation(TokenIntegrityLevel)` P/Invoke | ~50 |
| `Core/Native/Win32Types.cs` | 수정 | IL 관련 const 6종 추가 (`TOKEN_QUERY`, `TokenIntegrityLevel`, `SECURITY_MANDATORY_HIGH_RID` 등) | +15 |
| `App/Models/AppConfig.cs` | 수정 | `AdminElevation : bool` init 디폴트 1줄 | +3 |
| `App/Config/DefaultConfig.cs` | 수정 | `const bool AdminElevation = false;` 1줄 + 주석 | +5 |
| `App/Startup/StartupTaskManager.cs` | 수정 | `BuildStartupTaskXml(exePath, admin)` 시그니처 확장 + `SyncStartupPathAsync` 의 expected runLevel 계산 변경 + `ReregisterIfAdminChanged(newConfig)` 신규 헬퍼 | +50 |
| `App/UI/Tray.cs` | 수정 | `IDM_ADMIN_ELEVATION = 4012` const + `HandleMenuCommand` case + admin 토글 시 schtasks 재등록 호출 + 재시작 안내 MessageBox | +30 |
| `App/UI/Tray.Menu.cs` | 수정 | 메뉴 항목 + 체크 표시 + 시작 프로그램 등록 메뉴 옆 위치 | +10 |
| `App/UI/Dialogs/SettingsDialog.Fields.cs` | 수정 | 시스템 섹션에 `Bool("관리자 권한으로 실행", "Run as administrator", ...)` 1 행 | +6 |
| `App/Localization/I18n.cs` | 수정 | `I18nKey` enum 에 `MenuAdminElevation` / `AdminElevationDeniedTitle` / `AdminElevationDeniedMessage` / `AdminElevationRestartPrompt` 4 추가 + `_table` 4 행 + public surface 4 줄 | +25 |
| `app.manifest` | **변경 없음** | P5 invariant — asInvoker 유지. 주석에 admin_elevation 옵션 언급 추가만. | +3 (주석) |
| `Program.cs` | 수정 | `MainImpl` 시작부 (Settings.Load 직후, TryAcquireMutex 전) 에 `AdminElevation.TryRelaunchAsAdmin(_config)` 호출 + 결과 분기 (3 분기: Continue / Exit / Error) | +12 |
| `Program.Bootstrap.cs` | 수정 | (선택) self-elevation 시점 mutex Dispose 안전망 — 현 권장 흐름에선 불필요, 코드 명료성 위해 가드 1줄 | +0~3 |
| `CHANGELOG.md` | 수정 | `### 수정` 항목 1 — admin 콘솔 회귀 fix + admin_elevation 옵션 추가 + 두 메커니즘 설명 | +8 |
| `docs/improvement-plan/PR-15-admin-elevation.md` | **신규** | 본 문서 | (this) |
| `docs/improvement-plan/INDEX.md` | 수정 | PR-15 행 추가 + Sessions log | +5 |
| `docs/architecture.md` | 수정 | App-specific modules 표에 AdminElevation 추가, Core/Native/ 에 Advapi32 추가 | +6 |
| `docs/conventions.md` | 수정 | P5 절에 admin_elevation 옵션 부연 (asInvoker 는 그대로 + 런타임 self-elevation 으로 선택적 admin) + invariant grep 추가 | +8 |
| `docs/config-reference.md` | 수정 | `[시스템]` 섹션에 `admin_elevation` 키 1 행 | +3 |
| `docs/dev-notes/2026-05-27-admin-elevation.md` | **신규** | UIPI 메커니즘 + 두 경로 설계 + 검증 매트릭스 + 회귀 위험 dev-note | ~200 |

**총 라인 추정**: ~600 LOC (코드) + ~250 줄 (문서)

## 5. 테스트 계획

### unit (자동)

```csharp
// tests/KoEnVue.Tests/AdminElevationTests.cs (신규)
[Fact] void IsEnvFlagSet_returns_true_when_KOENVUE_ELEVATED_is_1()
[Fact] void IsEnvFlagSet_returns_false_when_KOENVUE_ELEVATED_is_unset()
[Fact] void IsEnvFlagSet_returns_false_when_KOENVUE_ELEVATED_is_other()
[Fact] void GetCurrentIntegrityLevel_returns_Medium_for_test_runner() // CI 가정 (admin runner 시 skip)

// tests/KoEnVue.Tests/StartupTaskXmlTests.cs (신규)
[Theory] [InlineData(true, "HighestAvailable")] [InlineData(false, "LeastPrivilege")]
void BuildStartupTaskXml_emits_correct_RunLevel(bool admin, string expectedRunLevel)
```

기존 `Settings.Validate` 테스트는 `AdminElevation` 이 bool 이라 변경 불필요.

### integration / manual

§3.10 의 6 시나리오 매트릭스 — 사용자 가시 검증. dev-note 갱신.

### invariant (Tier-2 grep)

```bash
git grep "DllImport"                                    # 0
git grep "KoEnVue\.App"            Core/                # 0
git grep "requireAdministrator"    app.manifest         # 0
git grep "RunLevel.*HighestAvailable" App/              # 1 (StartupTaskManager.BuildStartupTaskXml 분기)
git grep "ShellExecuteW.*runas"    App/Bootstrap/       # 1 (AdminElevation)
git grep "AdminElevation"          App/Models/          # 1 (AppConfig)
git grep "AdminElevation"          App/Config/          # 1 (DefaultConfig)
git grep "GetTokenInformation"     Core/Native/         # 1 (Advapi32)
git grep -E "0x3000|0x4000"        Core/Native/Win32Types.cs   # 2 (HIGH_RID, SYSTEM_RID)
```

## 6. P1–P6 영향 매트릭스

| P | 영향 | 강화/위반 위험 |
|---|------|-------------|
| P1 (NuGet 0) | 영향 없음 | 신규 P/Invoke 만, NuGet 추가 0 |
| P2 (UI ko / log en) | 신규 4 UI 메시지 (I18n.cs ko/en) + 신규 로그 메시지 영문 | 강화 — UI 한국어 디폴트 유지 |
| P3 (magic 금지) | 다수 신규 const (`"runas"`, `1223`, `0x3000`, `"KOENVUE_ELEVATED"` 등) | 강화 — 모든 매직 const 화 |
| P4 (단일 구현) | IL 체크 단일 헬퍼 `AdminElevation.IsElevated()` | 강화 — 중복 0 |
| P5 (asInvoker) | **manifest 무변경** | **invariant 보존**. `requireAdministrator` 로 회귀 금지 명시 |
| P6 (App → Core) | AdminElevation 은 App, Advapi32 는 Core. 단방향 | 영향 없음 — invariant 유지 |

## 7. 배포 영향

### CHANGELOG.md 초안 (`[Unreleased]` 또는 `## [v0.9.4.0]`)

```markdown
### 수정

- admin 권한 콘솔 한/영 표시 회귀 fix (v0.9.3.0 PR-03 부작용). 매니페스트는 `asInvoker` 유지 (P5 정책 보존) — 신규 `admin_elevation: bool` (default `false`) 옵션 추가로 사용자 선택적 admin 권한 실행 가능. 토글 ON 시: (1) 단일 실행은 부팅 시 자체 elevation (UAC 1회), (2) 부팅 자동 시작은 schtasks `/RL HIGHEST` 재등록 (UAC 0). UAC 거부 시 1회 알림 후 일반 권한 계속. 트레이 메뉴 + Settings 다이얼로그 양쪽에서 토글 가능. [docs/improvement-plan/PR-15-admin-elevation.md](docs/improvement-plan/PR-15-admin-elevation.md) + [docs/dev-notes/2026-05-27-admin-elevation.md](docs/dev-notes/2026-05-27-admin-elevation.md) (UIPI 메커니즘 진단 + 분담 설계).
```

### docs 갱신 파일

- `docs/improvement-plan/PR-15-admin-elevation.md` (신규) — 본 PR 명세
- `docs/improvement-plan/INDEX.md` — PR-15 행 + Sessions log
- `docs/architecture.md` — App/Bootstrap 폴더 + AdminElevation 모듈 표 + Advapi32 (Core/Native) 추가
- `docs/conventions.md` — P5 절에 admin_elevation 옵션 부연 + invariant grep 2종 추가
- `docs/config-reference.md` — `[시스템]` 섹션 `admin_elevation` 1 행
- `docs/dev-notes/2026-05-27-admin-elevation.md` (신규) — UIPI 메커니즘 + 분담 설계 + 회귀 위험

### 버전 bump

`KoEnVue.csproj` `<Version>0.9.3.0</Version>` → `0.9.4.0` (사용자 옵션 추가 + 회귀 fix, semantic 으로 minor bump 적절). `Directory.Build.targets` 의 `GenerateVersionConstants` 가 자동 emit.

## 8. 작업 분할 (commit 단위)

각 커밋이 `dotnet build` + `dotnet test` + AOT publish 통과해야 함 (CLAUDE.md 정책).

1. **Commit 1: Core 인프라 추가** — `Core/Native/Advapi32.cs` 신규 (P/Invoke 2종) + `Core/Native/Win32Types.cs` 에 IL 관련 const 6종 추가. 테스트: 빌드 + invariant 4종 통과.
2. **Commit 2: AppConfig + DefaultConfig 키 추가** — `AppConfig.AdminElevation` + `DefaultConfig.AdminElevation` const. 호환성: 기존 config.json 에 키 없으면 init 디폴트 false. 테스트: 빌드 + Settings.Load 정상.
3. **Commit 3: AdminElevation 모듈 신규** — `App/Bootstrap/AdminElevation.cs` 신규. `IsElevated()`, `IsEnvFlagSet()`, `TryRelaunchAsAdmin(config)` 3 public + private 보조. 단독으로 호출자 없어 빌드만 통과. 테스트: 빌드 + `AdminElevationTests.cs` 4 케이스.
4. **Commit 4: Program.cs 배선** — MainImpl 의 mutex 획득 전 `AdminElevation.TryRelaunchAsAdmin(_config)` 호출 + 결과 분기. 환경 변수 set 도 여기. 테스트: 빌드 + Tier-3 시나리오 A (default false, 정상 부팅) 통과 — admin_elevation 미설정 사용자 영향 0 검증.
5. **Commit 5: StartupTaskManager 확장** — `BuildStartupTaskXml(exePath, admin)` 시그니처 확장 + `SyncStartupPathAsync` 의 expected runLevel 변경 + `ReregisterIfAdminChanged(newConfig)` 신규. 테스트: `StartupTaskXmlTests.cs` 2 케이스 + Tier-3 시나리오 D (자동 시작 + admin true).
6. **Commit 6: UI 배선 — Tray + Settings + I18n** — `IDM_ADMIN_ELEVATION` const + HandleMenuCommand case + Tray.Menu 항목 + SettingsDialog Bool 필드 + I18n 4 키. 테스트: 빌드 + Tier-3 시나리오 E/F (토글 후 schtasks 재등록).
7. **Commit 7: 문서 + CHANGELOG + 버전 bump** — INDEX + architecture + conventions + config-reference + dev-notes + CHANGELOG + csproj `<Version>`. 테스트: 빌드 (Version.g.cs 재생성) + 문서 내부 링크 grep.

각 commit 후 즉시 push (CLAUDE.md hook 정책).

## 9. 리스크 + 미해결 질문

### 리스크

1. **UIPI 의 비대칭성** — Medium → High 메시지는 차단되지만 High → Medium 은 자유. admin_elevation=true 인 KoEnVue 가 일반 메모장의 한/영 잡는 건 OK. 검증 매트릭스 4 케이스 (dev-note L344-L348) 보존.
2. **ShellExecuteW("runas") 의 비동기성** — Windows 가 UAC 다이얼로그 표시는 비동기, ShellExecuteW 반환은 다이얼로그 결과 받은 후 동기. 그러나 일부 환경 (UAC 비활성 시) 즉시 spawn. 원본 인스턴스의 `return Exit` 가 자식 부팅 완료 전이라 race 발생 가능 — 자식이 mutex 잡으려 할 때 원본이 아직 안 죽음. **완화**: 원본 인스턴스가 mutex 잡은 적 없으므로 race 영역 0. 자식이 즉시 createdNew=true 획득.
3. **environment 변수 상속 실패 케이스** — ShellExecuteW 의 standard CreateProcess 경로는 환경 변수 자동 상속이지만, 일부 shell extension 이 fresh environment block 으로 변형 가능. **완화**: 환경 변수 set 실패 시 fallback 으로 argv 플래그 `--elevated` 추가 (Belt-and-suspenders). 본 PR 에서는 환경 변수만 사용 — 회귀 발견 시 argv 추가는 별도 PR.
4. **schtasks /xml 의 RunLevel 변경이 즉시 반영 안 되는 경우** — Task Scheduler 가 task 캐시. **완화**: `SyncStartupPathAsync` 의 기존 강제 재등록 패턴 그대로 사용. delete + create 시퀀스 보장.
5. **사용자 혼란** — "관리자 권한으로 실행" 토글 의미가 모호 — 매 부팅 UAC 가 뜬다고 오해할 수 있음. **완화**: I18n 의 `MenuAdminElevationTooltip` 에 정확히 "자동 시작과 단일 실행 모두 적용. 단일 실행 시 UAC 1회, 자동 시작 시 UAC 없음." 명시.

### 미해결 질문 (사용자 승인 필요)

1. **config 키 위치** — `AdminElevation` 을 top-level (현 권장) vs `Advanced.AdminElevation` 중첩. Top-level 권장 이유: UI 노출 빈도 (트레이 + Settings 양쪽) + 사용자 인지도 (security 관련 옵션). Advanced 중첩 대안: `ForceTopmostIntervalMs` 패턴 — 거의 안 만지는 옵션 그룹. **결정 필요**.
2. **자체 elevation 시점** — Mutex 획득 **전** (현 권장) vs **후**. 전 권장 이유: 원본 인스턴스가 mutex 안 잡았으므로 race 0 + Dispose 보일러플레이트 0. 후 대안: 이미 실행 중인 인스턴스가 admin 으로 재시작하는 시나리오 — 트레이 메뉴 "관리자 권한으로 재시작" — 이건 별도 명시적 액션이라 현 PR 스코프 외, 다음 PR 로. **결정 필요**.
3. **Fallback 메시지 톤** — (c) MessageBoxW 의 정확한 메시지 워딩. 한국어/영문 두 버전 모두 사용자 톤 확정 필요. 권장: 친절한 안내 톤 — `"관리자 권한 부여가 취소됐습니다. 일반 권한으로 계속 실행되며, 관리자 권한 콘솔 (관리자 cmd 등) 의 한/영 상태는 표시되지 않습니다. 다음에 적용하려면 트레이 메뉴에서 '관리자 권한으로 실행' 을 다시 켜고 재시작하세요."`. **확정 필요**.
4. **재시작 안내 UX** — Settings 다이얼로그에서 admin_elevation 토글 시 즉시 재시작 권장 다이얼로그를 띄울지, 다음 부팅 시 자동 적용으로 둘지. 트레이 토글은 즉시성 → MessageBox(Yes/No) 추천. Settings 는 OK 후 종료적 행위 → 별도 알림 vs 사일런트. **결정 필요**.
5. **Logger pre-Init 의 elevation 로그 가시성** — `AdminElevation.TryRelaunchAsAdmin` 은 Logger.Initialize 전에 호출됨 (MainImpl §3 직후). PR-09 의 pre-Init 버퍼가 잡아주지만, ShellExecuteW("runas") + return Exit 흐름에선 Logger.Initialize 가 아예 안 됨 → 버퍼가 flush 안 됨 → 로그 손실. **완화**: AdminElevation 안에서 `AppendCrashFile(...)` 패턴 (Program.cs 의 헬퍼 직접 호출 또는 동등 코드 인라인) 으로 koenvue_crash.txt 에 INFO 라인 기록 — 단 crash.txt 는 본래 크래시용. **별도 elevation.txt** 만들지 crash.txt 재사용할지 결정 필요. 권장: crash.txt 재사용 + 태그 `[INFO]` 분리.

---

## 결론

본 PR 는 P5 invariant 보존 + 사용자 선택적 admin 권한 + 두 실행 경로 (단일/부팅) 모두 커버 + portable UX 손상 0 (default OFF) 를 단일 옵션으로 달성. 7-commit 분할로 각 단계 빌드 통과 보장. Tier-3 6-시나리오 매트릭스로 사용자 가시 검증.

**다음 단계**: 사용자 승인 (특히 §9 미해결 질문 5종) → Commit 1 부터 구현 → Reviewer 서브에이전트 호출 (invariant + UIPI 회귀 매트릭스 검증).
