# Phase 5/5 — Task D: Tray 분리 + 문서 현행화 (C12 + docs)

> 이 파일은 **단일 세션에서 붙여 넣어 실행**하기 위한 self-contained 프롬프트다.
> Phase 4가 끝나고 C11 커밋이 있는 상태에서 시작한다.
>
> 이 Phase는 **코드 + 문서 두 가지 작업**을 함께 한다. 구현 실패가 확실해지면 Tray 작업을 Phase 6으로 분리하고, 문서 현행화만 단독 커밋으로 마감할 수 있다.

---

## Task

Stage 4의 마지막 Task D를 수행한다. `App/UI/Tray.cs`에서 Shell_NotifyIconW 상호작용/아이콘 렌더링을 `Core/Tray/NotifyIconHost`로 분리하고, App 쪽은 정적 facade로 남긴 채 메뉴 구성/schtasks/CleanupDialog 호출/I18n 텍스트 등 **KoEnVue 도메인 로직**은 그대로 유지한다. 그 후 Stage 4 전체 변화에 맞춰 `CLAUDE.md`와 `docs/reorg/` 전반을 현행화한다. 커밋 C12 + docs 커밋을 만든다.

## Context

- 저장소: `e:\dev\KoEnVue`
- 브랜치: `master`
- 직전 상태: Phase 4 C11 완료. Release exe는 Phase 1 기준 ±수십 KB 내외.
- 관련 문서:
  - `docs/reorg/05-stage4-core-extraction.md` Task D 섹션
  - `docs/reorg/07-stage6-docs-sync.md` — 문서 동기화 범위 (필독)
  - `docs/reorg/09-risks-and-reuse.md` Risk 4 (ImeState leak)
  - `CLAUDE.md` Tray 관련 Key Implementation Decisions (`NOTIFYICON_VERSION_4`, `NIF_SHOWTIP`, `WM_CONTEXTMENU`, Startup task sync)

## Hard constraints

- **P1~P6 유지.** `git grep "KoEnVue\.App" Core/` → 0.
- Core/Tray는 `AppConfig`, `DefaultConfig`, `ImeState`, `I18n` 등 App 도메인 타입을 **직접 참조하지 않는다**.
- Core/Tray는 `IDM_*` 메뉴 ID 상수나 schtasks/CleanupDialog 로직을 **포함하지 않는다**. 메뉴 콘텐츠는 raw HMENU + 이미 만들어진 아이콘만 받는다.
- `App/UI/Tray.cs`의 `internal static` API 6종 — `Initialize(IntPtr, ImeState, AppConfig)`, `UpdateState(ImeState, AppConfig)`, `Remove()`, `ShowMenu(IntPtr, AppConfig)`, `HandleMenuCommand(int, AppConfig, IntPtr, ...)`, `SyncStartupPathAsync()` — 은 시그니처 변경 금지. (Tray는 `public`이 아니라 `internal static class`임에 주의.)
- Release 퍼블리시 exe 크기는 Phase 4 기준 ±수 KB 내외.

## 계획 재검토 원칙

**구현 중 계획 재검토가 필요하면 먼저 알려줘.** 코드 에이전트가 `App/UI/Tray.cs`/`App/UI/TrayIcon.cs`를 실제로 읽어본 결과, 이 프롬프트의 `NotifyIconHost` 공개 API(생성자·`Add`/`Modify`/`Remove`/`SetVersion`/`ShowContextMenu`), `NIF_SHOWTIP` 자동 포함 원칙, 메뉴 빌드·schtasks·CleanupDialog·ScaleInputDialog·SettingsDialog·I18n·`IDM_*` 상수의 App 잔존 결정, Tray.cs `internal static` API 6종 시그니처 유지(`Initialize`/`UpdateState`/`Remove`/`ShowMenu`/`HandleMenuCommand`/`SyncStartupPathAsync`), Risk 4(AppConfig/DefaultConfig/ImeState/I18n 누출 금지) 중 하나라도 실제 코드와 중요한 불일치가 있다고 판단되면, **구현을 시작하기 전에** 메인 세션에 보고하고 사용자 판단을 받는다. 문서 에이전트 2대도 마찬가지 — 실제 저장소 구조가 `07-stage6-docs-sync.md` 체크리스트와 크게 다르거나, CLAUDE.md의 기존 Stage 3 narrowing 기록을 제거해야 한다고 판단되면 수정을 시작하기 전에 메인 세션에 확인을 요청한다. Tray 작업을 Phase 6으로 분리할지 여부는 메인 세션이 Agent 1 보고를 받은 뒤 사용자와 상의해 판단한다. 에이전트는 독자 판단으로 프롬프트의 계약을 바꾸지 않고, 보고 후 승인을 받을 때까지 기존 가정을 그대로 따른다.

