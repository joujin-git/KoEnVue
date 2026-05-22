# KoEnVue 기여 가이드

본 문서는 코드를 직접 수정하거나 PR 을 보내려는 분을 위한 안내입니다. 일반 사용자는 [README.md](README.md) 와 [docs/User_Guide.md](docs/User_Guide.md) 만 보시면 됩니다.

---

## 환경

- **Windows 10/11 x64**. Mac/Linux 빌드는 지원하지 않습니다 (`TargetFramework=net10.0-windows`).
- **.NET 10 SDK**. `dotnet --version` 으로 확인. CI 도 동일한 SDK 메이저 (`10.0.x`) 로 빌드합니다.
- **NuGet 액세스** — 본 repo 의 [NuGet.config](NuGet.config) 가 nuget.org 단일 소스를 명시합니다. 첫 빌드 시 xUnit 등 dev 패키지를 자동으로 받습니다.
- **PowerShell 7+** (Claude Code 하네스 hook 실행용). Windows 기본 내장 PowerShell 5.x 만으론 hook 이 작동하지 않습니다. 설치:
  ```powershell
  winget install --id Microsoft.PowerShell --source winget
  pwsh --version   # 7.x 이상 확인
  ```
  하네스 미사용 시(Claude Code 안 쓰는 경우) 생략 가능. 자세한 하네스 운영은 [docs/harness.md](docs/harness.md).

---

## 빌드 + 테스트 + 릴리스 publish

```bash
# 디버그 빌드 (분석기 + 모든 경고 표시)
dotnet build

# 단위 테스트
dotnet test tests/KoEnVue.Tests/

# NativeAOT 단일 exe (~4.7 MB) — release exe 산출
dotnet publish -r win-x64 -c Release
```

빌드 / 테스트 / publish 셋 모두 0 경고 0 오류여야 합니다. CI 가 같은 3 단계로 모든 PR 을 검증합니다.

---

## 개선 계획 (improvement-plan) 절차

본 repo 는 v0.9.3.0 까지의 코드베이스 개선을 [docs/improvement-plan/](docs/improvement-plan/) 의 13~14 PR 체크리스트로 추적합니다. 이미 머지된 PR 도 같은 구조를 따랐으므로 새 PR 도 동일 절차로 진행해 주세요.

1. [docs/improvement-plan/INDEX.md](docs/improvement-plan/INDEX.md) 의 진행 매트릭스에서 다음 ⏳ PR 선택.
2. 해당 `PR-NN-*.md` 본문의 §1 목적 / §2 변경 범위 / §3 검증 기준 / §4 위험을 모두 읽기.
3. `feat/pr-NN-*` 브랜치 생성 후 §2 의 체크리스트를 순서대로 처리.
4. **Tier-1** (필수): `dotnet build` + `dotnet test` + `dotnet publish -r win-x64 -c Release` 모두 0 경고. invariant 4종 (`git grep "KoEnVue\.App" Core/` / `git grep "ImeState" Core/` / `git grep "NonKoreanImeMode" Core/` / `git grep "DllImport"`) 0 매치.
5. **Tier-2** (필수): 각 PR §3 의 grep 가드 (rename-away / 신규 ns / 라인 카운트 슬립 등) 모두 통과.
6. **Tier-3** (권장): 명세 §3 의 수동 smoke 항목 — 사용자 가시 동작이 의도대로 변하는지 실제로 띄워서 확인.
7. PR 머지 후 `PR-NN-*.md §6` 와 `INDEX.md` 의 Sessions log 표에 진행 로그 한 줄 추가.

---

## 코딩 규약

[docs/conventions.md](docs/conventions.md) 의 P1~P6 + silent-catch policy + .NET 10 quirks 가 단일 진실원입니다. 핵심:

- **P1**: release exe 는 zero external NuGet. `tests/` 의 xUnit 은 dev-only 예외.
- **P2**: UI 텍스트 한국어 기본 / 로그·config 키는 영문.
- **P3**: 매직 넘버 금지 — `const` / `enum` / config 만.
- **P4**: 중복 구현 금지 — `Core/` 의 단일 진실원을 재사용.
- **P5**: UAC `asInvoker`. exe 가 user-non-writable 위치면 `%LOCALAPPDATA%\KoEnVue\` fallback.
- **P6**: `App/` → `Core/` 단방향만. `Core/` 는 KoEnVue 어휘를 알지 않아야 함.

`[LibraryImport]` 만 사용하고 `[DllImport]` 는 금지. AOT 분석기 (`EnableAotAnalyzer` / `EnableTrimAnalyzer`) 가 위반을 빌드 시점에 잡아냅니다.

---

## 이슈 / 기능 요청

GitHub Issues 에 한국어 또는 영문으로 자유롭게 작성해 주세요. PR 도 같은 언어 정책입니다.

---

## Claude Code 하네스

본 repo 는 [Claude Code](https://claude.com/claude-code) 사용을 전제로 한 하네스를 [.claude/](.claude/) 와 [docs/sessions/](docs/sessions/) 에 포함합니다. 자세한 운영 규칙은 [docs/harness.md](docs/harness.md) 참조.

핵심 워크플로우:
1. 작업 시작 — `claude` 실행 → `SessionStart` hook 이 최근 세션 컨텍스트 자동 주입
2. 다중 파일/새 기능/P규칙 변경 — `/plan` 으로 planner 위임
3. 코드 변경 — Edit/Write hook 이 영향받는 docs 알림
4. commit 직전 — reviewer 서브에이전트가 P규칙 invariant 검증
5. release 전 — verifier 서브에이전트가 `dotnet build` + `publish` + `test`
6. 작업 마무리 — `/wrap-up` 으로 docs-keeper + historian 정리
7. 다른 장비 이동 — `git pull` 후 `claude` → `/resume-session` 으로 이어가기
