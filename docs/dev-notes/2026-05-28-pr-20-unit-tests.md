# XML/Codec/Path 19 케이스 단위 테스트 + 가시성 완화 (2026-05-28, PR-20)

> **상태**: PR-20 완료 (3 commit 박음 + docs 동기화 + 박제 정정). 설계 박제: [docs/improvement-plan/PR-20.md](../improvement-plan/PR-20.md). 본 dev-note 는 commit 분할 회고 + 가시성 완화 결정 근거 + CI portable 패턴 + 환경 의존성 흡수 패턴 + 박제 가정 정정 4건.

## 무엇

3-commit (HEAD: `51ba261`) 으로 회귀 위험이 높은 세 함수 (`BuildStartupTaskXml` / `XmlEntityCodec` / `SanitizeLogPath`) 를 **현재 동작 박제** 목적의 단위 테스트로 가드. 회귀 발견-후-fix 가 아니라 v0.9.5.0 이후 누군가 (사용자 / Claude) 가 이 함수를 건드릴 때의 silent 회귀 차단이 목적.

| commit | 변경 | net LOC | PASS 추가 |
|--------|------|--------|----------|
| `24fdeae` (1/3) | `App/Startup/StartupTaskManager.cs` 가시성 `private → internal` + remarks 3줄 + [`StartupTaskXmlTests.cs`](../../tests/KoEnVue.Tests/Unit/StartupTaskXmlTests.cs) 신규 (60 LOC, 5 메서드) | +63 / -1 | +6 |
| `55ba31e` (2/3) | [`XmlEntityCodecTests.cs`](../../tests/KoEnVue.Tests/Unit/XmlEntityCodecTests.cs) 신규 (50 LOC, 5 메서드) | +50 | +9 |
| `51ba261` (3/3) | [`SanitizeLogPathTests.cs`](../../tests/KoEnVue.Tests/Unit/SanitizeLogPathTests.cs) 신규 (84 LOC, 6 메서드) | +84 | +10 |

게이트 결과:
- `dotnet build` (Debug) + `dotnet publish -r win-x64 -c Release` (AOT) 양쪽 0 warn / 0 error
- AOT publish 사이즈: **4,861,440 bytes** (PR-18 baseline 동일, ±0)
- 테스트: **40 → 65 PASS** (신규 25). 0 fail / 0 skip
- publish exe SHA256: `8484f89ba6e25b62e995ac31713c16e6391542061c86cd045ff568f0a0f252a4`
- P1-P6 invariant 22 항목 (13 기본 + 9 PR-20 신규) 모두 PASS

## 왜 (결정 1) — `BuildStartupTaskXml` 가시성 `private → internal` 완화

