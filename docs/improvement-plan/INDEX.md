# Improvement Plan — Progress Index

**Last updated**: 2026-05-21
**Current branch**: feat/pr-13-per-app-rendering (PR-13 구현 완료, 머지 대기)
**Next PR**: PR-13 머지 후 PR-03 (BREAKING, asInvoker) — 또는 다른 PR 선택

## Progress matrix

| #  | Title                                   | Status | Branch                          | Risk    | Size | Notes |
|----|-----------------------------------------|--------|---------------------------------|---------|------|-------|
| 00 | AbandonedMutex 캐치                     | ✅     | (merged → main, c4de0bd)        | Low     | S    | Tier-1+2 통과, Tier-3 deadlock 부재 확인 (catch 자체는 race 좁아 미trigger) |
| 01 | MergeProfile pipeline + WM_THEMECHANGED | ✅     | (merged → main, 4bca33d)        | Low     | M    | A1+A4+B3+H5. Tier-3 에서 PR-13 신설 근거 발견 |
| 02 | Analyzers + manifest hardening          | ✅     | (merged → main, 91621a4)        | Medium  | M    | G2+G3. Tier-1+2+3 통과. 분석기 활성 시점에 위반 0건. manifest PE 임베드 확인 |
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
| 13 | Per-app rendering config wiring         | 🚧     | feat/pr-13-per-app-rendering    | Medium  | M    | PR-01 Tier-3 발견. Tier-1+2 통과. Tier-3 수동 smoke 대기 (theme:dark / opacity / enabled:false 시나리오) |

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
| 2026-05-21 | PR-00 | Tier-3 smoke 실행 — taskkill /f (관리자) + 즉시 재실행 = deadlock 없음. 로그에 WARN 부재 (catch 자체는 race 좁아 미trigger). FF merge to main (3 commits: 8d08611, 28ba7f4, c4de0bd) + 브랜치 삭제 | 새 세션에서 PR-01 시작 |
| 2026-05-21 | PR-01 | A1+H5+B3+A4 4건 구현 + Tier-1 (debug + AOT publish 4.47 MB) + Tier-2 grep 가드 4종 통과 + invariant 4종 0매치. 문서 4건 갱신 (CHANGELOG / PRD §5.4 신설 / implementation-notes 프로필 머지 파이프라인 / dev-notes 2건) | Tier-3 smoke |
| 2026-05-21 | PR-01 | Tier-3 수동 smoke: ① 정상 부팅 ✅, ② 프로필 theme:dark 색 변화 없음 ❌, ③ overlay_class_name 폴백 정상 (Warning 은 logger init 이전이라 파일 미기록), ④ system accent 변경 색 변화 없음 ❌. ②/④ 분석: ②는 별개 미배선 결함 (감지→메인 스레드 `resolved` 마샬링 부재) → PR-13 신설, ④는 `GetSysColor(COLOR_HIGHLIGHT)` 데이터 소스 한계 (Win11 personalization). 본 PR 는 인프라 fix 로 partial-merge | INDEX/PRD/CHANGELOG/dev-notes 정정 + PR-13 stub + 머지 |
| 2026-05-21 | PR-02 | csproj 에 분석기 3종(`EnableAotAnalyzer`/`EnableTrimAnalyzer`/`EnableSingleFileAnalyzer`) 활성 — 신규 경고 0건 (codebase 가 reflection-free 라 clean). app.manifest 에 `<compatibility>` Win10/11 GUID + `longPathAware` 추가, `gdiScaling` 의도적 미추가. Tier-1 (debug + AOT publish clean) + Tier-2 grep 가드 4종 + invariant 4종 0매치 통과. PE 매니페스트 임베드 확인. exe 크기 4.47 MB 유지. 문서 3건 갱신 (CHANGELOG / conventions / implementation-notes) | 사용자 검토 후 머지 |
| 2026-05-21 | PR-02 | Tier-3 수동 smoke 통과 (사용자 부팅 확인). FF merge to main (91621a4) + 브랜치 삭제 | 다음 PR (PR-03 또는 PR-13) 선택 대기 |
| 2026-05-21 | PR-13 | Option B 채택 — 메인 스레드에 `ResolveCurrent()` 헬퍼 + `Overlay.Show/UpdateColor` 시그니처 확장 + 18개 렌더 호출처 일괄 전환 + `DisplayMode`/`EventTriggers` 분기도 resolved 기반. Tier-1 debug + clean AOT publish 0 경고 / 0 오류 (4.47 MB). Tier-2 grep 가드 통과 (ResolveCurrent\|ResolveForApp 18 매치, `Animation.TriggerShow.*_config` 0). invariant 4종 0매치. 문서 4건 갱신 | Tier-3 smoke 사용자 검증 후 머지 |
