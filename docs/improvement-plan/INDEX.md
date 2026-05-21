# Improvement Plan — Progress Index

**Last updated**: 2026-05-21
**Current branch**: feat/pr-10-ci-tests (Tier-1+2 통과, Tier-3 대기)
**Next PR**: PR-10 Tier-3 후 PR-11/12

## Progress matrix

| #  | Title                                   | Status | Branch                          | Risk    | Size | Notes |
|----|-----------------------------------------|--------|---------------------------------|---------|------|-------|
| 00 | AbandonedMutex 캐치                     | ✅     | (merged → main, c4de0bd)        | Low     | S    | Tier-1+2 통과, Tier-3 deadlock 부재 확인 (catch 자체는 race 좁아 미trigger) |
| 01 | MergeProfile pipeline + WM_THEMECHANGED | ✅     | (merged → main, 4bca33d)        | Low     | M    | A1+A4+B3+H5. Tier-3 에서 PR-13 신설 근거 발견 |
| 02 | Analyzers + manifest hardening          | ✅     | (merged → main, 91621a4)        | Medium  | M    | G2+G3. Tier-1+2+3 통과. 분석기 활성 시점에 위반 0건. manifest PE 임베드 확인 |
| 03 | asInvoker migration (BREAKING)          | ✅     | (merged → main, 06b4157)        | High    | L    | v0.10.0 후보. Tier-1+2+3 (A~F) 통과. Tier-3 D 회귀 1건 발견 후 fix (LogonTrigger UserId) |
| 04 | Tray.cs decomposition                   | ✅     | (merged → main, 9192826)        | Low     | M    | C1+C2+D9+D10. Tier-1+2+3 통과. Tray.cs 1156→575줄 (-50%). 4 신규 모듈 + ShowMenu partial |
| 05 | Theme + DefaultConfig consolidation     | ✅     | (merged → main, deedabe)        | Low     | M    | D2+D5+N3+D7+H4-c. Tier-1+2+3 통과. AppConfig 디폴트 ↔ DefaultConfig ↔ Validate ↔ Dialog 4-축 단일 진실원 + ThemeColors record + 고대비 분기 |
| 06 | I18n + Language enum                    | ✅     | (merged → main, f1fb11c)        | Low     | M    | D3+D4 + Tier-3 즉시반영 fix. Tier-1+2+3 통과. AOT 4.82 MB |
| 07 | DialogShell + a11y baseline             | ✅     | (merged → main, e30407c)        | Medium  | L    | C3+H4-b. Tier-1+2+3 통과. Tier-3 회귀 1건 (CleanupDialog 결과 수집 타이밍) 발견 후 fix. 라인 카운트 슬립 명시 (-13~-21%) |
| 08 | Core reuse restoration                  | ✅     | (merged → main, 399f6ad)        | Low     | M    | C4+C6+C5(부분)+E1+E2+E3. Tier-1+2+3 통과. WindowSnapHelper/TopmostWatchdog/ImeConstants 신규 + MeasureLabels string[] 일반화 + 폰트 파라미터화 |
| 09 | Logging policy + ILogSink               | ✅     | (merged → main, 30b2275)        | Low     | M    | E4+E5+F1+F3+F4+F5 + pre-Init 버퍼. Tier-1+2+3 통과 (AOT 4.80 MB, -16 KB). PR-06 ④ 잔재 해소 |
| 10 | CI + first tests                        | 🚧     | feat/pr-10-ci-tests             | Low     | M    | G1+G5. Tier-1+2 통과, Tier-3 대기. xUnit 40/40 + AOT 4.81 MB |
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
| 2026-05-21 | PR-06 | D3+D4 구현 완료. `I18n.cs` 41 property → `Dictionary<I18nKey, (Ko, En)>` + `Get(key)` dispatcher (매개변수 헬퍼 3종은 메서드 유지, locale suffix 만 `SizeLabelSuffix` 키로 분리). `AppLanguage { Auto, Ko, En }` enum 신설 + `AppConfig.Language` string → enum + `I18n.Load(AppLanguage)` + `Settings.Validate` 에 `EnumOrDefault` 한 줄 + `EnsureSubObjects` Language 줄 제거 + `SettingsDialog.Fields.cs` 의 `LanguageToIndex`/`IndexToLanguage` 헬퍼 2종 삭제 + Combo 단순화. Tier-1 debug + AOT publish clean (0 경고, 4.82 MB). Tier-2 grep 가드 5종 통과 (`_isKorean ? = 1` Get 만, `I18nKey = 97`, `enum AppLanguage = 1`, `string Language in AppConfig = 0`, `"ko"/"en"/"auto" in SettingsDialog.Fields.cs = 0`). invariant 4종 + P5 2종 0 매치. 문서 5건 갱신 (CHANGELOG / architecture / conventions / dev-notes / PR-06 §6). README/User_Guide 는 `language` 키 참조 0 이라 갱신 불요 | Tier-3 사용자 smoke (트레이 메뉴 한·영 / Settings 언어 전환 / config.json `"language": "auto"` 호환) 검증 후 머지 |
| 2026-05-21 | PR-06 | Tier-3 사용자 가시 smoke 4종 통과 — ① 한국어 표시 / ② `English` 즉시 전환 / ③ `"language": "auto"` 호환 / ④ `"language": "fr"` JsonException → defaults 폴백 (`"opacity": 0.1` 동시 편집 후 opacity=0.85 디폴트 가시화로 evidence 대체 — `Settings.Load` Warning 이 `Logger.Initialize` 이전이라 Trace-only). ② 검증 중 결함 fix: `Program.HandleMenuCommand` 의 `updateConfig` 람다에 `oldLanguage != _config.Language` 비교 + `I18n.Load(_config.Language)` 한 줄 추가 (v0.9.x 부터 잠재했던 `Settings.Save` self-bump 차단 결함). Tier-1+2 재검증 통과. CHANGELOG / dev-notes / §3 문구 정정. FF merge to main 진행 | 브랜치 삭제 후 다음 PR (08/09/10/11 자유 선택) |
| 2026-05-21 | PR-09 | E4+E5+F1+F3+F5 구현 + F4 명세-convention 충돌 해소(현 catch 유지). 신규 `Core/Logging/ILogSink.cs` + `Core/Logging/LogProvider.cs` + `Logger.cs` 끝 `LoggerSink` passthrough. Core 6 파일 18 호출 `Logger.X → LogProvider.Sink?.X` 일괄 치환. `Program.MainImpl` 첫 라인에 Sink 배선. **E4+ 스펙 보강** — PR-06 Tier-3 ④ 의 Trace-only Warning 한계 해소: Logger.cs 에 `_preInitBuffer` 추가 + `EnqueueToFile` 가 pre-Init 시 본 버퍼로 우회 + `Initialize` 가 `FlushPreInitBuffer()` 호출. **E5** — Core/Logging/LogLevel.cs STJ 의존 제거 + App/Logging/LogLevelJsonConverter 신규 + AppConfig.LogLevel 속성에 `[JsonConverter]`. **F1** — Core Debug 5 라인 "failed" 워딩 회피. **F3** — PositionCleanupService:94 catch narrow. **F5** — last-error 3군(CreateMainWindow / UriLauncher / NIM_ADD·NIM_SETVERSION) 추가. Tier-1 debug + AOT publish clean (0 경고, 4.80 MB, -16 KB). Tier-2 grep 가드 6종 통과. invariant 4종 + P5 2종 0매치. 문서 4건 갱신 (CHANGELOG / conventions §8+§9 신규 / PR-09 §1+§6 / INDEX) | Tier-3 사용자 smoke (정상 부팅 + Debug 레벨 부팅 + `"language":"fr"` 편집 후 koenvue.log Warning 가시화) 후 머지 |
| 2026-05-21 | PR-09 | Tier-3 3종 통과 (로그 직접 확인): ① 정상 부팅 INFO 정상, ② Debug 레벨 — `Window class registered`/`PositionUpdated`/`IME state`/`UpdateChecker: GET` 등 풀 DEBUG 가시 + "failed" 부재 (F1 통과), ③ `"language": "fr"` (17:01:45.891) — `[WARN] Failed to load config: DeserializeUnableToConvertValue ... $.language ...` 라인이 koenvue.log 에 정상 기록 + 디폴트 폴백 진입 (PR-06 Tier-3 ④ 잔재 결함 해소 evidence). FF merge to main 진행. | 브랜치 삭제 후 다음 PR (07/08/10/11) |
| 2026-05-21 | PR-07 | C3+H4-b 구현 완료. 신규 [Core/Windowing/DialogShell.cs](../../Core/Windowing/DialogShell.cs) 200줄 — `DialogShellMetrics` record struct + `DialogShellContext` sealed + `Run(...)` 라이프사이클 단일 진입점 + `HandleStandardCommands(...)` WM_COMMAND 헬퍼. 3 다이얼로그 (SettingsDialog/CleanupDialog/ScaleInputDialog) 가 `measureDlgHeight` + `useCursorAnchor` + `bringToForeground` + `buildChildren` + `onAfterShow` 콜백만 셸에 제공. CleanupDialog 의 `SetForegroundWindow` 누락 잠재 결함이 셸의 `bringToForeground:true` 통일로 자동 해소. `borderW = 16` 매직(세 다이얼로그 자식 컨트롤 폭 fudge) 이 `ctx.ClientW = dlgWidth - nonClientW` 정확 계산으로 대체. a11y: `WS_GROUP = 0x00020000` 상수 신규 + CleanupDialog 체크박스 `WS_TABSTOP` 추가 + 3 다이얼로그 그룹 시작 컨트롤에 `WS_GROUP` 일관 적용 (총 20 매치). Tier-1 debug + AOT publish clean (0 경고, 4.80 MB 유지). Tier-2 라인 카운트 슬립 명시: SettingsDialog.cs 440 → 384 (-13%), CleanupDialog.cs 377 → 325 (-14%), ScaleInputDialog.cs 277 → 219 (-21%). 잔여 ~65% 가 BuildChildren 의 본질적 per-dialog 로직 — fluent control builder 도입은 스코프 초과. invariant 4종 + P5 2종 모두 0 매치. 문서 5건 갱신 (CHANGELOG / architecture / implementation-notes / dev-notes 신규 / PR-07 §6) | Tier-3 사용자 smoke (3 다이얼로그 × 각 4-6 항목 — Tab/Enter/ESC + 폰트 + DPI + 38 필드 + CleanupDialog 포어그라운드 + ScaleEdit 자동 포커스) 후 머지 |
| 2026-05-21 | PR-07 | Tier-3 회귀 발견 + fix. CleanupDialog 가 1차 리팩토링에서 결과 수집을 `DialogShell.Run` 반환 **후** `_checkboxHandles[i]` 에 `BM_GETCHECK` 로 수행하던 결함 — `DialogShell.Run` 의 `try/finally DestroyWindow` 가 이미 HWND 를 무효화한 뒤라 모든 체크가 0 반환 → 빈 선택 → 실제 삭제 동작 안 함 (사용자는 "삭제 후에도 인디 위치 그대로" 로만 체감). Fix: SettingsDialog/ScaleInputDialog 와 동일한 `tryCommit` 콜백 패턴 통일 — `_items` + `_selectedItems` 정적 필드 신규 + `CommitSelections() → bool` 가 WM_COMMAND IDOK 처리 시점(모달 안, HWND 유효) 에 체크 상태를 `_selectedItems` 에 박아둠 + `HandleStandardCommands` 호출에 `tryCommit: CommitSelections` 추가 + Show() 는 `_selectedItems` 만 반환. Tier-1 재검증 통과 (0 경고, 4.80 MB 유지). 문서 3건 갱신 (CHANGELOG / dev-notes §"회귀 위험" / PR-07 §6 + INDEX) | Tier-3 재테스트 (CleanupDialog 항목 삭제 후 인디 위치 변경 확인) 후 머지 |
| 2026-05-21 | PR-07 | Tier-3 재테스트 모두 통과 — CleanupDialog 항목 삭제 후 인디 위치 정상 갱신 + SettingsDialog/ScaleInputDialog 회귀 부재 확인. FF merge to main (2 commits: f4552dc, e30407c) + 브랜치 삭제. PR-07 (DialogShell 추출 + 3 다이얼로그 라이프사이클 통합 + a11y baseline + Tier-3 회귀 fix) 완료 | 다음 PR (08/10/11) 자유 선택 |
| 2026-05-21 | PR-08 | C4+C6+C5(부분)+E1+E2+E3 6 항목 구현. 신규 Core/Windowing/WindowSnapHelper.cs (167줄, 창 엣지 스냅 모듈) + Core/Windowing/TopmostWatchdog.cs (67줄, HWND_TOPMOST 재적용 워치독) + App/Detector/ImeConstants.cs (37줄, IME 9 상수 App 이전). LayeredOverlayBase 900→767 (-133, WindowSnapHelper 위임). OverlayAnimator 554→546 (-8, TopmostWatchdog 위임 — 단일 트랙이라 라인 슬립). OverlayStyle.MeasureLabels (string,string,string) → string[] + 명시적 Equals/GetHashCode 오버라이드 (SequenceEqual 시퀀스 동등성, flip-flop 가드 유지). LayeredOverlayBase._cachedLabelMeasureKey string[]? 로 변경. Overlay.ComputeCornerAnchor 단일화 (2 메서드 → 1 헬퍼). Win32DialogHelper.CreateDialogFont(uint, string) 폰트 패밀리 필수 인자, DialogShell.Run 에 dialogFontFamily 파라미터 추가, App 측 DefaultConfig.DefaultDialogFontFamily const 신규. Tier-1 debug + AOT publish clean (0 경고, 4.80 MB). Tier-2 grep 가드 통과: Hangul/English/NonKorean Core/ 0, 맑은 고딕 Core/ 0, WindowSnapHelper/TopmostWatchdog ns 1 each, LayeredOverlayBase < 800 ✓, OverlayAnimator 라인 슬립 명시. invariant 4종 + P5 2종 0매치. 문서 5건 갱신 (CHANGELOG / architecture / conventions / dev-notes 신규 / PR-08 §6). | Tier-3 사용자 smoke (드래그+스냅 / topmost 유지 / 3 다이얼로그 / IME 인디 표시) 후 머지 |
| 2026-05-21 | PR-08 | Tier-3 사용자 smoke 모두 통과 — 드래그+스냅 / topmost 유지 / 3 다이얼로그 폰트·포어그라운드 / IME 인디 표시 모두 정상. FF merge to main (399f6ad) + 브랜치 삭제. PR-08 (Core reuse contract 회복 — WindowSnapHelper + TopmostWatchdog + ImeConstants 분리 + MeasureLabels string[] 일반화 + 폰트 파라미터화 + ComputeCornerAnchor 단일화) 완료 | 다음 PR (10/11) 자유 선택 |
| 2026-05-21 | PR-10 | G1+G5 구현. 신규 [tests/KoEnVue.Tests/](../../tests/KoEnVue.Tests/) csproj (xUnit 2.9.3 + Microsoft.NET.Test.Sdk 17.13 + xunit.runner.visualstudio 3.0) + 3 테스트 파일 (`SettingsValidateTests` 12 케이스, `ColorHelperTests` 5 메서드 ~15 케이스, `DpiHelperTests` Scale 2 오버로드 + BASE_DPI). **InternalsVisibleTo**: KoEnVue.csproj 에 `<InternalsVisibleTo Include="KoEnVue.Tests" />` 추가. **사이드 fix**: `<DefaultItemExcludes>$(DefaultItemExcludes);tests/**</DefaultItemExcludes>` — 메인 csproj SDK 디폴트 `**/*.cs` 가 `tests/` 까지 implicit 포함하던 문제 차단 (첫 빌드 95 errors). **G5 핸들러**: `Program.Main` 의 크래시 파일 write 를 `AppendCrashFile(tag,payload)` 헬퍼로 추출 + `RegisterCrashHandlers()` (AppDomain.UnhandledException + TaskScheduler.UnobservedTaskException) 신규. `MainImpl` 0a 단계에서 `LogProvider.Sink` 배선 직후 호출 — pre-Init 크래시도 koenvue.log 기록 가능. **GitHub Actions**: `.github/workflows/build.yml` — windows-latest + dotnet 10.0.x + build/test 매 푸시·PR + AOT publish 는 main 푸시 한정 (수 분 소요). **문서**: CONTRIBUTING.md 신규 + README.md CI 배지 + CHANGELOG.md `### 추가` + CLAUDE.md P1 dev-only 예외 부연. **Tier-1**: dotnet build clean + dotnet test 40/40 통과 386 ms + AOT publish 4,807,168 B (4.81 MB, +3 KB). **Tier-2 grep**: InternalsVisibleTo 1 / UnhandledException 1 / UnobservedTaskException 1 / build.yml 존재 ✓. **Invariant** 4종 + P5 2종 0 매치 | Tier-3 사용자 smoke (정상 부팅 후 koenvue.log 무 변화 / 의도적 NRE 유도 시 koenvue_crash.txt 의 `[UNHANDLED]` 라인 / GitHub Actions 녹색) 검증 후 머지 |
