# 앱별 프로필 머지 파이프라인 완성 — 결정 근거 (2026-05-21)

> **결과**: `Settings.MergeProfile` 의 후처리 파이프라인을 `EnsureSubObjects` 1단계 → `EnsureSubObjects → Validate → ApplyTheme` 3단계로 확장. PR-01 ([docs/improvement-plan/PR-01-merge-profile-pipeline.md](../improvement-plan/PR-01-merge-profile-pipeline.md)).

## 무엇 (What)

기존 `Settings.MergeProfile` 은 글로벌 `AppConfig` 를 JSON 으로 직렬화 → 프로필 키 머지 → 재역직렬화한 결과를 `AppSettingsManager.EnsureSubObjectsPublic` 한 단계만 거쳐 호출자에게 반환했다. 디스크 로드 경로(`JsonSettingsManager.Load`) 는 동일 시점에 `Migrate → Validate → ApplyTheme` 3단계를 추가로 수행한다. 머지 경로가 이 후처리를 통째로 우회하던 비대칭.

수정: `EnsureSubObjectsPublic` (호출처 0건의 dead) 을 제거하고 `ApplyMergedProfilePipeline(AppConfig)` 정적 헬퍼로 대체. 내부 순서는 `EnsureSubObjects → Validate → ApplyTheme` — 디스크 로드 파이프라인과 동일.

## 왜 (Why)

### 3가지 잠재 버그가 동시 노출

1. **테마 프리셋 무시**: 프로필이 `"theme":"dark"` 를 override 해도 `ThemePresets.Apply` 가 호출 안 돼 색상 6쌍이 갱신 안 됨. 사용자는 `theme` 키만 바꿔도 색이 그대로라는 직관 위반 시나리오 노출
2. **범위 외 값 통과**: 프로필이 `"poll_interval_ms":999999` 같은 비정상 수치를 넣어도 `Validate` 의 clamp(50-500ms 범위) 가 호출 안 돼 그대로 통과. (실제 `DetectionLoop` 은 글로벌 `PollIntervalMs` 만 사용하므로 표시 측 키 위주로 더 큰 실해가 발생: `event_display_duration_ms` 가 30분으로 박혀 인디가 계속 떠 있는 등)
3. **`Theme.Custom` 백업/복원 무력화**: 프로필이 `"theme":"custom"` 으로 명시할 때 `RestoreCustomBackup` 가 호출 안 돼 백업 복원 로직 dead

세 버그가 같은 누락 한 줄에서 파생되므로 한 PR 로 묶었다.

### 왜 `ApplyMergedProfilePipeline` 단일 정적 헬퍼인가

대안: 각 호출처(`MergeProfile`)가 `EnsureSubObjects` + `Settings.Validate` + `ThemePresets.Apply` 를 3줄로 직접 호출. 직접 호출은 미래에 단계가 추가/삭제될 때 호출처들이 동기화에서 누락될 위험 — 본 결함이 정확히 그 패턴이라(`Migrate` 가 추가됐을 때 머지 경로 업데이트 누락). 단일 진입점으로 묶으면 다음 호출처 추가 시(만약 생긴다면) 같은 진입점만 호출하면 된다.

### 왜 `Migrate` 는 끼우지 않았나 (지금은)

현재 `AppSettingsManager` 는 `JsonSettingsManager.Migrate` (identity virtual) 를 override 하지 않는다 — 프리-릴리스 단계라 schema 진화 트랙이 비어 있음 (v0.9.2.2 에서 마이그레이션 체인 전부 dead 정리, CHANGELOG 참조). 따라서 머지 파이프라인에서 `Migrate` 를 호출해도 identity 라 실효 변화 0. 대신 헬퍼 내부에 미래 훅 자리 주석으로 표시 — 추후 profile 단위 schema 변경이 생기면 `_manager` 인스턴스 경유로 hookup.

### 왜 `PostDeserializeFixup` 은 끼우지 않았나

`PostDeserializeFixup` 은 STJ AOT 의 `Dictionary<string, int[]>` 역직렬화 실패를 mergedJson 에서 수동 재조립한다 (`indicator_positions` 회복). 프로필 머지는 글로벌의 `IndicatorPositions` 를 그대로 상속하며, 머지된 JSON 의 같은 필드를 다시 역직렬화해도 글로벌이 이미 채워 둔 dictionary 가 그대로 보존되는지가 관건. 측정 결과 `Settings.MergeProfile` 의 globalJson 캐시 → 머지 → 재역직렬화 경로에서는 `IndicatorPositions` 가 채워진 상태로 통과해 추가 fixup 이 불필요. 추후 STJ 가 같은 결함을 다른 dictionary 필드에서 재현할 경우 진입점 추가 검토.

