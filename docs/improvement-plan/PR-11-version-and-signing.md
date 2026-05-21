# PR-11: Version single-source + SHA256 release

**Status**: ⏳ pending
**Branch**: feat/pr-11-version-signing
**Base**: main (PR-10 후 권장)
**Risk**: Medium (MSBuild target 자체의 안전성)
**Estimated session size**: L (반나절+)

## 1. 목적 (Why)

1. **D6**: [DefaultConfig.cs:118](../../App/Config/DefaultConfig.cs#L118)의 `AppVersion = "0.9.2.8"`가 [KoEnVue.csproj](../../KoEnVue.csproj)의 `<Version>`과 hand-sync. 코멘트가 footgun 인정. → MSBuild target으로 csproj `<Version>`을 단일 진실원, `Generated/Version.g.cs`를 자동 생성.

2. **G4**: 무서명 + requireAdministrator (PR-03 후엔 asInvoker)는 SmartScreen 트리거. EV/OV cert ROI 낮음. → GitHub Releases body에 SHA256 게시, README에 검증 가이드.

## 2. 변경 범위 (What)

### 코드 — D6 Version 단일 진실원
- [ ] [Directory.Build.targets](../../Directory.Build.targets) 신규 (또는 KoEnVue.csproj 안에 Target):
  ```xml
  <Target Name="GenerateVersionConstants" BeforeTargets="CoreCompile">
    <PropertyGroup>
      <VersionGeneratedFile>$(IntermediateOutputPath)Version.g.cs</VersionGeneratedFile>
    </PropertyGroup>
    <ItemGroup>
      <Compile Include="$(VersionGeneratedFile)" />
    </ItemGroup>
    <WriteLinesToFile
      File="$(VersionGeneratedFile)"
      Lines="namespace KoEnVue.App.Config%3B internal static partial class DefaultConfig { public const string AppVersion = &quot;$(Version)&quot;%3B }"
      Overwrite="true"
      WriteOnlyWhenDifferent="true" />
  </Target>
  ```
- [ ] [App/Config/DefaultConfig.cs](../../App/Config/DefaultConfig.cs)에서 `AppVersion` 상수 + 코멘트 블록 제거. **`partial class DefaultConfig`로 변경** (generator가 partial로 주입).
- [ ] CI에서 generated 파일을 invalidate 시 fresh build 보장 — `IntermediateOutputPath` 내부라 자동.

### 코드 — G4 SHA256 release
- [ ] [Directory.Build.targets](../../Directory.Build.targets)에 release publish 후 SHA256 emit target:
  ```xml
  <Target Name="EmitSha256" AfterTargets="Publish" Condition="'$(Configuration)' == 'Release'">
    <Exec Command="powershell -NoProfile -Command &quot;Get-FileHash -Algorithm SHA256 '$(PublishDir)KoEnVue.exe' | Format-List Algorithm, Hash | Out-File '$(PublishDir)KoEnVue.exe.sha256.txt' -Encoding ASCII&quot;" />
  </Target>
  ```
- [ ] [README.md](../../README.md)에 "다운로드 검증" 섹션 — 사용자가 SHA256 비교하는 방법
- [ ] [docs/release-procedure.md](../../docs/release-procedure.md) 신규 또는 README의 릴리스 섹션에 — `<Version>` 갱신 + tag + publish + GitHub Releases body에 SHA256 첨부 절차

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / Changed
- [ ] README 릴리스 절차에서 "DefaultConfig.AppVersion 수동 갱신" 단계 **삭제** (csproj `<Version>`만)
- [ ] `docs/dev-notes/2026-05-21-version-singlesource.md` 신규 — MSBuild target 디자인 + AOT 안전성 검증 (compile-time inline 확인)
- [ ] `docs/dev-notes/2026-05-21-signing-decision.md` 신규 — 무서명 + SHA256 선택 근거 + 사용자 베이스 확장 시 재검토 트리거

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과 + `KoEnVue.exe.sha256.txt` 생성됨
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep "AppVersion = " App/Config/DefaultConfig.cs` 0 매치 (generator로 이동)
- [ ] `git grep "partial class DefaultConfig" App/Config/DefaultConfig.cs` 1 매치
- [ ] `git grep "GenerateVersionConstants" Directory.Build.targets` 1 매치
- [ ] `git grep "EmitSha256" Directory.Build.targets` 1 매치
- [ ] csproj `<Version>`만 갱신해도 publish 후 exe의 AppVersion이 일치 (manual smoke로 확인)

### Tier 3 — 수동 smoke
- [ ] csproj `<Version>`을 `0.9.2.9`로 변경 → `dotnet build` → app 실행 → 트레이 메뉴 헤더에 `v0.9.2.9` 표시
- [ ] `dotnet publish -r win-x64 -c Release` → publish/KoEnVue.exe.sha256.txt 내용 확인
- [ ] 같은 publish를 다시 실행 → SHA256이 deterministic (NativeAOT가 timestamp 무관하게 같은 hash인지 — 일반적으로 그렇지만 검증)

## 4. 사이드 이펙트 / 위험

- **위험 1 (큼)**: MSBuild target이 fresh checkout/IDE/CI 모두에서 동작해야 함. Visual Studio/Rider/dotnet CLI 모두 `BeforeTargets="CoreCompile"`을 honor.
- **위험 2**: Generated 파일이 `obj/` 안에 있어 .gitignore 자동 처리. IntelliSense는 obj/ 안 파일도 인식하지만 일부 IDE 캐시 이슈 가능. 첫 빌드 후 IDE 재시작 권장.
- **위험 3**: `partial class DefaultConfig` 변경 시 다른 정적 멤버와의 중복 정의 없는지 확인. const는 partial에 자유롭게 분산.
- **위험 4**: PowerShell on `EmitSha256`는 Windows 빌드 가정. Linux/macOS 빌드 안 함 (Win32 앱이라 OK).
- **위험 5**: SHA256 deterministic — NativeAOT는 일반적으로 deterministic이지만 `<Deterministic>true</Deterministic>` 명시 권장. csproj에 추가.

## 5. 롤백 절차

- 단순 revert (Y) — Target 제거 + DefaultConfig.cs에 AppVersion 복원
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

(empty)
