# Improvement Plan — Progress Index

**Last updated**: 2026-05-21
**Current branch**: main (idle, PR-05 머지 완료)
**Next PR**: PR-06/08/09/10/11 자유 선택

## Progress matrix

| #  | Title                                   | Status | Branch                          | Risk    | Size | Notes |
|----|-----------------------------------------|--------|---------------------------------|---------|------|-------|
| 00 | AbandonedMutex 캐치                     | ✅     | (merged → main, c4de0bd)        | Low     | S    | Tier-1+2 통과, Tier-3 deadlock 부재 확인 (catch 자체는 race 좁아 미trigger) |
| 01 | MergeProfile pipeline + WM_THEMECHANGED | ✅     | (merged → main, 4bca33d)        | Low     | M    | A1+A4+B3+H5. Tier-3 에서 PR-13 신설 근거 발견 |
| 02 | Analyzers + manifest hardening          | ✅     | (merged → main, 91621a4)        | Medium  | M    | G2+G3. Tier-1+2+3 통과. 분석기 활성 시점에 위반 0건. manifest PE 임베드 확인 |
| 03 | asInvoker migration (BREAKING)          | ✅     | (merged → main, 06b4157)        | High    | L    | v0.10.0 후보. Tier-1+2+3 (A~F) 통과. Tier-3 D 회귀 1건 발견 후 fix (LogonTrigger UserId) |
| 04 | Tray.cs decomposition                   | ✅     | (merged → main, 9192826)        | Low     | M    | C1+C2+D9+D10. Tier-1+2+3 통과. Tray.cs 1156→575줄 (-50%). 4 신규 모듈 + ShowMenu partial |
| 05 | Theme + DefaultConfig consolidation     | ✅     | (merged → main, deedabe)        | Low     | M    | D2+D5+N3+D7+H4-c. Tier-1+2+3 통과. AppConfig 디폴트 ↔ DefaultConfig ↔ Validate ↔ Dialog 4-축 단일 진실원 + ThemeColors record + 고대비 분기 |
| 06 | I18n + Language enum                    | ⏳     | feat/pr-06-i18n-language        | Low     | M    | D3+D4 |
| 07 | DialogShell + a11y baseline             | ⏳     | feat/pr-07-dialog-shell         | Medium  | L    | C3+H4-b. 수동 smoke 필요 |
| 08 | Core reuse restoration                  | ⏳     | feat/pr-08-core-reuse           | Low     | M    | C4+C6+C5(TopmostWatchdog만)+E1+E2+E3 |
| 09 | Logging policy + ILogSink               | ⏳     | feat/pr-09-logging-policy       | Low     | M    | E4+E5+F1+F3+F4+F5 |
| 10 | CI + first tests                        | ⏳     | feat/pr-10-ci-tests             | Low     | M    | G1+G5 |
| 11 | Version single-source + SHA256 release  | ⏳     | feat/pr-11-version-signing      | Medium  | L    | D6+G4 |
| 12 | Documentation alignment                 | ⏳     | feat/pr-12-docs                 | Low     | S    | H1+H2+H3 |
| 13 | Per-app rendering config wiring         | ✅     | (merged → main, 11c3ec5)        | Medium  | M    | PR-01 Tier-3 발견. Tier-1+2+3 통과. theme/opacity/enabled:false 모두 사용자 가시 확인 |
| 14 | Win11 accent — DwmGetColorizationColor  | ✅     | (merged → main, 4818cd2)        | Low     | S-M  | PR-01 Tier-3 ④ 해소. Tier-1+2+3 통과. Windows 강조색 변경 시 인디 색 즉시 전환 가시 확인 |

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
| 2026-05-21 | PR-13 | Tier-3 수동 smoke 통과 — 사용자 가시 확인 (notepad 다크 / opacity 0.3 / enabled:false). 사용자 1차 config 오타 실패 → 정정 후 재시험 정상. FF merge to main (11c3ec5) + 브랜치 삭제 | 다음 PR 선택 대기 |
| 2026-05-21 | PR-14 | PR-01 Tier-3 ④ 잔존 결함 해소 PR 신설. `Dwmapi.DwmGetColorizationColor` + `TryGetColorizationRgb` 헬퍼 추가. `ApplySystemTheme` 가 DWM 우선 + `GetSysColor(COLOR_HIGHLIGHT)` 폴백 2단 분기. `WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320` 상수 추가 + WndProc fall-through 머지. Tier-1 debug + clean AOT publish 모두 경고 0 (4.47 MB). Tier-2 grep 가드 4종 통과 (DwmGetColorizationColor Core/ 2, WM_DWMCOLORIZATIONCOLORCHANGED Program.cs 1 + Win32Types.cs 1, TryGetColorizationRgb 1). invariant 4종 0매치. 문서 4건 갱신 | Tier-3 사용자 smoke (강조색 변경 시 인디 색 즉시 전환) 후 머지 |
| 2026-05-21 | PR-14 | Tier-3 수동 smoke 통과 (사용자 가시 확인 — 강조색 변경 시 인디 색 즉시 전환). FF merge to main (4818cd2) + 브랜치 삭제. PR-01 Tier-3 잔존 결함 전부 해소 | 다음 PR 선택 대기 |
| 2026-05-21 | PR-03 | BREAKING `requireAdministrator → asInvoker` + schtasks `LeastPrivilege` + App/Config/PortablePath.cs 신규 (BaseDirectory ↔ %LOCALAPPDATA%\KoEnVue fallback + SanitizeLogPath) + Settings/Logger 배선 + Tray.cs tempPath Guid:N + CreateNew + SyncStartupPathCore 가 RunLevel 동기화. CLAUDE.md P5 갱신 + invariant 2종 추가. 문서 6건 + dev-notes. Tier-1 debug + AOT publish clean (4.48 MB, +4 KB). Tier-2 grep 가드 4종 통과 (코멘트 단어 우회 정정 후). invariant 4종 0매치 | Tier-3 다중 시나리오 (A~F) 사용자 smoke 검증 후 머지 |
| 2026-05-21 | PR-03 | Tier-3 D 회귀 발견 + fix. schtasks /create 가 ExitCode=1 "액세스가 거부되었습니다" 로 silent 거부. 진단 가시화 (RunSchtasks ExitCode/STDERR 로깅 + post-check) 후 root cause 확정: `<LogonTrigger>` 가 `<UserId>` 없이 비면 ANY-user trigger 로 해석되어 admin 요구. v0.9.x admin 토큰이 통과시켜 줬을 뿐. Fix: LogonTrigger 안에 `<UserId>{domain}\{user}</UserId>` 추가 (Principal `<UserId>` 는 비워둠 — 명시 시 SID lookup 검증에서 admin 요구). PR-03 §6 + dev-notes 사후 발견 절 + CHANGELOG 인라인 fix 메모. Tier-1/2 재검증 통과. commit 8778b61 | Tier-3 나머지 시나리오 (A/B/C/E/F) 사용자 검증 후 머지 |
| 2026-05-21 | PR-03 | Tier-3 시나리오 A (정상 폴더) / B (Program Files fallback) / C (v0.9.x → v0.10.0 마이그) / D (schtasks 등록) / E (log_file_path sanitize) / F (tempPath GUID) 모두 사용자 가시 검증 통과. E 의 invalid JSON escape 사이드 — STJ 가 JsonReaderException 정상 throw 확인 (`\W` 직접 테스트), 인메모리 race 로 사용자 입력이 silent 정정되는 현상은 v0.9.x 부터의 기존 정책. FF merge to main (3 commits: 3ca644d, 8778b61, 06b4157) + 브랜치 삭제. PR-03 (BREAKING `requireAdministrator → asInvoker` + %LOCALAPPDATA% fallback + schtasks LeastPrivilege) 완료 | 다음 PR (04/05/06/08/09/10/11 자유 선택) |
| 2026-05-21 | PR-04 | Tray.cs god class 분해 — 4개 신규 모듈 (`Core/Xml/XmlEntityCodec` / `Core/Shell/UriLauncher` / `App/Startup/StartupTaskManager` / `App/Config/PositionCleanupService`) + `App/UI/Tray.Menu.cs` partial. Tray.cs 1156 → 575 줄 (-50%). Program.cs 의 `Tray.SyncStartupPathAsync` → `StartupTaskManager.SyncStartupPathAsync`. Tier-1 debug + AOT publish clean (0 경고, 4.77 MB 유지). Tier-2 grep 가드 6종 통과 (xmldoc 단어 회피 + ShowMenu partial 분리로 line 가드 만족). invariant 4종 0매치. 문서 5건 갱신 (CHANGELOG / architecture.md 모듈 3종 + Tray 갱신 / PR-04 §2/§3/§6 / dev-notes 신규 / INDEX) | Tier-3 사용자 smoke 검증 후 머지 |
| 2026-05-21 | PR-04 | Tier-3 사용자 수동 smoke 4항목 (트레이 메뉴 / 시작 등록 토글 / 홈페이지·설정파일·업데이트 / CleanupDialog) 모두 통과. FF merge to main (9192826) + 브랜치 삭제. PR-04 (Tray.cs god class 분해 1156→575) 완료 | 다음 PR (05/06/08/09/10/11 자유 선택) |
| 2026-05-21 | PR-05 | D2+D5+N3+D7+H4-c 5건 묶음. `DefaultConfig` 5건 rename (Fade/Highlight/Poll) + Min/Max 16쌍 32 const 신규 + AppConfig 6쌍 inline 디폴트 → const 참조. `Settings.Validate` clamp 18 리터럴 + `SettingsDialog.Fields` 13 리터럴 + `ScaleInputDialog` 2 리터럴 → 모두 `DefaultConfig.Min/MaxX` 참조. `ThemePresets` 의 4 preset 96 줄 → `record ThemeColors` + Dictionary 16 줄. `ApplySystemTheme` 에 고대비 (`SPI_GETHIGHCONTRAST` / `HCF_HIGHCONTRASTON`) 분기 추가 — `User32.IsHighContrastEnabled()` 헬퍼 + `SystemParametersInfoHighContrast` `[LibraryImport]` 추가. Tier-1 debug + AOT publish clean (0 경고, 4.77 MB). Tier-2 grep 가드 5종 통과 (rename-away 0 매치 / `record ThemeColors` 1 / `SPI_GETHIGHCONTRAST` in User32.cs 3 / `Min/MaxPollMs` 2 / AppConfig 리터럴 0). invariant 4종 + P5 2종 모두 0 매치. 문서 4건 갱신 (CHANGELOG / architecture / conventions P4 sub-rule / PR-05 §6) | Tier-3 사용자 smoke (테마 4종 / Custom 왕복 / 고대비 / SettingsDialog range) 검증 후 머지 |
| 2026-05-21 | PR-05 | Tier-3 수동 smoke 4항목 (테마 4종 전환 / Custom 왕복 / 고대비 모드 / SettingsDialog range) 모두 사용자 가시 통과. FF merge to main (deedabe) + 브랜치 삭제. PR-05 (DefaultConfig 단일 진실원 + ThemeColors record + 고대비 분기) 완료 | 다음 PR (06/08/09/10/11 자유 선택) |
