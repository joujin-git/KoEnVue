# Phase 1/5 — 설계·스켈레톤 (C9-0)

> 이 파일은 **단일 세션에서 붙여 넣어 실행**하기 위한 self-contained 프롬프트다.
> Phase 2~5는 각각 새로운 Claude Code 세션에서 순차 실행한다.

---

## Task

Stage 4 Core extraction의 준비 단계로, 이후 Task A/B/C/D가 공통으로 참조할 추상화와 디렉터리 스켈레톤을 만든다. 이 Phase에서는 **기존 코드 수정 없이** Core/ 하위에 새 서브디렉터리 3개(Core/Overlay, Core/Animation, Core/Config)와 skeleton 파일만 생성한다. 첫 빌드/스모크 기준선을 갱신한 뒤 커밋 C9-0을 만든다.

## Context

- 저장소: `e:\dev\KoEnVue`
- 브랜치: `master`
- 직전 상태: Stage 3 C8 완료 (AppConfig signature 좁히기). Release 퍼블리시 exe 4,891,136 바이트.
- 관련 문서:
  - `docs/reorg/05-stage4-core-extraction.md` — Stage 4 전체 스펙 (필독)
  - `docs/reorg/06-stage5-verification.md` — 스모크 매트릭스 정의 (필독)
  - `docs/reorg/07-stage6-docs-sync.md` — 문서 동기화 범위 (참고, Phase 5에서 적용)
  - `docs/reorg/08-stage7-final-gate.md` — 최종 게이트 기준 (참고)
  - `docs/reorg/09-risks-and-reuse.md` — Risk 2/4/5/6 상세 (필독)
  - `CLAUDE.md` — 프로젝트 하드 룰 (P1~P6), Stage 3 narrowing 현황

05는 Stage 4 전체(Task A~D 모두)의 상위 스펙이고 06/07/08은 Stage 4 이후 Stage 5/6/7을 정의한다. Phase 1은 05의 준비 단계만 수행한다.

## Hard constraints

- **P1~P6을 전부 지킨다.** 특히:
  - **P4** — 공유 모듈 경로는 Core/ 안에서만 만든다. App→Core로 역참조 금지.
  - **P6** — App→Core는 허용, Core→App은 금지. `git grep "KoEnVue\.App" Core/`는 0이어야 한다.
- 어떠한 **기존 파일도 삭제·이동**하지 않는다. 새 디렉터리·파일 추가만 수행한다.
- 새로 추가한 skeleton 인터페이스/레코드는 Core/ 안에서만 쓰이고, App/ 쪽과는 아직 연결하지 않는다.
- 이 Phase가 끝난 뒤에도 Release 퍼블리시 exe는 **4,891,136 바이트로 유지**되어야 한다(기존 코드 경로 변경 없음).

## 계획 재검토 원칙

**구현 중 계획 재검토가 필요하면 먼저 알려줘.** 에이전트 3대 조사 결과, 이 프롬프트의 skeleton 계약(Core 서브디렉터리 3개 이름, 6개 skeleton 파일 목록, `OverlayStyle`/`LayeredOverlayBase`/`OverlayAnimator`/`AnimationConfig`/`AnimationTimerIds`/`JsonSettingsManager<T>` 시그니처 초안, `JsonTypeInfo<T>` 생성자 주입 원칙) 중 하나라도 실제 코드와 중요한 불일치가 발견되면, **skeleton 파일 작성을 시작하기 전에 메인 세션이 사용자에게 보고하고 승인을 받는다**. 에이전트는 독자 판단으로 skeleton 계약을 바꾸지 않으며, 메인 세션도 본 Phase의 하드 룰(기존 파일 수정 금지, exe 크기 4,891,136 유지)을 깨야 한다고 판단되면 동일하게 보고한다. 보고 전까지는 기존 프롬프트의 가정을 그대로 따른다.

## 0. Sanity check (작업 시작 전 필수)

다음 모두를 확인한다. 하나라도 실패하면 작업을 중단하고 원인 조사부터 한다.

