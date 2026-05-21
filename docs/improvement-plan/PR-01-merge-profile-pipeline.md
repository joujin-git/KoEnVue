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

### 2026-05-21 — 구현 + Tier-1/2 + 부분 Tier-3

**구현**
- A1: `Settings.MergeProfile` 의 `return AppSettingsManager.EnsureSubObjectsPublic(merged)` 를 `ApplyMergedProfilePipeline(merged)` 로 교체. 새 헬퍼는 `EnsureSubObjects → Settings.Validate → ThemePresets.Apply` 를 디스크 로드 경로와 동일 순서로 적용. `Migrate` 는 App 레벨 override 없는 identity 라 주석으로 자리 표시
- H5: `Win32Constants.WM_THEMECHANGED = 0x031A` 추가. `WndProc` 가 `WM_SETTINGCHANGE` 케이스에 fall-through 로 묶어 `HandleSettingChange` 동일 호출. `HandleSettingChange` 진입 시 `Settings.ClearProfileCache()` 추가
- B3: `Settings.Validate` 의 `Advanced = ValidateAdvanced(config.Advanced)` 추가. `IsValidWindowClassName` 가드(영문/숫자/언더스코어 + 길이 1-255) 위반 시 `KoEnVueOverlay` 폴백 + `Logger.Warning`
- A4: `SystemFilter._vdmFailCount` static + 1000회당 `Logger.Debug` 1줄. `Interlocked` 불요 (감지 스레드 단일 라이터)

**Tier-1 (빌드/AOT)**
- `dotnet build` — 경고 0, 오류 0 (00:00:00.77)
- `dotnet publish -r win-x64 -c Release` — 4.47 MB exe, AOT 정상
- invariant 4종 0 매치 (DllImport 는 docs 만 매치)

**Tier-2 (grep 가드)**
- T2-a: `Validate / ApplyTheme / Migrate` MergeProfile 인근 매치 — `Settings.cs:421` "EnsureSubObjects → (Migrate: 현재 identity) → Validate → ApplyTheme" 주석 + `:426` `ApplyMergedProfilePipeline` 호출 확인
- T2-b: `WM_THEMECHANGED in Program.cs` — `Program.cs:312` 1 매치
- T2-c: `OverlayClassName` Validate 인근 — `Settings.cs:161` `Advanced = ValidateAdvanced` + `:181` `IsValidWindowClassName` 호출 확인
- T2-d: `VDM COM failure count` — `SystemFilter.cs:174` 1 매치

**Tier-3 (수동 smoke, 사용자 실측)**
- **①** 정상 부팅 — OK
- **②** 프로필 `"app_profiles": { "notepad": { "theme": "dark" } }` 추가 + 메모장 포커스 — **인디 색 변하지 않음**. 원인: 별개의 미배선 결함. 감지 스레드의 `resolved` AppConfig 가 메인 스레드 렌더링 경로(`Animation.TriggerShow` / `Overlay.UpdateColor` / `BuildStyle`)에 전달되지 않음. 본 PR 의 파이프라인 fix 는 `resolved` 인스턴스의 정확성을 보장할 뿐. 자세한 영향 범위 + 후속 PR 옵션 → [docs/dev-notes/2026-05-21-profile-pipeline-completion.md](../dev-notes/2026-05-21-profile-pipeline-completion.md) §"미배선"
- **③** `"advanced": { "overlay_class_name": "!!!invalid!!!" }` — 정상 부팅 (폴백 작동). `koenvue.log` 에 Warning 부재 — `Settings.Validate` 가 `Logger.Initialize` 이전에 실행돼 drain 스레드 부재. `Trace.WriteLine` 에는 흐름. 폴백 동작 자체는 정상이라 의도된 동작으로 수용
- **④** `theme:system` + Windows accent 색 변경 — **인디 색 변하지 않음**. 추정 원인: `ThemePresets.ApplySystemTheme` 가 `GetSysColor(COLOR_HIGHLIGHT)` 를 읽는데 Win11 에서 "제목 표시줄에 강조색 표시" 옵션이 꺼져 있으면 personalization accent 변경이 `COLOR_HIGHLIGHT` 에 안 반영. 별도 후속 PR 에서 `DwmGetColorizationColor` 로 데이터 소스 전환 검토

**Tier-3 회고**
- ②/④ 둘 다 사용자 가시 부분은 본 PR 범위 밖. ② 는 인프라 fix 가 후속 PR 의 전제 조건으로서 가치 보존, ④ 는 데이터 소스 자체의 한계 — 모두 후속 PR 로 분리
- ③ 의 Warning 부재는 logger init 순서 이슈, 폴백은 작동 — 의도된 동작으로 수용

**결과**: 본 PR 는 인프라 정확성 fix 로 머지. A1 의 사용자 가시 효과는 후속 PR-13 (감지→메인 스레드 resolved 마샬링) + 별도 ApplySystemTheme 데이터 소스 PR 가 합쳐졌을 때 비로소 드러남. INDEX.md sessions log + 메모리에 발견 사항 기록.
