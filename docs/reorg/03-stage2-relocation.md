# 03 — Stage 2: 파일 재배치

← Previous: [02 — Stage 1](02-stage1-dedupe.md) | → Next: [04 — Stage 3](04-stage3-narrowing.md)

## 목표

기계적인 파일 이동과 네임스페이스 재작성만 수행. **동작 변경 0건**. 단일 원자 커밋(C6)으로 묶어 중간 상태가 repo에 남지 않도록 한다.

> **선행 substep (DpiHelper → Config 의존 단절)**: [Utils/DpiHelper.cs:2](../../Utils/DpiHelper.cs#L2)의 `using KoEnVue.Config;`와 [DpiHelper.cs:13](../../Utils/DpiHelper.cs#L13)의 `public const int BASE_DPI = DefaultConfig.BASE_DPI;`가 남아있으면, `Utils/DpiHelper.cs`를 `Core/Dpi/`로 옮기는 순간 `Core → KoEnVue.App.Config` 레이어 위반이 발생한다. 파일 이동 직전에 DpiHelper의 `BASE_DPI` 선언을 `public const int BASE_DPI = 96;`로 인라인하고 `using KoEnVue.Config;`를 삭제한다. 두 값은 동일하므로 동작 변경 없음. [DefaultConfig.cs:72](../../Config/DefaultConfig.cs#L72)의 `BASE_DPI` 정의는 그대로 둔다 (Config 내부 및 App 모듈이 해당 이름으로 직접 참조할 가능성 대비 — Stage 5 스모크에서 실사용 여부 확인 후 잔존 시 삭제). 이 substep까지 **동일 C6 커밋**에 포함.

## 에이전트 구성

- **구성**: serial, 1x Plan + 1x general-purpose
- **Plan 에이전트 1대**가 이동 맵을 다시 검증 (Stage 1에서 새로 생긴 `Dialogs/` 파일과 `Utils/WindowProcessInfo.cs`가 포함됐는지 확인)
- **general-purpose 에이전트 1대**가 직렬 실행

## 이동 맵

| 출발 | 도착 |
|---|---|
| `Native/*.cs` (AppMessages 제외) | `Core/Native/` |
| `Native/AppMessages.cs` | `App/UI/AppMessages.cs` |
| `Utils/ColorHelper.cs` | `Core/Color/ColorHelper.cs` |
| `Utils/DpiHelper.cs` | `Core/Dpi/DpiHelper.cs` |
| `Utils/Logger.cs` | `Core/Logging/Logger.cs` |
| `Utils/Win32DialogHelper.cs` | `Core/Windowing/Win32DialogHelper.cs` |
| `Utils/WindowProcessInfo.cs` (Stage 1-E 신규) | `Core/Windowing/WindowProcessInfo.cs` |
| `Utils/I18n.cs` | `App/Localization/I18n.cs` |
| `Models/LogLevel.cs` | `Core/Logging/LogLevel.cs` |
| `Models/*` (나머지) | `App/Models/` |
| `Config/*` | `App/Config/` |
| `Detector/*` | `App/Detector/` |
| `UI/Overlay.cs`, `UI/Animation.cs`, `UI/Tray.cs`, `UI/TrayIcon.cs` | `App/UI/` |
| `UI/SettingsDialog.cs`, `UI/Dialogs/CleanupDialog.cs`, `UI/Dialogs/ScaleInputDialog.cs` | `App/UI/Dialogs/` |
| `Program.cs` | 루트 유지 |

## 네임스페이스 재작성

- `KoEnVue.Native` → `KoEnVue.Core.Native`
- `KoEnVue.Utils` → `KoEnVue.Core.Color` / `KoEnVue.Core.Dpi` / `KoEnVue.Core.Logging` / `KoEnVue.Core.Windowing` (파일별)
- `KoEnVue.Utils.I18n` → `KoEnVue.App.Localization`
- `KoEnVue.Models.LogLevel` → `KoEnVue.Core.Logging`
- `KoEnVue.Models.*` (나머지) → `KoEnVue.App.Models`
- `KoEnVue.Config` → `KoEnVue.App.Config`
- `KoEnVue.Detector` → `KoEnVue.App.Detector`
- `KoEnVue.UI` → `KoEnVue.App.UI` / `KoEnVue.App.UI.Dialogs`
- 루트 `KoEnVue` (Program.cs) 유지 — 진입점

## using 목록 갱신

컴파일러가 모든 참조 끊김을 드러내도록 빌드를 돌린 뒤 에러 리스트를 따라 일괄 수정. 소스 생성기(`[LibraryImport]`, `[JsonSerializable]`) 네임스페이스 이동에 따른 재빌드 필요.

**중간 상태에서 커밋 금지** — Stage 2는 단일 원자 커밋(C6).

내부 서브스텝 (agent 브리핑에 포함):
1. **선행**: `Utils/DpiHelper.cs`의 `using KoEnVue.Config;` 삭제 + `BASE_DPI = DefaultConfig.BASE_DPI` → `BASE_DPI = 96` 인라인 (위 선행 substep 참조)
2. `git mv` 일괄 실행
3. namespace 선언부 파일별 재작성
4. using 디렉티브 일괄 갱신
5. `dotnet build --no-incremental` 로 중간 확인
6. `dotnet build` + `dotnet publish` 통과 시에만 commit

> **알려진 일시 위반 (C6 완료 후 ~ C7 완료 전)**: Stage 2 종료 시점에서 `Core/Logging/Logger.cs`의 `Initialize(AppConfig config)` 시그니처([Logger.cs:40](../../Utils/Logger.cs#L40))가 아직 `KoEnVue.App.Models.AppConfig`를 참조한다. 따라서 `git grep "KoEnVue\.App" Core/`는 C6 직후엔 **0건이 아닐 수 있다** (Logger 1건). Stage 3-A (C7)에서 시그니처를 좁히면서 해소되므로 최종 검증 게이트(Stage 7)에는 영향 없음. Stage 2 자체 검증 게이트는 이 카운트를 강제하지 않는다.

## 검증 게이트

- `dotnet build` 성공, 경고 증가 없음
- `dotnet publish -r win-x64 -c Release` 성공
- `git diff --stat` 검토: 대부분 `R###` (rename) + `using`/`namespace` 라인 변경. 내용 편집은 네임스페이스 선언부 외 거의 없어야 함
- 스모크 실행: Stage 1과 동일한 5개 항목 (CleanupDialog Tab, ScaleDialog 저장, SettingsDialog 헥스 검증, IME 토글, 트레이 메뉴)
- publish된 exe 크기가 Stage 0 기준선과 ±2KB 이내

## 커밋 출력

| # | 커밋 제목 |
|---|---|
| C6 | `refactor: move files to Core/App folder structure and rewrite namespaces` |

## 관련 리스크

소스 생성기 참조 끊김이 가장 큰 리스크. 자세한 완화책은 [09 — Risks](09-risks-and-reuse.md)의 Risk #1 참조.

---

← Back to [README](README.md)