## 0. Sanity check

- `git status` clean.
- `git log --oneline -5`에 C11 커밋 존재.
- `git grep "KoEnVue\.App" Core/` → 0.
- `git grep "AppConfig" Core/` → 0.
- `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0.
- `git grep "using KoEnVue\.App" Core/Overlay/ Core/Animation/ Core/Config/` → 0.
- `dotnet build` 성공.

## 1. 작업 에이전트 구성

**3개의 general-purpose 에이전트**를 **병렬**로 스폰. 하나는 코드, 나머지 둘은 문서.

> **분리 옵션 (Phase 6 fallback)**
> 만약 Tray 코드 분리에서 Risk가 발견되거나 블로커가 생기면, Tray 작업을 Phase 6으로 분리하고 이 Phase에서는 **문서 현행화만** 마감한다. 실제 분리 판단은 Agent 1 결과를 받은 뒤 메인 세션이 내린다.

### Agent 1 — Task D 구현 (코드)

프롬프트 지시:

- 목적: `App/UI/Tray.cs`의 Shell_NotifyIconW 상호작용과 TrayIcon GDI 렌더를 `Core/Tray/NotifyIconHost` + `Core/Tray/TrayIconRenderer`(필요 시)로 분리. App 쪽은 static facade + 도메인 로직 유지.
- 필수 읽기:
  - `App/UI/Tray.cs` 전체
  - `App/UI/TrayIcon.cs`
  - `Core/Native/Win32Types.cs` (Shell_NotifyIconDataW 관련 구조체/상수)
  - `Core/Native/`의 Shell32 P/Invoke
  - `docs/reorg/05-stage4-core-extraction.md` Task D 상세
- 구현 지침:
  1. `Core/Tray/NotifyIconHost`:
     - 생성자: `(IntPtr hwnd, Guid trayGuid, uint callbackMessage)`.
     - public 메서드: `Add(string tooltip, IntPtr hIcon)`, `Modify(string tooltip, IntPtr hIcon)`, `Remove()`, `SetVersion()`(NOTIFYICON_VERSION_4 적용), `ShowContextMenu(IntPtr hMenu, int x, int y)`.
     - `NIF_SHOWTIP` 플래그는 Core가 항상 자동 포함.
     - 메뉴 아이템 식별자는 raw `uint`로 받아 호출자에게 그대로 전달 (WM_COMMAND → 콜백).
  2. `TrayIcon.cs`의 GDI 렌더가 Core로 이동해도 좋다면 `Core/Tray/TrayIconRenderer`로 분리. 이동이 복잡하면 App에 유지.
  3. `App/UI/Tray.cs` 리팩터링:
     - 정적 필드 `_host = new NotifyIconHost(...)` 보유.
     - 기존 static 메서드는 `_host.*`로 위임.
     - 메뉴 구성(`BuildContextMenu`), schtasks, CleanupDialog 호출, ScaleInputDialog 호출, I18n.*, IDM_* 상수, SettingsDialog 호출 — 전부 App에 유지.
     - `NIF_TIP` + `NIF_SHOWTIP` 동시 설정 관행 유지.
     - `NIM_ADD`, `NIM_MODIFY`, `NIM_DELETE`, `NIM_SETVERSION` 호출은 Core가 담당.
  4. `WM_CONTEXTMENU` 라우팅: Program.cs는 기존대로 `Tray.Handle*` 을 호출. 내부에서 `_host.ShowContextMenu(hMenu, x, y)`.
  5. **Risk 4 준수**: Core/Tray에 `using KoEnVue.App.*` 금지.
  6. `Program.cs`, `CleanupDialog.cs`, `SettingsDialog.cs`, `ScaleInputDialog.cs` 수정 없음.
- 보고 형식: ≤400단어 요약 + 파일 목록 + 잔존 App 로직 목록 + 미결 질문.

### Agent 2 — 문서 현행화 (CLAUDE.md)

프롬프트 지시:

- 목적: Stage 4 Task A~D 완료에 맞춰 `CLAUDE.md`를 **정확하게** 현행화. 실제 저장소 상태와 불일치가 없도록 검증.
- 필수 읽기:
  - `CLAUDE.md` 전체
  - `docs/reorg/05-stage4-core-extraction.md`
  - `docs/reorg/07-stage6-docs-sync.md`
  - 최신 `Core/` 트리 구조 (bash: `find Core -name "*.cs"`)
  - 최신 `App/` 트리 구조
- 갱신 항목 (07-stage6-docs-sync.md의 체크리스트 기준):
  1. "Project Structure" 섹션 — Core/Overlay, Core/Animation, Core/Config, Core/Tray 디렉터리 추가 반영.
  2. "Key Implementation Decisions" — Stage 4 관련 항목 추가/수정:
     - LayeredOverlayBase + OverlayStyle: 렌더링 분리, App facade 유지
     - OverlayAnimator + AnimationConfig + AnimationTimerIds: 상태 머신 분리, NonKoreanImeMode.Dim은 App 잔존
     - JsonSettingsManager<T>: JsonTypeInfo 주입, 삭제 안전 hot-reload / 손상 스팸 방지 / 자동 생성 모두 Core로 이전
     - NotifyIconHost: NIF_SHOWTIP 기본 포함, 메뉴 콘텐츠는 App 잔존
  3. P6 검증 명령어 예시에 Core/Overlay/Animation/Config/Tray가 포함되도록 명시 (이미 glob이면 수정 불필요, 명시적이면 확장).
  4. Stage 4 신규 Key Decisions 문단 추가 — 각 Task의 디자인 의도(왜 static facade를 유지했는지, 왜 ImeState를 Core에 내리지 않았는지, Risk 2 대응, Risk 6 대응 등).
  5. 오래된 Stage 3 narrowing 설명이 Stage 4 구현을 반영하도록 필요한 부분만 수정 (제거 금지 — 과거 기록은 유지).
- 원칙:
  - 실제 저장소 구조와 한 줄도 다르면 안 된다. 작성 후 반드시 저장소와 re-grep으로 검증.
  - 기존 문장 스타일(영어 본문 + 한국어 UI 문자열 예시 혼용 등)을 유지.
- 보고 형식: 변경된 섹션 목록 + diff 요약 + 실제 저장소 구조와 불일치가 없음을 증명하는 grep 결과.

### Agent 3 — 문서 현행화 (docs/reorg/)

프롬프트 지시:

- 목적: `docs/reorg/`의 Stage 4/5/6/7 파일을 현재 상태에 맞게 현행화.
- 필수 읽기:
  - `docs/reorg/README.md`
  - `docs/reorg/05-stage4-core-extraction.md` ~ `08-stage7-final-gate.md`
  - `docs/reorg/09-risks-and-reuse.md`
  - 최신 `Core/`, `App/` 구조
- 갱신 항목:
  1. `05-stage4-core-extraction.md` — 각 Task A/B/C/D에 "완료 커밋: Cxx" 표기 및 최종 시그니처 스냅샷 추가.
  2. `06-stage5-verification.md` — 이번 Phase 2/3/4/5에서 실제 돌린 스모크 매트릭스 결과 반영.
  3. `07-stage6-docs-sync.md` — CLAUDE.md 갱신 완료 체크.
  4. `08-stage7-final-gate.md` — 최종 게이트 기준(exe 크기, grep 결과, 스모크 7케이스) 숫자 업데이트.
  5. `09-risks-and-reuse.md` — Risk 2/4/5/6에 대해 Stage 4에서 어떻게 대응했는지 한 문단씩 추가.
  6. `README.md` — Stage 4 완료 표시.
- 원칙:
  - 사실만 기록. 추측이나 가정 금지.
  - 커밋 해시는 이 Phase가 끝난 뒤에 실제 C12와 docs 커밋 해시로 다시 채워야 하므로, 초안에서는 `C12` 같은 식별자만 쓰고 실제 해시는 메인 세션이 최종 삽입.
- 보고 형식: 변경 파일 목록 + 섹션별 요약.

## 2. 본 세션 작업 — 결과 통합

### 2.1 Agent 1 결과 검토

1. `Read`로 변경 파일(`App/UI/Tray.cs`, `App/UI/TrayIcon.cs`, `Core/Tray/*.cs`) 전체 일독.
2. `git grep "KoEnVue\.App" Core/` → 0.
3. `git grep "using KoEnVue\.App" Core/Tray/` → 0.
4. `git grep "AppConfig\|DefaultConfig\|ImeState\|I18n" Core/Tray/` → 0.
5. `git diff --name-only`에서 Program.cs/CleanupDialog/SettingsDialog/ScaleInputDialog가 수정되지 않았는지 확인.

### 2.2 분리 판단

Agent 1이 큰 블로커(Risk 4 누수, 빌드 실패, Program.cs 수정 필요)를 보고했다면:

- Tray 작업은 `git restore App/UI/Tray.cs Core/Tray/` 로 폐기.
- Phase 6으로 이월하는 TODO를 `docs/reorg/05-stage4-core-extraction.md`의 Task D 섹션에 명시.
- 이 Phase는 문서 현행화만 진행하고 커밋은 docs 커밋 하나만 만든다.

큰 블로커가 없으면 그대로 진행.

### 2.3 Agent 2/3 결과 병합

- CLAUDE.md 및 docs/reorg/*.md 파일들을 `Read`로 일독.
- 실제 저장소 트리와 한 줄이라도 불일치가 있으면 직접 수정 (에이전트 재요청보다 메인 세션이 수정하는 편이 더 정확한 경우 많음).
- Stage 4 커밋 해시를 넣을 자리는 아직 `C12`로 둔다. 최종 docs 커밋 직전에 해시로 교체.

## 3. 빌드·스모크

### 3.1 Tray 구현이 포함된 경우

- `dotnet build` / `dotnet publish -r win-x64 -c Release` 성공.
- exe 크기 Phase 4 대비 ±수 KB.
- Release exe 실행:
  1. 트레이 아이콘이 보이는지.
  2. 호버 시 툴팁이 뜨는지 (`NIF_SHOWTIP` 유지 검증).
  3. 우클릭 메뉴가 뜨는지 (`WM_CONTEXTMENU` 라우팅 유지 검증).
  4. 메뉴의 "설정", "정리", "스케일", 각종 토글이 정상 동작하는지.
  5. 시작 시 schtasks 동기화 경로가 동작하는지 (`koenvue.log`에 schtasks 관련 에러 없음).
  6. 트레이 클릭 액션(토글 등)이 정상 동작하는지.

### 3.2 Tray 구현이 연기된 경우

- `dotnet build` / `dotnet publish -r win-x64 -c Release` 성공 (문서만 변경되었으니 exe는 Phase 4 퍼블리시와 동일해야 한다).

## 4. 검증

모두 통과해야 커밋. 실패 시 `git restore .`로 폐기 후 재시도.

### 4.1 Tray 포함 커밋 검증

1. `git grep "KoEnVue\.App" Core/` → 0.
2. `git grep "using KoEnVue\.App" Core/Tray/` → 0.
3. `git grep "AppConfig\|DefaultConfig\|ImeState\|I18n" Core/Tray/` → 0.
4. `git grep "AppConfig" Core/` → 0 (Risk 4 광역 재확인).
5. `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0 (Risk 5 재확인).
6. `dotnet publish` 성공, exe 크기 편차 수 KB.
7. 3.1 스모크 매트릭스 6개 통과.

### 4.2 문서 커밋 검증

1. CLAUDE.md의 "Project Structure" 섹션이 실제 `find Core -type d` 결과와 일치.
2. CLAUDE.md의 Stage 4 Key Decisions 항목이 Task A~D를 모두 커버.
3. `docs/reorg/05`의 각 Task가 "완료 커밋" 표기를 가짐.
4. `docs/reorg/08`의 최종 게이트 숫자가 실제 측정값과 일치.
5. `docs/reorg/09`의 Risk 2/4/5/6에 Stage 4 대응 문단이 존재.

## 5. 커밋

### 5.1 Tray 커밋 (선택)

- 커밋 메시지: `refactor(reorg): Stage 4 Task D — Tray Shell_NotifyIconW → Core/Tray/NotifyIconHost (C12)`
- 본문: NotifyIconHost 신설, App 도메인 로직 유지, Risk 4 준수 확인, exe 크기 수치.

### 5.2 문서 커밋 (필수)

- 커밋 메시지: `docs: reflect Stage 4 Core extraction (Overlay/Animation/Config/Tray)`
- 본문: CLAUDE.md Key Decisions 업데이트, docs/reorg/ 05~09 갱신, Stage 4 완료 보고.
- Tray 커밋의 실제 해시를 `docs/reorg/` 파일들의 "완료 커밋" 자리에 삽입한 뒤 문서 커밋을 만든다.

Tray 커밋이 연기된 경우 문서 커밋만 만들고, 05의 Task D 섹션은 "Phase 6으로 이월" 메모를 남긴다.

## 6. Phase 종료 시 사용자에게 보고

- ≤250단어.
- 분리된 파일 목록 (Core/Tray/*, App/UI/Tray.cs 요약).
- 갱신된 문서 파일 목록.
- 최종 exe 크기 (Stage 4 전체 후 값, Phase 1 대비 총 변화량).
- Risk 4/5/6 grep 결과.
- Stage 4 전체 스모크 매트릭스 요약(Overlay/Animation/Settings/Tray 각 케이스).
- Tray가 연기되었다면 Phase 6 계획 안내.
- Stage 4 전체가 끝났음을 사용자에게 공식 보고.
