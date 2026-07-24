# 2026-05-29 — PR-15 후속 fix #4: 트레이 메뉴 체크 OR 로직 + 안내 메시지 단순화

**관련**: [2026-05-29-pr-15-tray-toggle-unified.md](2026-05-29-pr-15-tray-toggle-unified.md) (직전 fix #3 — 4 case 통일 흐름 + 단일 메시지) · [2026-05-28-pr-15-admin-downgrade.md](2026-05-28-pr-15-admin-downgrade.md) (fix #2 — admin → 일반 down-grade 분기) · [2026-05-28-pr-15-relaunch-race.md](2026-05-28-pr-15-relaunch-race.md) (fix #1 — 트레이 토글 mutex race) · [2026-05-27-admin-elevation.md](2026-05-27-admin-elevation.md) (PR-15 본 PR 진단) · [improvement-plan/PR-15-admin-elevation.md §7.4](../improvement-plan/PR-15-admin-elevation.md)

**Status**: 코드 + 검증 완료 (0 warn / 0 error AOT publish, 65/65 PASS, reviewer 통과)
**범위**: 2 파일 — `App/Localization/I18n.cs` (+1/-1 메시지 단순화) / `App/UI/Tray.Menu.cs` (+9/-1 OR 로직 + import + doc comment)
**Binary 영향**: 4,864,512 → 4,864,512 bytes (**±0 bytes**; AOT 페이지 경계 흡수 — fix #3 의 -512 bytes 감소 후 fix #4 도 ±0 으로 누적 -512 유지)
**SHA256**: `e7dfc79d93836d052d1e8f72aece1397998fd3771d55509b90275418f79a3dc1`

---

## 1. 사용자 보고/제안 — 누적 2종 동시 처리

fix #3 (4 case 통일 흐름 + 단일 메시지 + MB_OK + 자동 종료) 박제 직후 사용자 보고/제안 2종 동시 보고:

### 1.1 메시지 단순화 요청

사용자: fix #3 의 `AdminElevationChangeNotice` 두 번째 문장 "관리자 권한 옵션은 다음 실행부터 적용됩니다" 가 첫 번째 "관리자 권한 옵션이 변경되어 KoEnVue를 종료합니다" 와 **redundant**.

분석:

- 사용자 OK → 자동 종료 (`PostMessageW(WM_CLOSE)`) → 수동 재실행 흐름에서 **"다음 실행" 시점이 자명**.
- 두 번째 문장 = 정보 가치 0. 사용자 인지 비용만 증가.
- 행동 지시 ("KoEnVue를 다시 실행해 주세요") 가 빠진 게 오히려 직관 충돌 — "종료 알림 + 다음 단계 안내" 패턴 미달.

새 메시지:

- ko: "관리자 권한 옵션이 변경되어 KoEnVue를 종료합니다. KoEnVue를 다시 실행해 주세요."
- en: "The admin elevation option has been changed. KoEnVue will now exit. Please launch KoEnVue again."

간결 + 행동 지시 명확. fix #3 의 mental model 정합 의도 (Yes/No 컨벤션 충돌 회피) 와 같은 방향.

### 1.2 사용자 ultrathink 질문 — 메뉴 체크 OR

사용자: "관리자 권한으로 실행된 Total Commander 등에서 KoEnVue 를 실행할 경우, `admin_elevation` 옵션 값과 상관없이 '관리자 권한으로 실행' 항목에 체크가 되어 있어야 하지 않을까?"

질문 분석 — fix #3 까지의 메뉴 체크 분기를 정확히 식별:

```csharp
// fix #3 시점 (Tray.Menu.cs)
uint adminElevationFlags = config.AdminElevation ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
```

이 분기에서 **case 2** (`config.AdminElevation=false` + `IsCurrentProcessElevated()=true`) 의 시각 충돌:

- 사용자가 관리자 권한 Total Commander (혹은 admin cmd / admin 탐색기) 에서 KoEnVue.exe 실행.
- `ShellExecuteW("open")` 가 부모 토큰 상속 → KoEnVue 가 admin 권한으로 시작.
- 메뉴 체크는 OFF (config 기반) — **실 권한은 admin 인데 메뉴는 일반 권한 표시**.

사용자 mental model: "실 권한이 admin 이면 메뉴 항목도 ON 이어야 정직" — 채택.

---

## 2. 4-case 매트릭스 분석

### 2.1 fix #3 까지의 매트릭스

| # | `config.AdminElevation` | `IsCurrentProcessElevated()` | fix #3 체크 | 의미 | 정합? |
|---|---|---|---|------|------|
| 1 | `false` | `false` | OFF | 일반 권한 + 옵션 OFF | ✅ |
| 2 | **`false`** | **`true`** | **OFF** | **admin 환경 외부 spawn — config OFF 인데 실 admin** | ❌ **충돌** |
| 3 | `true` | `false` | ON | 일반 권한 + 옵션 ON (다음 실행 self-elevate) | ✅ |
| 4 | `true` | `true` | ON | admin 권한 + 옵션 ON | ✅ |

case 2 만 시각 충돌 — fix #3 박제 시 본 케이스를 식별 못 함 (`updateConfig` 흐름이 case 2 도 정상 처리하지만 메뉴 체크 표시는 별개).

### 2.2 fix #4 OR 로직 매트릭스

```csharp
bool isAdminEffective = config.AdminElevation || AdminElevation.IsCurrentProcessElevated();
uint adminElevationFlags = isAdminEffective ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
```

| # | `config.AdminElevation` | `IsCurrentProcessElevated()` | fix #4 체크 (OR) | 의미 | 정합? |
|---|---|---|---|------|------|
| 1 | `false` | `false` | OFF | 일반 권한 + 옵션 OFF | ✅ |
| 2 | **`false`** | **`true`** | **ON ✓** | **admin 환경 외부 spawn — 정직한 시각 노출** | ✅ **case 2 해결** |
| 3 | `true` | `false` | ON | 일반 권한 + 옵션 ON | ✅ |
| 4 | `true` | `true` | ON | admin 권한 + 옵션 ON | ✅ |

case 2 만 fix #4 의 OR 로 동작 변경. 다른 3 case 는 fix #3 와 동일.

**"현재 권한 OR 다음 실행 시 권한"** — 정직한 시각 노출:

- `config.AdminElevation=true` → "다음 실행 시 admin" (현재 일반 권한이어도 다음 실행 시 self-elevate)
- `IsCurrentProcessElevated()=true` → "현재 권한이 admin" (외부 spawn 토큰 상속 포함)
- OR → "현재 OR 다음 = admin 임을 사용자에게 명시"

---

## 3. 채택 근거

### 3.1 정직한 시각 노출

case 2 의 silent 충돌 차단. 사용자가 admin Total Commander 에서 KoEnVue.exe 실행 → 트레이 메뉴 봤을 때 즉시 "현재 admin 권한" 인지 가능. fix #3 까지는 사용자가 (a) 트레이 아이콘만 보고 권한 인지 불가, (b) 관리자 콘솔에서 한/영 표시 작동 여부로 역추론, (c) 별도 도구로 확인 — 정직성 결여.

### 3.2 다른 메뉴 항목과의 일관성 — admin 만 예외 정당

Snap / Animation / Cursor halo / Tray Click Action / Startup / Indicator Hidden 등 다른 메뉴 항목은 **모두 `config.*` 직접 반영** — 외부 환경 영향 받는 항목 0. 비교:

| 메뉴 항목 | 체크 분기 | 외부 환경 영향 |
|---------|---------|---------------|
| Snap | `config.SnapToWindows` | 없음 |
| Animation | `config.UseAnimation` | 없음 |
| Cursor halo | `config.CursorIndicatorEnabled` | 없음 |
| Startup | `config.StartupEnabled` | schtasks 등록 외부 상태 있음 — 별도 `IsStartupEnabledAsync` 로 dual check (config OR 실 등록) |
| Indicator hidden | `config.IndicatorHidden` | 없음 |
| **Admin elevation** | `config.AdminElevation` (fix #3) → **OR `IsCurrentProcessElevated()`** (fix #4) | **있음 — 부모 셸 토큰 상속** |

admin 항목만 **부모 셸 토큰 상속** 이라는 외부 환경 영향을 받는 유일한 케이스라 OR 정당. Startup 도 유사 패턴 (config + 실 등록 상태) 의 precedent. fix #4 는 메뉴 빌더 한 곳만 OR — 다른 메뉴 빌더에 OR 패턴 확산하지 않음.

### 3.3 토글 의미 보존

`IDM_ADMIN_ELEVATION` 분기 (fix #3 의 4 단계 단일 흐름) 는 **한 줄도 변경 안 됨**:

```csharp
case IDM_ADMIN_ELEVATION:
{
    AppConfig newAdminConfig = config with { AdminElevation = !config.AdminElevation };
    updateConfig(newAdminConfig);
    StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig);
    User32.MessageBoxW(hwndMain, I18n.AdminElevationChangeNotice, "KoEnVue", Win32Constants.MB_OK);
    User32.PostMessageW(hwndMain, Win32Constants.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}
break;
```

토글 클릭 = config 만 변경 + schtasks 재등록 + 안내 + `WM_CLOSE`. Windows token 모델 한계로 실 권한은 다음 부팅까지 영향 없음 — 클릭이 "지금 실 권한" 을 바꾸지 못함을 `MessageBoxW` 안내가 사용자 가이드.

**중요 보존**:

- 메뉴 체크 OR 은 **시각 표시만** 변경. 실 동작 (config 변경) 은 변경 안 됨.
- case 2 에서 사용자가 메뉴 클릭 → `config.AdminElevation` 토글 (`false → true`) → 다음 부팅부터 schtasks `HighestAvailable` 자동 등록. 부팅 self-elevation 흐름과 정합.
- 일반 사용자가 case 2 메뉴 체크를 보고 "왜 ON 인데 클릭하면 OFF 안 됨?" 의문 가능 — 안내 메시지 + Windows token 모델 한계가 사용자 가이드.

### 3.4 Windows token 모델 한계 정합

fix #2/#3 의 핵심 통찰: **부모 토큰 상속은 KoEnVue 통제 외**. 사용자 셸 = admin Total Commander → KoEnVue 자식 = admin. config 가 어떤 값이든 본 spawn 자체가 admin. KoEnVue 가 일반 권한으로 down-grade spawn 자체 불가 (Windows 보안 정책).

fix #4 의 OR 는 이 한계를 **시각화** — "외부 환경 영향" 을 사용자에게 정직하게 노출. fix #3 가 admin → 일반 down-grade 의 한계를 4 case 통일 흐름으로 처리한 것과 같은 방향.

---

## 4. 구현

### 4.1 [`App/Localization/I18n.cs`](../../App/Localization/I18n.cs) — 메시지 단순화 (1 spot)

```diff
 [I18nKey.AdminElevationChangeNotice] = (
-    "관리자 권한 옵션이 변경되어 KoEnVue를 종료합니다. 관리자 권한 옵션은 다음 실행부터 적용됩니다.",
-    "The admin elevation option has been changed. KoEnVue will now exit; the change will apply from the next launch."),
+    "관리자 권한 옵션이 변경되어 KoEnVue를 종료합니다. KoEnVue를 다시 실행해 주세요.",
+    "The admin elevation option has been changed. KoEnVue will now exit. Please launch KoEnVue again."),
```

doc comment (`_table` 위) 는 그대로 — fix #3 의 4 case 통일 흐름 + admin→일반 down-grade 한계의 사용자 수동 재실행 자연 회피 설명이 fix #4 에서도 valid.

### 4.2 [`App/UI/Tray.Menu.cs`](../../App/UI/Tray.Menu.cs) — OR 로직 + import + doc comment

```diff
+using KoEnVue.App.Bootstrap;
 using KoEnVue.App.Config;
 using KoEnVue.App.Localization;
 using KoEnVue.App.Models;
 ...
 // PR-15: 관리자 권한 토글 — UIPI 우회 (admin 콘솔의 한/영 표시). 시작 프로그램 등록 바로 옆에 배치.
-uint adminElevationFlags = config.AdminElevation ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
+// 체크 표시 = config.AdminElevation OR IsCurrentProcessElevated() (PR-15 후속 fix #4, 2026-05-29).
+// OR 의 이유 — admin 환경 외부 spawn (예: admin Total Commander 가 KoEnVue.exe 실행 시 admin
+// 토큰 상속) 경우, config 가 false 여도 실 권한이 admin 이면 사용자에게 명시적으로 시각 노출.
+// 다른 메뉴 항목 (Snap/Animation 등) 은 config 직접 반영 — admin 항목만 외부 환경 영향 받는
+// 유일한 케이스라 OR 정당. 토글 클릭은 여전히 config 만 변경 (Windows token 모델 한계 — 실
+// 권한은 다음 부팅까지 영향 없음, MessageBoxW 안내가 사용자 가이드).
+bool isAdminEffective = config.AdminElevation || AdminElevation.IsCurrentProcessElevated();
+uint adminElevationFlags = isAdminEffective ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
```

**부수 효과** — `using KoEnVue.App.Bootstrap;` import 재추가. fix #3 가 `Tray.cs` 에서 import 제거 (`AdminElevation.IsCurrentProcessElevated` / `ClearReentryGuard` / `SetRelaunchParentPidForTrayRestart` 호출 모두 폐기로 사용처 0) 했었음. fix #4 가 같은 partial class 의 **다른 파일** (`Tray.Menu.cs`) 에 추가 — partial class 의 다른 file 은 별도 namespace import 필요 (C# 컴파일 단위 = file).

### 4.3 호출처 변화

`AdminElevation.IsCurrentProcessElevated()` 호출처:

| 시점 | 호출처 | 비고 |
|------|-------|------|
| PR-15 본 PR | [`Program.cs`](../../Program.cs) `MainImpl` step 0c (부팅 시점) | `TryRelaunchAsAdmin` 의 IL 체크 — `IsCurrentProcessElevated()=true` 면 skip |
| fix #2 | [`App/UI/Tray.cs`](../../App/UI/Tray.cs) `IDM_ADMIN_ELEVATION` (down-grade 분기) | fix #3 에서 분기 제거 → fix #2 시점 호출처 폐기 |
| **fix #4** | [`App/UI/Tray.Menu.cs`](../../App/UI/Tray.Menu.cs) `ShowMenu` (메뉴 체크) | **신규** |

fix #4 시점 호출처 2 — `Program.cs` (부팅 분기) + `Tray.Menu.cs` (메뉴 체크). `AdminElevation` 클래스 자체는 변경 0.

---

## 5. 검증

### 5.1 빌드 / 테스트

```
dotnet build           → 0 warn / 0 error
dotnet publish -r win-x64 -c Release   → 0 warn / 0 error, 4,864,512 bytes (fix #3 4,864,512 → ±0)
dotnet test            → 65/65 PASS
```

SHA256: `e7dfc79d93836d052d1e8f72aece1397998fd3771d55509b90275418f79a3dc1`.

### 5.2 AOT 페이지 흡수

fix #3 → fix #4 의 IL 변화:

- (+) `Tray.Menu.cs` 의 `bool isAdminEffective = config.AdminElevation || AdminElevation.IsCurrentProcessElevated();` — 메서드 호출 site 1 (이미 다른 호출처 = `Program.cs` 가 있어서 메서드 IL 자체는 재사용)
- (+) doc comment 7 줄 (AOT IL 영향 0 — 주석)
- (−) `I18n.cs` ko 메시지 짧아짐 (~16 글자 감소, UTF-16 = 32 bytes)
- (−) `I18n.cs` en 메시지 짧아짐 (~30 글자 감소, UTF-16 = 60 bytes)

순 변화: 메서드 호출 site 추가 (몇 bytes IL) + UTF-16 문자열 감소 (~92 bytes). AOT 의 4 KB 페이지 경계 안에 흡수 — net 변화 0.

fix #3 의 -512 bytes 감소 후 fix #4 도 ±0 으로 누적 -512 유지. fix #1 (+2,560) → fix #2 (+1,024) → fix #3 (-512) → fix #4 (±0). v0.9.4.0 base (4,861,440) + 3,584 = 4,865,024 = fix #2 = fix #3 base. fix #3 의 -512 = 4,864,512. fix #4 = 4,864,512.

### 5.3 토글 의미 보존 검증

사용자가 case 2 (admin 환경 외부 spawn, fix #4 체크 ON) 에서 메뉴 클릭 시:

1. `config.AdminElevation` 토글: `false → true`
2. `updateConfig(newAdminConfig)` — config.json 디스크 저장
3. `StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig)` — schtasks `<RunLevel>` 을 `LeastPrivilege → HighestAvailable` 재등록
4. `MessageBoxW(I18n.AdminElevationChangeNotice, MB_OK)` — 안내
5. `PostMessageW(WM_CLOSE)` — 자동 종료
6. 사용자 수동 재실행:
   - admin Total Commander 에서 다시 실행 → admin 토큰 상속 → `IsCurrentProcessElevated()=true` → `TryRelaunchAsAdmin` noop (skip) → admin 동작 그대로
   - 일반 권한 셸 (탐색기) 에서 실행 → `config.AdminElevation=true` → `TryRelaunchAsAdmin` 호출 → UAC 1회 → admin 자식 spawn
   - 시작 프로그램 (schtasks) → `<RunLevel>HighestAvailable</RunLevel>` → 부팅마다 UAC 0 으로 admin 자동 시작

세 가지 모두 사용자 의도 ("admin 권한 옵션 ON 으로 변경") 와 정확히 일치. case 2 메뉴 체크 ON 표시 → 사용자 클릭 → config ON 토글 → 다음 부팅부터 의도된 admin 권한 동작.

**역방향** (case 4 → case 1) 도 검증: admin 권한 인스턴스 + `config.AdminElevation=true` 에서 메뉴 클릭 → config OFF → schtasks `LeastPrivilege` 재등록 → 안내 + 자동 종료 → 사용자 수동 재실행 → 일반 권한 (사용자 셸이 일반 권한이면 일반, admin 이면 admin 상속 — KoEnVue 통제 외, fix #3 §7.3 의 down-grade 한계 그대로 보존).

### 5.4 시각 회귀 검증

case 1/3/4 의 메뉴 체크 표시 = fix #3 와 동일. case 2 만 OFF → ON 변경. fix #3 에서 case 2 사용자 보고 없었던 이유 = 흔하지 않은 케이스 + 사용자가 admin Total Commander 등을 사용하는 경우에만 발생. fix #4 가 silent 충돌을 명시적으로 해결.

---

## 6. 학습

### 6.1 사용자 ultrathink 질문이 정확한 case 식별

사용자: "관리자 권한으로 실행된 Total Commander 등에서 KoEnVue를 실행할 경우, 관리자 권한 옵션과 상관없이 '관리자 권한으로 실행' 항목에 체크가 되어 있어야 하지 않을까?"

이 질문은 4-case 매트릭스의 case 2 를 정확히 식별. fix #2/#3 박제 시 4-case 매트릭스가 트레이 토글 동작 분기 중심이라 메뉴 체크 표시는 명시적 분석 누락. 사용자 질문이 누락 발견의 trigger — Windows token 모델 한계 분석 + 외부 환경 영향 식별의 한 단계.

### 6.2 partial class 의 import 가 file 별 분리

fix #3 가 `Tray.cs` 에서 `using KoEnVue.App.Bootstrap;` 제거 → 같은 partial class 의 `Tray.Menu.cs` 에서 `AdminElevation` 사용 시 별도 import 재추가 필요. C# 컴파일 단위 = file 이라 partial class 의 다른 file 은 namespace 별도 import. import 위치 = 함수가 호출하는 file. fix #4 는 `Tray.Menu.cs` 에 추가.

### 6.3 메뉴 체크 OR 패턴은 admin 만 예외

다른 메뉴 항목 (Snap/Animation 등) 은 config 직접 반영. admin 만 외부 환경 영향 받는 유일 케이스라 OR 정당. fix #4 의 OR 패턴을 다른 메뉴 항목에 확산하지 않음 — admin 의 특수성 (부모 셸 토큰 상속) 한정. 비슷한 외부 환경 영향 항목 추가 시 (예: 향후 "관리자 모드 자동 감지" 신규 항목) 동일 OR 패턴 적용 검토.

### 6.4 AOT 페이지 경계 흡수

작은 변경 (메서드 호출 site 1 + 문자열 감소) 은 AOT 의 4 KB 페이지 경계 안에 흡수되어 net 바이너리 변화 0. fix #1 (+2,560) → fix #2 (+1,024) → fix #3 (-512) → fix #4 (±0) 시계열 — 작은 IL 변경은 페이지 경계 이내, 큰 변경은 페이지 경계 넘는 단위. AOT 바이너리 사이즈 변화 보고는 정직성 유지.

### 6.5 박제 직후 후속 fix 패턴

fix #3 박제 → 검증 → 사용자 후속 보고/제안 → fix #4 즉시 박제 패턴. 짧은 turn 안에 fix 적용 + 박제는 (a) 같은 영역의 누적 회귀 가시화 (fix #1/#2/#3/#4 시계열 박제), (b) 미래 PR 의 회귀 가드 (각 fix 의 채택 근거 + 트레이드오프 정직), (c) 사용자 mental model 진화 (fix #3 의 "트레이 토글 = 옵션 변경 / 부팅 self-elevation = 옵션 효력 발생" 책임 분담 명료화에 fix #4 가 "메뉴 체크 = 정직한 시각 노출" 추가) 확보.

---

## 7. 후속 후보 (선택적)

본 fix 는 self-contained — 후속 작업 없이 종결. 미래 진입 조건:

- 사용자 보고 — case 2 메뉴 체크 ON 을 본 사용자가 "왜 클릭해도 OFF 안 되는지" 혼란 → 안내 메시지에 case 2 명시 추가 (현재 메시지는 일반 케이스 안내만)
- 다른 메뉴 항목이 외부 환경 영향 받는 케이스 추가 (예: "관리자 모드 자동 감지" 신규 항목) → fix #4 의 OR 패턴 precedent 로 적용
- `IsCurrentProcessElevated()` 호출 빈도 측정 — 트레이 메뉴 빌더는 우클릭마다 호출. `OpenProcessToken` + `GetTokenInformation` 4 P/Invoke 가 매 호출 — cache 패턴 검토 (`Lazy<bool>` 등) 가 가치 있을 정도면 도입. 현재는 우클릭 빈도가 낮아 cache 비용 가치 0.

---

## 8. fix #1/#2/#3/#4 시계열 정리

| fix # | 날짜 | 영역 | 변경 핵심 | Binary |
|-------|------|------|----------|--------|
| #1 | 2026-05-28 | 트레이 토글 자동 spawn race | `KOENVUE_RELAUNCH_PARENT_PID` + `Process.WaitForExit(5000)` (자식이 부모 종료 명시 wait) | +2,560 bytes |
| #2 | 2026-05-28 | admin → 일반 down-grade | `isDowngrade` 분기 + `MB_OK` 안내 + `break` (case 4 자동 spawn skip) | +1,024 bytes |
| #3 | 2026-05-29 | 4 case 통일 흐름 | 4 단계 단일 흐름 (config 변경 + schtasks 재등록 + `MB_OK` 안내 + `WM_CLOSE`) — fix #1 의 인프라 (`ClearReentryGuard` + `SetRelaunchParentPidForTrayRestart`) 자연 제거 | -512 bytes |
| **#4** | **2026-05-29** | **메뉴 체크 OR + 메시지 단순화** | **`bool isAdminEffective = config.AdminElevation \|\| AdminElevation.IsCurrentProcessElevated()` OR 로직 + 메시지 "다음 실행부터" redundant 제거** | **±0 bytes** |

누적 변화: +2,560 + 1,024 - 512 + 0 = +3,072 bytes. v0.9.4.0 base (4,861,440) + 3,584 = 4,865,024 (fix #1/#2 max) → -512 = 4,864,512 (fix #3/#4).

각 fix 의 dev-note + improvement-plan §7.1/7.2/7.3/7.4 시계열 박제 — 미래 PR 의 회귀 분석 시 컨텍스트 보존.
