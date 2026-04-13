# 07 — Stage 6: 문서 현행화

← Previous: [06 — Stage 5](06-stage5-verification.md) | → Next: [08 — Stage 7](08-stage7-final-gate.md)

## 목표

사용자가 **명시적으로 요구**한 마지막 단계. Stage 0~5를 통해 확정된 Core/App 구조를 CLAUDE.md와 docs/KoEnVue_PRD.md에 반영한다. 후속 세션의 모든 작업이 이 두 문서를 기준으로 진행되므로 현행화 누락 시 계획 전체의 가치가 떨어진다.

## 에이전트 구성

- **구성**: serial, 1x general-purpose
- **작업 대상**: `CLAUDE.md`, `docs/KoEnVue_PRD.md`

## CLAUDE.md 수정 범위

### 1. Project Structure 섹션 교체
기존 트리(Native/Models/Detector/UI/Config/Utils)를 새로운 `Core/` + `App/` 트리로 전면 재작성. [00 — Context & Target Structure](00-context-and-structure.md)의 트리를 그대로 옮긴다.

### 2. Hard Constraints 테이블 확장
- **P4에 "중복 구현 금지" 원칙 명시**: "동일 기능의 복수 구현 금지 — Stage 1에서 해소한 6건(ColorHelper.TryNormalizeHex, SafeFontHandle 다이얼로그 적용, CleanupDialog IsDialogMessageW, Tray.cs 분할, Config→Detector 레이어 해소, AppConfig 좁힘) + Stage 2 선행 substep 1건(DpiHelper → Config 의존 단절, BASE_DPI 인라인)을 worked example로 참조"
- **새 규칙 P6 (Core/App 분리) 추가**:
  > `Core/` 하위 파일은 `KoEnVue.App.*` 심볼을 참조할 수 없음. 검증: `git grep 'KoEnVue\.App' Core/` 는 항상 0건. 의존 방향은 `App → Core` 단방향.

### 3. Key Implementation Decisions 경로 전수 갱신
모든 bullet의 파일 경로를 새 구조로 수정:
- `Utils/Win32DialogHelper.cs` → `Core/Windowing/Win32DialogHelper.cs`
- `UI/Overlay.cs` → `App/UI/Overlay.cs`
- `UI/SettingsDialog.cs` → `App/UI/Dialogs/SettingsDialog.cs`
- `Native/AppMessages.cs` → `App/UI/AppMessages.cs`
- `Native/Dwmapi.cs` → `Core/Native/Dwmapi.cs`
- ... 모든 bullet 동일 처리

### 4. 새 섹션 "Core Reuse Contract" 추가
Core 폴더의 목적, 공개 API 요약(LayeredOverlayBase, OverlayAnimator, JsonSettingsManager\<T\>, NotifyIconManager, ModalDialogLoop, ColorHelper, DpiHelper, Logger, Win32DialogHelper, WindowProcessInfo, WindowFilter), 다른 프로젝트에서 재사용하는 방법(폴더 복사 또는 `<Compile Include>` 링크).

### 5. Stage 4에서 새로 생긴 구조에 대한 구현 결정 사항 추가
- OverlayStyle record
- AnimationConfig record
- JsonSettingsManager 훅 모델
- NotifyIconManager 콜백 주입 패턴
- Core→App ImeState 무의존 전략

## docs/KoEnVue_PRD.md 수정 범위

### 1. "Architecture — Core/App Split" 섹션 신설
- 재사용 계약
- 단방향 의존 규칙
- 각 Core 모듈의 범위 한 줄 설명

### 2. 기존 모듈 레이아웃 섹션 전면 재작성 (있다면)

### 3. 재사용 대상/비대상 명시
- Core가 다른 프로젝트에서 제공하는 기능
- KoEnVue-specific으로 남는 부분(ImeStatus, SystemFilter, ThemePresets, I18n 등)
- 둘을 분리해 기술

## 검증 게이트

1. **구 경로 grep 0건**:
   ```
   grep -E "Utils/(ColorHelper|DpiHelper|Logger|Win32DialogHelper|I18n)\.cs|Native/AppMessages\.cs|Models/LogLevel\.cs|UI/Tray\.cs|UI/SettingsDialog\.cs"
   ```
   CLAUDE.md와 docs/KoEnVue_PRD.md에서 매치 없음.

2. **신규 경로 존재 확인**: 문서에 명시된 모든 신규 경로가 실제 파일로 존재하는지 grep으로 확인.

3. **Project Structure 트리 정합성**: 실제 `ls -R` 결과와 CLAUDE.md의 Project Structure 트리가 글자 단위로 일치.

## 커밋 출력

| # | 커밋 제목 |
|---|---|
| C13 | `docs: sync CLAUDE.md and PRD with Core/App split` |

---

← Back to [README](README.md)
