# Release Procedure — v0.9.5.0 이후 (다음 v0.9.X.Y 준비)

**유지보수자 전용.** 일반 사용자는 [README.md](../README.md) 의 다운로드 섹션을 보세요. 본 문서는 PR-11 (Version 단일 진실원 + SHA256 release) 머지 후 절차입니다.

---

## 1. 사전 조건

- **메인 브랜치 클린 상태.** 모든 PR 머지 + working tree 깨끗.
- **로컬 [dotnet build](../KoEnVue.csproj) 통과** + **[dotnet test](../tests/KoEnVue.Tests/) 전부 통과 (현재 82개)** + **[GitHub Actions](../.github/workflows/build.yml) 녹색** (이전 main 푸시 기준).
- **CHANGELOG.md** 의 `[Unreleased]` 섹션이 비어 있지 않은지 확인. 릴리스 직전에 `[Unreleased]` → `[X.Y.Z] — YYYY-MM-DD` 로 승격하고 새 빈 `[Unreleased]` 헤더를 추가.

## 2. 버전 bump

**한 곳만 수정합니다** — [KoEnVue.csproj](../KoEnVue.csproj) 의 `<Version>` 요소.

```xml
<Version>0.9.5.0</Version>
```

형식: **`major.minor.build.revision` 4-part 필수** (본 프로젝트 컨벤션). 모든 PE 헤더 필드 (`AssemblyVersion` / `FileVersion` / `InformationalVersion`) 가 동일 4-part 로 박혀 Windows 탐색기 "자세히" 탭에서 혼동 없음. 빌드 시점에 [Directory.Build.targets](../Directory.Build.targets) 의 `GenerateVersionConstants` Target 가 `obj/.../Version.g.cs` 로 `DefaultConfig.AppVersion` partial 조각을 자동 생성하고, PE 헤더 3종도 같은 값에서 derive 합니다.

> `System.Version.TryParse` 는 2-part `0.10` 도 받아들이지만 그러면 PE 헤더에 `0.9.3.0` 으로 0-padding 돼 csproj 값과 사용자 가시 값이 어긋납니다. 4-part 로 명시.

**v0.9.x 시절 절차의 변화**: [App/Config/DefaultConfig.cs](../App/Config/DefaultConfig.cs) 의 `AppVersion` const + [docs/architecture.md](architecture.md) 의 미러 표기를 손으로 sync 하던 footgun 이 사라졌습니다 (PR-11 D6).

## 3. 빌드 + publish

```bash
dotnet build
dotnet publish -r win-x64 -c Release
```

산출물 — `bin/Release/net10.0-windows/win-x64/publish/`:

- `KoEnVue.exe` — NativeAOT 단일 exe (~4.8 MB).
- `KoEnVue.exe.sha256.txt` — SHA256 hash 두 줄 (`Algorithm` + `Hash`, PR-11 G4, [Directory.Build.targets](../Directory.Build.targets) 의 `EmitSha256` Target 가 publish 후 PowerShell `Get-FileHash | Format-List Algorithm, Hash` 로 자동 emit).

확인:

```bash
cat bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe.sha256.txt
```

`Algorithm : SHA256` / `Hash : ABC123...` 두 줄이 나옵니다.

> **주의 — NativeAOT 비결정성**: `<Deterministic>true</Deterministic>` 는 C# 컴파일러 결정성만 보장합니다. NativeAOT ILC 의 codegen 단계는 매 publish 마다 다른 SHA256 을 만들 수 있어요 — 같은 머신에서 `dotnet publish` 를 2회 돌려도 hash 가 다를 수 있습니다 (PR-11 Tier-3 발견, [docs/dev-notes/2026-05-21-signing-decision.md](dev-notes/2026-05-21-signing-decision.md) §함의). 그래서 **반드시 본 `publish/` 의 KoEnVue.exe + KoEnVue.exe.sha256.txt 한 쌍** 을 GitHub Releases 에 같이 첨부해야 사용자 검증이 성립합니다. 재빌드 하지 마세요.

## 4. Git tag + GitHub Release

**중요 — 3단계 publish 결과물 그대로 첨부**. NativeAOT 비결정성 때문에 재빌드하면 SHA256 이 바뀌어 사용자 검증이 실패합니다.

```bash
git tag vX.Y.Z.W          # 예: git tag v0.9.3.0.0 (4-part)
git push origin main      # main 푸시 → GitHub Actions 트리거
git push origin vX.Y.Z.W  # 태그 푸시
```

**GitHub 웹 UI** — `Releases → Draft a new release`:

- **Choose a tag**: `vX.Y.Z.W` (방금 푸시한 태그). 접두 `v` 필수 — [UpdateChecker.NormalizeVersion](../App/Update/UpdateChecker.cs) 가 벗겨냅니다. 4-part 컨벤션 (`v0.9.3.0.0` 등).
- **Release title**: `KoEnVue vX.Y.Z.W` 또는 자유 텍스트.
- **Describe**: CHANGELOG 의 해당 버전 섹션을 그대로 복붙. 본문 마지막에 SHA256 hash 인용:
  ```
  ## 다운로드 검증

  SHA-256: `ABC123...DEF`

  PowerShell 로 검증:
      Get-FileHash -Algorithm SHA256 .\KoEnVue.exe
  ```
- **Attach binaries** (Drag & drop):
  - `KoEnVue.exe`
  - `KoEnVue.exe.sha256.txt`
- **"Set as a pre-release" 체크 해제** — 0.x.x 라고 GitHub 가 자동 권장하지만 체크하면 `release.prerelease=true` 로 [UpdateChecker.PickLatestRelease](../App/Update/UpdateChecker.cs) 가 건너뛰어 사용자에게 알림이 안 갑니다.
- **Publish release**.

## 5. 검증 (선택)

이전 버전 exe 를 한 번 실행:

```bash
.\KoEnVue.exe   # 이전 버전 (예: v0.9.2.x)
```

5~10 초 후 트레이 메뉴 최상단 헤더 라벨이 `KoEnVue v{old} → v{new} — 다운로드` 로 자동 전환되면 [UpdateChecker](../App/Update/UpdateChecker.cs) 가 새 릴리스를 정상 감지한 것.

## 6. CHANGELOG 정리

```diff
- ## [Unreleased]
+ ## [X.Y.Z.W] — YYYY-MM-DD
```

새 빈 `## [Unreleased]` 헤더를 위에 추가 + 커밋 + 푸시.

---

## 부록 — 무서명 + SHA256 정책

EV/OV 코드 사인 인증서는 도입하지 않습니다 (PR-11 G4 결정). 근거 + 재검토 트리거는 [docs/dev-notes/2026-05-21-signing-decision.md](dev-notes/2026-05-21-signing-decision.md). SmartScreen 평판은 다운로드 누적에 따라 자연 학습됩니다 — SHA256 게시로 변조 검증만 보장하고 서명 비용은 ROI 가 명확해질 때까지 보류.
