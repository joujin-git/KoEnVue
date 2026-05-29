# 2026-05-29 — PR-15 후속 fix #5: 트레이 메뉴 라벨 — case 2 전용 `Config = User` hint suffix

**관련**: [2026-05-29-pr-15-tray-menu-or-logic.md](2026-05-29-pr-15-tray-menu-or-logic.md) (직전 fix #4 — 메뉴 체크 OR 로직) · [2026-05-29-pr-15-tray-toggle-unified.md](2026-05-29-pr-15-tray-toggle-unified.md) (fix #3 — 4 case 통일 흐름) · [2026-05-28-pr-15-admin-downgrade.md](2026-05-28-pr-15-admin-downgrade.md) (fix #2 — admin → 일반 down-grade 분기) · [2026-05-28-pr-15-relaunch-race.md](2026-05-28-pr-15-relaunch-race.md) (fix #1 — 트레이 토글 mutex race) · [2026-05-27-admin-elevation.md](2026-05-27-admin-elevation.md) (PR-15 본 PR 진단) · [improvement-plan/PR-15-admin-elevation.md §7.5](../improvement-plan/PR-15-admin-elevation.md)

**Status**: 코드 + 검증 완료 (0 warn / 0 error AOT publish, 65/65 PASS, reviewer 통과)
**범위**: 2 파일 — `App/Localization/I18n.cs` (+18 lines: enum + _table + public surface property + doc comment 8줄) / `App/UI/Tray.Menu.cs` (+12/-2 lines: case 2 검출 + 라벨 동적 선택 + doc comment 7줄)
**Binary 영향**: 4,864,512 → 4,865,024 bytes (**+512 bytes**; 페이지 경계 흡수 없음 — fix #4 의 ±0 (페이지 흡수) 후 fix #5 는 페이지 경계 초과)
**SHA256**: `417A877C66560F6861D6AA1408BD0CE1D2FC29B3F0B0D42FB1F509DB295C024F`

---

## 1. 사용자 보고/제안 시계열

fix #4 (메뉴 체크 OR 로직 + 안내 메시지 단순화) 박제 직후 사용자 추가 시계열:

### 1.1 사용자 질문 (시계열 시작)

> "관리자 권한 total commander 등에서 KoEnVue 실행 시 **설정값도 같이 알 수 있으면 제일 좋을 것 같아**"

질문 분석:

- fix #4 의 OR 로직 = **실 권한** 시각 노출 (case 2 의 admin 토큰 상속을 메뉴 체크 ON 으로 표시)
- 그러나 같은 case 2 의 **`config.AdminElevation` 값** (사용자 의도 = User) 은 여전히 invisible
- 사용자가 admin Total Commander 에서 KoEnVue 실행 후 메뉴 봤을 때 체크 ON 만 보고 "옵션이 ON 인지 외부 환경 영향인지" 구분 불가 — fix #4 의 시각 노출 정직성을 한 단계 강화 필요

### 1.2 ultrathink 옵션 비교 (자동 동기화 검토 + 거절)

옵션 A (검토): case 2 진입 시 `config.AdminElevation` 을 자동 true 저장 — 시각 충돌 자동 해결 + disk 동기화로 다음 부팅도 admin 자동 시작.

ultrathink 분석 (시나리오 매트릭스):

#### 시나리오 1 — 의도된 자동화 (사용자 환경과 정합)

```
1. 사용자가 admin Total Commander 상시 사용 (admin 콘솔 사용자 전형 패턴)
2. KoEnVue 도 항상 admin 으로 시작하길 원함
3. 자동 동기화 → config.AdminElevation=true 자동 저장 → schtasks HighestAvailable 재등록
4. 다음 부팅부터 자동 admin 시작 = 사용자 의도와 일치
```

→ 자동 동기화가 사용자 의도와 일치. OK.

#### 시나리오 2 — 일회성 admin 환경 사용 (가장 흔한 케이스, 부작용 큼)

```
1. 평소 일반 권한 사용자가 admin Total Commander 평소 부수적 사용
   (개발자/IT 사용자의 흔한 부수적 사용 패턴 — 평소 일반 권한 explorer, 특정 작업 시만 admin commander)
2. KoEnVue 를 admin commander 에서 한 번 실행
   (다른 작업 중 부차적 — UIPI 우회 의도 아닐 수 있음 — 그냥 평소처럼 단축키 입력)
3. KoEnVue 자식 admin 권한 시작 (case 2 진입)
4. [자동 동기화 채택 시] config.AdminElevation 자동 true 저장
   - 사용자 명시 의도 0 (UI 토글 안 함)
   - StartupTaskManager.ReregisterIfAdminChanged 호출 → schtasks <RunLevel>HighestAvailable</RunLevel> 재등록
5. 사용자 KoEnVue 종료
6. 다음 부팅 = schtasks 자동 시작 = HighestAvailable = UAC 0 admin 자동 진입
   - 사용자가 평소 사용하던 일반 권한 explorer 부팅과 비대칭
   - **사용자 명시 의도 0 의 상태가 부팅 환경 영구화** → 사용자 인지 없이 환경 변경
7. 사용자 인지 시점 = 다음 부팅 후 한참 후 "왜 KoEnVue 가 admin 으로 시작하지?"
   - schtasks 직접 확인 안 하면 영구 의문 → 사용자 신뢰 손상
```

→ schtasks 차원 부작용 = 자동 동기화의 본질적 위험. **거절**.

#### 시나리오 3 — 사용자 명시 토글 직후 외부 spawn (의도 reset 패턴)

```
1. 사용자가 명시적으로 트레이 메뉴 "관리자 권한으로 실행" 토글 OFF (config.AdminElevation=false 의도)
   - 메시지 안내 + 자동 종료 (fix #3 흐름)
   - 사용자 의도 명시: "User 모드로 사용"
2. 사용자 수동 재실행 (KoEnVue 가 일반 권한 시작)
3. 일정 시간 후 사용자가 admin Total Commander 에서 KoEnVue 재실행 (다른 작업 부수)
   - case 2 진입 (config=false + 실 admin)
4. [자동 동기화 채택 시] config.AdminElevation 자동 true 저장
   - **직전 사용자 명시 의도 (User) 가 case 2 진입 한 번에 reset (Admin)**
   - 사용자 명시 토글 → 외부 spawn 한 번 → 의도 reset = 신뢰 손상 패턴
5. 다음 부팅 = 자동 admin (시나리오 2 와 동일)
```

→ 사용자 명시 의도 무시 패턴. KoEnVue 의 "사용자 명시 의도 100% 존중" 원칙 정면 충돌. **거절 결론 강화**.

#### 자동 동기화 거절 결론

시나리오 2/3 = **자동 동기화의 본질적 부작용**. schtasks 차원 (부팅 환경 영구 변경) 까지 영향이 확산되는 비대칭 자동화. KoEnVue 의 자동화 정책 = "사용자 명시 의도 100% 존중" 과 정면 충돌. 채택 거절 결론 정직.

옵션 B (채택): case 2 만 라벨 hint suffix 노출 — 자동 동기화 0.

- 시각 정직성: case 2 의 실 권한 (체크 ON, fix #4 OR) + config 값 (라벨 hint, fix #5) **두 신호 모두 visible**
- 자동 동기화 0: `config.AdminElevation` disk 영향 0, schtasks 재등록 0, 사용자 명시 의도 100% 존중
- 메뉴 항목 추가 0: 라벨 동적 분기만 — 항목 수 변경 없음 → 메뉴 layout 회귀 0
- case 2 한정 (case 1/3/4 = 기본 라벨): 외부 환경 영향 시각화만, 사용자 명시 의도 케이스는 noise 회피

### 1.3 사용자 직접 제안 — 라벨 표기

> "관리자 권한으로 실행 (Config = User) 정도로 표시"

채택. case 2 한정 노출 + 자동 동기화 0 + 메뉴 layout 회귀 0 + 사용자 결정 100% 존중.

### 1.4 사용자 후속 정정 — 괄호 → 쉼표

> "관리자 권한으로 실행, Config = User"

영문 동등 정정:

- 기존 reviewer 시점: "Run as administrator (Config = User)" (괄호)
- 사용자 정정: "Run as administrator, Config = User" (쉼표 + 공백)

채택 — 사용자 결정 그대로 박제. **본 dev-note 의 모든 라벨 인용은 사용자 정정 후 표기 사용**.

정정 근거 (사용자 명시 추가 발화 없음, 박제 시 정직 추론):

- 메인 동사구 + suffix hint 흐름이 자연스러움 — "관리자 권한으로 실행" 마침표 후 "Config = User" 보다 쉼표 + 공백 이은 표기가 한 문장 흐름
- 한국어/영문 모두 동등한 부속 절 표기 — 영어 "Run as administrator, Config = User" 도 부속 한정사로 더 자연스러운 패턴

---

## 2. 4-case 매트릭스 fix #4 → fix #5 변화

### 2.1 fix #4 시점 (체크 OR 만)

| # | `config.AdminElevation` | `IsCurrentProcessElevated()` | 체크 | 라벨 |
|---|---|---|---|------|
| 1 | `false` | `false` | OFF | "관리자 권한으로 실행" |
| **2** | **`false`** | **`true`** | **ON ✓ (OR)** | "관리자 권한으로 실행" |
| 3 | `true` | `false` | ON | "관리자 권한으로 실행" |
| 4 | `true` | `true` | ON | "관리자 권한으로 실행" |

case 2 의 시각 충돌: 실 권한 (admin) 은 체크로 visible, config 값 (User) 은 invisible.

### 2.2 fix #5 시점 (체크 OR + 라벨 hint)

| # | `config.AdminElevation` | `IsCurrentProcessElevated()` | 체크 | **라벨** |
|---|---|---|---|------|
| 1 | `false` | `false` | OFF | "관리자 권한으로 실행" |
| **2** | **`false`** | **`true`** | **ON ✓ (OR)** | **"관리자 권한으로 실행, Config = User"** |
| 3 | `true` | `false` | ON | "관리자 권한으로 실행" |
| 4 | `true` | `true` | ON | "관리자 권한으로 실행" |

case 2 만 라벨 분기. case 1/3/4 = 기본 라벨 — 의도된 noise 회피.

### 2.3 case 1/3/4 라벨 hint 비노출 근거

- **case 1** = `config=false` + 일반 권한. 일관 OFF 상태 — config 값 = 실 권한 일치. hint 추가 시 정보 가치 0 + 길이 누적 노이즈.
- **case 3** = `config=true` + 일반 권한. 사용자 명시 토글 결과 (Admin 의도, 다음 실행 self-elevate 대기). hint "Config = Admin" 형태로 노출 시 사용자가 이미 config 에 명시 반영한 상태인데도 시각 노이즈 추가 = 사용자 의도 확인 redundant.
- **case 4** = `config=true` + admin 권한. 일관 ON 상태 — config 값 = 실 권한 일치. hint 추가 시 case 1 과 동일 노이즈.
- **case 2** = `config=false` + admin 권한. **외부 환경 영향** = 사용자 명시 의도 (`User`) 와 실 권한 (`admin`) 비대칭. fix #5 hint 의 정확한 대상 케이스.

→ fix #5 = **외부 환경 영향 시각화 한정**. 사용자 명시 의도 케이스는 hint 비노출 (noise 회피).

---

## 3. ko/en 영문 mix 정당성 (P2 정합 검증)

### 3.1 P2 정합 분석

P2 = "UI 텍스트 한국어 디폴트 + 영문 fallback". 본 라벨의 메인 동사구 "관리자 권한으로 실행" 그대로 한국어 — P2 정합.

영문 suffix "Config = User" mix 정당화:

#### (a) IT 통용어 — Windows 표준 어휘

- "Config" / "User" = Windows admin/User account, config file 어휘
- 한국어 번역 시 "설정값" / "사용자" — 길이/명확성 trade-off:
  - 한국어 직역: "관리자 권한으로 실행, 설정값 = 사용자"
  - 영문 mix: "관리자 권한으로 실행, Config = User"
  - 영문 mix 가 4 글자 짧음 (메뉴 한 줄 부담 감소) + 변수명/값 직관 명시

#### (b) KoEnVue 의 주 사용자 (admin 콘솔 사용자) 친화

- KoEnVue 의 주 사용 케이스 = 관리자 콘솔의 한/영 IME 표시 (UIPI 우회) — 사용자는 admin/User 권한 차이를 자주 인지하는 IT 사용자
- "Config" / "User" 영문 어휘 = 즉시 파악 가능 (한국어 번역보다 직설적)

#### (c) 직설성 — 변수명 = 값 직관

- "Config = User" = "변수명 = 값" 형식 (코드/설정 파일 어휘 직관)
- "설정값 = 사용자" 보다 변수명/값 직관이 명시적 — 사용자가 즉시 "이게 코드/설정 변수의 값" 인지

#### (d) 길이 trade-off 균형

- 메인 메뉴 한 줄 라벨에 hint 추가 시 길이 누적 부담
- 영문 mix = 짧음 + 정보 손실 0 = 균형

### 3.2 다른 한국어 우선 영역과의 정합

KoEnVue 의 다른 ko/en 영문 mix 케이스:

- 트레이 메뉴 헤더: "KoEnVue v{버전} — 다운로드" (브랜드 + 행위 단어 한국어, 버전 숫자 그대로)
- 트레이 툴팁: "한글" / "영문" / "비한글" (한국어, 영문 mix 0)
- 메뉴 항목: "관리자 권한으로 실행" (한국어, 영문 mix 0 — fix #5 이전)
- **fix #5**: "관리자 권한으로 실행, Config = User" — **외부 환경 영향 시각화 한정 케이스 영문 mix 도입**

비교: 트레이 툴팁 / 메뉴 메인 = 한국어 일관 (사용자 모든 환경 공통 노출), case 2 hint = 한정 노출 케이스 + IT 통용어 정당 = 영문 mix 허용 영역 명시.

미래 재검토 트리거:

- 사용자 보고 — "Config = User" 영문 mix 가 한국어 일관성 깨뜨림 → 한국어 직역 "설정값 = 사용자" 로 변경
- 다른 case (예: case 3 의 hint 도입) 가 동일 영문 mix 패턴 확산 시 정책 명시 (`docs/conventions.md` P2 sub-rule 추가)

본 fix 시점 = 사용자 직접 표현 100% 존중 + IT 통용어 정당 + 길이 trade-off 균형 = 영문 mix 허용.

---

## 4. 구현

### 4.1 [`App/Localization/I18n.cs`](../../App/Localization/I18n.cs) — 신규 i18n 키 (+18 lines)

```diff
 // 관리자 권한 (admin_elevation 옵션 — UIPI / admin 콘솔 IME)
 MenuAdminElevation, MenuAdminElevationTooltip,
 AdminElevationDeniedTitle, AdminElevationDeniedMessage,
 AdminElevationChangeNotice,
+MenuAdminElevationExternal,
 }

 ...

+// case 2 전용 라벨 (PR-15 후속 fix #5, 2026-05-29) — config.AdminElevation=false 인데
+// IsCurrentProcessElevated()=true (admin 환경 외부 spawn — admin Total Commander 등)
+// 케이스에서만 메뉴 라벨에 "(Config = User)" suffix 노출. fix #4 의 메뉴 체크 OR 로직과
+// 함께 — 체크 ON (실 권한) + 라벨 hint (config 값) → 사용자가 두 신호 모두 인지.
+[I18nKey.MenuAdminElevationExternal] = (
+    "관리자 권한으로 실행, Config = User",
+    "Run as administrator, Config = User"),

 ...

+/// <summary>
+/// 트레이 메뉴 라벨 — case 2 (config.AdminElevation=false + IsCurrentProcessElevated()=true,
+/// admin 환경 외부 spawn) 전용 (PR-15 후속 fix #5, 2026-05-29). fix #4 의 메뉴 체크 OR 로직과
+/// 함께 작동 — 체크 ON (실 권한 admin) + 라벨 hint "(Config = User)" (config 값 = false) → 두
+/// 신호 모두 사용자에게 visible. case 1/3/4 는 기본 <see cref="MenuAdminElevation"/> 사용.
+/// "Config" / "User" 영문 mix 의도 — IT 통용어 (Windows 표준 어휘) + KoEnVue 의 주 사용자
+/// (admin 콘솔 사용자) 친화 + 직설성 + 길이 trade-off 균형.
+/// </summary>
+public static string MenuAdminElevationExternal => Get(I18nKey.MenuAdminElevationExternal);
```

I18n 의 3-spot 패턴 (enum + _table + public surface) 정합. public surface 누적 = fix #4 의 41 → fix #5 의 42 속성.

### 4.2 [`App/UI/Tray.Menu.cs`](../../App/UI/Tray.Menu.cs) — case 2 검출 + 라벨 동적 선택 (+12/-2 lines)

```diff
 //
+// 라벨 hint (PR-15 후속 fix #5, 2026-05-29) — case 2 (config=false + IsElevated=true,
+// admin 환경 외부 spawn) 전용 "(Config = User)" suffix 노출. fix #4 OR 의 visible 정합 +
+// case 2 의 config 값 명시 = 사용자가 두 신호 (실 권한 + config 값) 모두 인지. case 1/3/4
+// 는 기본 라벨 — case 1 일관 OFF / case 3 사용자 명시 토글 결과 / case 4 일관 ON.
-bool isAdminEffective = config.AdminElevation || AdminElevation.IsCurrentProcessElevated();
+bool isCurrentlyElevated = AdminElevation.IsCurrentProcessElevated();
+bool isAdminEffective = config.AdminElevation || isCurrentlyElevated;
+bool isExternalElevation = !config.AdminElevation && isCurrentlyElevated;  // case 2
+string adminElevationLabel = isExternalElevation
+    ? I18n.MenuAdminElevationExternal
+    : I18n.MenuAdminElevation;
 uint adminElevationFlags = isAdminEffective ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
-User32.AppendMenuW(hMenu, adminElevationFlags, (nuint)IDM_ADMIN_ELEVATION, I18n.MenuAdminElevation);
+User32.AppendMenuW(hMenu, adminElevationFlags, (nuint)IDM_ADMIN_ELEVATION, adminElevationLabel);
```

**fix #4 OR 로직 보존**: `isAdminEffective` 한 줄 변경 0 — 체크 표시는 두 신호 OR 그대로, 라벨만 case 2 hint 단독.

**P/Invoke 절약**: `isCurrentlyElevated` 변수 분리 → `IsCurrentProcessElevated()` 호출 1회로 두 분기 (`isAdminEffective` + `isExternalElevation`) 공유. 우클릭마다 `OpenProcessToken` + `GetTokenInformation` 4 P/Invoke 1회.

### 4.3 변경 매트릭스

| 파일 | 변경 | LOC | 부수 효과 |
|------|------|----|----------|
| `App/Localization/I18n.cs` | 신규 i18n 키 `MenuAdminElevationExternal` (3-spot) + doc comment 8줄 | +18 / -0 | public surface 41 → 42 |
| `App/UI/Tray.Menu.cs` | `isCurrentlyElevated` 분리 + `isExternalElevation` 분기 + `adminElevationLabel` 동적 선택 + doc comment 7줄 | +12 / -2 | fix #4 OR 로직 한 줄 변경 0 |

---

## 5. 검증

### 5.1 빌드 / 테스트

```
dotnet build           → 0 warn / 0 error
dotnet publish -r win-x64 -c Release   → 0 warn / 0 error, 4,865,024 bytes (fix #4 4,864,512 → +512)
dotnet test            → 65/65 PASS
```

SHA256: `417A877C66560F6861D6AA1408BD0CE1D2FC29B3F0B0D42FB1F509DB295C024F`.

### 5.2 AOT 페이지 흡수 분석 (+512 bytes, 페이지 경계 초과)

fix #4 → fix #5 의 IL 변화:

- (+) `I18n.cs` 신규 i18n 키 `MenuAdminElevationExternal`:
  - enum 항목 1 (~4 bytes IL)
  - `_table` Dictionary 항목 1 — ko/en 문자열 2개 (UTF-16): "관리자 권한으로 실행, Config = User" (17 글자 × 2 = 34 bytes) + "Run as administrator, Config = User" (35 글자 × 2 = 70 bytes) = **104 bytes**
  - public surface property + Get(I18nKey.X) 호출 ~16 bytes IL
  - doc comment 8줄 (IL 영향 0 — 메타데이터)
- (+) `Tray.Menu.cs` 분기 로직:
  - `isCurrentlyElevated` 변수 분리 + `isExternalElevation` AND 분기 + `adminElevationLabel` 삼항 ~30 bytes IL
  - doc comment 7줄 (IL 영향 0)
- 합계: ~150 bytes 직접 IL + 메타데이터 (i18n 키 reflection 메타) → **AOT 4 KB 페이지 경계 초과 → 다음 페이지 할당 = +512 bytes**

fix #4 의 페이지 흡수 (±0) → fix #5 의 페이지 초과 (+512) 정직 박제 보고. 작은 IL 변경이라도 페이지 경계 부근에서 변경되면 +512 bytes 단위로 jump.

### 5.3 시계열 박제 (binary 누적)

| fix # | 날짜 | 영역 | 변경 핵심 | Binary 변화 | 누적 |
|-------|------|------|----------|------------|------|
| #1 | 2026-05-28 | 트레이 토글 자동 spawn race | `KOENVUE_RELAUNCH_PARENT_PID` + `Process.WaitForExit(5000)` | +2,560 | +2,560 |
| #2 | 2026-05-28 | admin → 일반 down-grade | `isDowngrade` 분기 + `MB_OK` 안내 + `break` | +1,024 | +3,584 |
| #3 | 2026-05-29 | 4 case 통일 흐름 | 4 단계 단일 흐름 + 메서드 2개 제거 | -512 | +3,072 |
| #4 | 2026-05-29 | 메뉴 체크 OR + 메시지 단순화 | `isAdminEffective = config OR IsElevated` + 메시지 축약 | ±0 (페이지 흡수) | +3,072 |
| **#5** | **2026-05-29** | **case 2 전용 라벨 hint** | **`isExternalElevation` 분기 + `adminElevationLabel` 동적 선택 + 신규 i18n 키** | **+512 (페이지 경계 초과)** | **+3,584** |

v0.9.4.0 base 4,861,440 + 3,584 = 4,865,024 = fix #5 = fix #1/#2 max 와 동일 (-512 + 512 균형).

### 5.4 라벨 표시 검증 (4-case)

```
case 1 (config=false + 일반): 체크 OFF + 라벨 "관리자 권한으로 실행" ✓
case 2 (config=false + admin, 외부 spawn): 체크 ON + 라벨 "관리자 권한으로 실행, Config = User" ✓ (신규)
case 3 (config=true + 일반): 체크 ON + 라벨 "관리자 권한으로 실행" ✓
case 4 (config=true + admin): 체크 ON + 라벨 "관리자 권한으로 실행" ✓
```

case 2 만 라벨 분기 — case 1/3/4 = 기본 라벨 (사용자 명시 의도 케이스, noise 회피).

### 5.5 토글 의미 보존 검증

fix #4 의 OR 로직 (`isAdminEffective`) 한 줄 변경 0 → 체크 표시 메커니즘 그대로. 토글 클릭 동작도 변경 0 (fix #3 의 4 단계 단일 흐름 그대로):

1. `config.AdminElevation` 토글
2. `updateConfig(newAdminConfig)` — config.json 디스크 저장
3. `StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig)` — schtasks `<RunLevel>` 재등록
4. `MessageBoxW(I18n.AdminElevationChangeNotice, MB_OK)` — 안내
5. `PostMessageW(WM_CLOSE)` — 자동 종료

사용자 수동 재실행 시 새 옵션 적용 (Windows token 모델 한계 + 부모 셸 토큰 상속 + schtasks `<RunLevel>` 자동 재등록 = 종합 동작 fix #3 그대로 보존).

case 2 사용자가 라벨 hint "Config = User" 본 후 메뉴 클릭 시 동작:

- `config.AdminElevation` 토글 `false → true`
- 안내 + 자동 종료
- 사용자 수동 재실행:
  - admin Total Commander 에서 다시 실행 → admin 토큰 상속 → `IsCurrentProcessElevated()=true` → `TryRelaunchAsAdmin` noop (skip) → admin 동작 그대로 + 다음부터는 case 4 (`config=true` + admin)
  - 일반 권한 셸 (탐색기) 에서 실행 → `config.AdminElevation=true` → `TryRelaunchAsAdmin` 호출 → UAC 1회 → admin 자식 spawn
  - 시작 프로그램 (schtasks) → `<RunLevel>HighestAvailable</RunLevel>` 자동 재등록됐으므로 부팅마다 UAC 0 admin 자동 시작

세 가지 모두 사용자 의도 ("Config = User 봤지만 admin 의도로 변경") 와 정확히 일치.

---

## 6. 학습

### 6.1 사용자 직접 제안 + ultrathink 자동화 거절의 정합

사용자가 자동 동기화를 명시 요청하지 않은 상태에서, KoEnVue 가 자동 동기화를 "선의의 자동화" 로 도입할 유혹 존재 (시각 충돌 해결의 가장 자연스러운 방법으로 보임). ultrathink 시나리오 매트릭스 분석으로 시나리오 2/3 의 schtasks 차원 부작용 식별 → 거절 결정 → 사용자에게 옵션 비교 제시 → 사용자 직접 제안 (라벨 hint) 채택.

학습 패턴:

- **시각 충돌 = 자동 동기화의 trigger 처럼 보이지만**, schtasks 차원 (disk 저장 + 부팅 환경 변경) 까지 자동화 영향이 확산되면 **본질적 부작용**
- **자동화 거절 + 시각화 채택 = 사용자 명시 의도 100% 존중**
- **사용자 결정 100% 존중 = 시계열 정직 박제로 미래 자동화 재검토 시 트리거 보존** (사용자 보고 누적 → "수동 토글 마찰" 측정 가능 시 재평가)

### 6.2 case 한정 시각화 (외부 환경 영향 한정)

fix #5 의 라벨 hint = case 2 만 노출. case 1/3/4 = 기본 라벨.

학습:

- **외부 환경 영향 시각화 = case 한정** (사용자 명시 의도 케이스는 noise 회피)
- 다른 메뉴 항목 (Snap/Animation 등) 도 외부 환경 영향 받지 않으므로 OR 패턴/라벨 hint 패턴 확산 0 (fix #4 학습 정합)
- 미래 외부 환경 영향 항목 추가 시 (예: "관리자 모드 자동 감지" 신규 항목) 동일 case 한정 시각화 precedent 로 적용

### 6.3 ko/en 영문 mix 정당화 — case 한정 + IT 통용어

P2 = "UI 한국어 디폴트 + 영문 fallback". 본 fix 의 라벨 영문 mix 정당화:

- **메인 동사구 한국어 유지** ("관리자 권한으로 실행") = P2 정합
- **suffix 영문 mix** ("Config = User") = case 한정 + IT 통용어 + 사용자 직접 표현 + 길이 trade-off

학습:

- **한국어 우선 + 영문 mix 허용 영역 = case 한정 + IT 통용어** = 패턴 명시
- 다른 한국어 영역 (트레이 툴팁/메뉴 메인) = 한국어 일관, 사용자 모든 환경 공통 노출
- 미래 재검토 트리거: 사용자 보고 + 정책 명시 (`docs/conventions.md` P2 sub-rule)

### 6.4 라벨 표기 정정 시계열 — 사용자 직접 결정 존중

reviewer 시점 라벨 = "(Config = User)" (괄호) → 사용자 정정 = ", Config = User" (쉼표 + 공백).

학습:

- **사용자 결정 100% 존중** = 사용자가 직접 표현 정정 시 코드/문서/박제 모두 일관 갱신
- 박제 시 시계열 정직 (정정 전/후 명시) = 미래 박제 시 일관성 검증 패턴

### 6.5 AOT 페이지 경계 초과 정직 보고 (+512 bytes)

fix #4 의 ±0 (페이지 흡수) → fix #5 의 +512 (페이지 경계 초과) 정직 박제. 작은 IL 변경이라도 페이지 경계 부근에서 변경되면 +512 bytes 단위로 jump.

학습:

- **fix #1~#5 누적 binary 변화 정직 박제** = 미래 PR 의 binary diff 추적 baseline
- ±0 (페이지 흡수) vs +512 (페이지 경계) 의 정확한 분류 = AOT 사이즈 추적의 정확성

### 6.6 박제 직후 후속 fix 패턴 — 누적 5단계

fix #4 박제 → 검증 → 사용자 후속 보고/제안 → fix #5 즉시 박제. fix #1/#2/#3/#4/#5 시계열:

- fix #1 = 자동 spawn race 차단 (인프라 추가)
- fix #2 = admin → 일반 down-grade 분기 (case 별 분기)
- fix #3 = 4 case 통일 흐름 (mental model 단순화 + 인프라 자연 제거)
- fix #4 = 메뉴 체크 OR (정직한 시각 노출 — 실 권한)
- **fix #5 = case 2 라벨 hint (정직한 시각 노출 — config 값) + 자동 동기화 거절 (사용자 명시 의도 존중)**

학습:

- 짧은 turn 안에 fix 적용 + 박제 = (a) 같은 영역의 누적 회귀 가시화, (b) 미래 PR 의 회귀 가드 (각 fix 의 채택 근거 + 트레이드오프 정직), (c) 사용자 mental model 진화 박제
- fix #5 의 사용자 mental model 진화 = "메뉴 = 정직한 시각 노출 (실 권한 + config 값) + 자동화 = 사용자 명시 의도 100% 존중"

---

## 7. 후속 후보 (선택적)

본 fix 는 self-contained — 후속 작업 없이 종결. 미래 진입 조건:

- **자동 동기화 재평가**: 사용자 보고 — case 2 사용자가 "매번 라벨 봤지만 수동 토글이 번거롭다" 마찰 측정 가능 시 시나리오 2/3 부작용 trade-off 재평가 (자동 동기화 + opt-out config 키 추가 등). 본 fix 시점 = 사용자 명시 의도 100% 존중 우선.
- **다른 외부 환경 영향 항목 추가**: 예: "Sandbox 모드 자동 감지" 신규 항목 추가 시 fix #4/#5 의 case 한정 시각화 (OR + 라벨 hint) precedent 로 적용. 영문 mix 도 동일 패턴 (IT 통용어 + 사용자 직접 표현 + 길이 trade-off).
- **`IsCurrentProcessElevated()` 호출 빈도 측정**: 트레이 메뉴 빌더는 우클릭마다 호출. `OpenProcessToken` + `GetTokenInformation` 4 P/Invoke 가 매 호출. fix #5 가 `isCurrentlyElevated` 변수 분리로 한 우클릭 안에서는 1회 (두 분기 공유) — cache 추가 ROI 작음.
- **`docs/conventions.md` P2 sub-rule 추가**: ko/en 영문 mix 허용 영역 명시 (case 한정 + IT 통용어 + 사용자 직접 표현 패턴). 미래 다른 외부 환경 영향 항목 추가 시 패턴 일관성 가드.

---

## 8. fix #1~#5 시계열 정리

| fix # | 날짜 | 영역 | 변경 핵심 | Binary | 누적 | I18n 키 변화 |
|-------|------|------|----------|--------|------|-------------|
| #1 | 2026-05-28 | 트레이 토글 자동 spawn race | `KOENVUE_RELAUNCH_PARENT_PID` + `Process.WaitForExit(5000)` | +2,560 | +2,560 | 0 |
| #2 | 2026-05-28 | admin → 일반 down-grade | `isDowngrade` 분기 + `MB_OK` 안내 + `break` | +1,024 | +3,584 | +1 (`AdminElevationDowngradeNotice`) |
| #3 | 2026-05-29 | 4 case 통일 흐름 | 4 단계 단일 흐름 + 메서드 2개 제거 | -512 | +3,072 | -1 (-2 `AdminElevationRestartPrompt` + `AdminElevationDowngradeNotice` + +1 `AdminElevationChangeNotice`) |
| #4 | 2026-05-29 | 메뉴 체크 OR + 메시지 단순화 | `isAdminEffective = config OR IsElevated` + 메시지 축약 | ±0 (페이지 흡수) | +3,072 | 0 (메시지 단순화만) |
| **#5** | **2026-05-29** | **case 2 전용 라벨 hint** | **`isExternalElevation` 분기 + `adminElevationLabel` 동적 선택 + 신규 i18n 키 + 자동 동기화 ultrathink 거절** | **+512** | **+3,584** | **+1 (`MenuAdminElevationExternal`)** |

v0.9.4.0 base 4,861,440 + 3,584 = 4,865,024.

I18n public surface 누적: PR-15 본 PR 4 키 → fix #2 +1 = 5 → fix #3 -1 = 4 → **fix #5 +1 = 5 키**. 전체 public surface 41 → 42 속성 (fix #5 net +1).

각 fix 의 dev-note + improvement-plan §7.1/7.2/7.3/7.4/7.5 시계열 박제 — 미래 PR 의 회귀 분석 시 컨텍스트 보존. fix #5 는 시각화 한정 + 자동 동기화 거절 = 사용자 명시 의도 존중 패턴의 정직 박제 baseline.
