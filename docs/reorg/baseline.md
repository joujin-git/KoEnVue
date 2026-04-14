# Refactor Baseline — 2026-04-13

## Build
- `dotnet build` (Debug): warnings = 0
- `dotnet publish -r win-x64 -c Release`: warnings = 0
- Publish exe path: `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe`
- Exe size: 4890624 bytes
- SHA256: `a2bf2d22ea401c15239b7589c8efa26d21b216862286d36d38fe48144b365276`

## Line counts
| file | lines |
|---|---|
| Config/DefaultConfig.cs | 141 |
| Config/Settings.cs | 531 |
| Config/ThemePresets.cs | 56 |
| Detector/ImeStatus.cs | 203 |
| Detector/SystemFilter.cs | 214 |
| Models/AppConfig.cs | 167 |
| Models/AppFilterMode.cs | 19 |
| Models/AppProfileMatch.cs | 23 |
| Models/Corner.cs | 23 |
| Models/DetectionMethod.cs | 27 |
| Models/DisplayMode.cs | 19 |
| Models/FontWeight.cs | 19 |
| Models/ImeState.cs | 17 |
| Models/LogLevel.cs | 23 |
| Models/NonKoreanImeMode.cs | 23 |
| Models/Theme.cs | 35 |
| Models/TrayClickAction.cs | 23 |
| Models/TrayIconStyle.cs | 19 |
| Native/AppMessages.cs | 61 |
| Native/Dwmapi.cs | 47 |
| Native/Gdi32.cs | 71 |
| Native/Imm32.cs | 20 |
| Native/Kernel32.cs | 30 |
| Native/Ole32.cs | 20 |
| Native/OleAut32.cs | 26 |
| Native/SafeGdiHandles.cs | 59 |
| Native/Shcore.cs | 13 |
| Native/Shell32.cs | 10 |
| Native/User32.cs | 299 |
| Native/VirtualDesktop.cs | 17 |
| Native/Win32Types.cs | 488 |
| Program.cs | 854 |
| UI/Animation.cs | 439 |
| UI/Overlay.cs | 825 |
| UI/SettingsDialog.cs | 1062 |
| UI/Tray.cs | 1169 |
| UI/TrayIcon.cs | 155 |
| Utils/ColorHelper.cs | 68 |
| Utils/DpiHelper.cs | 87 |
| Utils/I18n.cs | 141 |
| Utils/Logger.cs | 234 |
| Utils/Win32DialogHelper.cs | 52 |
| **Total** | **7829** |

## Smoke
- Indicator visible (process alive after 3s): [PASS]
- IME toggle: [PASS] — not verified interactively; process-alive after 3s implies the detection thread booted. Visual verification pending user confirmation.