### 시나리오 검증 (Tier 3 수동 smoke) — 2026-05-21

실측 결과:

- **① 정상 부팅** — OK
- **② 프로필 `theme:dark`** — `config.json` 에 `"app_profiles": { "notepad": { "theme": "dark" } }` 추가, 메모장 포커스 → **인디 색 변하지 않음**. 근본 원인: **별개의 미배선 결함** 노출 (아래 §미배선 절 참조). 본 PR 의 파이프라인 fix 는 `resolved` 인스턴스 정확성은 보장하지만, 그 인스턴스가 렌더링까지 도달하지 않음
- **③ `overlay_class_name` 비정상** — `"advanced": { "overlay_class_name": "!!!invalid!!!" }` → 정상 부팅 (폴백 동작 작동). 단 `Logger.Warning` 이 `koenvue.log` 에 안 남음. 원인: `Settings.Validate` 는 `Settings.Load` 안에서 `Logger.Initialize` 이전에 실행되므로 drain 스레드가 없는 시점. `Trace.WriteLine` 에는 흐른다. 폴백 자체는 정상 → 본 PR 는 의도된 동작으로 수용
- **④ 시스템 강조색 변경** — `theme:system` 으로 변경 후 Windows 설정 → 개인 설정 → 색 → 강조색 변경 → **인디 색 변하지 않음**. 추정 원인: `ThemePresets.ApplySystemTheme` 가 `GetSysColor(COLOR_HIGHLIGHT)` 를 읽는데 Win11 에서 "제목 표시줄 / 창 테두리에 강조색 표시" 옵션이 꺼져 있으면 personalization accent 변경이 `COLOR_HIGHLIGHT` 에 반영되지 않음. 별도 후속 PR 에서 `DwmGetColorizationColor` 로 데이터 소스 전환 검토

## 미배선 — 프로필 시각 override 가 렌더링까지 도달하지 않음 (Tier-3 ② 의 근본 원인)

### 추적

`Settings.ResolveForApp(global, hwnd)` 는 감지 스레드의 `TryHandleFilter` 안에서 호출되어 `resolved` AppConfig 를 만든다. 이 `resolved` 는 같은 틱 안에서 다음 3 영역에만 전달된다:

1. `SystemFilter.ShouldHide(hwnd, hwndFocus, resolved)` — 9-조건 평가의 파라미터
2. `TrackWindowMove(..., resolved, ...)` — `PositionMode` 판정
3. `ImeStatus.Detect(hwndFocus, threadId, resolved.DetectionMethod)` — IME 감지 방법 분기

그 외 모든 곳은 메인 스레드의 글로벌 `_config` 를 사용한다. 특히:

- `Program.HandleImeStateChanged` / `HandleFocusChanged` / `HandlePositionUpdated` 는 `Animation.TriggerShow(x, y, state, _config, imeChanged: …)` 호출 — `_config` 는 글로벌
- `Animation.TriggerShow` 가 `Overlay.UpdateColor(state)` 호출 — `Overlay._config` (Initialize/HandleConfigChanged 가 글로벌만 주입) 참조
- `Overlay.BuildStyle(_config, state)` 가 `HangulBg` / `EnglishBg` / `NonKoreanBg` / 라벨 크기 / 폰트 등 시각 필드 추출

감지 스레드 → 메인 스레드 메시지(`WM_FOCUS_CHANGED` / `WM_IME_STATE_CHANGED` / `WM_POSITION_UPDATED`) 는 `lParam`/`wParam` 에 hwnd 또는 ImeState 만 실어 보낸다. `resolved` AppConfig 인스턴스를 메인 스레드로 마샬링하는 채널이 부재.

### 영향 범위

프로필 시각 필드 override 가 작동하지 않는 키 (PRD §5.4 미배선 절 참조):

