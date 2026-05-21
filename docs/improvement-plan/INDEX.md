# Improvement Plan — Progress Index

**Last updated**: 2026-05-21
**Current branch**: feat/pr-00-mutex-abandoned (코드 완료, Tier-1+2 통과, NuGet.config 처분 + 머지 결정 대기)
**Next PR**: PR-01

## Progress matrix

| #  | Title                                   | Status | Branch                          | Risk    | Size | Notes |
|----|-----------------------------------------|--------|---------------------------------|---------|------|-------|
| 00 | AbandonedMutex 캐치                     | ✅     | feat/pr-00-mutex-abandoned      | Low     | S    | Tier-1+2 통과. 머지 + NuGet.config 결정 대기 |
| 01 | MergeProfile pipeline + WM_THEMECHANGED | ⏳     | feat/pr-01-merge-profile        | Low     | M    | A1+A4+B3+H5 묶음 |
| 02 | Analyzers + manifest hardening          | ⏳     | feat/pr-02-analyzers-manifest   | Medium  | M    | G2+G3 |
| 03 | asInvoker migration (BREAKING)          | ⏳     | feat/pr-03-asinvoker            | High    | L    | v0.10.0 후보. CLAUDE.md P5 갱신 |
| 04 | Tray.cs decomposition                   | ⏳     | feat/pr-04-tray-decomposition   | Low     | M    | C1+C2+D9+D10 |
| 05 | Theme + DefaultConfig consolidation     | ⏳     | feat/pr-05-theme-and-defaults   | Low     | M    | D2+D5+N3+D7+H4-c |
| 06 | I18n + Language enum                    | ⏳     | feat/pr-06-i18n-language        | Low     | M    | D3+D4 |
| 07 | DialogShell + a11y baseline             | ⏳     | feat/pr-07-dialog-shell         | Medium  | L    | C3+H4-b. 수동 smoke 필요 |
| 08 | Core reuse restoration                  | ⏳     | feat/pr-08-core-reuse           | Low     | M    | C4+C6+C5(TopmostWatchdog만)+E1+E2+E3 |
| 09 | Logging policy + ILogSink               | ⏳     | feat/pr-09-logging-policy       | Low     | M    | E4+E5+F1+F3+F4+F5 |
| 10 | CI + first tests                        | ⏳     | feat/pr-10-ci-tests             | Low     | M    | G1+G5 |
| 11 | Version single-source + SHA256 release  | ⏳     | feat/pr-11-version-signing      | Medium  | L    | D6+G4 |
| 12 | Documentation alignment                 | ⏳     | feat/pr-12-docs                 | Low     | S    | H1+H2+H3 |

Legend: ⏳ pending · 🚧 in progress · ✅ merged · ⏸ blocked · ❌ aborted
Size: S ≤30분 · M 1-2시간 · L 반나절+

## Dependency graph

```
PR-00 ─┐
PR-01 ─┤
PR-02 ─┴─→ PR-03 (BREAKING) ─┬─→ PR-04 ─┐
                              │           ├─→ PR-07 ─┐
                              ├─→ PR-05 ─┤            ├─→ PR-12
                              ├─→ PR-06 ─┤            │
                              ├─→ PR-08 ─┘            │
                              ├─→ PR-09 ──────────────┤
                              ├─→ PR-10 ──────────────┤
                              └─→ PR-11 ──────────────┘
```

- PR 00/01/02는 PR-03 전에 (안전 게이트)
- PR-03 이후 PR 04-11는 서로 독립, 순서 자유
- PR-07은 PR-04 (Tray 분해 후가 깔끔)
- PR-12는 모든 코드 PR 후 (문서 통합)

## Verification invariants (every PR)

```bash
# Tier 1 — 빌드
dotnet build
dotnet publish -r win-x64 -c Release

# Tier 1 — invariant 4종 (0 매치)
git grep "KoEnVue\.App"     Core/
git grep "ImeState"         Core/
git grep "NonKoreanImeMode" Core/
git grep "DllImport"
```

PR별 Tier-2 grep 가드 + Tier-3 수동 smoke은 각 `PR-NN-*.md` §3 참조.

## Sessions log

| Date | PR | What happened | Next |
|---|---|---|---|
| 2026-05-21 | setup | improvement-plan 셋업 완료 (16 파일 + 메모리) | PR-00 시작 |
| 2026-05-21 | PR-00 | TryAcquireMutex catch 추가 + CHANGELOG + dev-notes. invariant 4종 + Tier-2 grep 통과. NuGet.config(nuget.org) 추가 후 Tier-1 빌드/AOT publish 클린 (4.47 MB exe) | NuGet.config 처분 결정 → 머지 → PR-01 |
