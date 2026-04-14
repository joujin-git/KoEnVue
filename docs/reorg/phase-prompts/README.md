# Stage 4 Phase Prompts

Stage 4 (Core extraction)를 5개의 독립 세션으로 분할 실행하기 위한 self-contained 프롬프트 모음.
각 파일은 **단일 Claude Code 세션에서 통째로 붙여 넣어 실행**하도록 설계되었다.

## 실행 순서

1. `phase1-skeleton.md` — 설계·스켈레톤 (C9-0)
   - Core/Overlay, Core/Animation, Core/Config 신규 디렉터리 생성
   - LayeredOverlayBase, OverlayStyle, OverlayAnimator, AnimationConfig, JsonSettingsManager 스켈레톤
   - 기존 코드 수정 없음, exe 크기 4,891,136 유지
2. `phase2-overlay.md` — Task A: Overlay 분리 (C9)
   - Overlay 렌더링 로직을 Core/Overlay/LayeredOverlayBase로 이전
   - App/UI/Overlay.cs는 static facade로 유지
   - Stage 4 중 가장 큰 단일 작업
3. `phase3-animation.md` — Task B: Animation 분리 (C10)
   - 상태 머신 + WM_TIMER 루프를 Core/Animation/OverlayAnimator로 이전
   - NonKoreanImeMode.Dim 분기는 App 잔존
4. `phase4-settings.md` — Task C: Settings 분리 (C11)
   - **Risk 2 (NativeAOT ILC 회귀) 가장 민감한 Phase**
   - JsonSettingsManager<T> + JsonTypeInfo<T> 주입
   - 삭제 안전 hot-reload / 손상 스팸 방지 / 자동 생성 유지
5. `phase5-tray-docs.md` — Task D: Tray 분리 + 문서 현행화 (C12 + docs)
   - Tray Shell_NotifyIconW 상호작용을 Core/Tray/NotifyIconHost로 이전
   - CLAUDE.md + docs/reorg/ 05~09 전체 현행화
   - Tray 작업이 블로커에 부딪히면 Phase 6으로 이월 가능 (문서만 단독 커밋)

## 세션당 1 Phase 원칙

- 각 Phase는 **새로운 세션**에서 시작한다. 세션 간 상태는 git만으로 전달된다.
- 중간에 세션이 끊겨도 해당 Phase는 원자적으로 롤백/재시작 가능 — `git restore .` 후 동일 프롬프트로 재실행.
- Phase 완료 커밋이 반드시 존재해야 다음 Phase의 sanity check를 통과한다.

## 공통 검증 패턴

모든 Phase는 다음 3가지 grep 가드를 공유하며, Phase별로 추가 가드가 붙는다.

**공유 가드 (모든 Phase)**:

- `git grep "KoEnVue\.App" Core/` → 0 (P6: Core→App 역참조 금지)
- `git grep "AppConfig" Core/` → 0 (Risk 4: Core에서 AppConfig 직접 참조 금지)
- `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0 (Risk 5: Config→Detector 역참조 금지)

**Phase별 추가 가드**: `ImeState`, `DefaultConfig`, `ThemePresets`, `I18n` 등을 해당 Phase가 작업하는 Core 서브디렉터리(`Core/Overlay/`, `Core/Animation/`, `Core/Config/`, `Core/Tray/`)에 대해 검사한다.

## 롤백 정책

모든 검증은 **커밋 직전**에 수행한다. 실패 시:

1. `git restore .`로 작업 내용 폐기
2. 원인 분석 (에이전트 프롬프트 부족 / Risk 2 누락 / 등)
3. 에이전트 프롬프트 재작성 후 재실행

커밋이 이미 만들어진 뒤 문제를 발견했다면 `git reset --hard HEAD~1`은 **사용하지 않는다**. 대신 revert 커밋으로 되돌린다 (git 정책).

## 관련 스펙 문서

- `../05-stage4-core-extraction.md` — Stage 4 전체 상위 스펙
- `../06-stage5-verification.md` — 스모크 매트릭스 정의
- `../07-stage6-docs-sync.md` — 문서 동기화 범위
- `../08-stage7-final-gate.md` — 최종 게이트 기준
- `../09-risks-and-reuse.md` — Risk 2/4/5/6 상세 (필독)

## exe 크기 기준선

| Phase | 기준 | 허용 편차 |
|-------|------|-----------|
| Phase 1 완료 | 4,891,136 바이트 (변화 없음) | 0 |
| Phase 2 완료 | Phase 1 ±수 KB | +50KB 미만 |
| Phase 3 완료 | Phase 2 ±수 KB | +50KB 미만 |
| Phase 4 완료 | Phase 3 ±100KB | +100KB 미만 (Risk 2) |
| Phase 5 완료 | Phase 4 ±수 KB | +50KB 미만 |

Phase 4에서 큰 증가(>+100KB)는 ILC가 reflection fallback을 include했다는 강력한 신호 — 즉시 롤백 후 재설계.
