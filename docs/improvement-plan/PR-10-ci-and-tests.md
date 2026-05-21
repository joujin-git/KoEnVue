# PR-10: CI + first unit tests + UnhandledException handlers

**Status**: ⏳ pending
**Branch**: feat/pr-10-ci-tests
**Base**: main (PR-03 후 권장)
**Risk**: Low
**Estimated session size**: M (1-2시간)

## 1. 목적 (Why)

10.5k LOC + dev-notes 두 postmortem이 보여준 fragility에도 불구하고:
- **G1**: 테스트 0개, CI 0개. 매 릴리스가 manual `dotnet publish`. 회귀 detection이 prod 사용자에게 의존.
- **G5**: Crash diagnostic이 [Program.cs:84-86](../../Program.cs#L84)의 `Main` catch뿐. background 스레드 크래시 무흔적.

본 PR은 인프라 도입 — 검증 자동화. 첫 단위 테스트는 가장 stable한 layer(순수 함수)부터.

## 2. 변경 범위 (What)

### 코드 — 테스트 프로젝트
- [ ] [tests/KoEnVue.Tests/KoEnVue.Tests.csproj](../../tests/KoEnVue.Tests/) 신규 — xUnit 또는 MSTest. .NET 10. **NuGet 패키지 단 1개 예외 허용** (xunit + xunit.runner.visualstudio) — CLAUDE.md P1과 충돌하나 테스트 디렉토리는 P1 적용 제외(별도 dev dependency)
- [ ] [tests/KoEnVue.Tests/Unit/SettingsValidateTests.cs](../../tests/KoEnVue.Tests/Unit/) 신규 — `Settings.Validate`의 clamp/enum fallback 테스트 (~10케이스)
- [ ] [tests/KoEnVue.Tests/Unit/ColorHelperTests.cs](../../tests/KoEnVue.Tests/Unit/) 신규 — `HexToColorRef`/`RgbToHex` 라운드트립 테스트 (~5케이스)
- [ ] [tests/KoEnVue.Tests/Unit/DpiHelperTests.cs](../../tests/KoEnVue.Tests/Unit/) 신규 — `Scale`/`GetScale` 순수 계산 (~5케이스)
- [ ] (선택) [tests/KoEnVue.Tests/Unit/ThemePresetsTests.cs](../../tests/KoEnVue.Tests/Unit/) — PR-05 후의 ThemeColors 적용/복원 (~6케이스)
- [ ] (선택) [tests/KoEnVue.Tests/Unit/XmlEntityCodecTests.cs](../../tests/KoEnVue.Tests/Unit/) — PR-04 신설 모듈 (~5케이스)
- [ ] 테스트 대상 internal 멤버 접근 — KoEnVue.csproj에 `[assembly: InternalsVisibleTo("KoEnVue.Tests")]` 추가

### 코드 — UnhandledException 핸들러 (G5)
- [ ] [Program.cs:MainImpl](../../Program.cs) 또는 [Program.Bootstrap.cs](../../Program.Bootstrap.cs)에서:
  ```csharp
  AppDomain.CurrentDomain.UnhandledException += (s, e) => {
      Logger.Error($"UnhandledException: {e.ExceptionObject}");
      // koenvue_crash.txt에도 append
  };
  TaskScheduler.UnobservedTaskException += (s, e) => {
      Logger.Error($"UnobservedTaskException: {e.Exception}");
      e.SetObserved();
  };
  ```
- [ ] background 스레드(DetectionLoop, UpdateChecker, Logger drain, StartupTaskManager.SyncStartupPathCore)의 outer try/catch에 같은 sink로 일관 출력

### 코드 — GitHub Actions CI
- [ ] [.github/workflows/build.yml](../../.github/workflows/) 신규:
  ```yaml
  name: build
  on: [push, pull_request]
  jobs:
    build:
      runs-on: windows-latest
      steps:
        - uses: actions/checkout@v4
        - uses: actions/setup-dotnet@v4
          with: { dotnet-version: '10.0.x' }
        - run: dotnet build
        - run: dotnet test tests/KoEnVue.Tests/
        - run: dotnet publish -r win-x64 -c Release --no-self-contained
  ```
- [ ] (선택) `aot.yml` — NativeAOT publish smoke (시간 오래 걸리므로 main 푸시에만)

### 문서
- [ ] [CONTRIBUTING.md](../../CONTRIBUTING.md) 신규 — 빌드/테스트/CI 게이트 + 본 improvement-plan 워크플로우 안내
- [ ] [README.md](../../README.md) 갱신 — CI 배지 + `dotnet test` 명령
- [ ] `CHANGELOG.md` [Unreleased] / Added

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep "InternalsVisibleTo.*KoEnVue.Tests" KoEnVue.csproj` 1 매치
- [ ] `git grep "AppDomain.CurrentDomain.UnhandledException" Program.cs Program.Bootstrap.cs` 1+ 매치
- [ ] `ls .github/workflows/build.yml` 존재

### Tier 3 — 수동 smoke
- [ ] `dotnet test tests/KoEnVue.Tests/` 통과 (모든 테스트 green)
- [ ] GitHub Actions 빌드 통과 (push 후 확인)
- [ ] (선택) 의도적 NullReferenceException 유도 → koenvue_crash.txt에 unhandled exception 라인 기록

## 4. 사이드 이펙트 / 위험

- **위험 1 (큼)**: xUnit 도입은 CLAUDE.md P1("Zero external NuGet packages")의 예외. 테스트 프로젝트는 별도 csproj이고 release exe에 포함 안 되니 P1 정신은 유지됨. **결정**: 명시적 예외. CLAUDE.md에 "P1은 release 출력에만 적용. 테스트 의존성은 dev-only 예외" 추가.
- **위험 2**: InternalsVisibleTo 도입 시 NativeAOT publish가 깨지지 않는지 — 일반적으로 안전. CI에서 자동 검증.
- **위험 3**: GitHub Actions runner의 `windows-latest`는 .NET 10 preview일 수 있음. `actions/setup-dotnet@v4`로 명시 설치.
- **위험 4**: AOT publish는 시간 오래 걸림(~수 분). 빈도 제한 권장 (main branch 푸시만).
- **위험 5**: UnhandledException 핸들러가 GUI 호출하면 thread affinity 문제. 로그만 기록하고 user32 호출 금지.

## 5. 롤백 절차

- 단순 revert (Y) — 신규 디렉토리(tests/, .github/) 삭제 + KoEnVue.csproj InternalsVisibleTo 제거
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

### 2026-05-21 — 1차 구현 (Tier-1 + Tier-2 통과)

**상태**: ✅ Tier-1 + Tier-2 통과, Tier-3 사용자 smoke 대기.

**구현**:

- **테스트 프로젝트** ([tests/KoEnVue.Tests/KoEnVue.Tests.csproj](../../tests/KoEnVue.Tests/KoEnVue.Tests.csproj))
  - `<TargetFramework>net10.0-windows</TargetFramework>` — 메인 프로젝트와 동일 (ProjectReference 호환). `<IsTestProject>true</IsTestProject>` 명시. `<IsPackable>false</IsPackable>` 로 NuGet 패키지화 차단.
  - 의존성 3종: `Microsoft.NET.Test.Sdk` (17.13.0), `xunit` (2.9.3), `xunit.runner.visualstudio` (3.0.0, `PrivateAssets=all` 로 전이 차단).
  - 메인의 AOT/Trim/SingleFile 분석기는 **명시적으로 끔** — 테스트 코드는 reflection 위에서 동작. csproj 에 3 토글 `false` 추가.
  - `<ProjectReference Include="..\..\KoEnVue.csproj" />` 한 줄로 main 어셈블리 참조.
- **InternalsVisibleTo**: [KoEnVue.csproj](../../KoEnVue.csproj) 에 `<InternalsVisibleTo Include="KoEnVue.Tests" />` 추가 — 테스트가 `internal` 타입 (Settings / DefaultConfig / AppConfig / ColorHelper / DpiHelper) 직접 접근.
- **사이드 fix — DefaultItemExcludes**: KoEnVue.csproj 가 SDK 디폴트로 `**/*.cs` implicit 포함이라 `tests/**/*.cs` 까지 메인 어셈블리에 컴파일됨 (첫 빌드에서 95 errors). `<DefaultItemExcludes>$(DefaultItemExcludes);tests/**</DefaultItemExcludes>` 추가로 차단.
- **테스트 3 파일**:
  - [tests/KoEnVue.Tests/Unit/SettingsValidateTests.cs](../../tests/KoEnVue.Tests/Unit/SettingsValidateTests.cs) — 12 케이스 (PollIntervalMs 양 끝 clamp + Opacity 양 끝 clamp + FontSize 하단 clamp + IndicatorScale 1자리 round + DisplayMode/Language/Theme invalid cast → enum fallback + NonKoreanIme valid passthrough + AdvancedConfig OverlayClassName 한국어 → "KoEnVueOverlay" 폴백 + valid ASCII 보존).
  - [tests/KoEnVue.Tests/Unit/ColorHelperTests.cs](../../tests/KoEnVue.Tests/Unit/ColorHelperTests.cs) — 5 메서드 ~15 케이스 (HexToColorRef BGR 순서, HexToRgb 채널 추출, RgbToHex 라운드트립, TryNormalizeHex trim/case/문법, ColorRefToRgb 채널 언패킹).
  - [tests/KoEnVue.Tests/Unit/DpiHelperTests.cs](../../tests/KoEnVue.Tests/Unit/DpiHelperTests.cs) — Scale(int,double) 5 케이스 + Scale(double,double) 5 케이스 (banker's rounding 명시 케이스 포함 — F-S05 정수 절삭 회귀 가드) + BASE_DPI=96.
  - 첫 실행 결과: **40 통과 / 0 실패 / 386 ms**.
- **AppDomain unhandled 핸들러** ([Program.cs:100-140](../../Program.cs#L100))
  - `Main()` 의 `try/catch` 안 크래시 파일 write 로직을 신규 정적 헬퍼 `AppendCrashFile(string tag, object payload)` 로 추출 — `[FATAL]` / `[UNHANDLED]` / `[UNOBSERVED]` 3 태그를 동일 포맷으로 통합.
  - 신규 `RegisterCrashHandlers()` — `AppDomain.CurrentDomain.UnhandledException` (terminating 플래그 로깅 + Logger.Shutdown) + `TaskScheduler.UnobservedTaskException` (e.SetObserved 호출로 finalizer 종료 차단) 두 핸들러 등록.
  - `MainImpl` 의 단계 0a 에서 `LogProvider.Sink` 배선 직후 호출 — Logger 초기화 이전 크래시도 pre-Init 버퍼 경유로 koenvue.log 에 기록 가능.
  - 핸들러 안 **GUI 호출 금지** — thread affinity 문제로 `Logger.Error` + `File.AppendAllText` 만 사용. P/Invoke (User32) 도 부르지 않음.
- **GitHub Actions** ([.github/workflows/build.yml](../../.github/workflows/build.yml))
  - `windows-latest` + `actions/checkout@v4` + `actions/setup-dotnet@v4` (`dotnet-version: '10.0.x'`).
  - 3 스텝: `dotnet build --configuration Debug` → `dotnet test ... --no-build` → (조건부) `dotnet publish -r win-x64 -c Release`.
  - **AOT publish 는 `main` 푸시 한정** (`if: github.event_name == 'push' && github.ref == 'refs/heads/main'`) — PR 마다 돌리면 ~수 분 소요라 비용 큼. PR 회귀는 `dotnet build` + `dotnet test` 가 잡고, AOT-specific 회귀 (trim warnings 등) 는 main 머지 시점에 게이트.
- **문서**:
  - [CONTRIBUTING.md](../../CONTRIBUTING.md) 신규 — 환경 / 빌드+테스트+publish / improvement-plan 절차 / P1~P6 요약.
  - [README.md](../../README.md) — CI 배지 추가 + `dotnet test` 명령 추가 + CONTRIBUTING.md 링크.
  - [CHANGELOG.md](../../CHANGELOG.md) Unreleased `### 추가` 신설 + PR-10 1줄 엔트리.
  - [CLAUDE.md](../../CLAUDE.md) P1 행에 "tests/ 의 dev-only 의존성 예외" 부연.

**Tier-1**:
- `dotnet build` clean — 0 경고 0 오류 (DefaultItemExcludes 추가 후).
- `dotnet test tests/KoEnVue.Tests/` — 40/40 통과 386 ms.
- `dotnet publish -r win-x64 -c Release` clean — 0 경고. exe 4,807,168 B (≈ **4.81 MB**, PR-09 의 4.80 MB 와 +3 KB / 노이즈 범위).

**Tier-2 grep 가드** (PR-10 §3):
- `git grep "InternalsVisibleTo.*KoEnVue\.Tests" KoEnVue.csproj` = 1 ✓
- `git grep "AppDomain.CurrentDomain.UnhandledException" Program.cs` = 1 ✓
- `git grep "TaskScheduler.UnobservedTaskException" Program.cs` = 1 ✓ (보너스)
- `ls .github/workflows/build.yml` 존재 ✓

**Invariant 4종**: 모두 0 매치 (`KoEnVue.App` / `ImeState` / `NonKoreanImeMode` in Core/, `[DllImport` in `*.cs`).
**P5 2종**: 모두 0 매치 (`requireAdministrator` in app.manifest, `RunLevel.*HighestAvailable` in App/).

**남은 작업 (Tier-3)**:
1. `dotnet test` 사용자 PC 에서도 통과 — 본 세션 PC 는 통과했지만 NuGet 캐시가 비어 있던 PC 가 처음 복원 시 ~17 초 소요 (CI runner 도 동일 비용).
2. (선택) 의도적 NullReferenceException 유도 → `koenvue_crash.txt` 에 `[UNHANDLED] ... NullReferenceException ...` 라인 기록 확인. 자연 발생을 기다리거나 디버그 빌드에서 일회성 throw 코드 임시 주입.
3. GitHub Actions 실제 빌드 통과 — feat/pr-10-ci-tests 푸시 시점에 PR / push 트리거 자동 활성, Actions 탭에서 녹색 상태 확인.

