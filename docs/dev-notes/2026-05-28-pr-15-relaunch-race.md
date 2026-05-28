# 2026-05-28 — PR-15 후속 fix: 트레이 토글 재시작 mutex race 차단

**관련**: [2026-05-27-admin-elevation.md](2026-05-27-admin-elevation.md) · [improvement-plan/PR-15-admin-elevation.md §7](../improvement-plan/PR-15-admin-elevation.md) · [2026-05-21-mutex-abandoned-handling.md](2026-05-21-mutex-abandoned-handling.md)

**Status**: 코드 + 검증 완료 (0 warn / 0 error AOT publish, 65/65 PASS, reviewer 통과)
**범위**: 3 파일 — `App/Bootstrap/AdminElevation.cs` (+93) / `App/UI/Tray.cs` (+3) / `Program.cs` (+5)
**Binary 영향**: 4,861,440 → 4,864,000 bytes (+2,560 = +2.5 KB)

---

## 1. 사용자 보고

트레이 메뉴 **"관리자 권한으로 실행"** 클릭 → "다음 실행부터 적용됩니다. 지금 재시작하시겠습니까?" 대화상자에서 **"예"** 클릭 → **재시작이 일어나지 않음**. 메뉴 체크 상태는 토글되지만 새 인스턴스가 화면에 나타나지 않는다.

수동으로 다시 실행하면 관리자 권한 체크는 정상 (즉 self-elevation 자체는 작동) — 토글 직후 자동 재시작 경로만 silent fail.

---

## 2. 시계열 분석 — `koenvue.log` + `koenvue_crash.txt`

`publish/` 폴더의 두 파일을 시계열로 정렬하면 race 의 정확한 위치가 드러난다.

### 2.1 첫 번째 토글 — 자식 spawn 실패

| 시각 | 출처 | 이벤트 |
|------|------|--------|
| 20:48:51 (T₀) | koenvue.log | 원본 (admin 권한 부팅 중) — `Shell32.ShellExecuteW` `open` verb 로 자식 spawn 시작 (트레이 메뉴 YES 분기) |
| 20:48:51 (T₀+) | koenvue_crash.txt | 자식 부팅 — `admin_elevation=false skipping` 태그 + **`koenvue.log` 의 starting 라인 없음** |
| 20:48:51.989 | koenvue.log | 원본 stopped (cleanup 완료) |

자식이 부팅했지만 `Logger.Initialize` 가 호출되기 전에 종료 → `koenvue.log` 에 "starting" 라인이 안 찍힘. 즉 `Program.MainImpl` 의 mutex 시도 단계에서 `createdNew=false` → `NotifyExistingInstance(원본)` → return → 정상 흐름의 `Logger.Initialize` 진입 불가.

**원인 후보**:
- 원본의 `OnProcessExit` cleanup 시퀀스가 **자식 부팅 시점에 아직 진행 중**
- 그 안의 `_mutex?.Dispose()` 가 마지막 단계 직전이라 mutex 가 자식의 `Mutex(true, ...)` 호출 시점에 살아있음

### 2.2 사용자 수동 재실행

| 시각 | 이벤트 |
|------|--------|
| 20:48:56 | 사용자 수동 재실행 인스턴스 starting (5초 후) |

원본 사망 + cleanup 완전 종료된 뒤라 mutex 해제됨 → 새 인스턴스 정상 부팅.

### 2.3 두 번째 토글 — 같은 race 재현 + 손자 등장

| 시각 | 이벤트 |
|------|--------|
| 20:49:04 (T₁) | 사용자 또 한 번 메뉴 토글 → 자식 spawn |
| 20:49:04+60ms | 손자 부팅 (자식 안의 self-elevation 이 ShellExecuteW("runas") 호출) |
| 20:49:04.326 | 원본 stopped |

손자가 mutex 시도하는 시점은 자식 spawn + 60ms (UAC 다이얼로그가 즉시 yes 통과한 경우). 원본 stopped 가 그 후. **손자가 race 패배**.

여기서 자식 / 손자 / 원본 셋이 mutex 순서에 끼이는 시나리오가 명확해진다.

---

## 3. 근본 원인

