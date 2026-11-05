# Release Procedure

**유지보수자 전용.** 일반 사용자는 [README.md](../README.md) 의 다운로드 섹션을 보세요. 본 문서는 PR-11 (Version 단일 진실원 + SHA256 release) 머지 후 절차입니다.

---

## 1. 사전 조건

- **메인 브랜치 클린 상태.** 모든 PR 머지 + working tree 깨끗.
- **로컬 [dotnet build](../KoEnVue.csproj) 통과** + **[dotnet test](../tests/KoEnVue.Tests/) 전부 통과** + **[GitHub Actions](../.github/workflows/build.yml) 녹색** (이전 main 푸시 기준). 테스트 개수는 적지 않는다 — 릴리즈마다 늘어 stale 이 되고, "전부 통과" 가 실제 기준이다.
- **CHANGELOG.md 승격** — `[Unreleased]` 섹션이 비어 있지 않은지 확인하고 **여기서 바로 승격한다.**

  ```diff
  - ## [Unreleased]
  + ## [X.Y.Z.W] — YYYY-MM-DD — 한 줄 요약
  ```

  새 빈 `## [Unreleased]` 헤더를 위에 추가한다. 커밋은 §2 의 버전 bump 와 **한 커밋으로** 묶는다.

  > **릴리즈 후가 아니라 사전에 하는 이유** — 태그가 가리키는 커밋에 CHANGELOG 가 이미 들어 있어야 §4 의 릴리즈 노트를 그 섹션에서 그대로 뽑을 수 있다. (2026-08-04 통합 — 이전에는 §6 「CHANGELOG 정리」가 같은 작업을 릴리즈 후에 한 번 더 지시해, 어느 시점에 하는 것이 맞는지 모호했다.)

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

- `KoEnVue.exe` — NativeAOT 단일 exe (~5 MB).
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
git tag vX.Y.Z.W          # 예: git tag v1.0.0.0 (4-part — 숫자 4개, 점 3개)
git push origin main      # main 푸시 → GitHub Actions 트리거
git push origin vX.Y.Z.W  # 태그 푸시
```

**GitHub 웹 UI** — `Releases → Draft a new release`:

- **Choose a tag**: `vX.Y.Z.W` (방금 푸시한 태그). 접두 `v` 필수 — [UpdateChecker.NormalizeVersion](../App/Update/UpdateChecker.cs) 가 벗겨냅니다. 4-part 컨벤션 (`v1.0.0.0` 등 — 숫자 4개).
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
- **"Set as a pre-release" 체크 해제** — 체크하면 `release.prerelease=true` 로 [UpdateChecker.PickLatestRelease](../App/Update/UpdateChecker.cs) 가 건너뛰어 사용자에게 알림이 안 갑니다.
- **Publish release**.

## 5. 검증 (선택)

이전 버전 exe 를 한 번 실행:

```bash
.\KoEnVue.exe   # 이전 버전 (예: v0.9.2.x)
```

5~10 초 후 트레이 메뉴 최상단 헤더 라벨이 `KoEnVue v{old} → v{new} — 다운로드` 로 자동 전환되면 [UpdateChecker](../App/Update/UpdateChecker.cs) 가 새 릴리스를 정상 감지한 것.

**트레이를 열지 않고 로그로 확인하는 방법** (2026-11-05 v1.0.3.0 → v1.0.4.0 에서 실측):

1. 이전 버전 asset 을 **새 빈 폴더**에 받는다 — `gh release download v{old} -p KoEnVue.exe -p KoEnVue.exe.sha256.txt -D <빈 폴더>` 후 해시 대조. exe 폴더가 쓰기 가능하면 설정·로그가 그 폴더에 생기므로([PortablePath](../App/Config/PortablePath.cs)) 쓰던 설정을 건드리지 않는다. 기본값이 `log_to_file=true`·`update_check_enabled=true` 라 설정 파일을 미리 만들 필요도 없다.
2. 쓰던 KoEnVue 가 실행 중이면 먼저 트레이에서 종료한다 — 단일 인스턴스라 이전 버전이 바로 끝난다.
3. 실행 후 그 폴더의 `koenvue.log` 에 `UpdateChecker: new version available — current={old} latest=v{new}` 와 `Tray: update available — v{new}` 두 줄이 찍히면 통과 (실측 0.3초).
4. 종료는 트레이 「종료」. 무인으로 할 때 `taskkill /PID`(무 `/F`)는 숨은 메인 창에 닿지 않아 안 꺼진다 — `KoEnVueMain` 클래스 창에 `WM_CLOSE` 를 보내면 트레이 아이콘까지 정리하고 끝난다(로그 `KoEnVue stopped`).

> **주의 — 시작 프로그램 등록**: 등록돼 있으면 이전 버전이 기동 직후 [StartupTaskManager](../App/Startup/StartupTaskManager.cs) 의 경로 동기화로 작업 경로를 **빈 폴더의 exe 로 바꿔 등록**한다. 확인 전에 `schtasks /query /tn KoEnVue` 로 등록 여부를 보고, 등록돼 있었다면 끝난 뒤 쓰던 exe 를 한 번 실행해 경로를 되돌린다(같은 동기화가 다시 고친다).

## 6. 문서 갱신

**손대야 하는 곳은 한 군데뿐이다** — 나머지는 낡지 않도록 써 두었다.

[improvement-plan/INDEX.md](improvement-plan/INDEX.md):

- 상단 요약의 **`현재 버전 \`X.Y.Z.W\``** 표기
- **progress matrix 맨 아래에 이번 릴리즈 행** 추가 (날짜 · 무엇을 · 검증 결과)

> **낡는 값은 문서에 박지 않는다.** 2026-08-04 에 메뉴 도식 3곳(User_Guide · PRD · implementation-notes)에 그날의 실제 버전을 적었다가 **같은 날 릴리즈로 두 시간 만에 낡았다.** 지금은 `v{cur}` / `v{지금 버전}` placeholder 로 두어 릴리즈와 무관하다. 같은 이유로 **테스트 개수**(§1)와 **I18n 속성 수**([architecture.md](architecture.md))도 적지 않는다 — 대신 세는 명령을 적어 둔다.
>
> 반면 **「v0.9.3.0 부터 관리자 권한이 필요 없습니다」 같은 도입 시점 서술은 낡지 않는다.** 예시가 아니라 영구 사실이므로 그대로 둔다. 릴리즈 때 버전 문자열을 일괄 치환하려 들면 이쪽까지 망가진다.

**왜 이 단계가 있는가** — 2026-08-04 v1.0.1.0 직후 `improvement-plan/INDEX.md` 가 여전히 `현재 버전 1.0.0.2` 였다. 절차에 이 단계가 없어서 빠진 자리이고, 같은 날 stale 세 건(I18n 속성 수 · MANUAL-VERIFICATION 의 옛 파일명 · 이 버전 표기)이 **모두 다른 일을 하다 우연히** 드러났다.

---

## 부록 — 무서명 + SHA256 정책

EV/OV 코드 사인 인증서는 도입하지 않습니다 (PR-11 G4 결정). 근거 + 재검토 트리거는 [docs/dev-notes/2026-05-21-signing-decision.md](dev-notes/2026-05-21-signing-decision.md). SmartScreen 평판은 다운로드 누적에 따라 자연 학습됩니다 — SHA256 게시로 변조 검증만 보장하고 서명 비용은 ROI 가 명확해질 때까지 보류.
