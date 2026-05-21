# Tray.cs god class 분해 (PR-04)

**Date**: 2026-05-21
**PR**: improvement-plan PR-04
**Files**: `App/UI/Tray.cs`, `App/UI/Tray.Menu.cs` (신규), `App/Startup/StartupTaskManager.cs` (신규), `App/Config/PositionCleanupService.cs` (신규), `Core/Shell/UriLauncher.cs` (신규), `Core/Xml/XmlEntityCodec.cs` (신규)

## 무엇이 바뀌었나

v0.9.x 의 `App/UI/Tray.cs` 가 1156 줄짜리 단일 파일에 6개 책임을 안고 있었음:

1. 트레이 아이콘 라이프사이클 + NIM_ADD 재시도 (정당)
2. 팝업 메뉴 빌드 + 라디오/체크 상태 결정 (정당)
3. WM_COMMAND 디스패치 (정당)
4. **schtasks XML 조립 + CLI 호출 + 결과 검증 + 경로/지연/RunLevel 동기화** (~260 줄, UI 와 무관)
5. **`indicator_positions` 정리 비즈니스 로직** (Process enum + dict diff + 라벨링, ~70 줄)
6. **3개 거의 동일한 `ShellExecuteW` 블록** (`OpenUpdatePage` / `OpenHomepage` / `OpenConfigFile`) + **XML escape/unescape 5 entities 의 2개 중복 구현**

PR-04 분해 결과:

| 신규 모듈 | 책임 | 외부 의존성 |
|---|---|---|
| `App/Startup/StartupTaskManager.cs` | schtasks 등록/조회/동기화 전부 (11 메서드 + 5 상수) | `Logger`, `XmlEntityCodec` |
| `App/Config/PositionCleanupService.cs` | `indicator_positions` 정리 비즈니스 로직 (Compute + RemoveSelected) | `Logger`, `I18n`, `AppConfig` |
| `Core/Shell/UriLauncher.cs` | ShellExecuteW `open` verb + `rc <= 32` 실패 검출 단일 진입점 | `Shell32`, `Win32Constants`, `Logger` |
| `Core/Xml/XmlEntityCodec.cs` | 5 predefined entities escape/unescape | — |
| `App/UI/Tray.Menu.cs` (partial) | `ShowMenu` 메뉴 빌더만 분리 | (Tray internals 공유) |

Tray.cs 본체: 1156 → 575 줄 (-50%). 책임 = 라이프사이클 + 디스패치 + helpers + `OpenConfigFile`/`OpenHomepage`/`OpenUpdatePage` (각 ~10 줄로 축약) + `CleanupPositions` (3-call sequence ~16 줄).

## 왜 이렇게 (디자인 근거)

### Q1. `UriLauncher.Open` vs `OpenAsync`?

PR-04 명세 §4 위험 3 결정대로 `Open` 으로 명명. ShellExecuteW 는 즉시 반환하는 동기 호출 — `OpenAsync` 라고 명명하면 호출자가 `await` 가능을 기대하게 됨. 진짜 비동기가 아니므로 의미를 그대로 반영하는 `Open` 이 정확.

### Q2. `PositionCleanupService` 가 다이얼로그까지 호출하지 않는 이유?

다이얼로그 호출 (`CleanupDialog.Show`) + empty 안내 (`MessageBoxW` + `ModalDialogLoop.RunExternal`) 는 UI 책임이라 Tray.cs 에 남김. PositionCleanupService 는 (1) `Compute(config)` 가 키 합집합 + 실행 중 라벨링까지 만들어 반환, (2) `RemoveSelected(config, displayItems, originalNames, selectedDisplay)` 가 사용자 선택을 두 dict 제거한 새 AppConfig 반환 — 두 단계 사이에 다이얼로그 호출이 끼어들도록 의도. UI 의존성 0 인 pure 비즈니스 로직 모듈을 만들면 단위 테스트 가능 + Core 로 lift 도 잠재 가능.

### Q3. `StartupTaskManager.RunLevelLeastPrivilege` 가 const 인 이유?

PR-03 후 `<RunLevel>LeastPrivilege</RunLevel>` 가 두 곳에서 사용 — (1) `BuildStartupTaskXml` 가 새 task XML 에 박을 때, (2) `SyncStartupPathCore` 가 기존 등록된 task 의 RunLevel 과 비교해 v0.9.x 의 `HighestAvailable` 잔재를 강제 재등록할 때. 매직 문자열 (P3) 회피 + 향후 RunLevel 정책 변경 시 한 곳만 수정.

### Q4. ShowMenu 를 partial 로 분리한 이유?

PR-04 명세 §3 Tier-2 가드 `wc -l App/UI/Tray.cs < 700` 를 만족하기 위함. xmldoc 압축으로 778 → 743 까지는 줄었지만 ShowMenu 본체가 170 줄이라 partial 분리가 가장 깔끔. 명세 §1 의 "god class 분해 의도" 와 일관 — 메뉴 빌더 책임이 라이프사이클/디스패치/helpers 와 한 파일에서 섞이지 않는 게 가독성 측면에서도 유리. `internal static partial class Tray` 라 메뉴 ID 상수 + 내부 상태 (`_pendingUpdate` / `_initialized` / `OpacityTolerance` / `ScaleIntegerMin/Max`) 모두 공유 가능. 책임 자체는 동일 클래스 — 단순 파일 분할.

### Q5. `XmlEntityCodec` 가 Core 에 있는 이유?