Tray 메뉴 토글 재시작 시 **원본의 mutex 가 `OnProcessExit` 의 마지막 단계까지 살아있다**. cleanup 시퀀스 (PR-19 에서 도입한 step 0~7) 는:

1. 감지 스레드 합류 (`_detectionThread?.Join(500)`)
2. WTS unregister
3. WinEvent unhook
4. NotifyIcon NIM_DELETE
5. DestroyWindow × 3 (overlay / cursor / main)
6. Logger.Shutdown
7. `_mutex?.Dispose()`  ← **여기서야 mutex 해제**

원본이 1~6 을 도는 수백 ms (특히 NIM_DELETE 가 셸 응답 대기로 느림) 동안 자식이 `Mutex(true, DefaultConfig.MutexName, out createdNew)` 호출 → `createdNew=false` → 자식의 `NotifyExistingInstance` 분기 → return → 종료.

원본의 mutex 만의 문제가 아니다:

- **trayicon GUID** — `NIM_ADD` 가 GUID 중복으로 거부됨 (원본의 NIM_DELETE 가 아직 안 끝남)
- **WTS notification** — 원본이 unregister 안 끝났으면 자식의 register 가 silent fail
- **IME hook** — `SetWinEventHook(EVENT_OBJECT_IME_CHANGE)` 가 중복 등록 시 정의되지 않은 동작
- **log file lock** — 원본의 drain thread 가 닫기 전 자식이 같은 파일 열려고 시도 시 sharing violation

mutex 만 fix 해도 다른 race 들이 잠재. 한 번에 차단할 메커니즘 필요.

---

## 4. 가설 — 부모 종료 보장 메커니즘

세 가지 옵션 비교.

### Option A — 원본이 spawn 직전 `_mutex?.Dispose()` 명시 호출

장점: 최소 변경, 자식은 무지(無知).
단점:
- mutex 외의 race (NIM_DELETE / WTS / log file lock) 미해결
- `OnProcessExit` 안 다른 cleanup 도 같이 끌어 올려야 함 → cleanup ordering 두 곳 분기 (정상 종료 / spawn 종료) 로 분리되며 회귀 위험
- spawn 후 곧바로 `WM_CLOSE` 보내지만 OS message loop 가 cleanup 을 실행하는 데 임의 시간 소요 — 결국 자식이 race 위험 잔존

### Option B (채택) — 부모 PID + `Process.WaitForExit`

자식이 mutex 시도 전 부모 종료를 명시 대기. 환경변수 `KOENVUE_RELAUNCH_PARENT_PID=<PID>` + `Process.WaitForExit(5000)`.

장점:
- **모든 race 근본 차단** — 부모 프로세스 객체가 살아있는 한 OS 가 그 프로세스의 핸들/리소스를 유지. 부모가 죽으면 mutex, trayicon GUID, WTS subscription, log file lock 등 **모든 리소스 ownership 이 OS 레벨에서 해제 보장**.
- 자식 코드만 변경 — 부모의 cleanup ordering 은 그대로
- timeout (5초) 안전망 — 부모가 hang 해도 영구 대기 안 함

단점:
- 환경변수 전파 의존 (`ShellExecuteW` 의 환경변수 상속은 PR-15 의 `KOENVUE_ELEVATED` 와 동일 패턴이라 검증됨)
- PID 재사용 paranoid 케이스 (§5 참조)

### Option C — Mutex Wait + Retry

자식이 `Mutex` 획득 실패 시 retry loop. 장점: 다른 spawn 경로에도 일반화. 단점:
- mutex 외 race (trayicon GUID 등) 여전히 미해결
- retry 빈도 / timeout 튜닝 어려움
- KoEnVue 의 단일 인스턴스 정책 (createdNew=false 면 NotifyExistingInstance 후 즉시 종료) 과 정면 충돌 — 본 fix 가 아닌 정책 변경 필요

**채택**: Option B. 정직한 결정 근거 — A 는 부분 해결, C 는 정책 충돌, B 는 모든 race 차단 + 변경면 최소 + PR-15 패턴 (환경변수) 재사용.

---

## 5. 구현

### 5.1 신규 const 2 종

[`App/Bootstrap/AdminElevation.cs`](../../App/Bootstrap/AdminElevation.cs):