본 메서드 ([App/Startup/StartupTaskManager.cs:163](../../App/Startup/StartupTaskManager.cs#L163)) 는 schtasks 2.0 XML 의 단일 진실원 — PR-03 D 회귀 (LogonTrigger `<UserId>` 누락 → ANY-user trigger → admin 요구) 의 발생지이자 PR-15 RunLevel 분기 (`LeastPrivilege` vs `HighestAvailable`) 의 단일 책임. 두 회귀가 모두 같은 메서드에 박혀있어 단위 테스트로 박제하면 단일 grep + 단일 메서드로 회귀 차단 가능.

가시성 완화 비용/이득:

| 항목 | 비용 | 완화 |
|------|------|------|
| 캡슐화 약화 | App assembly 안에서 다른 모듈이 직접 호출 가능 | `BuildStartupTaskXml` 호출처는 `RegisterStartupTaskWithXml` 단일 — invariant grep 으로 보장 |
| 외부 노출 | Core / 다른 App 모듈에 새 노출 | `InternalsVisibleTo("KoEnVue.Tests")` 만 새로 본다 — 즉 tests 외 노출 0 |
| IL 사이즈 | C# 컴파일러 출력 IL 동일 | publish 사이즈 ±0 bytes 확인 |
| remarks 코멘트 부담 | 3줄 (정당화) | 미래 reviewer 가 가시성 완화 의도를 즉시 이해 |

대안 비교:

- 옵션 A — 가시성 유지 + reflection 으로 접근: NativeAOT + `JsonSerializerIsReflectionEnabledByDefault=false` 에서 `MethodInfo.Invoke` 가 안정성 떨어짐 + 분석기 위반 + 호출 비용
- 옵션 B — 가시성 유지 + 외부 wrapper static method 신설: wrapper 가 단일 진실원을 깨뜨림 — `RegisterStartupTaskWithXml` 가 직접 호출하는 길과 wrapper 통한 테스트 길이 두 갈래 = 회귀 차단 약화

가시성 완화가 가장 직접적 + 회귀 차단 가치 최대.

## 왜 (결정 2) — CI portable 패턴: `LogonTrigger.UserId` 박제

PR-03 D 회귀의 핵심은 `<UserId>` 가 비어있어 ANY-user trigger 로 해석되는 것. 박제는 "비어있지 않음" 이 본질이고, 실제 값 (예: `WINDOWS-PC\jin`) 은 환경 의존.

```csharp
// StartupTaskXmlTests.cs:29-42
public void LogonTrigger_UserId_NotEmpty_PR03_D_RegressionGuard()
{
    string xml = StartupTaskManager.BuildStartupTaskXml(@"C:\KoEnVue.exe", false);
    const string openTag = "<UserId>";
    const string closeTag = "</UserId>";
    int start = xml.IndexOf(openTag, System.StringComparison.Ordinal);
    int end = xml.IndexOf(closeTag, System.StringComparison.Ordinal);
    Assert.True(start > 0 && end > start, "UserId 태그 쌍이 존재해야 함");
    string userId = xml.Substring(start + openTag.Length, end - start - openTag.Length);
    Assert.NotEmpty(userId);
    Assert.Contains("\\", userId);
}
```

검증 강도 정당화:

- `Assert.NotEmpty(userId)` — 회귀 차단 본질 (비어있으면 ANY-user)
- `Assert.Contains("\\", userId)` — `domain\user` 형식 가드 (단순 username 만 emit 되면 SID lookup 실패 가능)
- `Environment.UserDomainName` 직접 매치 회피 — GitHub Actions runner 호스트명이 ASCII 가정 못 함 + 개발자별 호스트명 다양

대안 (CI runner 환경 검증) 의 위험: `Environment.UserDomainName` 이 환경에 따라 다른 형태 (`AzureAD\user@domain.com`, 컨테이너 환경의 SYSTEM 등) — 박제 실패 false positive 위험.

## 왜 (결정 3) — 환경 의존성 흡수 패턴: `SanitizeLogPath` ParentTraversal

PR-03 §B1 의 ParentTraversal 케이스는 `BaseDirectory` 의 깊이 (`E:\dev\KoEnVue\bin\Debug\net10.0-windows\` = 5 depth vs `C:\Program Files\KoEnVue\` = 2 depth) 에 따라 `..\..\..\evil.log` 가 정규화 후 `BaseDirectory` 하위로 떨어질 수도 있고 벗어날 수도 있음 — 환경 의존성이 큼.

```csharp
// SanitizeLogPathTests.cs:27-43
public void Reject_ParentTraversal_NormalizedAndChecked()
{
    string traversal = Path.Combine(PortablePath.BaseDirectory, @"..\..\..\evil.log");
    string result = PortablePath.SanitizeLogPath(traversal, out string? reason);
    if (PortablePath.IsUnderAllowedRoot(Path.GetFullPath(traversal)))
    {
        Assert.Null(reason);   // BaseDirectory 깊이가 얕아 ..\..\..\ 가 안에 머문 경우
    }
    else
    {
        Assert.NotNull(reason);
        Assert.Contains("outside allowed roots", reason);
        Assert.Equal(PortablePath.ResolveLogPath(), result);
    }
}
```

`IsUnderAllowedRoot` 분기로 환경 깊이 의존성을 흡수. 두 가능한 결과 모두 정당한 동작이라 분기 검증으로 박제 가능.

대안 비교:

- 옵션 A — `BaseDirectory` 모킹: `PortablePath` 가 정적 클래스 + readonly field 라 분리 + 재주입 인프라가 큼. 본 PR 범위 초과
- 옵션 B — 임시 디렉토리 (`Path.GetTempPath` + `..\..\..\` 충분히 깊은 경로) 만들기: 테스트 실행 부작용 + 정리 책임
- 옵션 C — `BaseDirectory` 깊이 가정 (>= 4) 박제: CI runner 환경에서 깨질 위험

옵션 C 의 환경 흡수가 가장 robust.

## 왜 (결정 4) — NUL 문자 InvalidChars 채택 정당화

박제 가정 (PR-20.md §2.4 case 8) 의 `"C:\\test|invalid<>chars*.log"` 사용은 .NET 의 `Path.GetFullPath` invalid char 정책이 .NET 8+ 에서 완화된 사실을 반영 못 함 — `|<>*` 같은 Windows 예약 문자가 `ArgumentException` 을 항상 던지지 않을 가능성 있음.

```csharp
// SanitizeLogPathTests.cs:74-83
public void Fallback_InvalidChars_NormalizationFails_ReportsReason()
{
    // NUL 문자 — Path.GetFullPath 가 ArgumentException 던지는 확실한 invalid char.
    string invalid = "C:\\test\0invalid.log";
    string result = PortablePath.SanitizeLogPath(invalid, out string? reason);
    Assert.NotNull(reason);
    Assert.Contains("could not be normalized", reason);
    Assert.Equal(PortablePath.ResolveLogPath(), result);
}
```

NUL (`\0`) 문자는 모든 BCL 버전에서 `Path.GetFullPath` 가 throw — string 종료자로 해석되어 path-as-C-string 변환 시점에서 차단. 박제 의도 ("normalization 실패 시 fallback + reason 리포트") 는 그대로 유지하면서 .NET 버전 의존성 0.

## 박제 가정 정정 4건

PR-20 design doc (planner 산출) 박제 가정 vs 실제 결과:

| 박제 가정 | 실제 결과 | 정정 |
|----------|----------|------|
| "test 40 → 59 PASS (19 신규)" | 실제 = **40 → 65 PASS (25 신규)** | xUnit 이 `[Theory]` 의 InlineData 를 개별 test 로 카운트 — 메서드 단위 19 ≠ PASS 25. PR-20.md §5.1 + §9 정정표 박음 |
| §2.2 case 5/6 검증식 raw `"` | 실제 결과는 `&quot;` — `XmlEntityCodec.Escape` 가 `"` 를 `&quot;` 로 변환 (XML 1.0 5 entity 의 일부) | 박제 가정 cosmetic 정정. 테스트 코드 (StartupTaskXmlTests.cs:50/58) 는 이미 `&quot;` 형태로 정확 |
| §2.4 case 8 InvalidChars = `\|<>*` | `\|<>*` 는 .NET 8+ 에서 throw 안 할 수 있음 — `Path.GetFullPath` invalid char 정책 완화 | NUL 문자 (`\0`) 로 변경. 테스트 코드 (SanitizeLogPathTests.cs:78) 는 이미 NUL 사용 |
| "InternalsVisibleTo 1줄 신규 추가" | PR-10 (de98e7b, 2026-05-21) 때 이미 추가 — KoEnVue.csproj:44 | planner 가정 틀림. commit 1 의 1줄 작업은 `BuildStartupTaskXml` 가시성 완화 (`private → internal`) — PR-20.md §9 박음 |

박제 정정 4건 모두 cosmetic / 카운트 — 코드 동작 변화 0, 박제 사실의 정확성만 회복.

## 3-commit 분할 회고

각 commit 의 의미와 분할 정당화:

### commit 1 — 가시성 변경과 검증 같이 박기

`private → internal` 1글자 변경 + remarks 3줄 + StartupTaskXmlTests 5 메서드 같이 commit. 가시성 변경의 의도 (단위 테스트 박제) 와 검증 (실제 6 PASS) 가 같은 commit 에 있어 reviewer 가 정당화를 분리 추적 불필요.

대안 (가시성만 먼저 + 테스트 다음 commit) 의 위험: 가시성 변경 commit 자체는 caller 없는 dead code 노출 — reviewer 가 "왜 internal 인가?" 의문 발생. 테스트 commit 시점에 가서야 답이 나옴.

### commit 2 — 가장 단순한 함수 먼저 (XmlEntityCodec)

`XmlEntityCodec.Escape/Unescape` 는 외부 의존성 0 + 순수 함수 + Core 모듈 — 가장 단순한 검증. 5 메서드 모두 single line `Assert.Equal` 패턴. internal class → `InternalsVisibleTo` 로 접근 (PR-10 인프라).

### commit 3 — 환경 의존성 흡수 패턴 박기 (SanitizeLogPath)

`SanitizeLogPath` 는 public 메서드라 가시성 변경 0 — 본 commit 의 가치는 환경 의존성 흡수 패턴 (`IsUnderAllowedRoot` 분기) + NUL 문자 invalid char 선택의 정당화. 6 메서드 (Fact 4 + Theory 2) 가 10 PASS — 가장 InlineData 효과 큰 commit.

## CI portable invariant (xUnit Theory 활용 패턴)

PR-20 의 5 Theory 사용:

| 위치 | InlineData 수 | 패턴 |
|------|--------------|------|
| StartupTaskXmlTests `RunLevel_DerivedFromAdminElevation` | 2 | (bool admin, string expected) 매트릭스 — RunLevel 분기 박제 |
| XmlEntityCodecTests `RoundTrip_Preserves` | 5 | (string input) 다양성 — empty / plain / mixed / 한글 / `&&&&&` |
| SanitizeLogPathTests `Reject_OutsideAllowedRoot_FallsBackAndReports` | 3 | (string requested) — System32 / UNC / OtherUser 3 거부 경로 |
| SanitizeLogPathTests `Fallback_NullOrEmpty_ReturnsResolveLogPath_NoReason` | 3 | (string? requested) — null / "" / "   " 빈 입력 |

Theory 활용 5건이 InlineData 13개 분리 → 메서드 단위 19 → PASS 25 (= 19 + InlineData 분리 추가 PASS - Theory 메서드 중복 카운트). xUnit 표준 패턴 — 같은 검증 로직의 입력 매트릭스 압축에 효과적.

## 후속 후보 (reviewer 식별)

본 PR 의 비범위지만 같은 패턴이 적용 가능한 표면:

1. **PR-15 §5 AdminElevation 4 케이스** (`IsEnvFlagSet` + `GetCurrentIntegrityLevel`) — PR-15 design doc 박제. 환경 의존성 (`Environment.GetEnvironmentVariable` + `Advapi32.GetCurrentProcessIntegrityLevelRid`) 흡수 패턴 필요
2. **`SyncStartupPathCore` integration 테스트** — schtasks.exe 실 호출 의존이라 mock 인프라 필요 — 본 PR 비범위 (보류 권고)
3. **`conventions.md` line 68 `ShellExecuteW.*runas` grep stale** — PR-15 의 `RunAsVerb = "runas"` const 추출 후 stale. 본 docs 동기화에서 `RunAsVerb` 직접 grep 으로 정정 (수정 완료)

## 학습

### 의도적 가시성 완화는 정당한 도구

P4 "하나의 구현만" + 단일 진실원 원칙은 가시성 완화와 충돌 없음 — 단일 진실원이 회귀 위험이 클수록 단위 테스트 박제 가치가 큼. `internal` + `InternalsVisibleTo` 는 외부 노출 0 을 유지하면서 회귀 차단 가치 확보하는 최적 패턴.

### 박제 가정과 실제의 격차는 정직하게 명시

PR-20 의 박제 가정 4건 정정 (`19 → 25 PASS` / `"` → `&quot;` / `|<>*` → `\0` / "InternalsVisibleTo 신규" → "PR-10 기존") 모두 박제 시점의 정보 한계로 발생. 정정 자체가 향후 박제 가정의 정확성을 높임 — design doc 의 `§9 박제 가정 정정` 표가 누적되어 회고 자료.

### Theory 의 InlineData 카운트 효과는 매트릭스 압축의 본질

19 메서드 매트릭스 ≠ 25 PASS — `[Theory]` 활용이 메서드 수를 줄이면서 PASS 수를 늘림. PR-20 의 5 Theory 가 13 InlineData 를 압축 — xUnit 표준 패턴의 효과 측정 가능. 향후 단위 테스트 추가 시 같은 패턴 적용 권장.
