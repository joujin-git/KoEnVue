# PR-02: AOT/Trim analyzers + app.manifest hardening

**Status**: ⏳ pending
**Branch**: feat/pr-02-analyzers-manifest
**Base**: main
**Risk**: Medium (분석기 경고 폭증 가능)
**Estimated session size**: M (1-2시간)

## 1. 목적 (Why)

1. **G2**: 현 [KoEnVue.csproj](../../KoEnVue.csproj)는 `PublishAot=true`만 명시. `EnableTrimAnalyzer`/`EnableSingleFileAnalyzer`/`EnableAotAnalyzer`가 빠져 있어 build 시점에 trim/AOT 경고가 안 뜸. 후속 PR들이 reflection API를 추가할 때 즉시 검출 안 됨.

2. **G3**: 현 [app.manifest](../../app.manifest)는 DPI 인식만 선언. `longPathAware`/`supportedOS`/`gdiScaling`이 빠져 있어 일부 Windows API가 legacy 동작.

본 PR이 PR-03(asInvoker) 이전에 와야 하는 이유: 새 경고/manifest 항목이 PR-03의 새 코드 경로(파일 fallback 등)를 즉시 검증.

## 2. 변경 범위 (What)

### 코드
- [ ] [KoEnVue.csproj](../../KoEnVue.csproj)에 다음 properties 추가:
  ```xml
  <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
  <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
  <EnableAotAnalyzer>true</EnableAotAnalyzer>
  ```
- [ ] 새로 발생하는 trim/AOT 경고 검토 + 수정. 예상 위치:
  - `Marshal.SizeOf<T>()` 호출 — generic 이미 .NET 8+ 안전, 추가 작업 없음
  - `Enum.IsDefined(value)` — generic overload 자동 resolve, 안전
  - `Process.GetProcesses()` — trim 안전
  - `Assembly.GetExecutingAssembly()` — 사용처 있으면 검토 (PR-11에서 본격 처리, 본 PR에서는 경고 묵음 또는 NoWarn 일시 추가)
- [ ] [app.manifest](../../app.manifest)에 다음 항목 추가:
  - `<longPathAware>true</longPathAware>` (windowsSettings 안)
  - `<gdiScaling>true</gdiScaling>` (검토 후 추가)
  - `<compatibility><application>` 블록에 `supportedOS` Windows 10 + Windows 11 GUID

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / Changed에 항목 추가
- [ ] `docs/conventions.md`에 P-i 항목 추가: "AOT/Trim/SingleFile analyzer 활성. 신규 경고는 같은 PR에서 fix 또는 명시적 NoWarn + 이유"
- [ ] `docs/implementation-notes.md`의 NativeAOT 섹션에 본 변경 반영

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과 (경고 0 또는 검토된 NoWarn만)
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep "EnableTrimAnalyzer" KoEnVue.csproj` 1 매치
- [ ] `git grep "EnableAotAnalyzer" KoEnVue.csproj` 1 매치
- [ ] `git grep "longPathAware" app.manifest` 1 매치
- [ ] `git grep "supportedOS" app.manifest` 1+ 매치

### Tier 3 — 수동 smoke
- [ ] 빌드 후 정상 부팅
- [ ] (선택) long path 시나리오 — `config.json`을 매우 깊은 디렉토리에 두고 부팅

## 4. 사이드 이펙트 / 위험

- **위험 1**: 신규 trim/AOT 경고가 다수 발생 가능. 본 PR에서 모두 처리 불가하면 명시적 `NoWarn`으로 일시 처리하되 `// TODO: PR-NN에서 정리` 코멘트 + DECISIONS.md에 기록.
- **위험 2**: `supportedOS` GUID 잘못 입력 시 manifest parse 실패 → 앱 부팅 불가. **검증**: build 후 PE 헤더 inspection (`sigcheck`/`manifest tool`).
- **위험 3**: `gdiScaling=true`는 GDI를 DPI-aware로 강제. 본 앱은 이미 PerMonitorV2라 충돌 가능. 검토 후 결정.

## 5. 롤백 절차

- 단순 revert (Y)
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

(empty)