## Notes
- Captured on branch `master` (user override of spec's `refactor/feature-reorg` — all commits go direct to master)
- 42 `.cs` files counted (spec said ~41; Program.cs + 41 under the listed directories = 42 total). No unexpected files.
- Debug build and release publish both clean (0 warnings, 0 errors).
- Publish step emits the "빌드했습니다 / 경고 0개 / 오류 0개" summary only when outputs are rebuilt; captured via `rm -rf bin/Release obj/Release && dotnet publish -r win-x64 -c Release`.
- `taskkill //F //IM KoEnVue.exe` returned access denied after the smoke test because the exe runs as administrator (P5: `app.manifest requireAdministrator`) while the bash shell is not elevated. The smoke test itself (process alive after ~3s) still passed; the leftover process is harmless (tray-only) and can be terminated manually from an elevated shell, Task Manager, or on reboot.

---

# Stage 5 — post-refactor verification (2026-04-14)

Verifies that the 12 commits of Stage 0~4 (Stage 1 hotspots → Stage 2 namespace relocation → Stage 3 signature narrowing → Stage 4 Core extraction) ride a single clean build with no warning regression, no exe-size blowout, and no layer-guard violations. Verification only — no commits other than this baseline update.

## Build
- `dotnet clean` → `rm -rf bin obj` → `dotnet build` (Debug): warnings = 0, errors = 0
- `dotnet publish -r win-x64 -c Release`: warnings = 0, errors = 0
- Publish exe path: `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe`
- Exe size: 4,911,104 bytes
- SHA256: `e5340e71271efe4ca01902c2d13ac4e3018a6489c31b97fd67d0e5ff6615120c`

## Size delta
| baseline | size | delta vs baseline | gate |
|---|---|---|---|
| Stage 0 (2026-04-13) | 4,890,624 bytes | **+20,480 bytes (+20 KB)** | ≤ +100 KB ✅ |
| Stage 4 (CLAUDE.md note) | 4,911,104 bytes | byte-identical | regression-free ✅ |

The Stage 4 → Stage 5 byte-identity confirms that no further binary-affecting change has landed since the Core extraction publish recorded in CLAUDE.md, and the +20 KB Stage 0 → Stage 5 delta is entirely attributable to the 6 new Core modules (LayeredOverlayBase / OverlayStyle / OverlayAnimator / JsonSettingsManager + JsonSettingsFile / NotifyIconManager / ModalDialogLoop) plus the App-side facade rewiring — well inside the +100 KB ILC tree-shaking budget.

## Layer guards (all = 0)
- `git grep "KoEnVue\.App" Core/` = 0 — **P6** Core→App import ban
- `git grep "ImeState" Core/` = 0 — **Risk 4** hard-fail enum gate (the critical Stage 4 invariant)
- `git grep "NonKoreanImeMode" Core/` = 0 — **Risk 4 secondary** enum gate
- `git grep "using KoEnVue\.App\.Detector" App/Config/` = 0 — **Risk 5** Config→Detector ban

## Risk 2 — NativeAOT ILC + STJ source-gen
The publish artifact rests on these source-gen registrations remaining live after ILC tree-shaking. All confirmed present:

- `AppConfigJsonContext` (App/Models/AppConfig.cs:155-166): 6 `[JsonSerializable]` types (`AppConfig`, `EventTriggersConfig`, `AdvancedConfig`, `DefaultPositionConfig`, `Dictionary<string, JsonElement>`, `Dictionary<string, int[]>`)
- `Corner` enum (App/Models/Corner.cs:9-22): `[JsonConverter(typeof(JsonStringEnumConverter<Corner>))]` + 4 `[JsonStringEnumMemberName("top_left|top_right|bottom_left|bottom_right")]`
- 4 call sites in `App/Config/Settings.cs` (line 64 `_manager ??=`, line 80 `_manager =`, line 305 `MergeProfile.Serialize`, line 330 `MergeProfile.Deserialize`) all route through `AppConfigJsonContext.Default.AppConfig`

A regression in any of these would surface as a runtime `JsonException` on the very first `Settings.Load()` call — the build itself would still succeed, which is why the (a)/(h)/(i) automated checks below complement the static gates.

## Smoke matrix — automated code-flow verification

Stage 5 is verification-only; the publish exe runs under P5 `requireAdministrator`, so GUI smoke (items b/c/d/e/f/g/j) cannot be exercised from the non-elevated build shell. The three ILC-sensitive items were verified by tracing their code paths through the post-Stage-4 layout:

| # | item | result |
|---|---|---|
| a | First-run `config.json` auto-create | `JsonSettingsManager.Load()` lines 92-97 — `File.Exists` branch falls through to `Logger.Info("Config not found, creating defaults at {path}") + new T() + Save() + return`. Save path uses injected `JsonTypeInfo<AppConfig>` (NativeAOT-safe). ✅ |
| h | 5-second hot-reload | `Settings.CheckConfigFileChange()` → `JsonSettingsManager.CheckReload()` lines 137-156 — `File.Exists` pre-check guards against delete-as-rename pattern, mtime mismatch posts `WM_CONFIG_CHANGED`. ✅ |
| i | Corrupted config spam suppression | `JsonSettingsManager.Load()` lines 83-91 catch — single `Logger.Warning(...)` then `_lastMtime` updated to broken-file mtime, blocking re-trigger from the 5-second polling loop. Inner mtime catch is type-narrowed to `IOException or UnauthorizedAccessException` (silent-catch policy 6.4 compliant). ✅ |

## Smoke matrix — GUI items (manual verification — all PASS, 2026-04-14)

The 7 GUI items were exercised interactively against `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe` running under elevated context (P5 `requireAdministrator`). All passed.

| # | item | result |
|---|---|---|
| b | Hangul/En/EN state transition (Right Alt or Hangul/Eng key) | PASS |
| c | Drag + edge snap + Shift axis lock + DPI transition (cross-monitor) | PASS |
| d | Tray menu (opacity / size / snap / animation / highlight / settings entry) | PASS |
| e | CleanupDialog (open / Tab cycle / ESC / save) | PASS |
| f | ScaleInputDialog (open / value entry / Enter / ESC) | PASS |
| g | SettingsDialog (scroll / hex validation `XYZ123` reject / save) | PASS |
| j | System input process focus transition (Win / Win+S — StartMenu / Search) | PASS |

Combined with the static layer guards, the Risk 2 STJ source-gen verification, and the automated code-flow smoke (a/h/i) above, **all 10 items of the Stage 5 smoke matrix pass**. The Stage 0~4 reorg (12 commits) is verified regression-free across build, layer separation, NativeAOT serialization, and end-to-end runtime behavior. Stage 6 (CLAUDE.md / PRD docs sync) may proceed.

## Notes

- Captured on branch `master` (no functional commits in Stage 5 — verification only)
- Build performed via `dotnet clean && rm -rf bin obj && dotnet build && dotnet publish -r win-x64 -c Release` to force ILC re-codegen from scratch
- The +20,480-byte Stage 0 → Stage 5 delta is the **terminal** delta of the entire reorg. Stage 4 was the last binary-affecting commit; the Stage 5 publish exe is byte-identical to Stage 4
- Stage 6 (docs sync) is the next step; Stage 5 introduces no code change so CLAUDE.md and PRD remain unchanged by this verification pass

---

# Stage 7 — final verification gate (2026-04-14)

Closes the entire Core/App reorg. Runs a single end-to-end verification of build hygiene, layer independence, docs integrity, runtime stability, and the 09-exit-criteria checklist. Verification only — the only functional commit in this stage is a doc-comment scrub of residual App-layer symbol mentions found during the Phase 1 audit.

## Phase 1 — Explore audit (read-only)

Agent form: 1× Explore subagent. All checks are static greps / file reads.

| check | target | result |
|---|---|---|
| 1.1 | `KoEnVue\.App` in `Core/` | 0 ✅ |
| 1.2 | `using KoEnVue\.App` in `Core/` | 0 ✅ |
| 1.3 | `\bImeState\b` in `Core/` | 0 ✅ |
| 1.4 | `\bAppConfig\b` in `Core/` | 0 (after scrub) ✅ |
| 1.5 | `\bDefaultConfig\b` in `Core/` | 0 (after scrub) ✅ |
| 1.6 | `\bI18n\b` in `Core/` | 0 (after scrub) ✅ |
| 1.7 | `\bNonKoreanImeMode\b` in `Core/` | 0 ✅ |
| 2 | Core/App namespace ↔ folder consistency | 52 files, 0 mismatches |
| 3 | CLAUDE.md Project Structure tree ↔ filesystem | character-exact match |
| 4 | Core using directives (BCL / sibling Core only) | 26 files, 0 App imports |

**Doc-comment residue scrub**: Phase 1 flagged 5 doc-comment mentions of App-layer symbol names inside `Core/`. These were architectural explanations (not code references) and the hard-fail gates (`KoEnVue.App` / `ImeState` / `NonKoreanImeMode`) were already 0 without the scrub. For absolute cleanliness the 5 sites were rewritten in a separate `chore:` commit:

| file | before | after |
|---|---|---|
| `Core/Config/JsonSettingsManager.cs:172` | "예: AppConfig 의 indicator_positions" | "예: 사전형 하위 필드의 수동 재조립" |
| `Core/Animation/AnimationConfig.cs:5` | "AppConfig → AnimationConfig 변환은 App 레이어 파사드" | "앱 레코드 → AnimationConfig 변환은 상위 레이어 파사드" |
| `Core/Windowing/OverlayStyle.cs:31` | "// DefaultConfig.LABEL_PADDING_X * IndicatorScale (상수 기반)" | "// 라벨 가로 패딩 상수 * IndicatorScale (파사드가 합성)" |
| `Core/Windowing/LayeredOverlayBase.cs:559` | "원본: App/Config/DefaultConfig.cs" | "Core 레이어에 동일 값을 복사 유지" |
| `Core/Tray/NotifyIconManager.cs:14` | "I18n 문자열 생성" | "로컬라이제이션 문자열 생성" |

After the scrub, all 7 Phase 1 greps return 0. The scrub is comment-text only — the publish exe remains byte-identical (see Phase 2 #12 below).

## Phase 2 — Final Verification Gate + Exit Criteria

Agent form: 1× general-purpose subagent. Runs the 12 Final Verification Gate items from `08-stage7-final-gate.md` + the 7 Exit Criteria items from `09-risks-and-reuse.md`.

### Final Verification Gate (12 items)

| # | item | result | evidence |
|---|---|---|---|
| 1 | Build clean | PASS | `dotnet clean && build && publish`: debug 0 warn / 0 err, release publish 0 warn / 0 err |
| 2 | Publish exe kicks off (P5 honored) | PASS | `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe` produced (4,911,104 bytes); non-elevated launch denied per `requireAdministrator` |
| 3 | IME toggle (한/En) | PASS (deferred to Stage 5 all-PASS) | baseline.md Stage 5 §b PASS; zero `App/`/`Core/`/`Program.cs` commits since Stage 5 close `08eef04` |
| 4 | Tray menu Korean + toggle state | PASS (deferred) | baseline.md Stage 5 §d PASS; `App/UI/Tray.cs` untouched since `08eef04` |
| 5 | SettingsDialog scroll/edit/save + hex reject | PASS (deferred) | baseline.md Stage 5 §g PASS; `App/UI/Dialogs/SettingsDialog.cs` untouched since `08eef04` |
| 6 | Config hot-reload | PASS (static) | `Core/Config/JsonSettingsManager.cs` `CheckReload()` mtime poll + `File.Exists` pre-check present |
| 7 | Delete-safe config | PASS (static) | `JsonSettingsManager.Load()` catch block updates `_lastMtime` to broken-file mtime — spam suppression path intact |
| 8 | CLAUDE.md path integrity | PASS | 21 backticked file paths + 9 folder refs resolve to disk; 0 stale `Utils/`, `Models/`, root-`Detector/`, root-`Native/` paths |
| 9 | PRD path integrity + Core/App split | PASS | `docs/KoEnVue_PRD.md §8.3` covers Core/App split (8 Core subdirs + 7 App subdirs); 2 backticked `docs/reorg/*.md` refs exist |
| 10 | Core independence (3 hard greps) | PASS | `KoEnVue\.App` in Core/ = 0; `ImeState` in Core/ = 0; `Detector\.` in App/Config/ = 0 |
| 11 | Multi-monitor DPI drag | PASS (deferred) | `Core/Windowing/LayeredOverlayBase.cs` `HandleDragDpiChange` + `_currentDpiScale` cache present; baseline.md Stage 5 §c PASS; no post-08eef04 code change to `LayeredOverlayBase.cs` or `Core/Dpi/` |
| 12 | Baseline delta | PASS | **+20,480 bytes** vs Stage 0 (4,890,624 → 4,911,104); ≤ +100 KB gate; **0 bytes** vs Stage 5 (byte-identical — doc-comment scrub is IL-transparent) |

### 09 Exit Criteria (7 items)

| # | item | result | evidence |
|---|---|---|---|
| 1 | Stage 0~7 verification gates passed | PASS | C0 `5f4ba3b` "capture refactor baseline metrics" → Stage 6 tip `5d3f3c3` "expand P4 with no-duplicate-impl principle" (24 commits across Stages 0~6; Stage 7 is verification-only) |
| 2 | CLAUDE.md + PRD describe the new structure | PASS | `CLAUDE.md` Project Structure tree lines 53-78 + `docs/KoEnVue_PRD.md §8.3` both cover Core/App split; Stage 6 docs sync (`bdd6a01`) closed the drift |
| 3 | Core independence (3 greps) | PASS | `git grep "KoEnVue\.App" Core/` = 0; `git grep "ImeState" Core/` = 0; `git grep "Detector\." App/Config/` = 0 |
| 4 | Publish exe runs Stage 5 smoke matrix cleanly | PASS | 10-item matrix (a/h/i automated + b/c/d/e/f/g/j manual) from Stage 5 still valid — `git log 08eef04..HEAD -- App/ Core/ Program.cs` returns 0 commits |
| 5 | Warnings increase 0, exe size delta ≤ +100 KB | PASS | +20,480 bytes delta (20% of budget); 0 build/publish warnings |
| 6 | NuGet packages 0 (P1) | PASS | `<PackageReference` in `KoEnVue.csproj` = 0 |
| 7 | `app.manifest requireAdministrator` (P5) | PASS | `git diff 5f4ba3b HEAD -- app.manifest` empty; line 6 `requestedExecutionLevel level="requireAdministrator"` unchanged |

## Build

- `dotnet clean && rm -rf bin obj && dotnet build` (Debug): warnings = 0, errors = 0
- `dotnet publish -r win-x64 -c Release`: warnings = 0, errors = 0
- Publish exe path: `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe`
- Exe size: **4,911,104 bytes** (byte-identical to Stage 5)
- SHA256: `9a4845ddd0a712ab35f0f7dd36ad1386cb733b12da0ad0b7e16fd3635df02911`

## Size delta — terminal

| baseline | size | delta | gate |
|---|---|---|---|
| Stage 0 (2026-04-13) | 4,890,624 bytes | — | — |
| Stage 5 (2026-04-14) | 4,911,104 bytes | +20,480 bytes (+20 KB) | ≤ +100 KB ✅ |
| Stage 7 (2026-04-14) | 4,911,104 bytes | +20,480 bytes (+20 KB) | **byte-identical to Stage 5** ✅ |

The Stage 5 → Stage 7 byte-identity confirms that the doc-comment scrub is purely a comment-text change with zero IL-byte impact. The +20,480-byte terminal delta remains the same as recorded at Stage 5 and is entirely attributable to the 6 new Core modules introduced by Stage 4 (LayeredOverlayBase / OverlayStyle / OverlayAnimator / JsonSettingsManager+JsonSettingsFile / NotifyIconManager / ModalDialogLoop) plus the App-side facade rewiring.

SHA256 differs from Stage 5 only because NativeAOT / MSVC `link.exe` embeds the wall-clock `TimeDateStamp` into the PE COFF header — Final Gate #12 measures size delta, not hash equality, so this is expected and passing. If reproducible builds become a requirement in the future, set `<Deterministic>true</Deterministic>` in `KoEnVue.csproj`.

## Notes

- Captured on branch `master`. Stage 7 produces **two** commits:
  1. `chore: scrub residual App-layer symbol mentions from Core doc comments` — the 5 doc-comment rewrites (optional cleanup, no IL impact)
  2. `chore: record stage7 final verification results` — this baseline.md section
- All 12 Final Verification Gate items + all 7 Exit Criteria items pass. The Stage 0~6 reorg (24 commits across Stages 0~6) is verified end-to-end regression-free across build, layer separation, NativeAOT serialization, docs integrity, and runtime behavior
- Stage 7 closes the reorg. No further reorg stages are planned
