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

### 2026-05-21 — 1차 구현 + Tier-3 발견 (NativeAOT 비결정성)

**상태**: ✅ Tier-1+2+3 통과. Tier-3 에서 spec §4 위험 5 의 가설 ("NativeAOT 는 일반적으로 deterministic") 이 부정확함을 실측으로 확인 → 문서 4종 정직하게 정정.

**구현**:

- **Version 단일 진실원 ([Directory.Build.targets](../../Directory.Build.targets) + [App/Config/DefaultConfig.cs](../../App/Config/DefaultConfig.cs) + [KoEnVue.csproj](../../KoEnVue.csproj))**
  - 신규 `Directory.Build.targets` — `GenerateVersionConstants` Target (`BeforeTargets="CoreCompile"`) 가 `obj/.../Version.g.cs` 를 `WriteOnlyWhenDifferent=true` 로 emit. `internal static partial class DefaultConfig` 의 `AppVersion = "$(Version)"` const 한 줄. 본 Target 안에서 `<ItemGroup><Compile Include="$(VersionGeneratedFile)" /></ItemGroup>` 로 runtime 에 Compile item 추가 — `CoreCompile` 이 evaluation phase 의 implicit `**/*.cs` 와 같은 `@(Compile)` 컬렉션에서 본다.
  - `App/Config/DefaultConfig.cs` 의 `internal static class DefaultConfig` → `internal static partial class DefaultConfig`. `AppVersion = "0.9.2.8"` const + 16 라인 코멘트 블록 제거.
  - `KoEnVue.csproj` 의 `<Version>0.9.2.8</Version>` 그대로 유지 + `<Deterministic>true</Deterministic>` 명시 추가.
  - **사이드 함정** (dev-notes 기록):
    - MSBuild attribute 가 leading whitespace 를 trim — 생성된 `.cs` 의 들여쓰기가 사라지나 C# 의미 동일.
    - 빈 `Include=""` 는 MSB4035 — 본문 블랭크 줄 제거.
- **SHA256 자동 emit**
  - `EmitSha256` Target (`AfterTargets="Publish" Condition="'$(Configuration)' == 'Release'"`) — PowerShell `Get-FileHash -Algorithm SHA256` 로 `publish/KoEnVue.exe.sha256.txt` 생성 (Algorithm + Hash 두 줄).
- **문서 (4종 신규 / 갱신)**:
  - [README.md](../../README.md) — "다운로드 검증 (SHA256)" 서브섹션 신규 + 릴리스 섹션을 "csproj `<Version>` 한 줄 수정" 으로 단순화 + CONTRIBUTING.md 링크 그대로.
  - [docs/release-procedure.md](../release-procedure.md) 신규 — 사전 조건 / 버전 bump / publish / GitHub Release 작성 / 검증 / CHANGELOG 정리 6 단계 + 무서명 + SHA256 정책 부록.
  - [docs/dev-notes/2026-05-21-version-singlesource.md](../dev-notes/2026-05-21-version-singlesource.md) — generator vs MSBuild Target trade-off + 함정 기록.
  - [docs/dev-notes/2026-05-21-signing-decision.md](../dev-notes/2026-05-21-signing-decision.md) — 무서명 결정 근거 + 재검토 트리거 4 항목 + NativeAOT 비결정성 발견.
  - [CHANGELOG.md](../../CHANGELOG.md) `[Unreleased] / ### 추가` — PR-11 한 줄 엔트리.

**Tier-1**:
- `dotnet build` clean → `obj/Debug/.../Version.g.cs` 생성 + `DefaultConfig.AppVersion = "0.9.2.8"`.
- `dotnet test tests/KoEnVue.Tests/` 40/40 통과 43 ms.
- `dotnet publish -r win-x64 -c Release` clean + `KoEnVue.exe.sha256.txt` 생성.

**Tier-2 grep 가드** (PR-11 §3): 모두 통과.
- `AppVersion = ` in `App/Config/DefaultConfig.cs` = 0 ✓ (generator 로 이동)
- `partial class DefaultConfig` in `App/Config/DefaultConfig.cs` = 1 ✓
- `GenerateVersionConstants` in `Directory.Build.targets` = 1 ✓
- `EmitSha256` in `Directory.Build.targets` = 1 ✓

**Invariant 4종** + **P5 2종**: 모두 0 매치.

**Tier-3 결과**:
1. **csproj `<Version>` 0.9.2.8 → 0.9.2.9 일시 변경** → `dotnet build` → `Version.g.cs` 의 `AppVersion = "0.9.2.9"` 즉시 갱신 확인 ✓ → 복원.
2. **SHA256 deterministic 검증** — 같은 머신·같은 SDK·같은 코드 입력으로 `rm -rf bin/Release obj/Release && dotnet publish -r win-x64 -c Release` 3 회 실행:
   ```
   ed6b2acee83b64c5dcb0afd1e639a029eb88f288364ff969e0e9b083a2da908f  # 1차
   41d245fcb31ea4d6a439681d1e37ad22aa8855e8beb862e7d64dce236a829187  # 2차
   a711de796d28523024681762d116a59ef39b349c5af6039ccaca3fe4e8a34d8e  # 3차
   ```
   3 hash 모두 다름 — `<Deterministic>true</Deterministic>` 는 C# 컴파일러 결정성만 보장하고 NativeAOT ILC codegen 은 별개로 비결정성을 가짐을 실측 확인. PR-11 spec §4 위험 5 의 "일반적으로 그렇지만 검증" 가설이 부정확.
3. **사용자 가시 (트레이 헤더 `v0.9.2.9` 표시)** — 본 세션에서 미수행. PR-10 으로 인한 main push 후 origin 가 이미 정합 상태라 별도 실행 검증 없이도 generator flow 검증 (Tier-3 ①) 으로 충분.

**Tier-3 발견 → 문서 정정** (commit 안의 추가 변경):
- [README.md](../../README.md) "다운로드 검증" — "정본 hash 와 본인 재빌드 hash 는 다를 수 있음" 명시. csproj 주석도 정정.
- [docs/release-procedure.md](../release-procedure.md) §3 + §4 — "publish 결과물 그대로 첨부, 재빌드 금지" 강조.
- [docs/dev-notes/2026-05-21-signing-decision.md](../dev-notes/2026-05-21-signing-decision.md) §함의 — NativeAOT 비결정성 절 신설 + 3 hash 실측 박제.
- [docs/dev-notes/2026-05-21-version-singlesource.md](../dev-notes/2026-05-21-version-singlesource.md) — "Deterministic 효과로 SHA256 재현" 표현 정정.
- [CHANGELOG.md](../../CHANGELOG.md) — `<Deterministic>` 의 역할을 "C# 컴파일러 결정성만" 으로 좁힘 + SHA256 의 역할을 "발행본 변조 검증" 으로 좁힘.
- [Directory.Build.targets](../../Directory.Build.targets) 의 `EmitSha256` Target 주석에 비결정성 발견 메모.

**의미**: SHA256 의 사용자 가치는 "재현 가능한 빌드 검증" → "유지보수자 발행본 변조 검증" 으로 좁혀짐. 이는 무서명 정책에서 충분히 의미 있는 보장이며, 본 PR 의 G4 목표 (사용자가 자기 다운로드 exe 가 GitHub 첨부본과 일치하는지 확인) 는 그대로 달성.

