# PR-01: MergeProfile pipeline + WM_THEMECHANGED + VDM 측정

**Status**: ⏳ pending
**Branch**: feat/pr-01-merge-profile
**Base**: main
**Risk**: Low
**Estimated session size**: M (1-2시간)

## 1. 목적 (Why)

3가지 잠재 정합성 결함을 한 묶음으로 해결:

1. **A1**: [Settings.MergeProfile](../../App/Config/Settings.cs#L331)은 `EnsureSubObjectsPublic`만 호출. 반면 `JsonSettingsManager.Load`는 `Migrate`→`Validate`→`EnsureSubObjects`→`ApplyTheme` 전체 파이프라인. 결과: 프로필이 `"theme":"dark"` override 시 색이 안 바뀌고, `"poll_interval_ms":999999` 같은 범위 외 값이 clamp 안 됨.

2. **H5**: [Program.cs:HandleSettingChange](../../Program.cs#L848)는 `WM_SETTINGCHANGE`만 처리. `WM_THEMECHANGED`(0x031A)는 visual style 변경의 별도 브로드캐스트.

3. **B3**: [Program.Bootstrap.cs:96](../../Program.Bootstrap.cs#L96)에서 `config.Advanced.OverlayClassName`이 그대로 `RegisterClassExW`에 전달. 검증 없음.

4. **A4 측정**: VDM COM cross-thread 호출의 실 실패 빈도 측정용 카운터 추가 (현 동작은 유지).

## 2. 변경 범위 (What)

### 코드
- [ ] [App/Config/Settings.cs:347-385](../../App/Config/Settings.cs#L347) `MergeProfile`의 `return AppSettingsManager.EnsureSubObjectsPublic(merged)` 부분을 `Migrate → Validate → EnsureSubObjects → ApplyTheme` 4단계로 확장. `AppSettingsManager`의 protected hooks를 `Settings.MergeProfile`이 호출할 수 있도록 정적 접근점 추가 또는 직접 `Settings.Validate`/`ThemePresets.Apply` 호출.
- [ ] [Program.cs:848-861](../../Program.cs#L848) `HandleSettingChange`에서 `Settings.ClearProfileCache()` 호출 추가 (시스템 테마 변경 시 프로필 캐시 무효화).
- [ ] [Program.cs](../../Program.cs)의 `WndProc`에 `Win32Constants.WM_THEMECHANGED` 케이스 추가 — `HandleSettingChange`와 동일 핸들러 호출.
- [ ] [Core/Native/Win32Types.cs](../../Core/Native/Win32Types.cs)에 `WM_THEMECHANGED = 0x031A` 상수 추가 (없으면).
- [ ] [App/Config/Settings.cs:103-159](../../App/Config/Settings.cs#L103) `Validate`에 `OverlayClassName` 검증 추가: 영문/숫자/언더스코어 + 길이 1-255. 위반 시 `Advanced = config.Advanced with { OverlayClassName = "KoEnVueOverlay" }` + Logger.Warning.
- [ ] [App/Detector/SystemFilter.cs:160-170](../../App/Detector/SystemFilter.cs#L160) `IsWindowOnCurrentVirtualDesktop`의 catch 블록에 실패 카운터 추가. `static int _vdmFailCount`. 1000회당 1줄 `Logger.Debug($"VDM COM failure count: {_vdmFailCount}")` 로깅.

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / Fixed에 A1+H5+B3 항목 추가
- [ ] `docs/KoEnVue_PRD.md` 프로필 동작 섹션에 "프로필 머지 시에도 Migrate/Validate/ApplyTheme 전체 적용" 명시
- [ ] `docs/implementation-notes.md`에 "프로필 머지 파이프라인" 섹션 신설
- [ ] `docs/dev-notes/2026-05-21-profile-pipeline-completion.md` 신규 — A1 결정 근거 + 시나리오 검증
- [ ] `docs/dev-notes/2026-05-21-vdm-com-threading.md` 신규 — A4 측정 계획. 4주 후 데이터 기반 재결정 명시.

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep "Validate\|ApplyTheme\|Migrate" App/Config/Settings.cs` MergeProfile 인근 매치 확인
- [ ] `git grep "WM_THEMECHANGED" Program.cs` 1+ 매치
- [ ] `git grep "OverlayClassName" App/Config/Settings.cs` Validate 인근 매치
- [ ] `git grep "VDM COM failure count" App/Detector/SystemFilter.cs` 1 매치

### Tier 3 — 수동 smoke
- [ ] 정상 부팅
- [ ] `config.json`에 `"app_profiles": { "test": { "theme": "dark" } }` 추가 + 매칭되는 프로세스(예: notepad)에 포커스 → 다크 테마 색 적용 확인
- [ ] `config.json`에 `"advanced": { "overlay_class_name": "!!!invalid!!!" }` → fallback 로그 + 정상 동작
- [ ] Windows 설정 → 개인 설정 → 색 → 강조색 변경 (Theme.System 사용 중) → 인디 색 즉시 갱신

## 4. 사이드 이펙트 / 위험

- **위험 1**: 프로필이 `"theme":"system"` 명시 시 `ApplySystemTheme`가 `GetSysColor` 호출. 매 ResolveForApp이 GetSysColor 호출하면 비용. 그러나 LRU 캐시 hit으로 첫 매칭 후 캐시됨. `ClearProfileCache`는 시스템 변경 시에만.
- **위험 2**: 사용자가 의도적으로 프로필에서 `poll_interval_ms: 5000` 등 큰 값 설정 → Validate가 500ms로 clamp. 단, DetectionLoop는 `_config.PollIntervalMs`(글로벌)을 사용하므로 프로필의 PollIntervalMs는 어차피 효과 없음. README/User_Guide에 명시 권장.
- **위험 3**: B3 검증 변경 후 기존 사용자가 `OverlayClassName`을 비정상 값으로 설정해 둔 경우 fallback. 사실상 무영향 (대부분 디폴트 사용).
- **위험 4**: VDM 카운터의 static 변수가 detection 스레드만 쓰므로 race 없음. Interlocked 불요.

## 5. 롤백 절차

- 단순 revert (Y)
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

(empty)
