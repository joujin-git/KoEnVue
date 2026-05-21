# PR-13: Per-app rendering config wiring

**Status**: ⏳ pending
**Branch**: feat/pr-13-per-app-rendering
**Base**: main
**Risk**: Medium
**Estimated session size**: M (1-2시간)
**Origin**: PR-01 Tier-3 smoke 에서 발견. 자세한 분석 → [docs/dev-notes/2026-05-21-profile-pipeline-completion.md](../dev-notes/2026-05-21-profile-pipeline-completion.md) §"미배선" 절.

## 1. 목적 (Why)

`Settings.ResolveForApp` 가 만든 per-app `resolved` AppConfig 가 감지 스레드 안에서만 사용되고 메인 스레드의 렌더링 경로(`Animation.TriggerShow` / `Overlay.UpdateColor` / `BuildStyle`)까지 도달하지 않는다. 결과: `app_profiles` 의 시각 필드(theme / 색 6쌍 / 투명도 / 라벨 크기·폰트·텍스트 / 애니메이션 / non_korean_ime 등 30+ 키) override 가 무동작.

영향 범위 + 작동 vs 미배선 키 전체 목록은 dev-notes 의 §"미배선" 영향 범위 표 참조.

## 2. 변경 범위 (What)

두 가지 접근 중 선택. 측정/검증 후 결정.

### Option A: 메시지 페이로드에 resolved 마샬링

- 감지 스레드: `EmitStateChanges` 가 `resolved` 를 `volatile AppConfig _resolvedForRendering` 필드에 게시 → `PostMessageW` 로 메시지 ID 만 보냄
- 메인 스레드: WM_FOCUS_CHANGED / WM_IME_STATE_CHANGED / WM_POSITION_UPDATED 핸들러가 필드에서 인스턴스를 꺼내 `Animation.TriggerShow(x, y, state, resolvedForRendering, …)` 호출
- 일관성 우려: 같은 틱 안 다중 메시지가 같은 인스턴스를 봐야 함. 페이로드 게시 시점이 모든 메시지보다 먼저면 OK

### Option B: 메인 스레드도 ResolveForApp 재호출

- 메인 스레드 핸들러가 `_config` 대신 `Settings.ResolveForApp(_config, _lastForegroundHwnd) ?? _config` 호출
- LRU 캐시 hit (같은 hwnd 라 즉시 반환) — 추가 비용 거의 0
- 단점: 메인 스레드 hwnd 갱신 시점 ≠ 감지 스레드 시점 — 짧은 race window 에서 다른 resolved 인스턴스 사용 가능. 다음 틱에서 정상화

### 추천 — Option B

- 변경 표면 최소 (메인 스레드 5-6개 핸들러의 `_config` 사용처를 `ResolveCurrent()` 헬퍼 호출로 교체)
- LRU 캐시 덕분에 hot path 비용 무시 가능
- race window 가 다음 틱에서 자연 수렴

### 코드
- [ ] [Program.cs](../../Program.cs) 에 `private static AppConfig ResolveCurrent()` 헬퍼 — `_currentProcessName.Length > 0 ? Settings.ResolveForApp(_config, _lastForegroundHwnd) ?? _config : _config` 반환
- [ ] `HandleImeStateChanged` / `HandleFocusChanged` / `HandlePositionUpdated` / `HandleActivateRequest` 등이 `Animation.TriggerShow` 에 넘기는 `_config` 인자를 `ResolveCurrent()` 결과로 교체
- [ ] `HandleConfigChanged` 의 `Overlay.HandleConfigChanged(_config)` 는 글로벌 그대로 (Initialize 시점이라 hwnd 없음)
- [ ] `Animation.TriggerShow` 가 매 호출마다 `UpdateConfig` 로 스냅샷 갱신하므로 추가 변경 불요
- [ ] `Overlay.UpdateColor(state)` 가 `Overlay._config` 를 참조 — 호출 직전 `Overlay.HandleConfigChanged(resolved)` 를 묶어 강제 동기화하거나 시그니처에 `AppConfig` 받게 확장

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / Fixed 에 본 PR 결과 추가 — "프로필 시각 필드 override 가 처음으로 실효되도록 메인 스레드 렌더링 경로를 per-app resolved 로 전환"
- [ ] `docs/KoEnVue_PRD.md` §5.4 미배선 절을 작동 절로 통합
- [ ] `docs/implementation-notes.md` 프로필 머지 파이프라인 §"미배선" 단락 제거 또는 작동 단락으로 갱신
- [ ] `docs/dev-notes/2026-05-21-profile-pipeline-completion.md` §"미배선" 절에 본 PR 머지 일자 + 결과 추기

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep "ResolveCurrent\|ResolveForApp" Program.cs` 6+ 매치 (헬퍼 + 호출처들)
- [ ] `git grep "Animation.TriggerShow(.*_config" Program.cs` 0 매치 (모두 resolved 로 전환)

### Tier 3 — 수동 smoke (PR-01 ② 재시도)
- [ ] `config.json` 에 `"app_profiles": { "notepad": { "theme": "dark" } }` 추가 + 메모장 포커스 → 다크 테마 색(`#065F46` 등) 적용 확인
- [ ] 다른 앱으로 전환 → 글로벌 색 복귀 확인
- [ ] `"app_profiles": { "code": { "opacity": 0.3 } }` → VS Code 포커스 시 인디 투명도 0.3 적용 확인
- [ ] `"app_profiles": { "explorer": { "enabled": false } }` → 탐색기 포커스 시 인디 비표시 (기존 동작 회귀 검증)

## 4. 사이드 이펙트 / 위험

- **위험 1**: 매 핸들러에서 `ResolveCurrent()` 호출 — `Settings.ResolveForApp` 의 LRU 캐시가 hit 보장. 캐시 미스 시 JSON roundtrip 비용. 메인 스레드 응답성에 영향 가능
- **위험 2**: race — 메인 스레드 `_lastForegroundHwnd` 와 감지 스레드 `state.LastHwndForeground` 가 일시적으로 다를 수 있음. 다음 틱에서 자연 수렴이지만 시각적 깜빡임 가능
- **위험 3**: `Overlay._config` 의 동기화 — `Overlay.UpdateColor(state)` 호출 직전에 `Overlay.HandleConfigChanged(resolved)` 가 호출되도록 보장해야 함. 현재 `Animation.TriggerShow` 가 묶어 처리하지만, 동시 호출 순서 확인 필요

## 5. 롤백 절차

- 단순 revert (Y)
- 데이터 영향 없음 (N)

## 6. 의존성

- **PR-01 머지 완료 후** 진행 가능 (`ApplyMergedProfilePipeline` 의 정확성에 의존)
- 다른 PR 들과는 독립

## 7. 세션 진행 로그

(empty)