```csharp
private const string RelaunchParentPidEnvVarName = "KOENVUE_RELAUNCH_PARENT_PID";
private const int RelaunchParentWaitMs = 5000;
```

P3 (no magic string / number) — env var 이름과 timeout 둘 다 const.

### 5.2 신규 메서드 2 종

`SetRelaunchParentPidForTrayRestart()` — Tray 메뉴 YES 분기에서 자식 spawn (`UriLauncher.Open`) **직전** 에 호출. 현재 PID 를 환경변수에 set.

`WaitForRelaunchParentIfAny()` — `Program.MainImpl` 의 step 0b (`Settings.Load`) 직후 호출. 환경변수에 부모 PID 있으면 `Process.GetProcessById(parentPid).WaitForExit(5000)`. 정상 부팅 (환경변수 없음) 에는 noop.

핵심: `WaitForRelaunchParentIfAny` 가 환경변수를 읽은 직후 **클리어** 한다. 정상 부팅으로 진입한 인스턴스에 stale PID 가 남지 않게. ShellExecuteW("runas") 분기는 `TryRelaunchAsAdmin` 안에서 별도로 재설정하므로 손자 generation 에도 정확한 부모 PID (= 자식 PID) 전달.

### 5.3 self-elevation 경로 (손자 spawn) 보강

기존 `TryRelaunchAsAdmin` 의 ShellExecuteW 직전:

```csharp
Environment.SetEnvironmentVariable(ElevatedEnvVarName, ElevatedEnvVarValue);
Environment.SetEnvironmentVariable(
    RelaunchParentPidEnvVarName,
    Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
```

자식 (Medium IL, "본 인스턴스") 이 손자 (High IL) spawn 시 본 인스턴스의 PID 를 전달. 본 인스턴스는 `ExitForChild` 분기로 곧 종료 (mutex 안 잡음) — 그래도 손자가 본 인스턴스 종료까지 wait 하는 게 일관성 + 안전망.

### 5.4 catch narrowing 4 타입

reviewer 권장 수렴:

```csharp
catch (Exception ex) when (ex is ArgumentException
    or InvalidOperationException
    or Win32Exception
    or SystemException)
```

- `ArgumentException` — `Process.GetProcessById` 의 invalid PID
- `InvalidOperationException` — 부모가 이미 종료된 직후 (`Process` 객체 detached)
- `Win32Exception` — `WaitForExit(int)` 의 핸들 접근 거부 (IL 비대칭 시나리오)
- `SystemException` — `Process` 의 기타 OS-level 오류 (broad safety net)

모든 케이스 동일 처리 — "wait 불가, 진행" + Log. 부모가 이미 죽었으면 진행이 정답.

### 5.5 PID 재사용 paranoid 케이스

부모가 종료된 후 OS 가 같은 PID 를 다른 프로세스에 재할당 → `Process.GetProcessById(parentPid)` 가 그 새 프로세스 핸들을 잡고 wait. 자식 spawn ↔ `GetProcessById` 사이 ms 단위 (60ms 측정) 라 재할당 확률 0 에 가깝지만, 그래도 일어나면 최대 5초 대기 후 강행 — 영구 hang 회피. **timeout 자체가 paranoid 케이스의 안전망**.

---

## 6. 시퀀스 다이어그램 — fix 적용 후

### 6.1 트레이 토글 재시작 경로

