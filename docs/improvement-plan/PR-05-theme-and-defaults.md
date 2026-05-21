# PR-05: Theme + DefaultConfig consolidation + high-contrast

**Status**: ✅ merged (deedabe)
**Branch**: feat/pr-05-theme-and-defaults (deleted)
**Base**: main (PR-03 후 권장)
**Risk**: Low
**Estimated session size**: M (1-2시간)

## 1. 목적 (Why)

5개 발견을 한 PR에 묶음 — 모두 theme/defaults 영역:

1. **D2**: [ThemePresets.cs:25-50, 58-77, 84-108](../../App/Config/ThemePresets.cs#L25)의 6-필드 반복 → `record ThemeColors`
2. **D5**: `AppConfig` vs `DefaultConfig`의 6쌍 진짜 중복 (이름이 다른 경우 포함):
   - `AppConfig.FadeInMs = 150` ↔ `DefaultConfig.FadeInDurationMs = 150`
   - `AppConfig.FadeOutMs = 400` ↔ `DefaultConfig.FadeOutDurationMs = 400`
   - `AppConfig.HighlightScale = 1.3` ↔ `DefaultConfig.ScaleFactor = 1.3`
   - `AppConfig.HighlightDurationMs = 300` ↔ `DefaultConfig.ScaleReturnMs = 300`
   - `AppConfig.AlwaysIdleTimeoutMs = 3000` ↔ `DefaultConfig.AlwaysIdleTimeoutMs = 3000`
   - `AppConfig.PollIntervalMs = 80` ↔ `DefaultConfig.PollingIntervalMs = 80`
3. **N3**: DefaultConfig의 이름 4개를 AppConfig 키와 일치화 (FadeInDurationMs→FadeInMs 등)
4. **D7**: Settings.Validate clamp 범위 vs SettingsDialog min/max 범위 hand-sync → DefaultConfig const화
5. **H4-c**: 고대비 감지 + system color fallback (`SystemParametersInfo(SPI_GETHIGHCONTRAST)`) — `ThemePresets.ApplySystemTheme`에 분기

## 2. 변경 범위 (What)

### 코드
- [ ] [App/Config/ThemePresets.cs](../../App/Config/ThemePresets.cs)에 신규 `record ThemeColors(string HBg, string HFg, string EBg, string EFg, string NBg, string NFg)` 정의 (file-private 또는 internal sealed)
- [ ] preset 4개(Minimal/Vivid/Pastel/Dark)를 `Dictionary<Theme, ThemeColors>` 로 표현. `Apply`/`EnsureBackup`/`RestoreCustomBackup`이 ThemeColors 위에서 동작
- [ ] System theme 분기는 유지 (2-필드 부분 적용 + 새 고대비 감지 분기)
- [ ] [Core/Native/User32.cs](../../Core/Native/User32.cs)에 `SystemParametersInfoW` (SPI_GETHIGHCONTRAST=0x0042) `LibraryImport` 추가
- [ ] [App/Config/ThemePresets.cs:ApplySystemTheme](../../App/Config/ThemePresets.cs#L111) — 고대비 감지 시 `GetSysColor(COLOR_HIGHLIGHT/COLOR_WINDOW/COLOR_WINDOWTEXT)` 사용한 contrast-safe 색 적용
- [ ] [App/Config/DefaultConfig.cs](../../App/Config/DefaultConfig.cs):
  - `FadeInDurationMs` → `FadeInMs`로 rename (그리고 호출자 갱신)
  - `FadeOutDurationMs` → `FadeOutMs`
  - `ScaleFactor` → `HighlightScale`
  - `ScaleReturnMs` → `HighlightDurationMs`
  - `PollingIntervalMs` → `PollIntervalMs`
  - `MinPollMs = 50`, `MaxPollMs = 500` 등 Validate range 신규 const 추가 (총 ~12개)
- [ ] [App/Models/AppConfig.cs](../../App/Models/AppConfig.cs)의 inline default 6개를 `DefaultConfig.X` const 참조로 변경 (C# const는 compile-time inline이라 STJ source gen 동작 동일)
- [ ] [App/Config/Settings.cs:108-138](../../App/Config/Settings.cs#L108) Validate clamp 리터럴을 `DefaultConfig.Min/MaxX`로 교체
- [ ] [App/UI/Dialogs/SettingsDialog.Fields.cs:86,89,103,…](../../App/UI/Dialogs/SettingsDialog.Fields.cs#L86) min/max 리터럴을 같은 DefaultConfig const로 교체

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / Changed에 항목
- [ ] `docs/architecture.md` 테마 섹션에 ThemeColors 디자인 + 고대비 감지 명시
- [ ] `docs/conventions.md` P4 항목에 "수치/색상 default는 DefaultConfig 단일 진실원" 추가

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep "ScaleFactor\|ScaleReturnMs\|FadeInDurationMs\|FadeOutDurationMs\|PollingIntervalMs" --` 0 매치 (모두 rename됨)
- [ ] `git grep "record ThemeColors" App/Config/ThemePresets.cs` 1 매치
- [ ] `git grep "SPI_GETHIGHCONTRAST" Core/Native/User32.cs` 1 매치
- [ ] `git grep "MinPollMs\|MaxPollMs" App/Config/DefaultConfig.cs` 1+ 매치
- [ ] `git grep "= 150\|= 400\|= 300\|= 3000" App/Models/AppConfig.cs` 0 매치 (모두 DefaultConfig 참조로)

### Tier 3 — 수동 smoke
- [ ] 트레이 메뉴에서 테마 4종 (Minimal/Vivid/Pastel/Dark) 전환 → 색 변경 확인
- [ ] Custom → Preset → Custom 왕복 → 원래 색 복원 확인 (CustomBackup 동작)
- [ ] Windows 설정 → 접근성 → 고대비 활성 → 인디 색이 system colors로 전환되는지 확인 (Theme.System 사용 시)
- [ ] SettingsDialog 값 입력 → Validate range 일치 확인

## 4. 사이드 이펙트 / 위험

- **위험 1**: DefaultConfig 이름 변경 → 호출자 다수 영향 (OverlayAnimator, Animation, Program 등). 모든 호출자 grep 후 일괄 변경 필요.
- **위험 2**: ThemeColors record가 internal이라 STJ가 직렬화하지 않음 (의도). AppConfig 스키마는 무변경.
- **위험 3**: 고대비 감지 시 색 자동 전환 — 사용자가 의도적으로 specific 색을 원했다면 변경됨. **결정**: Theme.System 사용자만 영향 (Custom/Preset은 사용자 명시 색 유지).
- **위험 4**: SettingsDialog min/max 변경 시 기존 사용자가 슬라이더에서 더 넓은 값을 받을 수 있음. Validate가 결국 clamp.

## 5. 롤백 절차

- 단순 revert (Y) — 단 호출자 다수라 revert 충돌 가능. 본 PR을 단일 commit으로 squash 권장.
- 데이터 영향 없음 (N) — AppConfig 스키마 무변경.

## 6. 세션 진행 로그

### 2026-05-21

**구현**:
- `App/Config/DefaultConfig.cs`:
  - `FadeInDurationMs` → `FadeInMs`, `FadeOutDurationMs` → `FadeOutMs`, `ScaleFactor` → `HighlightScale`,
    `ScaleReturnMs` → `HighlightDurationMs`, `PollingIntervalMs` → `PollIntervalMs` (N3 5건 rename).
    `<see cref="AppConfig.X"/>` cref 코멘트로 단일 진실원 의도 명시. 호출자 0건이라 안전.
  - 신규 const 32개 (Min/MaxX 16쌍): Poll / EventDisplay / AlwaysIdle / Opacity / HighlightScale /
    Fade / SnapGapPx / FontSize / LabelWidth / LabelHeight / LabelBorderRadius / BorderWidth /
    IndicatorScale / LogMaxSizeMb / ForceTopmostMs. 주석으로 D7 (4-축 hand-sync 제거) 의도 기록.
- `App/Models/AppConfig.cs`:
  - `using KoEnVue.App.Config;` 추가.
  - 6쌍 inline 디폴트 → const 참조: `FadeInMs` / `FadeOutMs` / `HighlightScale` / `HighlightDurationMs` /
    `AlwaysIdleTimeoutMs` / `PollIntervalMs`.
- `App/Config/ThemePresets.cs`: 전체 rewrite.
  - `internal sealed record ThemeColors(string HBg, ..., string NFg)` 신규.
  - 4 preset 을 `Dictionary<Theme, ThemeColors> _presets` 정적 사전으로 표현. `Apply` 가 dict TryGetValue
    단일 분기로 4 preset 처리. `Custom`/`System` 은 dict 외 분기 유지 (런타임 동적 계산).
  - `ApplySystemTheme` 진입부에 `User32.IsHighContrastEnabled()` 분기 추가 — ON 이면 HIGHLIGHT/
    HIGHLIGHTTEXT/WINDOW/WINDOWTEXT contrast-safe 팔레트로 BG+FG 4 필드 전환. OFF 면 PR-14 의 DWM
    accent 우선 + GetSysColor 폴백 2단 분기 유지.
- `Core/Native/User32.cs`:
  - `SystemParametersInfoHighContrast(uint, uint, ref HIGHCONTRAST, uint)` `[LibraryImport]`
    (`EntryPoint="SystemParametersInfoW"`) — SystemParametersInfoW 의 PVOID pvParam 이 uiAction 별로
    다른 구조체를 받는 generic 시그니처라 SPI_GETHIGHCONTRAST 전용 오버로드로 둠.
  - `IsHighContrastEnabled()` 헬퍼 — `cbSize = Marshal.SizeOf<HIGHCONTRAST>()` 설정 후 호출,
    `dwFlags & HCF_HIGHCONTRASTON` 비트 검사, 실패 시 false.
- `Core/Native/Win32Types.cs`: `SPI_GETHIGHCONTRAST = 0x0042`, `HCF_HIGHCONTRASTON = 0x00000001`,
  `COLOR_WINDOW = 5`, `COLOR_WINDOWTEXT = 8`, `COLOR_HIGHLIGHTTEXT = 14` 5개 상수 신규. `HIGHCONTRAST`
  struct 는 기 정의됨.
- `App/Config/Settings.cs`: `Validate` 의 clamp 리터럴 18개를 모두 `DefaultConfig.Min/MaxX` 참조로 교체.
- `App/UI/Dialogs/SettingsDialog.Fields.cs`: `using KoEnVue.App.Config;` 추가 + Int/Dbl 의 min/max 인자
  13개를 모두 `DefaultConfig.Min/MaxX` 참조로 교체.
- `App/UI/Dialogs/ScaleInputDialog.cs`: `using KoEnVue.App.Config;` 추가 + `ScaleMinValue` / `ScaleMaxValue`
  를 `DefaultConfig.Min/MaxIndicatorScale` 참조로 통일. `Tray.Menu.cs` 의 서브메뉴 범위 표시도 자동 동기.

**Tier-1 통과**:
- `dotnet build -c Debug` 클린 (0 경고, 0 오류, 3.41s).
- `dotnet publish -r win-x64 -c Release` AOT 클린 (0 경고, 4.77 MB 유지).

**Tier-2 grep 가드** (5종 + invariant 4종 + P5 2종 모두 통과):
- `git grep "ScaleFactor\|ScaleReturnMs\|FadeInDurationMs\|FadeOutDurationMs\|PollingIntervalMs" -- '*.cs'` → 0 매치 (docs 의 self-reference 잔존만 OK).
- `git grep "record ThemeColors" App/Config/ThemePresets.cs` → 1 매치.
- `git grep "SPI_GETHIGHCONTRAST" Core/Native/User32.cs` → 3 매치 (1+ 충족).
- `git grep "MinPollMs\|MaxPollMs" App/Config/DefaultConfig.cs` → 2 매치 (1+ 충족).
- `git grep "= 150\|= 400\|= 300\|= 3000" App/Models/AppConfig.cs` → 0 매치.
- invariant 4종 (`KoEnVue\.App`/`ImeState`/`NonKoreanImeMode` in Core/, `DllImport`) → 0 매치.
- P5 invariant 2종 (`requireAdministrator` in app.manifest, `RunLevel.*HighestAvailable` in App/) → 0 매치.

**문서 갱신**:
- `CHANGELOG.md` [Unreleased] / 변경 최상단에 PR-05 항목.
- `docs/architecture.md` 의 DefaultConfig + ThemePresets 라인 갱신 (단일 진실원 + 고대비 분기 명시).
- `docs/conventions.md` P4 sub-rule 신설 ("수치/색상 디폴트는 DefaultConfig 단일 진실원").
- `docs/improvement-plan/INDEX.md` Sessions log + Progress matrix.

**Tier-3 수동 smoke 완료** (사용자 가시 검증 4건 모두 통과):
1. 테마 4종 (Minimal/Vivid/Pastel/Dark) 전환 → 색 변경 확인.
2. Custom → Preset → Custom 왕복 → 원래 색 복원 확인 (`custom_backup_*` 동작).
3. `Theme.System` + Windows 고대비 모드 ON ↔ OFF 토글 → 인디 색이 contrast-safe 팔레트 ↔ DWM accent
   사이로 즉시 전환 확인.
4. SettingsDialog 의 입력 range 일치 확인 — 경계 외 값에 에러 정확 노출.

**머지**: FF merge to main (deedabe) + 브랜치 삭제.
