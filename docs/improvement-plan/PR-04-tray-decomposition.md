# PR-04: Tray.cs decomposition

**Status**: ✅ merged (9192826)
**Branch**: feat/pr-04-tray-decomposition (deleted)
**Base**: main (PR-03 후 권장)
**Risk**: Low
**Estimated session size**: M (1-2시간)

## 1. 목적 (Why)

[App/UI/Tray.cs](../../App/UI/Tray.cs)가 1156줄 god class. 5개 책임 혼재:
1. 트레이 아이콘 생애주기 + 메뉴 구성 (정당)
2. **schtasks XML/CLI + XML escape/unescape** (~260줄, UI와 무관)
3. **CleanupPositions 비즈니스 로직** (~70줄, UI 한 줄 + 데이터 로직)
4. **3개 거의 동일 ShellExecute 블록** (`OpenUpdatePage`/`OpenHomepage`/`OpenConfigFile`)
5. **XML escape/unescape 2개 구현** (같은 5 entities 두 번)

목표: 본 PR 후 Tray.cs **< 700줄**. 분리된 모듈은 단독 책임 + 단순 호출.

## 2. 변경 범위 (What)

### 코드 — 신규 모듈
- [x] [App/Startup/StartupTaskManager.cs](../../App/Startup/StartupTaskManager.cs) 신규 — `IsStartupRegistered`, `ToggleStartupRegistration`, `RegisterStartupTaskWithXml`, `BuildStartupTaskXml`, `SyncStartupPathAsync`, `SyncStartupPathCore`, `QueryRegisteredTask`, `RunSchtasks`, `PathsEqual`, `ExtractTagFromXml`, `StartupTaskDelay` 등 schtasks 관련 전부 이동.
- [x] [App/Config/PositionCleanupService.cs](../../App/Config/PositionCleanupService.cs) 신규 — `Compute(config)` (키 합집합 + 실행 중 라벨링) + `RemoveSelected(config, displayItems, originalNames, selected)` 두 단계. 다이얼로그 호출은 Tray.cs 에 남김.
- [x] [Core/Shell/UriLauncher.cs](../../Core/Shell/UriLauncher.cs) 신규 — `Open(string uriOrPath)` + `Open(string file, string parameters)`. ShellExecuteW + rc<=32 + 로깅 공통화. PR-03 후엔 Admin 토큰 상속 우려도 사라짐.
- [x] [Core/Xml/XmlEntityCodec.cs](../../Core/Xml/XmlEntityCodec.cs) 신규 — `Escape`/`Unescape` (5 entities). 두 구현 통합.

### 코드 — Tray.cs 정리
- [x] schtasks 관련 메서드 삭제 (StartupTaskManager로 이동됨)
- [x] `CleanupPositions` 핸들러를 `PositionCleanupService.Compute()` 호출 + `CleanupDialog.Show()` 호출로 축약
- [x] `OpenUpdatePage`/`OpenHomepage`/`OpenConfigFile`이 `UriLauncher.Open(...)` 호출 (위험 3 결정대로 `OpenAsync` 가 아닌 `Open`)
- [x] 메뉴 커맨드 핸들러는 위 모듈로 dispatch만
- [x] **추가**: `ShowMenu` 를 [App/UI/Tray.Menu.cs](../../App/UI/Tray.Menu.cs) `internal static partial class Tray` 로 분리 — Tray.cs `wc -l < 700` 가드 만족 + 메뉴 빌더 책임 분리 (god class 분해 의도와 일관)

### 코드 — 메뉴 ID 공간
- [x] IDM_STARTUP 등 schtasks 관련 ID는 Tray.cs에 유지 (메뉴는 Tray가 빌드). StartupTaskManager는 ID를 모름, 핸들러 함수만 export.