```
원본 (admin 권한) Tray 메뉴 IDM_ADMIN_ELEVATION YES 분기:
  1. AdminElevation.ClearReentryGuard()
  2. AdminElevation.SetRelaunchParentPidForTrayRestart()  ← NEW
       Environment.SetEnvironmentVariable("KOENVUE_RELAUNCH_PARENT_PID", "<원본 PID>")
  3. UriLauncher.Open(exePath)
       ShellExecuteW("open", currentExe) → 자식 spawn (환경변수 자동 상속)
  4. PostMessageW(_hwndMain, WM_CLOSE, ...)
       → 메시지 큐에 들어가서 OnProcessExit cleanup 시작 (수백 ms)

자식 (admin or normal) MainImpl:
  0. LogProvider.Sink 배선
  0a. RegisterCrashHandlers
  0b. _config = Settings.Load()
  0b-1. AdminElevation.WaitForRelaunchParentIfAny()  ← NEW
        환경변수 read → 원본 PID = N → 환경변수 클리어
        try { Process.GetProcessById(N).WaitForExit(5000) }
        catch (...) { 진행 }
        → 원본의 OnProcessExit 가 _mutex.Dispose() 까지 끝낸 후 자식 진행
  0c. AdminElevation.TryRelaunchAsAdmin(_config)
       config.AdminElevation=true & 자식이 Medium IL → ShellExecuteW("runas")
       (env var 다시 set + 부모 PID = 자식 PID 환경변수 set)
       return ExitForChild → Main 종료
  
  자식이 ExitForChild 였으면:
  손자 (High IL) MainImpl:
    0b-1. WaitForRelaunchParentIfAny()
          자식이 mutex 안 잡았지만 일관성 + 안전망으로 wait
          (자식의 ExitForChild 후 즉시 종료 → 거의 ms 단위로 통과)
    1. TryAcquireMutex() — createdNew=true ✓
    ... 정상 부팅 진행
```

### 6.2 정상 부팅 경로 (변화 없음)

```
일반 사용자가 koenvue.exe 클릭:
  0b. Settings.Load
  0b-1. WaitForRelaunchParentIfAny() — KOENVUE_RELAUNCH_PARENT_PID 미설정 → noop ✓
  0c. TryRelaunchAsAdmin — config 분기
  1. mutex 정상 획득
```

**noop 보장** = 정상 부팅에는 함수 진입 후 첫 줄 `if (string.IsNullOrEmpty(pidStr)) return;` 으로 즉시 return. cost 0.

---

## 7. 검증

### 7.1 빌드

```
dotnet build           → 0 warn / 0 error
dotnet publish -r win-x64 -c Release   → 0 warn / 0 error, 4,864,000 bytes
dotnet test            → 65/65 PASS
```

### 7.2 시나리오 매트릭스

| # | 시나리오 | 기대 | 결과 |
|---|---------|------|------|
| 1 | 정상 부팅 (env var 없음) | `WaitForRelaunchParentIfAny` noop | 코드 검토 + log 추적 |
| 2 | 트레이 토글 YES (admin=true → 같은 admin=true 재시작) | 자식이 원본 종료까지 wait → mutex 정상 | 시계열 trace 진행 가능 |
| 3 | 트레이 토글 YES + self-elevation (Medium → High) | 자식 → 손자 두 단계 wait 모두 정상 | 환경변수 두 번 set (자식 spawn + 손자 spawn) |
| 4 | 부모 PID 재사용 (paranoid) | timeout 5초 후 진행 | timeout 분기 코드 검토 |
| 5 | 부모가 hang | timeout 5초 후 진행 | 동일 |
| 6 | 환경변수 변형 (invalid PID, e.g. "abc") | `int.TryParse` 실패 → 진행 | catch 분기 검토 |

본 시나리오 매트릭스의 (2), (3) 은 사용자 실제 검증으로 박제 예정.

### 7.3 race 잔존 명시

5초 timeout 후 강행하므로 **부모가 5초 안에 안 죽는 극단 케이스에서 race 잔존**. 다만:

- KoEnVue 의 `OnProcessExit` 가 5초 넘는 경우는 (a) drain thread hang, (b) NIM_DELETE 셸 응답 무한 대기 — 둘 다 OS 가 강제 종료해야 할 상황
- timeout 5초는 사용자 인내심 (재시작 안 됨 → 수동 재실행) 의 상한선 기준
- timeout 도달 시 Log 에 명시 ("did not exit within 5000ms — proceeding") 후 사용자가 진단 가능

본 fix 는 **race 확률 0 에 매우 가까운 차단** — 절대 0 보장이 아닌 이유는 timeout 이 있기 때문.

---

## 8. PR-15 §7 (단일 인스턴스 정책과의 상호작용) 갱신

planner 산출 PR-15 plan §7 의 "원본이 mutex 안 잡은 상태" 가정은 **부팅 시점 self-elevation 한정**:

```
원본 인스턴스 (Medium IL) Main()
  → MainImpl()
  → AdminElevation.TryRelaunchAsAdmin(config) = ShellRun + Exit
     (mutex 아직 획득 안 함 — TryAcquireMutex 호출 전)
```