- `theme` / 색 6쌍 (`hangul_bg` / `hangul_fg` / `english_bg` / `english_fg` / `non_korean_bg` / `non_korean_fg`)
- `opacity` / `idle_opacity` / `active_opacity`
- `label_width` / `label_height` / `label_border_radius` / `border_width` / `border_color` / `indicator_scale`
- `font_family` / `font_size` / `font_weight`
- `hangul_label` / `english_label` / `non_korean_label`
- `animation_enabled` / `fade_in_ms` / `fade_out_ms` / `change_highlight` / `highlight_scale` / `highlight_duration_ms` / `slide_animation` / `slide_speed_ms`
- `display_mode` / `event_display_duration_ms` / `always_idle_timeout_ms` / `event_triggers`
- `non_korean_ime` / `tray_*` / `update_check_enabled` / `language` / `log_*`
- `default_indicator_position*` / `snap_to_windows` / `snap_gap_px` / `drag_modifier`

작동하는 키:

- `system_hide_classes` / `system_hide_classes_user` / `system_hide_processes` / `system_hide_processes_user`
- `hide_in_fullscreen` / `hide_when_no_focus`
- `app_filter_mode` / `app_filter_list`
- `position_mode` / `indicator_positions*`
- `detection_method`

### 후속 PR 의 시뇨리지

후속 PR 의 모양은 두 가지 접근 중 선택:

- **A. 메시지 페이로드에 resolved 마샬링**: WM_FOCUS_CHANGED / WM_IME_STATE_CHANGED 등에 `resolved` 인스턴스 참조를 `volatile` 필드로 게시 + 메시지 ID 만 PostMessage. 메인 스레드 핸들러가 필드에서 인스턴스를 꺼내 `Animation.TriggerShow` 의 `config` 인자로 사용. 단점: WM_FOCUS_CHANGED 와 WM_IME_STATE_CHANGED 가 같은 틱 안에서 발생하면 두 핸들러가 같은 인스턴스를 봐야 일관성 유지 가능 — 마샬링 순서 보장 필요
- **B. 메인 스레드도 ResolveForApp 재호출**: 메인 스레드의 핸들러가 `_config` 대신 `Settings.ResolveForApp(_config, _lastForegroundHwnd)` 를 호출. 동일 인스턴스라 LRU 캐시가 즉시 hit. 단점: 메인 스레드에서 hwnd 가 변하는 시점과 감지 스레드의 시점이 약간 다를 수 있음

본 PR 의 인프라 fix (`ApplyMergedProfilePipeline`) 는 어느 접근을 택하든 `resolved` 가 정확한 색/clamp/테마를 가지도록 하는 전제 조건. 미배선 자체는 별도 PR (improvement-plan 에 PR-13 으로 추가) 의 범위.

## 대안 (Alternatives considered)

### A. `_manager` 인스턴스 경유로 protected hooks 직접 호출

```csharp
_manager?.RunMergeHooks(merged)  // protected Migrate/Validate/ApplyTheme 호출
```

`MergeProfile` 이 `_manager?.…` null-check 부담을 짊어지고, `JsonSettingsManager<T>` 에 public/internal 진입점을 별도로 노출해야 함. 정적 헬퍼는 `Settings.Validate` 가 이미 public static 이고 `ThemePresets.Apply` 가 public static 이므로 인스턴스 의존 없이 동일 효과. 단순성 우위.

### B. `Settings.Load()` 로 풀-리로드 강제

머지 경로 자체를 제거하고 매 프로필 매칭마다 `Settings.Load()` 를 재호출. 80ms 폴링 핫패스에서 디스크 I/O + JSON roundtrip 전체를 매 틱 수행 — 명백한 과잉. 기각.

### C. JSON 머지 자체를 record-with 체인으로 대체

`AppConfig` 의 ~70개 필드를 일일이 record-with 패턴으로 머지 — JSON-element-driven override 가 사라져 사용자 프로필의 키 set 을 정적으로 알 수 없는 문제. 현 JSON-level merge 는 프로필이 명시한 키만 정확히 덮어쓰는 구조. 기각.

## 회고 (Lessons learned)

- **파이프라인 누락은 단일 누락으로 멀티 결함을 낳는다**. 머지 경로의 후처리 누락 한 줄이 (theme/clamp/backup) 3개의 잠재 버그로 분기. 다음 PR 부터 "load 와 동일 후처리 시퀀스가 모든 경로에서 적용되는지" 를 grep 가드로 묶는다 (Tier-2 grep 가드에 `ApplyMergedProfilePipeline` 추가)
- **AOT 단일 사용자 단계에서 `Migrate` 가 identity 라도 코드 슬롯은 유지**한다. 향후 schema 진화 시 누락 가능성을 봉쇄
