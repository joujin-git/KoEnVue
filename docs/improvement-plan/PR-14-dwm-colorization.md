# PR-14: ApplySystemTheme — DwmGetColorizationColor 데이터 소스 전환

**Status**: ⏳ pending
**Branch**: feat/pr-14-dwm-colorization
**Base**: main
**Risk**: Low (소규모 변경 + 명시적 폴백 경로)
**Estimated session size**: S-M (30분 ~ 1시간)
**Origin**: PR-01 Tier-3 smoke ④. 자세한 분석 → [docs/dev-notes/2026-05-21-profile-pipeline-completion.md](../dev-notes/2026-05-21-profile-pipeline-completion.md) §"시나리오 검증 ④"

## 1. 목적 (Why)

`ThemePresets.ApplySystemTheme` 가 시스템 강조색을 `User32.GetSysColor(COLOR_HIGHLIGHT)` 에서 읽는다. Win11 의 personalization 패널에서 사용자가 강조색을 바꿔도, "제목 표시줄과 창 테두리에 강조색 표시" 옵션이 꺼져 있으면 `COLOR_HIGHLIGHT` 가 즉시 갱신되지 않는 경우가 있어 인디케이터가 옛 색을 박제한다.

Win11 부터 DWM 이 personalization accent 의 source-of-truth 를 제공한다. `DwmGetColorizationColor(out uint argb, out bool opaqueBlend)` 가 항상 현재 accent 값을 반환하며, 이 API 는 "제목 표시줄 강조색 표시" 옵션과 무관하게 항상 최신값을 돌려준다.

**보조 결함**: 강조색 변경 시 셸이 `WM_DWMCOLORIZATIONCOLORCHANGED` (0x0320) 를 브로드캐스트하지만 본 앱에는 핸들러가 없다. `WM_THEMECHANGED` / `WM_SETTINGCHANGE` 가 동반 발화될 수도 있지만 보장되지 않는다 — 데이터 소스 전환과 함께 메시지 시그널도 함께 잡아 즉시 갱신을 보장.

## 2. 변경 범위 (What)

### 코드