이 가정은 **PR-15 본 PR 의 부팅 self-elevation 경로** 에서 valid. 하지만 **트레이 토글 재시작 경로** 는 원본이 mutex + 모든 리소스 (trayicon / WTS / IME hook / log file) 를 이미 보유한 정상 실행 상태에서 자식 spawn → 가정 깨짐.

본 fix 의 §7.1 (PR-15 plan 갱신) — 트레이 토글 경로의 race 차단 패턴.

---

## 9. 학습

### 9.1 시계열 분석의 정직한 가치

본 race 는 **단일 로그 라인만 보면 fix 가 안 보인다** — `koenvue.log` 의 "starting" 미존재 + `koenvue_crash.txt` 의 `admin_elevation=false skipping` 만으로는 race 가설에 도달하기 어렵다. 두 파일을 ms 단위 시계열로 정렬 + 원본 종료 시각 / 자식 시도 시각의 60ms gap 측정이 진단의 핵심.

진단 절차 박제: `publish/koenvue.log` + `publish/koenvue_crash.txt` 동시 확인 + tail 부분 시각 매핑.

### 9.2 환경변수 패턴의 일관성

PR-15 의 `KOENVUE_ELEVATED=1` 재진입 가드 + 본 fix 의 `KOENVUE_RELAUNCH_PARENT_PID=<PID>` 대기 신호 — 두 환경변수 모두 **자식 프로세스 라이프사이클 한정** + **`ShellExecuteW` 의 환경변수 상속 의존**. 명명 prefix 일관 (`KOENVUE_*`) + 의미 분리 (가드 vs PID). invariant grep 추가 가치 잠재.

### 9.3 race 차단 메커니즘 선택의 정직한 기준

세 옵션 비교 시 "변경 최소" 가 아닌 **"미해결 race 잔존"** 을 1차 기준으로. Option A 가 변경 최소지만 mutex 외 race 미해결 → 채택 거부. Option B 가 변경 면적 약간 더 크지만 **모든 race 근본 차단** → 채택. 이 우선순위가 KoEnVue 의 silent fail 정책 (catch silent 금지, 회귀 박제 우선) 정신과 정합.

### 9.4 timeout 의 안전망 가치

`Process.WaitForExit(5000)` 의 timeout 은 paranoid PID 재할당 + 부모 hang 두 케이스의 안전망. **race 절대 차단은 timeout 0 (무한 대기) 만 가능** 하지만 그건 사용자가 hang 으로 체감 → 정직하게 timeout + Log 명시. KoEnVue 의 모든 wait 패턴 (drain thread Join 500ms, detection thread Join 500ms 등) 과 동일 철학.

---

## 10. 후속 후보 (선택적)

- (A) `Mutex.Wait` 패턴 일반화 — 다른 spawn 경로 (예: 미래의 트레이 "원격 종료 후 재실행") 도 환경변수 패턴 재사용. 본 PR 범위 외.
- (B) `koenvue_crash.txt` 에 timing 정보 추가 — 부모/자식 시각 매핑을 직접 박제. 진단 정직성 향상.
- (C) invariant grep 추가 — `git grep "KOENVUE_RELAUNCH_PARENT_PID" App/Bootstrap/` = 1 (`AdminElevation` const 단일) + `git grep "KOENVUE_RELAUNCH_PARENT_PID" App/UI/` = 0 (Tray.cs 는 const 가 아닌 메서드 호출). 본 PR 시점에 conventions.md 추가 가치 평가 필요.

---

## 11. 결론

본 fix 는 사용자 보고 (재시작 안 됨) → 시계열 진단 (60ms race window) → 가설 비교 (Option A/B/C) → Option B 채택 (모든 race 근본 차단) → 검증 (build 0 warn / 65 PASS / reviewer 통과) 의 정직한 차단 절차를 박제한다.

핵심 가치: **UX 유지** (트레이 토글 후 자동 재시작이 정상 작동 = PR-15 의 원래 UX 의도) + **race 근본 차단** (mutex / trayicon / WTS / log lock 등 모든 리소스 ownership) + **5초 timeout 안전망** (paranoid 케이스에서도 영구 hang 회피).
