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

(empty)