- [ ] [Core/Native/Dwmapi.cs](../../Core/Native/Dwmapi.cs) — `DwmGetColorizationColor(out uint pcrColorization, [MarshalAs(UnmanagedType.Bool)] out bool pfOpaqueBlend)` `[LibraryImport]` 추가. wrapper 헬퍼 `TryGetColorizationRgb(out byte r, out byte g, out byte b)` — DWM 호출 성공 시 ARGB → RGB 분리, 실패 시 false. ARGB 는 0x00RRGGBB 또는 0xAARRGGBB — `GetSysColor` 의 0x00BBGGRR (BGR) 와 byte 순서가 다르므로 분리 로직이 ColorHelper 의 기존 `ColorRefToRgb` 를 그대로 쓸 수 없음 (BGR vs RGB).
- [ ] [App/Config/ThemePresets.cs:ApplySystemTheme](../../App/Config/ThemePresets.cs#L111) — DWM 우선 + 실패 시 `GetSysColor(COLOR_HIGHLIGHT)` 폴백:
  ```csharp
  if (!Dwmapi.TryGetColorizationRgb(out byte r, out byte g, out byte b))
  {
      uint accentColor = User32.GetSysColor(Win32Constants.COLOR_HIGHLIGHT);
      (r, g, b) = ColorHelper.ColorRefToRgb(accentColor);
  }
  ```
  이후 hex 변환 + 보색 계산은 그대로.
- [ ] [Core/Native/Win32Types.cs](../../Core/Native/Win32Types.cs) — `WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320` 상수 추가 (P3 매직 넘버 방지).
- [ ] [Program.cs](../../Program.cs) `WndProc` — `case Win32Constants.WM_DWMCOLORIZATIONCOLORCHANGED:` 추가, 기존 `HandleSettingChange()` 호출 (캐시 클리어 + `Theme.System` 일 때 재적용 + 인디 재렌더). `WM_SETTINGCHANGE` / `WM_THEMECHANGED` 의 fall-through 에 새 case 만 추가.

### 문서

- [ ] `CHANGELOG.md` [Unreleased] / 수정 항목 추가
- [ ] `docs/KoEnVue_PRD.md` §5 의 `system` 테마 설명에서 데이터 소스를 "DWM colorization (Win11 +) 우선, COLOR_HIGHLIGHT 폴백" 으로 갱신
- [ ] `docs/implementation-notes.md` 의 테마/시스템 색 관련 단락 갱신 (해당 단락 없으면 신설 — `WM_DWMCOLORIZATIONCOLORCHANGED` + DWM API 폴백 정책)
- [ ] `docs/dev-notes/2026-05-21-profile-pipeline-completion.md` §"시나리오 검증 ④" 에 PR-14 머지 완료 notice 추기

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과 (경고 0)
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep "DwmGetColorizationColor" Core/` 1+ 매치 (P/Invoke + wrapper)
- [ ] `git grep "WM_DWMCOLORIZATIONCOLORCHANGED" Program.cs` 1+ 매치 (WndProc case)
- [ ] `git grep "WM_DWMCOLORIZATIONCOLORCHANGED" Core/Native/Win32Types.cs` 1 매치 (상수)
- [ ] `git grep "TryGetColorizationRgb" Core/Native/Dwmapi.cs` 1 매치 (wrapper)

### Tier 3 — 수동 smoke
- [ ] `config.json` 에 `"theme": "system"` 명시 (또는 기본값) 후 부팅
- [ ] Windows 설정 → 개인 설정 → 색 → 강조색 변경 (제목 표시줄 강조색 표시 옵션 **OFF** 상태) → **인디 색이 즉시 새 강조색의 보색 쌍으로 변경**
- [ ] 제목 표시줄 강조색 표시 옵션 **ON** 상태로도 동일 확인
- [ ] DWM 비활성 환경(이론적, 일반 사용 환경에선 발생 X)에서는 GetSysColor 폴백으로 부팅 가능

## 4. 사이드 이펙트 / 위험

- **위험 1**: ARGB byte 순서 혼동 — DWM API 는 0xRRGGBB (또는 alpha 포함 0xAARRGGBB), `GetSysColor` 는 COLORREF=0x00BBGGRR (BGR). `ColorHelper.ColorRefToRgb` 를 잘못 재사용하면 색이 뒤집힌다. 검증: hex string 비교로 사용자가 가시 확인 가능
- **위험 2**: `DwmGetColorizationColor` 가 alpha 채널을 포함해 반환 (Win11 시) — alpha 를 무시하고 RGB 만 사용해야 한다 (인디 배경 색은 별도 alpha 채널 적용)
- **위험 3**: `WM_DWMCOLORIZATIONCOLORCHANGED` 가 매우 빈번히 발화 가능 (테마 슬라이더 드래그 중) — `HandleSettingChange` 가 이미 `ClearProfileCache` + `ThemePresets.Apply` + `TriggerShow` 를 묶어 처리하므로 폭주 시 메인 스레드 갱신 부담. 그러나 사용자 인터랙션 빈도 (수 Hz) 라 실해 무시
- **위험 4**: `DwmGetColorizationColor` 호출 자체가 실패하는 케이스 — DWM composition 비활성, 안전 모드 등. 폴백 경로(`GetSysColor`) 가 명시적으로 있어 부팅 보장

## 5. 롤백 절차

- 단순 revert (Y) — `ApplySystemTheme` 가 다시 `GetSysColor` 만 쓰는 직전 상태로 돌아간다
- 데이터 영향 없음 (N) — `theme:custom` 사용자 백업 색 / `app_profiles` 등 모든 사용자 설정 무관

## 6. 의존성

- 다른 PR 들과 독립. PR-01 머지 후 어디서나 가능.
- PR-05 (theme/defaults consolidation) 가 `ApplySystemTheme` 의 고대비 분기를 추가할 예정이지만 분기는 별개 — 본 PR 의 DWM 우선 데이터 소스가 base case 가 된 뒤 PR-05 가 고대비 케이스를 additive 로 추가.

## 7. 세션 진행 로그

| Date | Action | Result |
|---|---|---|
| 2026-05-21 | `Core/Native/Dwmapi.cs` 에 `DwmGetColorizationColor` `[LibraryImport]` + `TryGetColorizationRgb(out r, out g, out b)` 헬퍼 추가. ARGB 분리는 `ColorHelper.ColorRefToRgb` (BGR) 와 byte 순서가 달라 헬퍼 내부에 비트 시프트로 직접 구현 (alpha 무시) | LibraryImport 시그너처 적합, MarshalAs(UnmanagedType.Bool) 로 BOOL 마샬링 |
| 2026-05-21 | `App/Config/ThemePresets.cs:ApplySystemTheme` 가 DWM 우선 + `GetSysColor(COLOR_HIGHLIGHT)` 폴백 2단 분기. boolean 폴백 게이트는 `Dwmapi.TryGetColorizationRgb` 의 반환값 | 한글 배경 직접 + 영문 배경 보색 계산 로직은 그대로 |
| 2026-05-21 | `Core/Native/Win32Types.cs` 에 `WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320` 상수 추가. `Program.WndProc` 의 `WM_SETTINGCHANGE` / `WM_THEMECHANGED` fall-through 에 새 case 추가 — 기존 `HandleSettingChange` 재사용 | 별도 핸들러 신설 없이 단일 진입점 유지 |
| 2026-05-21 | 문서 갱신 — CHANGELOG [Unreleased]/수정 1건, PRD §5 `system` 테마 행 갱신, implementation-notes.md "`theme:system` 데이터 소스 및 시그널" sub-section 신설, dev-note 시나리오 ④ 에 PR-14 머지 완료 추기 | Tier-1 debug + clean AOT publish 모두 경고 0 (exe 4.47 MB). Tier-2 grep 가드 4종 모두 통과. invariant 4종 0매치 |
