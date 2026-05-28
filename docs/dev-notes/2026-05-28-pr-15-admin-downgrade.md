# 2026-05-28 — PR-15 후속 fix #2: admin → 일반 권한 down-grade Windows token 모델 한계

**관련**: [2026-05-28-pr-15-relaunch-race.md](2026-05-28-pr-15-relaunch-race.md) (직전 fix #1 — 트레이 토글 mutex race) · [2026-05-27-admin-elevation.md](2026-05-27-admin-elevation.md) (PR-15 본 PR 진단) · [improvement-plan/PR-15-admin-elevation.md §7.2](../improvement-plan/PR-15-admin-elevation.md)

**Status**: 코드 + 검증 완료 (0 warn / 0 error AOT publish, 65/65 PASS, reviewer 통과)
**범위**: 4 파일 — `App/Localization/I18n.cs` (+14) / `App/UI/Tray.cs` (+15) / `Core/Native/Win32Types.cs` (+2) / `App/Bootstrap/AdminElevation.cs` (+1/-1)
**Binary 영향**: 4,864,000 → 4,865,024 bytes (+1,024 = +1 KB; net +512 vs 직전 fix #1)
**SHA256**: `d54a3bfb30c8b4d1682a6ca534bd1019c326c276782c74869d2e11b918e79b19`

---

## 1. 사용자 보고

관리자 권한 인스턴스에서 트레이 메뉴 **"관리자 권한으로 실행"** (체크 ON 상태) 클릭으로 옵션 비활성화 → "다음 실행부터 적용됩니다. 지금 재시작하시겠습니까?" 대화상자 → **"예"** 클릭 → 자식 인스턴스가 spawn 됐지만 **여전히 관리자 권한**.

직전 fix #1 (mutex race) 직후의 보고이며, 자식이 정상 spawn 한 (race 차단 성공) 상황에서 **권한 down-grade 가 안 됨** 이 본 fix 의 회귀.

수동 검증: 작업 관리자 → 자식 프로세스 우클릭 → 속성 → "관리자 권한으로 실행 중" 확인. config.json 의 `admin_elevation: false` 는 정확히 저장됨 + schtasks 도 `LeastPrivilege` 로 재등록 → 즉 **다음 부팅부터는 자동으로 일반 권한** 이지만, 지금 즉시 적용이 안 됨.

---

## 2. 원인 분석 — Windows token 모델 한계

### 2.1 ShellExecuteW 의 토큰 상속 동작

[`Shell32.ShellExecuteW("open", currentExe)`](../../App/UI/Tray.cs) (PR-15 본 PR 의 [`UriLauncher.Open`](../../Core/Shell/UriLauncher.cs) 위임 경로) 는 표준 `CreateProcess` 경로를 거친다. `CreateProcess` 의 기본 동작:

> If the parent has a primary access token, the new process inherits that token unless explicitly overridden via `CreateProcessAsUserW` or `CreateProcessWithTokenW`.

즉 admin (high IL) 부모 → admin (high IL) 자식. 단순 spawn 으로는 **권한 강등 불가** — Windows 가 명시적으로 차단하는 보안 정책 (high IL → medium IL 강등은 사용자 동의 없이 발생할 수 없음).

PR-15 본 PR 의 `runas` verb 는 **반대 방향** (medium → high) — `runas` 가 UAC 다이얼로그를 trigger 해 사용자 동의를 받아 high IL 토큰 발급. medium → high 는 사용자 동의 + UAC 가, high → medium 은 어떤 표준 verb 도 제공 안 함.

### 2.2 우회로 후보 3종

| 옵션 | 메커니즘 | 변경면 | 회귀 위험 |
|------|---------|--------|----------|
| A | `Advapi32.SaferCreateLevel(SAFER_LEVELID_NORMALUSER)` + `SaferComputeTokenFromLevel` + `CreateProcessAsUserW` — 새 medium IL 토큰 합성 후 명시 사용 | +200 LOC + Advapi32 P/Invoke 3종 신규 | 다층 — 토큰 합성 / SECURITY_ATTRIBUTES / lpStartupInfo 마샬링 / inherit handle 정책 |
| B | `explorer.exe` 위임 (CMSTPLUA COM `IShellExecuteHook::Execute`) — explorer 의 medium IL 컨텍스트에서 자식 spawn | +150 LOC + COM 인터페이스 marshalling | NativeAOT 호환 불확실 + IShellExecuteHook 사용 deprecated 경고 |
| **C** | down-grade 케이스만 분기 → 자동 spawn 안 함 → MB_OK 안내 → 사용자 수동 종료/재실행 | 4 파일 +30 LOC | 0 — 기존 자동 spawn 흐름 3 케이스 그대로 |

### 2.3 채택 — Option C

근거:

1. **즉시성 비용이 작다** — `StartupTaskManager.ReregisterIfAdminChanged` 가 이미 schtasks 를 `LeastPrivilege` 로 재등록 → 다음 부팅부터는 자동으로 일반 권한 시작. "지금 즉시 적용" 만 사용자 결정 (수동 종료/재실행) 대상이고, 그것도 정확한 절차 안내 (`MenuExit` 라벨과 일치하는 '종료' / 'Exit' 단어) 면 사용자가 명확히 인지 가능.
2. **변경면 최소** — 4 파일 ~30 LOC vs Option A 의 +200 LOC. P/Invoke 신규 0 (`Win32Constants.MB_OK` 만 추가, 이미 있던 `User32.MessageBoxW` 호출 재사용).
3. **회귀 위험 0** — 다른 3 케이스 (일반→admin / 일반→일반 / admin→admin) 의 기존 자동 spawn 흐름은 한 줄도 변경 안 됨. admin→admin 만 토큰 상속이 의도 일치 (admin 이 admin 으로 재시작), 나머지 두 케이스는 일반 부모라 `ShellExecuteW` 가 정확한 권한 자식 생성.
4. **silent fail 정책 정합** — KoEnVue 정책 ("동작 안 하면 사용자에게 명시 안내, 절대 silent") 과 정직하게 일치. Option A/B 가 작동 안 할 때의 회귀 표면 (토큰 합성 실패 silent / COM 인터페이스 미지원 silent) 보다 명시 안내가 정직.

미래 진입 조건 — Option A 재평가:
- 사용자 보고가 누적되어 "수동 재실행이 매번 마찰" 이 측정 가능한 비용으로 부상.
- 다른 기능 (예: per-process 권한 격리, 샌드박스 spawn) 이 `SaferCreateLevel` API 를 이미 도입해 Advapi32 P/Invoke 표면이 기존재.

본 fix 시점에는 둘 다 미충족.

---

## 3. 4-case 분기 매트릭스

| # | 출발 (현재 인스턴스) | 도착 (사용자 의도) | `newAdminConfig.AdminElevation` | `AdminElevation.IsCurrentProcessElevated()` | `isDowngrade` | 동작 |
|---|---------------------|-------------------|---|---|---|------|
| 1 | 일반 권한 | admin | `true` | `false` | `false` | YESNO 안내 → YES 시 spawn → 자식의 PR-15 self-check 가 UAC 1회 (기존 흐름) |
| 2 | 일반 권한 | 일반 권한 | `false` | `false` | `false` | YESNO 안내 → YES 시 spawn → 자식이 일반 권한 (기존 흐름, drop-down 회귀 없음 — 일반 부모 토큰 상속) |
| 3 | admin | admin | `true` | `true` | `false` | YESNO 안내 → YES 시 spawn → 자식이 admin 토큰 상속 (기존 흐름, 의도 일치) |
| 4 | admin | 일반 권한 | `false` | `true` | **`true`** | **MB_OK 안내만 + 자동 spawn 안 함** (신규) — 사용자 수동 "종료" + 재실행으로 새 인스턴스 일반 권한 시작 |

case 1/2/3 의 기존 자동 spawn 흐름은 한 줄도 변경 안 됨 (분기 추가 전 코드 그대로). case 4 만 새 분기에서 `break` 로 자동 spawn 경로 (`ClearReentryGuard` / `SetRelaunchParentPidForTrayRestart` / `UriLauncher.Open`) 모두 skip.

---

## 4. 구현

### 4.1 신규 I18n 키

[`App/Localization/I18n.cs`](../../App/Localization/I18n.cs):

```csharp
// I18nKey enum 에 추가
AdminElevationDowngradeNotice,

// _table 에 추가
// admin → 일반 권한 down-grade 케이스 — Windows token 모델 한계로 자동 spawn 불가
// (ShellExecuteW 가 부모 admin 토큰 상속). 사용자에게 수동 종료/재실행 안내.
[I18nKey.AdminElevationDowngradeNotice] = (
    "관리자 권한 옵션이 비활성화됐습니다. 다음 실행부터 일반 권한으로 시작됩니다. 지금 적용하려면 트레이 메뉴의 '종료' 후 KoEnVue 를 다시 실행하세요.",
    "Admin elevation has been disabled. KoEnVue will start with normal privileges from the next launch. To apply now, choose 'Exit' from the tray menu and re-run KoEnVue."),

// public surface
/// <summary>
/// admin → 일반 down-grade 케이스 안내 (PR-15 후속 fix #2). Windows token 모델 한계로
/// admin 인스턴스가 일반 권한 자식을 자동 spawn 못 함 (ShellExecuteW 가 부모 토큰 상속).
/// 사용자에게 트레이 '종료' + 수동 재실행 안내. <see cref="MenuExit"/> 라벨과 메시지 안의
/// '종료' / 'Exit' 표기가 일치해야 사용자가 다음 단계를 명확히 인지.
/// </summary>
public static string AdminElevationDowngradeNotice => Get(I18nKey.AdminElevationDowngradeNotice);
```

**일관성 invariant**: 메시지 안 '종료' / 'Exit' 단어가 `I18n.MenuExit` 라벨과 정확 일치 — 사용자가 안내 메시지의 "트레이 메뉴의 '종료'" 를 읽고 트레이 메뉴를 열었을 때 정확히 동일한 단어를 찾을 수 있게.

### 4.2 Tray 분기

[`App/UI/Tray.cs`](../../App/UI/Tray.cs) `IDM_ADMIN_ELEVATION` 핸들러 — `updateConfig(newAdminConfig)` + `StartupTaskManager.ReregisterIfAdminChanged` 직후, `int answer = MessageBoxW(...YESNO)` 직전:

```csharp
// 분기 — admin → 일반 권한 down-grade 는 Windows token 모델 한계로 자동 spawn
// 불가 (ShellExecuteW("open") 가 부모 admin 토큰 상속 → 자식도 admin). 사용자에게
// 수동 종료/재실행 안내만 노출 + MB_OK. 다른 3 케이스 (일반→admin, 일반→일반,
// admin→admin) 는 기존 자동 spawn 흐름 — UAC 1회 또는 권한 유지.
bool isDowngrade = !newAdminConfig.AdminElevation
    && AdminElevation.IsCurrentProcessElevated();
if (isDowngrade)
{
    User32.MessageBoxW(hwndMain,
        I18n.AdminElevationDowngradeNotice, "KoEnVue",
        Win32Constants.MB_OK);
    break;
}
```

`break` 의 의미 — `case IDM_ADMIN_ELEVATION:` 의 switch break. 후속 자동 spawn 경로 (`ClearReentryGuard` / `SetRelaunchParentPidForTrayRestart` / `UriLauncher.Open` / `PostMessageW(WM_CLOSE)`) 모두 skip. config 저장과 schtasks 재등록은 이미 분기 직전에 끝난 상태라 "지금 즉시 적용" 만 안 되고 "다음 부팅부터는 일반 권한" 은 보장.

### 4.3 MB_OK const 화 (P3)

[`Core/Native/Win32Types.cs`](../../Core/Native/Win32Types.cs) `Win32Constants` 클래스의 `// --- MessageBox 스타일 ---` 절:

```csharp
/// <summary>MessageBox 단일 OK 버튼 (기본값 — uType=0 과 동치).</summary>
public const uint MB_OK             = 0x00000000;
public const uint MB_YESNO          = 0x00000004;   // 기존
```

[`App/Bootstrap/AdminElevation.cs`](../../App/Bootstrap/AdminElevation.cs) `ShowDeniedMessage`:

```csharp
// 변경 전
uType: 0);

// 변경 후
uType: Win32Constants.MB_OK);
```

P3 정합 — 매직 넘버 `0` 을 const 로 치환. PR-15 본 PR 시점부터 잔존하던 한 곳 — 본 fix 가 새 MB_OK 호출을 추가하면서 발견 후 같이 정리.

---

## 5. 보완 동작 — schtasks 즉시 재등록

본 fix 직전 (분기 추가 전) 코드:

```csharp
updateConfig(newAdminConfig);                                            // config.json 즉시 저장
StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig);             // schtasks RunLevel 즉시 갱신
```

`StartupTaskManager.ReregisterIfAdminChanged` 가 `BuildStartupTaskXml(exePath, adminElevation: false)` → `<RunLevel>LeastPrivilege</RunLevel>` → schtasks `/xml` 재등록. 즉 사용자가 안내 메시지 OK 클릭 후:

1. **트레이 "종료"** 클릭 → `OnProcessExit` cleanup → 인스턴스 종료
2. **사용자가 KoEnVue 를 직접 실행** (탐색기에서 더블클릭 또는 시작 메뉴) → 부모가 일반 권한 (사용자 셸) 이라 `ShellExecuteW` 의 부모 토큰 상속이 정확히 일반 권한 → 새 인스턴스 일반 권한 시작
3. **다음 부팅** → schtasks 트리거 → `LeastPrivilege` 로 자동 시작 (UAC 0, PR-03 디폴트 정책 복귀)

즉 본 fix 의 "수동 종료/재실행" 은 **"지금 즉시 적용" 만의 비용** — 다음 부팅부터는 무조건 자동. 사용자 마찰은 한 번뿐.

---

## 6. 시퀀스 다이어그램

### 6.1 admin → 일반 down-grade 경로 (본 fix 신규)

```
admin 인스턴스 Tray 메뉴 IDM_ADMIN_ELEVATION 분기 (체크 ON → OFF):
  1. newAdminConfig = currentConfig with { AdminElevation = false }
  2. updateConfig(newAdminConfig)
       → Settings.Save → config.json 의 admin_elevation: false 즉시 저장
  3. StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig)
       → schtasks /xml /tn KoEnVue → <RunLevel>LeastPrivilege</RunLevel>
       (등록 안 됐으면 noop)
  4. isDowngrade = (!false && true) = true   ← NEW
  5. User32.MessageBoxW(hwndMain,
        I18n.AdminElevationDowngradeNotice,
        "KoEnVue", Win32Constants.MB_OK)     ← NEW (modal 차단 + 사용자 OK 대기)
  6. break;                                    ← NEW (case 의 switch break)
     → 후속 자동 spawn 경로 (ClearReentryGuard / SetRelaunchParentPidForTrayRestart /
        UriLauncher.Open / PostMessageW(WM_CLOSE)) 모두 skip
     → admin 인스턴스 정상 실행 계속 (admin 그대로)

사용자 수동 액션:
  7. 트레이 메뉴 → "종료" 클릭
       → IDM_EXIT 분기 → PostMessageW(_hwndMain, WM_CLOSE) → OnProcessExit
       → cleanup → 인스턴스 종료
  8. 사용자 KoEnVue.exe 직접 실행 (탐색기 / 시작 메뉴)
       → 부모가 사용자 셸 (medium IL) → ShellExecuteW 의 토큰 상속 = medium IL
       → 새 인스턴스가 일반 권한 시작 ✓
```

### 6.2 다른 3 케이스 (기존 흐름, 변화 없음)

```
isDowngrade 분기 통과 (false) → 기존 자동 spawn 흐름 그대로:
  4'. answer = User32.MessageBoxW(YESNO)
  5'. if (answer == IDYES) {
        ClearReentryGuard();
        SetRelaunchParentPidForTrayRestart();  // 직전 fix #1 의 race 차단 환경변수
        UriLauncher.Open(exePath);             // 자식 spawn
        PostMessageW(_hwndMain, WM_CLOSE);     // 본 인스턴스 종료
      }
```

case 1 (일반→admin): 자식이 PR-15 의 `TryRelaunchAsAdmin` 에서 UAC 1회 → high IL 손자.
case 2 (일반→일반): 자식이 일반 권한 그대로 (parent token 상속).
case 3 (admin→admin): 자식이 admin 토큰 상속 (parent token 상속, 의도 일치).

---

## 7. 검증

### 7.1 빌드

```
dotnet build           → 0 warn / 0 error
dotnet publish -r win-x64 -c Release   → 0 warn / 0 error, 4,865,024 bytes
dotnet test            → 65/65 PASS
```

SHA256: `d54a3bfb30c8b4d1682a6ca534bd1019c326c276782c74869d2e11b918e79b19`

### 7.2 시나리오 매트릭스

| # | 시나리오 | 기대 | 결과 |
|---|---------|------|------|
| 1 | 일반 인스턴스 → admin 토글 (case 1) | YESNO → YES → 자식 UAC 1회 → 자식 admin | 기존 흐름 보존 — 회귀 없음 |
| 2 | 일반 인스턴스 → 일반 유지 토글 (case 2, no-op 가까운 경우) | YESNO → YES → 자식 일반 권한 | 기존 흐름 보존 |
| 3 | admin 인스턴스 → admin 유지 토글 (case 3) | YESNO → YES → 자식 admin 토큰 상속 | 기존 흐름 보존 |
| 4 | **admin 인스턴스 → 일반 토글 (case 4)** | **MB_OK 안내만 + 자동 spawn 안 함** | **본 fix 신규** — 사용자 수동 종료/재실행 |
| 5 | case 4 후 사용자 트레이 "종료" + 직접 재실행 | 새 인스턴스 일반 권한 시작 | 사용자 셸 토큰 상속 → medium IL |
| 6 | case 4 후 다음 부팅 (schtasks 트리거) | 자동 시작 일반 권한 (UAC 0) | `ReregisterIfAdminChanged` 가 `LeastPrivilege` 재등록 |

### 7.3 invariant + 일관성

- `git grep "MB_OK" Core/Native/Win32Types.cs` = 1 (const 정의)
- `git grep "Win32Constants.MB_OK" App/` = 2 (Tray.cs + AdminElevation.cs ShowDeniedMessage)
- `git grep "uType: 0" App/` = 0 (이전에 AdminElevation.cs 한 곳, 본 fix 가 정리)
- `git grep "AdminElevationDowngradeNotice" App/` = 2 (I18n.cs 정의 + Tray.cs 사용)
- I18n 메시지 '종료' 단어 vs `MenuExit` 라벨 일치 — manual 검토 PASS

---

## 8. PR-15 plan 갱신 — §7.2 추가

[improvement-plan/PR-15-admin-elevation.md](../improvement-plan/PR-15-admin-elevation.md) 의 §7.1 (직전 fix #1, mutex race) 다음에 본 fix 의 **§7.2 admin → 일반 down-grade Windows token 모델 한계** 추가. 4-case 매트릭스 + Option A/B/C 비교 + `isDowngrade` 코드 인용 + `StartupTaskManager.ReregisterIfAdminChanged` 보완 동작 박제. PR-15 의 시나리오 F (`admin_elevation: true → false` 토글) 가 "다음 부팅부터 자동" 으로 정확하지만 즉시 적용 경로의 메시지 정렬 오류를 본 fix 가 정정.

---

## 9. 학습

### 9.1 메시지 문구가 동작 모델과 정확히 정렬돼야 함

PR-15 본 PR 의 "다음 실행부터 적용됩니다. 지금 재시작하시겠습니까?" 메시지는 다른 3 케이스에는 정확하지만 case 4 (admin → 일반 down-grade) 에는 **즉시 적용 기대** 와 잘못 정렬. 사용자는 "지금 재시작하시겠습니까?" 의 YES → 즉시 일반 권한 새 인스턴스 기대. 실제는 자식이 admin 토큰 상속 → silent fail.

박제 — UI 메시지 작성 시 **모든 분기 케이스에서 정확** 한지 검토 필수. case-별 분기가 동작 차이를 만들면 메시지도 case-별로 분기.

### 9.2 OS 모델 한계는 우회보다 명시 안내

권한 down-grade 우회 (Option A `SaferCreateLevel` / Option B explorer 위임) 는 가능하지만 변경면 +200 LOC + 회귀 표면 다층. KoEnVue 의 silent fail 정책 ("동작 안 하면 사용자에게 명시 안내, 절대 silent") 과 정합하게 **OS 한계를 정직하게 노출** + 사용자 결정 그대로 존중. "지금 즉시" 만의 비용 (수동 종료/재실행 한 번) 이 우회 메커니즘의 회귀 위험보다 작음.

### 9.3 분기 매트릭스의 완전성

4-case 매트릭스 (출발 admin/일반 × 도착 admin/일반 = 4) 를 표로 박제하면 어떤 분기가 신규/기존 인지 명확. case 4 만 신규, 나머지 3 은 기존 흐름 보존 — 회귀 위험 0 의 정직한 시각화.

### 9.4 I18n 메시지의 액션 단어 일관성

본 fix 메시지 안의 '종료' / 'Exit' 단어가 `MenuExit` 라벨 ("종료" / "Exit") 과 정확 일치해야 사용자가 다음 단계 (트레이 메뉴 → "종료" 클릭) 를 명확히 인지. 메시지에 "닫기" / "Close" 같은 다른 단어를 쓰면 사용자가 트레이 메뉴에서 같은 단어를 찾지 못해 마찰. invariant grep 자동화 후보 (메시지 안 "'종료'" `MenuExit` 일치 검증) — 본 PR 시점에는 manual 검토 + dev-note 박제로 충분.

---

## 10. 후속 후보 (선택적)

- (A) `SaferCreateLevel` 우회 도입 — §2.3 의 진입 조건 둘 중 하나라도 충족 시 재평가. 별도 PR (~+200 LOC + Tier-3 매트릭스 확장).
- (B) I18n 메시지 액션 단어 invariant grep — `git grep "'종료'" App/Localization/I18n.cs` 와 `MenuExit` 정의 단어 자동 일치 검증. 본 fix 시점에는 manual 검토.
- (C) 트레이 메뉴 "종료" 항목에 시각적 강조 (case 4 안내 메시지가 띄워진 직후) — 사용자가 메뉴를 열었을 때 종료 항목을 더 쉽게 찾게. UI 변경면 + 모달 흐름 복잡도 증가라 본 PR 시점에는 미진입.

---

## 11. 결론

본 fix 는 사용자 보고 (down-grade silent fail) → 원인 분석 (Windows token 모델 ShellExecuteW 토큰 상속) → 가설 비교 (Option A 우회 / B 위임 / C 명시 안내) → Option C 채택 (silent fail 정책 정합, 변경면 최소, 회귀 위험 0) → 검증 (build 0 warn / 65 PASS / reviewer 통과) 의 정직한 차단 절차를 박제한다.

핵심 가치: **OS 한계 정직 노출** (우회 변경면 회피) + **분기 매트릭스 완전성** (case 1/2/3 회귀 위험 0) + **schtasks 보완 동작 박제** (다음 부팅부터는 무조건 자동, "지금 즉시" 만 사용자 결정) + **I18n 액션 단어 일관성** (`MenuExit` 라벨과 메시지 단어 정확 일치).
