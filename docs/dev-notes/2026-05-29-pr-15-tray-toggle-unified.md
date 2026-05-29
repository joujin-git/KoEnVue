# 2026-05-29 — PR-15 후속 fix #3: 트레이 토글 4 case 통일 흐름

**관련**: [2026-05-28-pr-15-admin-downgrade.md](2026-05-28-pr-15-admin-downgrade.md) (직전 fix #2 — admin → 일반 down-grade 분기) · [2026-05-28-pr-15-relaunch-race.md](2026-05-28-pr-15-relaunch-race.md) (fix #1 — 트레이 토글 mutex race) · [2026-05-27-admin-elevation.md](2026-05-27-admin-elevation.md) (PR-15 본 PR 진단) · [improvement-plan/PR-15-admin-elevation.md §7.3](../improvement-plan/PR-15-admin-elevation.md)

**Status**: 코드 + 검증 완료 (0 warn / 0 error AOT publish, 65/65 PASS, reviewer 통과)
**범위**: 3 파일 — `App/Bootstrap/AdminElevation.cs` (-28) / `App/Localization/I18n.cs` (+5/-5) / `App/UI/Tray.cs` (+8/-30)
**Binary 영향**: 4,865,024 → 4,864,512 bytes (-512 bytes; 메서드 2개 제거 + 분기 단순화 ~40 LOC → ~14 LOC 의 IL 감소)
**SHA256**: `d2efa84ae876af701ab890310d728a3e86dc4e3e8167c0e0eed3ee0cc836695c`

---

## 1. 사용자 보고 — 누적 2종

fix #2 (admin → 일반 down-grade 분기 + `MB_OK` 안내 + `break`) 박은 후 사용자 추가 보고 2종:

### 1.1 MB_YESNO mental model 충돌

사용자: "관리자 권한으로 실행" 메뉴 클릭 → "다음 실행부터 적용됩니다. 지금 재시작하시겠습니까?" 대화상자 → **"아니오"** 클릭 → 메뉴 체크 표시 변경됨.

표준 Yes/No 컨벤션의 "아니오" = "취소" 직관과 다르게 **이미 옵션 변경 완료** 상태. 메시지박스 표시 **전** 에 `updateConfig(newAdminConfig)` + `StartupTaskManager.ReregisterIfAdminChanged` 가 disk 반영 (`config.json` + schtasks XML) 완료. 사용자가 "아니오" 누르더라도 "다음 부팅부터는 새 옵션 적용" 이 이미 보장된 상태 — `MB_YESNO` 의 "재시작하시겠습니까?" 가 **재시작만의** Yes/No 의미인데 사용자 직관은 **전체 동작의** Yes/No.

PR-15 design doc §3.4 의 "다음 실행부터 적용됩니다" 메시지가 이 의도를 명시했어도 사용자 직관 (Yes/No = 전체 액션의 결정) 과 충돌. UI 단어 정확성보다 mental model 자체의 충돌.

### 1.2 메인 인디 잔존 회귀

사용자 (admin 권한 인스턴스): "관리자 권한으로 실행" 메뉴 클릭 → fix #2 의 case 4 (admin → 일반 down-grade) 진입 → MB_OK 안내 → 안내 OK → **admin 인디 그대로 잔존**.

원인 — fix #2 의 case 4 흐름의 자연 결과:

```csharp
if (isDowngrade)
{
    User32.MessageBoxW(hwndMain, I18n.AdminElevationDowngradeNotice, "KoEnVue", Win32Constants.MB_OK);
    break;   // ← case IDM_ADMIN_ELEVATION 의 switch break
}
```

`break` 가 `case IDM_ADMIN_ELEVATION:` switch break → `WM_CLOSE` 미발화 → `OnProcessExit` 미진입 → `Overlay.Dispose` 미실행 → 메인 인디 그대로. fix #2 시점에는 "수동 종료/재실행" 안내 메시지가 이 단계를 명시 ('종료' / 'Exit' 단어 일관성으로 보강) 했으나 — 사용자가 안내 OK → admin 인디 그대로 → 트레이 "종료" 추가 클릭 → 새 인스턴스 수동 실행 = 3 단계.

마찰 누적 — 사용자가 "다음 실행부터 적용된다" 의 의미를 정확히 이해하더라도 "지금 인스턴스는 admin 그대로" + "다음 단계는 종료 + 수동 재실행" 의 2 단계가 fix #2 시점에는 안내만으로 처리.

### 1.3 두 보고의 동시성

두 보고가 별개 케이스로 보이지만 본질은 **동일** — fix #2 의 4-case 비대칭 (case 1/2/3 자동 spawn + case 4 안내 + `MB_YESNO`) 이 사용자 mental model 과 정렬 안 됨.

- 1.1 = case 1/2/3 의 `MB_YESNO` 가 mental model 충돌
- 1.2 = case 4 의 MB_OK + break 가 메인 인디 잔존 회귀

---

## 2. 사용자 직접 제안

사용자 메시지 (요약):

> 4 case 모두 단일 메시지 + `MB_OK` 단일 버튼 + 자동 종료 (`PostMessageW(WM_CLOSE)`) 로 통합. 메시지는 "관리자 권한 옵션이 변경되어 KoEnVue를 종료합니다. 관리자 권한 옵션은 다음 실행부터 적용됩니다."

(fix #3 시점 원본 메시지 — fix #4 에서 "관리자 권한 옵션이 변경되어 KoEnVue를 종료합니다. KoEnVue를 다시 실행해 주세요." 로 단순화. "다음 실행부터 적용됩니다" 부분이 종료 → 수동 재실행 흐름에서 redundant — [2026-05-29-pr-15-tray-menu-or-logic.md](2026-05-29-pr-15-tray-menu-or-logic.md) 참고.)

핵심 아이디어:

1. **`MB_YESNO` → `MB_OK`** — Yes/No 컨벤션 충돌 자체 회피. "확인" 단일 버튼 = "안내 읽음 + 종료 동의" 정확 일치.
2. **자동 종료 (`PostMessageW(WM_CLOSE)`)** — 사용자가 OK 클릭 후 `OnProcessExit` 시퀀스 도달 → 메인 인디 잔존 회귀 자동 해결.
3. **자동 spawn 안 함** — 사용자가 수동 재실행 시 새 옵션 적용. Windows token 모델의 admin→일반 down-grade 한계 자연 회피 (사용자 셸 = 일반 권한 → ShellExecuteW 토큰 상속 = 일반 권한).
4. **4 case 통일** — `isDowngrade` 분기 자체가 없어짐. 4 케이스 모두 동일 흐름.

---

## 3. 채택 근거 + 트레이드오프 정직

### 3.1 채택 근거

| # | 근거 | 비고 |
|---|------|------|
| 1 | mental model 단순화 | Yes/No 컨벤션 충돌 회피 — `MB_OK` 단일 버튼 = "확인" 직관과 정확 일치 |
| 2 | Windows token 모델 한계 자연 회피 | admin→일반 down-grade 도 사용자 수동 재실행으로 처리 — 사용자 셸 토큰 상속 |
| 3 | 메인 인디 잔존 회귀 자동 해결 | `WM_CLOSE` → `OnProcessExit` → `Overlay.Dispose` 종료 시퀀스 도달 |
| 4 | 코드 단순화 부수 효과 | `IDM_ADMIN_ELEVATION` 분기 ~40 LOC → ~14 LOC, IL 감소 -512 bytes |

### 3.2 트레이드오프 정직

| 항목 | fix #2 | fix #3 |
|------|--------|--------|
| 일반→admin (가장 흔한 use case) | 자동 spawn + UAC → 즉시 admin 인스턴스 시작 | 사용자가 수동 재실행 1 단계 추가 |
| admin→일반 | MB_OK 안내 + 사용자 수동 종료/재실행 (메인 인디 잔존) | 자동 종료 + 사용자 수동 재실행 (메인 인디 자동 dispose) |
| 일반→일반 / admin→admin (no-op) | YESNO → YES → 자동 spawn (사용성 낮음) | 자동 종료 (no-op 가까운 경우라도 종료 필요) |
| mental model | 4 case 비대칭 | 4 case 통일 |
| Windows token 모델 한계 처리 | case 4 만 분기 | 모든 case 자연 회피 |
| 사용자 UX 단계 (일반→admin) | 1 단계 (메뉴 클릭 → 자동 UAC) | 2 단계 (메뉴 클릭 → 안내 OK + 자동 종료 → 사용자 수동 재실행 → UAC) |

핵심 트레이드오프 = **일반→admin 자동 UAC spawn UX 약간 후퇴** (1 단계 → 2 단계). 사용자가 트레이 토글 후 즉시 admin 인스턴스를 못 받음.

### 3.3 보상 — 분담 명료화

| 메커니즘 | 책임 | 시점 | 비고 |
|---------|------|------|------|
| 트레이 토글 | **옵션 변경** (config 디스크 저장 + schtasks 재등록 + 종료) | 사용자 메뉴 클릭 시 | fix #3 의 4 단계 단일 흐름 |
| 부팅 self-elevation ([`TryRelaunchAsAdmin`](../../App/Bootstrap/AdminElevation.cs)) | **옵션 효력 발생** (`config.AdminElevation=true` + 일반 권한 부모 → UAC 1회 + admin 자식) | 사용자 일반 권한 재실행 시 mutex 획득 전 (step 0c) | PR-15 UIPI 우회 가치 자체 |

둘은 별개 책임 — 사용자가 트레이에서 "옵션 변경" 후 일반 권한 재실행 (탐색기 더블클릭 또는 시작 메뉴) 하면 부팅 self-elevation 이 UAC 1회로 admin 자동 진입. **총 UAC 횟수 1회는 동일**, 단계만 2 → 2 (fix #2 는 메뉴 클릭 → 자동 UAC = 1 단계, fix #3 는 메뉴 클릭 → 안내 OK + 자동 종료 → 수동 재실행 → 자동 UAC = 2 단계).

분담 명료화의 가치:

1. 트레이 토글의 책임이 "옵션 변경" 만으로 좁혀짐 — 분기 단순화 (case 1/2/3/4 동일 흐름).
2. 부팅 self-elevation 의 가치 = "옵션 효력 발생" 이 명확. 트레이 자동 spawn 제거 (fix #3) ≠ `TryRelaunchAsAdmin` 무가치.
3. 사용자가 mental model 을 단순화 — "트레이 = 설정 변경, 부팅 = 효력".

---

## 4. 분담 명료화 — `TryRelaunchAsAdmin` 유지 필수

### 4.1 사용자 질문 — "더이상 필요없지 않아?"

사용자 (fix #3 직전 turn): "TryRelaunchAsAdmin self-elevation 더이상 필요없지 않아?"

자연스러운 추론 — 트레이 자동 spawn 폐기 (fix #3) → `TryRelaunchAsAdmin` 의 호출처 (트레이 YES 분기 + 부팅 시점) 중 트레이 부분이 사라짐 → 부팅 부분만 남음 → 부팅 부분도 필요 없지 않나?

### 4.2 답 — 유지 필수

`TryRelaunchAsAdmin` 의 부팅 호출처 = 사용자가 트레이 토글로 "옵션 변경" 한 후 **수동 재실행 한 새 인스턴스** 의 mutex 획득 전 (step 0c). 일반 권한 부모 (사용자 셸) → 일반 권한 자식 → `TryRelaunchAsAdmin` 호출 → `config.AdminElevation=true` 면 `ShellExecuteW("runas")` 로 UAC 1회 + admin 자동 진입.

즉:

| 부팅 시점 self-elevation 호출처 | 시나리오 | 메커니즘 |
|-------------------------------|---------|---------|
| 사용자 메뉴 클릭 후 수동 재실행 (fix #3) | `config.AdminElevation=true` + 사용자가 일반 권한으로 KoEnVue.exe 실행 | `TryRelaunchAsAdmin` → UAC 1회 → admin 자식 |
| 부팅 자동 시작 — schtasks 트리거 | `config.AdminElevation=true` + schtasks `<RunLevel>HighestAvailable</RunLevel>` | schtasks 가 토큰 부여 → `IsCurrentProcessElevated()=true` → `TryRelaunchAsAdmin` noop (skip) |
| 사용자 직접 실행 — 탐색기 / 시작 메뉴 | `config.AdminElevation=true` + 사용자 셸이 일반 권한 | `TryRelaunchAsAdmin` → UAC 1회 → admin 자식 |
| 사용자 직접 실행 — 관리자 cmd 등 | `config.AdminElevation=true` + 부모 셸이 admin | `IsCurrentProcessElevated()=true` → `TryRelaunchAsAdmin` noop (skip) |

부팅 self-elevation 제거 시 PR-15 UIPI 우회 가치 (관리자 콘솔 한/영 표시) 자체 소멸 — `admin_elevation: true` 옵션이 schtasks 자동 등록 경로에만 의존 → 사용자 직접 실행 시 UAC 다이얼로그 0 → 일반 권한 → 관리자 콘솔 한/영 미표시 회귀.

**결론** — 트레이 토글 자동 spawn (fix #1/#2 시점의 `ClearReentryGuard` + `SetRelaunchParentPidForTrayRestart`) 만 폐기, **부팅 self-elevation (`TryRelaunchAsAdmin` + `WaitForRelaunchParentIfAny`) 은 유지 필수**. 사용자 질문에 대한 정확한 답.

---

## 5. 구현

### 5.1 [`App/UI/Tray.cs`](../../App/UI/Tray.cs) — `IDM_ADMIN_ELEVATION` 분기 단순화

변경 전 (fix #2, ~40 LOC):

```csharp
case IDM_ADMIN_ELEVATION:
{
    AppConfig newAdminConfig = config with { AdminElevation = !config.AdminElevation };
    updateConfig(newAdminConfig);
    StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig);

    // 분기 — admin → 일반 권한 down-grade ...
    bool isDowngrade = !newAdminConfig.AdminElevation
        && AdminElevation.IsCurrentProcessElevated();
    if (isDowngrade)
    {
        User32.MessageBoxW(hwndMain,
            I18n.AdminElevationDowngradeNotice, "KoEnVue",
            Win32Constants.MB_OK);
        break;
    }

    // 결정 #4 (트레이): 즉시 재시작 안내. Yes = 일반 권한 재실행 → ...
    int answer = User32.MessageBoxW(hwndMain,
        I18n.AdminElevationRestartPrompt, "KoEnVue",
        Win32Constants.MB_YESNO);
    if (answer == Win32Constants.IDYES)
    {
        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            AdminElevation.ClearReentryGuard();
            AdminElevation.SetRelaunchParentPidForTrayRestart();
            UriLauncher.Open(exePath);
            User32.PostMessageW(hwndMain, Win32Constants.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
break;
```

변경 후 (fix #3, ~14 LOC):

```csharp
// --- 관리자 권한 토글 (PR-15 후속 fix #3, 2026-05-29: 4 case 통일) ---
// 흐름: config 토글 + schtasks 재등록 + 통일 안내 + WM_CLOSE 자동 종료.
// 자동 spawn 안 함 — Windows token 모델의 admin→일반 down-grade 한계 자연 회피.
// 사용자가 수동 재실행 시 새 옵션 적용 (일반 권한 재실행 + config=true → TryRelaunchAsAdmin
// self-elevation UAC 1회 / config=false → 일반 권한 유지). admin 환경 재실행은 토큰 상속
// (KoEnVue 통제 외) — PR-15 §7.2 의 down-grade 한계 그대로 보존.
case IDM_ADMIN_ELEVATION:
{
    AppConfig newAdminConfig = config with { AdminElevation = !config.AdminElevation };
    updateConfig(newAdminConfig);
    // schtasks 의 RunLevel 즉시 갱신 — 등록 안 됐으면 noop.
    StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig);

    User32.MessageBoxW(hwndMain,
        I18n.AdminElevationChangeNotice, "KoEnVue",
        Win32Constants.MB_OK);

    // "확인" 후 자동 종료 — 메인 인디 잔존 회귀 차단 + 사용자 mental model 정합.
    User32.PostMessageW(hwndMain, Win32Constants.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}
break;
```

부수 효과 — `using KoEnVue.App.Bootstrap;` import 제거 (`AdminElevation.IsCurrentProcessElevated` / `ClearReentryGuard` / `SetRelaunchParentPidForTrayRestart` 호출 모두 폐기로 사용처 0).

### 5.2 [`App/Localization/I18n.cs`](../../App/Localization/I18n.cs) — 키 정리

| 키 | 변경 | 비고 |
|---|------|------|
| `AdminElevationRestartPrompt` | **제거** | `MB_YESNO` 메시지 — fix #3 의 `MB_OK` 통일로 불필요 |
| `AdminElevationDowngradeNotice` | **제거** | fix #2 의 case 4 안내 — fix #3 의 통일 메시지로 흡수 |
| `AdminElevationChangeNotice` | **신규** | `MB_OK` 통일 메시지 |

신규 키:

```csharp
// I18nKey enum
AdminElevationChangeNotice,

// _table
[I18nKey.AdminElevationChangeNotice] = (
    "관리자 권한 옵션이 변경되어 KoEnVue를 종료합니다. 관리자 권한 옵션은 다음 실행부터 적용됩니다.",
    "The admin elevation option has been changed. KoEnVue will now exit; the change will apply from the next launch."),

// public surface
public static string AdminElevationChangeNotice => Get(I18nKey.AdminElevationChangeNotice);
```

(fix #3 시점 원본 — fix #4 에서 단순화: ko "관리자 권한 옵션이 변경되어 KoEnVue를 종료합니다. KoEnVue를 다시 실행해 주세요." / en "The admin elevation option has been changed. KoEnVue will now exit. Please launch KoEnVue again." [2026-05-29-pr-15-tray-menu-or-logic.md](2026-05-29-pr-15-tray-menu-or-logic.md) 참고.)

XML doc comment 에 fix #3 의 4 case 통일 흐름 + admin→일반 down-grade 한계의 사용자 수동 재실행 자연 회피 명시.

### 5.3 [`App/Bootstrap/AdminElevation.cs`](../../App/Bootstrap/AdminElevation.cs) — 사용처 0 메서드 제거

| 메서드 | 변경 | 사유 |
|---|------|------|
| `ClearReentryGuard()` | **제거** | fix #2 의 트레이 YES 분기 직전 호출 site 였으나 fix #3 의 자동 spawn 폐기로 사용처 0 |
| `SetRelaunchParentPidForTrayRestart()` | **제거** | 동일 (fix #1 의 race 차단 패턴이 트레이 자동 spawn 흐름 한정 → fix #3 에서 흐름 자체 제거) |

**유지 (인프라 가치)**:

| 메서드 / 상수 | 비고 |
|------|------|
| `TryRelaunchAsAdmin(config)` | 부팅 시점 self-elevation (옵션 효력 발생) — 본 dev-note §4 참고 |
| `WaitForRelaunchParentIfAny()` | `TryRelaunchAsAdmin` 의 손자 generation 부모 wait (UAC 다이얼로그 통과 후 손자가 자식 종료를 명시 wait — race 차단) |
| `IsCurrentProcessElevated()` | `Program.cs` (부팅 시점 분기) 의 IL 체크 |
| `IsReentryGuardSet()` + `Result` enum | `TryRelaunchAsAdmin` 의 결정 분기 |
| `KOENVUE_ELEVATED` 환경변수 + `ElevatedEnvVarName` const | 재진입 가드 (UAC 거부 후 무한 루프 차단) |
| `KOENVUE_RELAUNCH_PARENT_PID` 환경변수 + `RelaunchParentPidEnvVarName` const | 손자 generation 부모 PID 전파 (`TryRelaunchAsAdmin` 의 `ShellExecuteW("runas")` 직전 set) |

`TryRelaunchAsAdmin` 의 `ShellExecuteW("runas")` 직전의 `SetEnvironmentVariable(RelaunchParentPidEnvVarName, ...)` 도 유지 — 부팅 self-elevation 경로의 손자 race 차단은 트레이 토글 자동 spawn 흐름과 독립.

---

## 6. 4-case 매트릭스 변화

### 6.1 fix #2 매트릭스

| # | 출발 | 도착 | `newAdminConfig.AdminElevation` | `IsCurrentProcessElevated()` | `isDowngrade` | 동작 |
|---|------|------|---|---|---|------|
| 1 | 일반 | admin | `true` | `false` | `false` | YESNO → YES → 자동 spawn → 자식 UAC 1회 |
| 2 | 일반 | 일반 | `false` | `false` | `false` | YESNO → YES → 자동 spawn → 자식 일반 권한 |
| 3 | admin | admin | `true` | `true` | `false` | YESNO → YES → 자동 spawn → 자식 admin 상속 |
| 4 | admin | 일반 | `false` | `true` | **`true`** | MB_OK 안내만 + `break` (메인 인디 잔존) |

### 6.2 fix #3 매트릭스 — 통일

| # | 출발 | 도착 | 동작 |
|---|------|------|------|
| 1 | 일반 | admin | 안내 + 자동 종료 → 사용자 수동 재실행 → UAC 1회 (자식 admin) |
| 2 | 일반 | 일반 | 안내 + 자동 종료 → 사용자 수동 재실행 → 일반 권한 (no-op 가까운 케이스) |
| 3 | admin | admin | 안내 + 자동 종료 → 사용자 수동 재실행 (admin 환경 → admin 상속) |
| 4 | admin | 일반 | 안내 + 자동 종료 → 사용자 수동 재실행 (사용자 셸 = 일반 권한 상속) |

변화:

- 4 case 모두 동일 흐름 (안내 + 자동 종료)
- `isDowngrade` 분기 자체 제거
- case 4 의 메인 인디 잔존 회귀 자동 해결 (`WM_CLOSE` → `OnProcessExit` → `Overlay.Dispose`)
- 모든 case 의 mental model 충돌 해결 (`MB_YESNO` → `MB_OK`)

---

## 7. 시퀀스 다이어그램

```
사용자 트레이 메뉴 "관리자 권한으로 실행" 클릭:
  1. WM_COMMAND IDM_ADMIN_ELEVATION → Tray.HandleMenuCommand
  2. newAdminConfig = currentConfig with { AdminElevation = !currentConfig.AdminElevation }
  3. updateConfig(newAdminConfig)
       → Settings.Save → config.json 즉시 저장 (mtime self-bump)
  4. StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig)
       → schtasks /xml /tn KoEnVue → <RunLevel> 갱신 (등록 안 됐으면 noop)
  5. User32.MessageBoxW(I18n.AdminElevationChangeNotice, MB_OK)
       → 사용자 OK 클릭 대기 (modal)
  6. User32.PostMessageW(_hwndMain, WM_CLOSE)
       → WM_CLOSE → DefWindowProcW → WM_DESTROY → PostQuitMessage(0)
       → 메시지 루프 종료 → return from Program.MainImpl
       → OnProcessExit cleanup (PR-19 step 0~7)
       → Overlay.Dispose → 메인 인디 자동 dispose ✓
       → CursorOverlay.Dispose → 커서 인디 자동 dispose ✓
       → NIM_DELETE → 트레이 아이콘 제거
       → _mutex.Dispose() → mutex 해제
       → 프로세스 종료

사용자 수동 재실행 (탐색기 더블클릭 또는 시작 메뉴):
  1. Program.Main → MainImpl
  2. Settings.Load → config.AdminElevation = (새 값)
  3. WaitForRelaunchParentIfAny → 환경변수 없음 → noop
  4. AdminElevation.TryRelaunchAsAdmin(config):
       case config=true + 부모 일반 권한:
         IsCurrentProcessElevated() = false → ShellExecuteW("runas") → UAC 1회
         → 사용자 동의 → admin 자식 spawn → 본 인스턴스 종료
         → admin 자식: AlreadyElevated() = true → return Continue → 정상 부팅
       case config=true + 부모 admin (예: 관리자 cmd 에서 실행):
         IsCurrentProcessElevated() = true → return Continue → 정상 부팅 (admin)
       case config=false:
         ShouldElevate() = false → return Continue → 정상 부팅 (일반 권한)
  5. TryAcquireMutex → createdNew=true (이전 인스턴스 종료 완료)
  6. CreateMainWindow → CreateOverlayWindow → ... → 정상 부팅
```

---

## 8. 검증

### 8.1 빌드

```
dotnet build           → 0 warn / 0 error
dotnet publish -r win-x64 -c Release   → 0 warn / 0 error, 4,864,512 bytes
dotnet test            → 65/65 PASS
```

SHA256: `d2efa84ae876af701ab890310d728a3e86dc4e3e8167c0e0eed3ee0cc836695c`.

### 8.2 사이즈 변동

| Stage | Bytes | Δ | 비고 |
|-------|-------|---|------|
| fix #1 (relaunch race) | 4,864,000 | +2,560 vs PR-15 baseline | 환경변수 2종 + 메서드 2종 신규 |
| fix #2 (admin downgrade) | 4,865,024 | +1,024 | `isDowngrade` 분기 + MB_OK const + I18n 키 1개 + ShowDeniedMessage 정리 |
| **fix #3 (4 case 통일)** | **4,864,512** | **-512** | 메서드 2개 제거 + 분기 단순화 ~40 LOC → ~14 LOC + I18n 키 net -1 |

사이즈 감소 -512 bytes 의 정직한 해석 — IL 감소는 메서드 제거 (`ClearReentryGuard` / `SetRelaunchParentPidForTrayRestart`) 의 약 +300 bytes + 분기 단순화의 약 +200 bytes 합 vs 신규 키 (`AdminElevationChangeNotice`) 의 약 -100 bytes 추가. AOT 의 unused code elimination 이 메서드 제거의 영향을 1:1 반영하므로 사용자 코드 표면이 줄어든 만큼 binary 도 줄어듦.

### 8.3 시나리오 매트릭스

| # | 시나리오 | 기대 | 결과 |
|---|---------|------|------|
| 1 | 일반 인스턴스 → admin 토글 (case 1) | 안내 + 자동 종료 → 수동 재실행 → UAC 1회 | 통일 흐름 PASS |
| 2 | 일반 인스턴스 → 일반 유지 토글 (case 2) | 안내 + 자동 종료 → 수동 재실행 → 일반 권한 | 통일 흐름 PASS |
| 3 | admin 인스턴스 → admin 유지 토글 (case 3) | 안내 + 자동 종료 → 수동 재실행 → admin 상속 (admin 환경) | 통일 흐름 PASS |
| 4 | admin 인스턴스 → 일반 토글 (case 4) | 안내 + 자동 종료 → 수동 재실행 → 일반 권한 (사용자 셸 상속) | 통일 흐름 PASS — fix #2 의 메인 인디 잔존 회귀 자동 해결 |
| 5 | 메인 인디 잔존 회귀 (fix #2 의 case 4) | 자동 dispose | `WM_CLOSE` → `OnProcessExit` → `Overlay.Dispose` 도달 — PASS |
| 6 | 사용자 셸 = admin (관리자 cmd 에서 실행) → 토글 | 사용자 수동 재실행 시 admin 상속 (Windows token 모델 그대로) | KoEnVue 통제 외 — fix #2 §7.2 한계 보존 |

### 8.4 invariant + 일관성

- `git grep "AdminElevationChangeNotice" App/` = 4 (I18n.cs 3 — enum + `_table` + public surface, Tray.cs 1 — 호출)
- `git grep "AdminElevationRestartPrompt" App/` = 0 (fix #3 에서 제거)
- `git grep "AdminElevationDowngradeNotice" App/` = 0 (fix #3 에서 제거)
- `git grep "ClearReentryGuard" App/` = 0 (메서드 자체 제거)
- `git grep "SetRelaunchParentPidForTrayRestart" App/` = 0 (메서드 자체 제거)
- `git grep "TryRelaunchAsAdmin" App/` = 2 이상 (Program.cs 호출 + AdminElevation.cs 정의) — 인프라 유지 확인
- `git grep "WaitForRelaunchParentIfAny" App/` = 2 이상 (Program.cs 호출 + AdminElevation.cs 정의) — 인프라 유지 확인
- `git grep "KoEnVue\.App\.Bootstrap" App/UI/Tray.cs` = 0 (using import 제거)

reviewer 종합:

- P1-P6 invariant 모두 통과
- 4 case 통일 흐름 검증
- `OnProcessExit` 종료 시퀀스 도달 확인

---

## 9. 학습

### 9.1 사용자 mental model 이 UI 단어 정확성보다 우선

PR-15 본 PR + fix #2 의 `MB_YESNO` "다음 실행부터 적용됩니다. 지금 재시작하시겠습니까?" 메시지가 의도를 정확히 명시했어도 사용자는 Yes/No = "전체 액션의 결정" 으로 직관함. UI 단어가 정확해도 mental model 자체가 충돌하면 사용자 마찰. fix #3 의 `MB_OK` 단일 버튼 = "확인" 직관과 정확 일치 — 메시지가 짧고 단순한 게 정렬에 유리.

### 9.2 코드 단순화는 부수 효과지 목표가 아님

fix #3 의 `IDM_ADMIN_ELEVATION` 분기 ~40 LOC → ~14 LOC + 메서드 2개 자연 제거는 mental model 정합 + 메인 인디 잔존 회귀 해결의 **부수 효과**. 코드 단순화 자체가 목표였다면 사용자 UX 후퇴 (자동 spawn 폐기) 의 정직한 평가가 누락됐을 것. 사용자 UX 와 코드 단순화가 같은 방향이면 채택, 충돌하면 사용자 UX 우선.

### 9.3 책임 분담 명료화가 추론 오류 차단

사용자 질문 "TryRelaunchAsAdmin 더이상 필요없지 않아?" 는 자연스러운 추론 — 트레이 자동 spawn 폐기 → `ClearReentryGuard` / `SetRelaunchParentPidForTrayRestart` 자연 제거 → `TryRelaunchAsAdmin` 도 같은 운명? 답은 **아니오** — 두 메커니즘의 책임이 다름. 트레이 토글 = "옵션 변경", 부팅 self-elevation = "옵션 효력 발생". 책임 분담을 명시적으로 박제하면 추론 오류 차단 + 미래 PR 의 회귀 가드.

### 9.4 누적 fix 의 시계열 박제

fix #1 (mutex race) → fix #2 (admin down-grade) → fix #3 (4 case 통일) 의 시계열은 동일 영역 (트레이 토글 admin elevation) 의 세 다른 회귀 해결 — 각 fix 의 dev-note + improvement-plan §7.1/7.2/7.3 를 시계열로 박제하면 미래의 누적 회귀 분석 시 컨텍스트 보존. 특히 fix #3 가 fix #1 의 인프라 (`SetRelaunchParentPidForTrayRestart`) 를 자연 제거하는 케이스 — fix #1 dev-note 에 시계열 link 추가하지 않고도 fix #3 dev-note 가 backref 로 분담 + 자연 제거 명시.

---

## 10. 후속 후보 (선택적)

본 fix 는 self-contained — 후속 작업 없이 종결. 미래 진입 조건:

- 사용자 보고가 누적되어 "수동 재실행이 매번 마찰" 이 측정 가능한 비용으로 부상 → fix #2 §7.2 의 Option A (`SaferCreateLevel` + `CreateProcessAsUserW`) 재평가
- 다른 기능 (예: per-process 권한 격리, 샌드박스 spawn) 이 `SaferCreateLevel` API 를 이미 도입해 Advapi32 P/Invoke 표면이 기존재 → Option A 진입 비용 급감

현 시점 진입 조건 미충족 → fix #3 의 통일 흐름이 정직한 비용 대비 가치 균형.

---

## 11. 결론

fix #3 = 사용자 직접 제안 채택 + 4 case 통일 흐름. 코드 단순화 -512 bytes + mental model 충돌 해결 + 메인 인디 잔존 회귀 자동 해결 + 책임 분담 명료화. 트레이드오프 (일반→admin UAC spawn UX 단계 +1) 는 분담 명료화로 보상. PR-15 본 PR + fix #1 + fix #2 + fix #3 의 시계열로 누적 회귀 + 누적 학습 박제 완료.