Tray.cs 는 App 레이어 (KoEnVue.App.*), StartupTaskManager 도 App 레이어. 그러나 XML 5 entities escape/unescape 는 KoEnVue 도메인과 무관한 .NET 1.0 spec 유틸. 향후 다른 Core 모듈이나 다른 프로젝트로 lift 시 의존성 없이 재사용 가능. 본격 XML 처리는 `System.Xml.XmlReader` 등을 쓰는 게 맞고, 본 모듈은 schtasks XML 조립처럼 의존성 없이 5 entities 만 필요한 경량 케이스용.

### Q6. `Escape` / `Unescape` 의 순서 보장?

- `Escape`: `&` 를 **가장 먼저** 처리 → 다른 entity 안의 `&` (e.g. `&amp;` 결과의 `&`) 가 중복 인코딩되어 `&amp;amp;` 가 되는 것 방지.
- `Unescape`: `&amp;` 를 **마지막에** 처리 → 원본 `&` 가 다른 entity 의 앰퍼샌드 (e.g. 입력 `&lt;` 의 `&`) 를 잡아채 `<lt;` 가 되는 것 방지.

이는 v0.9.x 부터 안전하게 동작한 이유의 명시화 — 기존 코드가 우연히 이 순서를 지키고 있었음을 모듈 추출 시 의도로 굳혔다.

## 대안 (검토 후 기각)

### A1. ShowMenu 를 별도 파일 + 별도 클래스 (`TrayMenuBuilder`) 로 분리

기각 이유: 메뉴 ID 상수 + `_pendingUpdate` / `_initialized` / `OpacityTolerance` / `ScaleIntegerMin/Max` 등 내부 상태가 ShowMenu 와 강한 의존. 별도 클래스로 빼면 (a) 상수들을 internal 로 노출해 다른 호출자가 잡을 위험 또는 (b) `ShowMenu(IntPtr, AppConfig, ref bool initialized, UpdateInfo? pendingUpdate, ...)` 처럼 인자 폭증. partial 분할이 책임 분리 + 캡슐화 동시 충족.

### A2. xmldoc 압축만으로 < 700 만족

778 → 743 까지는 줄였지만 더 줄이려면 의미 있는 코멘트도 잘라야 했음. 메뉴 빌더 헤더 라인 코멘트 / Initialize NIM_ADD 재시도 설명 등은 보존 가치 있는 WHY 정보. partial 분할 한 번으로 575 줄로 떨어져 향후 메뉴 변경 여지도 확보.

### A3. `Tray.OpenConfigFile` 도 `OpenConfigFileService` 같은 별도 모듈

기각 이유: `Settings.ConfigFilePath` 한 줄 + null 가드 한 줄 + `UriLauncher.Open` 호출 한 줄 = 3 줄짜리 함수. 분리 효과 없음. Tray.cs 안에 남겨두는 게 인지 비용 < 호출 한 줄.

### A4. `PositionCleanupService` 가 `Action<AppConfig>` 콜백을 받아 updateConfig 호출까지

기각 이유: 호출자(Tray)가 dialog 결과를 직접 받아서 분기 — `selected is null || selected.Count == 0` 면 updateConfig 호출 자체를 skip 해야 함. service 안에서 콜백 호출하면 그 분기를 service 가 알아야 하므로 책임 누수.

## 회귀 위험

- **Risk 1: 메뉴 핸들러 클로저 변경 가능성** — `updateConfig: Action<AppConfig>` 가 분리된 모듈 (StartupTaskManager / PositionCleanupService) 에도 전달되어야 했지만, 실 분해 결과 StartupTaskManager 는 콜백 불요 (schtasks 자체가 stateful) 이고 PositionCleanupService 는 새 AppConfig 를 반환 — Tray 의 핸들러가 `updateConfig(PositionCleanupService.RemoveSelected(...))` 로 호출. 클로저 시그너처 변경 없음.
- **Risk 2: schtasks 호출 동시성** — `StartupTaskManager.SyncStartupPathAsync` 가 background thread 시작 + `Logger` 가 thread-safe (Stage 3-A). 변경 없음.
- **Risk 3: NIM_ADD 재시도 타이머 + ShowMenu 분리** — `ShowMenu` 는 `_initialized` 만 읽고 다른 라이프사이클 상태와 무관. partial 분할로 동작 차이 0.
- **Risk 4: `XmlEntityCodec.Unescape` 의 순서 변경** — 원래 Tray.cs 의 ExtractTagFromXml 안에서는 `&amp; → &quot; → &apos; → &lt; → &gt;` 순서였음. 모듈 추출 시 `&quot; → &apos; → &lt; → &gt; → &amp;` 로 변경 — `&amp;` 를 마지막에 처리해 원본 `&` 가 다른 entity 의 앰퍼샌드를 잡아채는 것 방지가 더 안전. 입력이 well-formed XML 이면 두 순서 모두 동일 결과지만, 손상된 입력에서는 후자가 더 robust. schtasks 출력은 well-formed 이라 회귀 0.

## 측정

- **Tier-1**: `dotnet build` 0 경고/0 오류, `dotnet publish -r win-x64 -c Release` clean. exe 크기 4.77 MB 유지 (분해로 인한 변화 없음).
- **Tier-2 grep 가드 6종**: `wc -l App/UI/Tray.cs` = 575 (목표 <700), schtasks/ShellExecuteW/EscapeXml 모두 Tray.cs + Tray.Menu.cs 에서 0 매치, namespace 가드 (StartupTaskManager / UriLauncher) 1 매치.
- **Invariant 4종**: 0 매치 (Core/ 에 `KoEnVue.App` / `ImeState` / `NonKoreanImeMode` 0, 전체 `DllImport` 0 — docs 만 매치).
- **Tier-3 수동 smoke**: 사용자 검증 (트레이 우클릭 메뉴 / 시작 등록 토글 / 홈페이지·설정파일·업데이트 열기 / 저장 위치 정리) 후 머지 예정.