- `git status`가 clean.
- `git log --oneline -5`에 Stage 3 narrowing 커밋들이 존재한다.
- `git grep "KoEnVue\.App" Core/` → 0건 (P6 위반 없음).
- `git grep "AppConfig" Core/` → 0건 (Risk 4: Core에서 AppConfig 참조 없음).
- `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0건 (Risk 5: Config→Detector 역참조 없음).
- `dotnet build` 성공.
- `dotnet publish -r win-x64 -c Release` 성공, exe 크기 4,891,136 바이트.

## 1. 작업 에이전트 구성

다음 3개의 general-purpose 에이전트를 **병렬**로 스폰한다. 각 에이전트는 **연구·조사만** 수행하며, 결과를 돌려주면 메인 세션이 그걸 바탕으로 직접 스켈레톤 파일을 작성한다.

> **Tray는 Phase 1 범위에 없다.** Core/Tray/NotifyIconHost는 Phase 5에서 처음 설계·구현된다. Phase 1에서 미리 연구해도 결과를 담을 skeleton 파일이 없어 버려지므로, Tray 연구는 Phase 5 Agent 1에 일임한다.

### Agent 1 — LayeredOverlay 추상화 설계 조사

- 목적: Core/Overlay/LayeredOverlayBase + OverlayStyle record 시그니처 도출.
- 읽어야 할 파일: `App/UI/Overlay.cs` 전체, `Core/Dpi/DpiHelper.cs`, `Core/Color/ColorHelper.cs`, `Core/Native/SafeGdiHandles.cs`, `Core/Native/Win32Types.cs` 관련 섹션, `docs/reorg/05-stage4-core-extraction.md` Task A 부분.
- 조사 항목:
  1. Overlay.cs에서 `_config` (AppConfig) 필드를 읽는 private helper들을 모두 나열 (`EnsureResources`, `EnsureFont`, `CalculateFixedLabelWidth`, `RenderIndicator`, `HandleDragDpiChange`).
  2. 각 helper가 실제로 읽는 AppConfig 필드를 나열.
  3. 그 필드들로 구성된 **불변 record `OverlayStyle`** 시그니처 제안.
  4. `LayeredOverlayBase`가 가져야 할 protected 멤버 목록 (DIB 핸들, layered window 훅, UpdateLayeredWindow 호출 경로, Dispose 패턴).
  5. `ImeState`가 Core 내부로 새어 들어가는 곳이 있는지 확인 (Risk 4). 있으면 분리 가능한지 서술.
- 보고 형식: ≤400단어 + 위 5개 항목.

### Agent 2 — OverlayAnimator 추상화 설계 조사

- 목적: Core/Animation/OverlayAnimator + AnimationConfig record + AnimationTimerIds record struct 시그니처 도출.
- 읽어야 할 파일: `App/UI/Animation.cs` 전체, `App/Models/AppConfig.cs`의 애니메이션 관련 필드, `App/UI/AppMessages.cs`, `docs/reorg/05-stage4-core-extraction.md` Task B 부분, `docs/reorg/09-risks-and-reuse.md` Risk 6 부분.
- 조사 항목:
  1. Animation.cs의 `_config.*` 읽기 지점 전수 조사.
  2. `NonKoreanImeMode.Dim` 분기가 `GetTargetAlpha`에서 하는 일 — Core로 옮기면 안 되는 App 로직과 Core로 옮겨도 되는 보간·타이머 로직을 분리.
  3. `AnimationConfig` record 시그니처 제안.
  4. WM_TIMER ID는 현재 Animation.cs에서 hard-coded const. Core에서도 hard-code 가능한지, 아니면 `AnimationTimerIds` record struct로 주입해야 하는지 판단. Risk 6 참고.
  5. `OverlayAnimator`가 Core/Overlay/LayeredOverlayBase와 어떻게 통신할지 — 직접 참조인가, 콜백 주입인가.
  6. `ImeState`는 App에서만 들어오도록 유지할 수 있는 설계안.
- 보고 형식: ≤400단어 + 위 6개 항목.

### Agent 3 — JsonSettingsManager<T> 제네릭 설계 조사

- 목적: Core/Config/JsonSettingsManager<T> 시그니처 도출. **Risk 2(NativeAOT ILC 회귀)가 가장 민감한 부분**이므로 반드시 JsonTypeInfo<T> 주입 방식으로 설계.
- 읽어야 할 파일: `App/Config/Settings.cs` 전체, `App/Models/AppConfig.cs`, `App/Config/DefaultConfig.cs`, `docs/reorg/05-stage4-core-extraction.md` Task C 부분, `docs/reorg/09-risks-and-reuse.md` Risk 2 부분.
- 조사 항목:
  1. Settings.cs의 Load/Save/MergeWithDefaults/Validate/Migrate/CheckConfigFileChange 흐름.
  2. AppConfig specific 로직(ThemePresets.Apply, EnsureSubObjects, MergeProfile, ResolveMatchKey)은 Core에 포함하지 않는다. App/Config에 남는다.
  3. `JsonSettingsManager<T>` public 시그니처. **반드시** `JsonTypeInfo<T>` 파라미터를 생성자에서 주입받아야 한다.
  4. 삭제 안전 hot-reload: `CheckFileChange`에서 `File.Exists` 가드를 먼저 확인하는 기존 동작 유지.
  5. 손상된 파일 스팸 방지: `Load()` 실패 시 `_lastMtime`을 손상된 파일의 mtime으로 갱신하는 기존 동작도 유지.
  6. 자동 생성: 파일이 없으면 default를 만들고 즉시 Save까지 수행하는 로직 유지.
- 보고 형식: ≤400단어 + 시그니처 초안 + Risk 2 관련 경고 포인트.

## 2. 본 세션 작업 (에이전트 결과 수렴 후)

각 에이전트 결과를 받아 다음 skeleton 파일만 생성한다. **구현은 비우고** XML doc comment로 계약만 기록한다.

### 2.1 새 디렉터리 생성

`Core/Overlay/`, `Core/Animation/`, `Core/Config/` — **세 디렉터리 모두 새로 만들어야 한다**. Write 툴은 상위 디렉터리가 없으면 에러를 낼 수 있으므로 먼저 다음을 수행한다.

- bash: `mkdir -p Core/Overlay Core/Animation Core/Config`
- bash: `ls Core/` — 기존(Native/Color/Dpi/Logging/Windowing) + 새로 추가된 3개가 보여야 함.

### 2.2 Skeleton 파일

다음 파일들을 **본문 비우고 시그니처만** 추가한다.

1. `Core/Overlay/LayeredOverlayBase.cs` — abstract class, protected DIB/font/layered window 필드 선언만.
2. `Core/Overlay/OverlayStyle.cs` — Agent 1 설계의 record.
3. `Core/Animation/OverlayAnimator.cs` — class, state machine 진입점 시그니처만.
4. `Core/Animation/AnimationConfig.cs` — Agent 2 설계의 record.
5. `Core/Animation/AnimationTimerIds.cs` — Risk 6 대응 record struct. Phase 3 hard constraint가 이 skeleton 존재를 전제로 하므로 **반드시** 이번 Phase에서 만든다. 필드 목록은 비워 두고 XML doc으로 "Phase 3에서 주입" 계약만 명시.
6. `Core/Config/JsonSettingsManager.cs` — `internal sealed class JsonSettingsManager<T> where T : class` 시그니처, 생성자에서 `JsonTypeInfo<T>` 주입 명시.

`Core/Tray/`는 이번 Phase에서는 만들지 않는다 — Phase 5에서 처음 생성한다.

### 2.3 csproj 영향 검토

`.csproj`가 glob 방식으로 `Core/**/*.cs`를 잡는지 확인. 잡히지 않으면 명시 추가. 현재 설정은 glob일 가능성이 높다.

## 3. 빌드·검증

- `dotnet build` 성공.
- `dotnet publish -r win-x64 -c Release` 성공. **exe 크기는 4,891,136 바이트로 유지**되어야 한다 — Phase 1은 새 타입이 호출되지 않으므로 ILC tree-shaking이 전부 걷어낸다. 크기가 달라지면 Core 새 타입이 어딘가에서 참조되고 있다는 뜻이니 원인 조사.
- `git grep "KoEnVue\.App" Core/` → 0.
- `git grep "AppConfig" Core/` → 0 (skeleton도 AppConfig를 직접 참조하면 안 된다).
- `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0.

모든 검증은 커밋 직전에 수행한다. 실패 시 `git restore .`로 작업 내용을 폐기하고 원인을 먼저 파악한 뒤 재시도한다.

## 4. 커밋

- 커밋 메시지: `chore(reorg): Stage 4 Phase 1 — Core/Overlay/Animation/Config skeleton (C9-0)`
- 본문: 추가된 파일 목록, exe 크기 유지 확인, 다음 Phase에서 구현 예정이라는 안내.
- 문서 업데이트는 하지 않는다 (문서는 Phase 5에서 일괄 현행화).

## 5. Phase 종료 시 사용자에게 보고할 내용

- ≤200단어로 요약.
- 추가된 파일 목록 (풀 패스).
- exe 크기 유지 여부.
- Sanity 검증 결과: 3개 grep (`KoEnVue\.App Core/`, `AppConfig Core/`, `App\.Detector App/Config/`) + publish 크기 유지 확인.
- 다음 Phase에서 수행할 Task A 개요를 1~2문장 선행 안내.