### 문서
- [x] `CHANGELOG.md` [Unreleased] / Changed에 항목
- [x] `docs/architecture.md` 모듈 목록에 App/Startup, App/Config/PositionCleanupService, Core/Shell, Core/Xml 추가 + Tray.cs 항목에 partial 분할 반영
- [x] `docs/dev-notes/2026-05-21-tray-decomposition.md` — 분해 디자인 근거

## 3. 검증 기준 (Done When)

### Tier 1
- [x] `dotnet build` 통과 (0 경고, 0 오류)
- [x] `dotnet publish -r win-x64 -c Release` 통과 (4.77 MB, 변화 없음)
- [x] invariant 4종 0 매치

### Tier 2 — grep 가드
- [x] `wc -l App/UI/Tray.cs` = 575 (목표 <700, 1156 → 575, -50%) + `Tray.Menu.cs` = 182
- [x] `git grep "schtasks" App/UI/Tray.cs App/UI/Tray.Menu.cs` 0 매치 (모두 StartupTaskManager로)
- [x] `git grep "ShellExecuteW" App/UI/Tray.cs App/UI/Tray.Menu.cs` 0 매치 (모두 UriLauncher로)
- [x] `git grep "EscapeXml\|UnescapeXml" App/UI/Tray.cs App/UI/Tray.Menu.cs` 0 매치
- [x] `git grep "namespace KoEnVue.App.Startup" App/Startup/StartupTaskManager.cs` 1 매치
- [x] `git grep "namespace KoEnVue.Core.Shell" Core/Shell/UriLauncher.cs` 1 매치

### Tier 3 — 수동 smoke
- [ ] 트레이 우클릭 → 메뉴 정상 표시
- [ ] "시작 시 자동 실행" 토글 → schtasks 등록/해제 확인
- [ ] "홈페이지" / "설정 파일 열기" / "업데이트 페이지" 클릭 → 각각 ShellExecute 정상
- [ ] "저장 위치 정리..." → CleanupDialog 정상 표시 + 항목 삭제 동작

## 4. 사이드 이펙트 / 위험

- **위험 1**: 메뉴 핸들러의 `updateConfig` 클로저가 분리된 모듈로도 전달되어야 함. 시그너처 일관 유지.
- **위험 2**: `StartupTaskManager.SyncStartupPathAsync`가 background thread를 시작. Logger 호출이 stage(현 thread-safe). 변경 없음.
- **위험 3**: `UriLauncher.OpenAsync`가 진짜 비동기일 필요는 없음 — 이름만 Async, 실은 ShellExecute 호출 후 즉시 반환. 또는 `Open()`로 명명. **결정**: `Open()` 권장 (혼동 회피).
- **위험 4**: 일부 helper(`PathsEqual`)가 다른 곳에서도 쓰일 수 있음. 검색 후 적절한 위치 결정 (Core/Util이 없으면 StartupTaskManager의 private static).

## 5. 롤백 절차

- 단순 revert (Y) — 신규 파일 4개 삭제, Tray.cs 복원
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

| Date | What happened |
|---|---|
| 2026-05-21 | 4개 신규 모듈 (XmlEntityCodec / UriLauncher / StartupTaskManager / PositionCleanupService) + Tray.Menu.cs partial 분리. Tray.cs 1156 → 575 줄. Program.cs `Tray.SyncStartupPathAsync` → `StartupTaskManager.SyncStartupPathAsync`. Tier-1 debug + AOT publish clean (0 경고, 4.77 MB 유지). Tier-2 grep 가드 6종 통과 (xmldoc 단어 회피 + partial 분리로 line 가드 충족). invariant 4종 0매치. 문서 5건 갱신 (CHANGELOG / architecture.md 모듈 3종 추가 + Tray 항목 갱신 / PR-04 §2/§3/§6 / dev-notes 신규 / INDEX) |
| 2026-05-21 | Tier-3 사용자 수동 smoke 4항목 (트레이 메뉴 / 시작 등록 토글 / 홈페이지·설정파일·업데이트 / CleanupDialog) 모두 통과. FF merge to main (9192826) + 브랜치 삭제. PR-04 완료 |
