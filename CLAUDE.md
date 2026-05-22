# CLAUDE.md — KoEnVue

Windows IME state indicator (한 / En / EN). C# 14 / .NET 10 + NativeAOT single exe (~4.7 MB). Zero external NuGet.

## Hard rules (P1–P6)

| Rule | What |
|------|------|
| P1 | Zero external NuGet in release exe. .NET 10 BCL + Win32 API only. tests/ 는 dev-only 예외 |
| P2 | UI 한국어, 로그/config 키 영어 |
| P3 | const/enum/config — no magic numbers, no string compare |
| P4 | 하나의 구현만 — 공유는 `Core/` |
| P5 | app.manifest=asInvoker; `%LOCALAPPDATA%\KoEnVue\` fallback |
| P6 | `App/ → Core/` 단방향 의존 |

`[DllImport]` 금지 — `[LibraryImport]` 사용. invariant grep 은 [docs/conventions.md](docs/conventions.md).

## Workflow

- **빌드 = 항상 둘 다**: `dotnet build` (debug) + `dotnet publish -r win-x64 -c Release` (AOT). 한쪽만 하면 release exe outdated.
- **커밋 = 항상 푸시까지**: `git commit` 후 즉시 `git push`. PostToolUse hook 이 자동 처리 — wip 커밋도 동일.

## Documentation map

| File | Purpose |
|------|---------|
| [docs/harness.md](docs/harness.md) | Claude Code 하네스 (subagents, hooks, sessions) |
| [docs/INDEX.md](docs/INDEX.md) | 문서 전체 인덱스 |
| [docs/architecture.md](docs/architecture.md) | Core/App 모듈 분리 |
| [docs/conventions.md](docs/conventions.md) | P1–P6 enforcement, invariant grep |
