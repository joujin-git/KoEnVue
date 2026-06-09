---
name: feedback-harness-design
description: KoEnVue 의 Claude Code 하네스 설계 결정들. 인터뷰로 확정. 변경 시 docs/harness.md 와 함께 갱신.
metadata: 
  node_type: memory
  type: feedback
  originSessionId: c492f502-5d0a-450d-853d-101a243df772
---

KoEnVue 의 Claude Code 하네스 설계 결정 (2026-05-22 인터뷰 확정).

## 핵심 규칙

- **모델**: `opus` (Opus 4.8). `effortLevel: "xhigh"` — settings 파일 최대 유효값. `max`/`ultracode` 는 session-only 라 파일 스코프 무효(2026-06-08 AUDIT-2 claude-code-guide 2회 검증). schema enum 엔 `max` 포함되나(2026-06-09 schemastore 확인) 그건 JSON 작성 허용일 뿐 파일 스코프 persistent 적용과 별개 — 그래서 파일엔 xhigh. `CLAUDE_CODE_EFFORT_LEVEL=max` (env, 우선순위 최상 — 실효 max 강제). 검증: statusline payload 가 `effort.level=max` 로 받음.
- **Thinking 항상**: `alwaysThinkingEnabled: true`. ultrathink 키워드는 `UserPromptSubmit` hook 으로 매 턴 자동 주입
- **단일 세션 + 항상 서브에이전트**: Agent Team 안 씀 (토큰 3–5배, resume 미지원, 동시 1팀만)
- **권한**: `bypassPermissions` 전체 — 사용자가 git 으로 책임짐. 속도 우선
- **PR 없음**: main 직커밋 (1인 프로젝트 흐름 유지)
- **언어**: 대화 한국어, 코드/커밋 영어 (P2 정책)

**Why**: 사용자가 명시한 "비용 무제한, 깊이 최우선" + "단일 세션 + 항상 서브에이전트" + "main 직커밋 유지" 결정. 인터뷰 4라운드 결과.

**How to apply**: 하네스 관련 결정을 다시 묻지 말 것. 일상적 하네스 수정(설정 정합·버그 수정·정합성 교정)은 사전 확인 없이 바로 진행 — 사용자 명시 위임(2026-06-09 "안 물어보고 곧바로 고쳐도 돼"). 단 이 설계 결정과 충돌하는 변경(예: agent team 활성화, 자동 PR 생성, effort/ultracode 끄기)이나 비가역·권한 약화(permissions.deny 완화 등)는 제안 전 명시 확인.

## 서브에이전트 6명

- **explorer**: 탐색/검색 (Read/Glob/Grep/Bash)
- **planner**: 설계 (구현 안 함). 다중 파일/새 기능/P규칙 변경 자동 위임
- **reviewer**: 코드 변경 후 P규칙 invariant + 빌드 + 품질
- **docs-keeper**: docs/ 동기화 (PostToolUse hook 신호 받음)
- **historian**: 세션 요약 → `docs/sessions/YYYY-MM-DD.md`
- **verifier**: build/publish/test (UI 동작은 검증 불가)

전체 정의는 `.claude/agents/*.md`.

## ultracode — 멀티에이전트 워크플로우 (2026-06-08 전면 도입 확정)

- **발동**: 항상 ON. `inject-turn-context.ps1` hook 이 매 턴 "ultracode" 키워드 + 행동 지시 주입. substantive 작업(다중 파일 변경·리뷰·감사·버그헌트·설계비교·하네스변경)은 Workflow 도구로 오케스트레이션, trivial 은 solo.
- **effort 와 별개 축**: ultracode 는 effort 레벨이 아니다. `CLAUDE_CODE_EFFORT_LEVEL=max` 는 유지 — ultracode 가 effort 를 대체하지 않음(env 를 ultracode 로 바꾸면 max 손실 위험). 이번 세션이 env=max + 키워드 ultracode 조합으로 동작한 게 증거.
- **Agent Team 은 여전히 거부**: Workflow 도구는 Agent Team(TeamCreate)과 다른 메커니즘 — 결정론적·resume(resumeFromRunId)·budget 지원. "단일 세션 + 서브에이전트" 철학과 충돌 없음.
- **저장 워크플로우 5개**: `.claude/workflows/*.js` — release-review, bug-hunt, codebase-audit, design-compare, harness-optimize. `Workflow({name})` 호출 또는 `/<name>`.
- **검증 상태(2026-06-08 갱신)**: 워크플로우 `/<name>` 자동 노출 + `Workflow({name})` 다중 에이전트 fan-out 확인됨(release-review/harness-optimize 각 6 에이전트 실행). hook 키워드가 ultracode "런타임 플래그"를 켜는지만 미확인이나 명시적 지시 + 워크플로우 실행으로 행동 보장. statusLine `ultracode` 는 항상 하드코딩 표시라 검증 신호 아님.

**Why**: 사용자 "비용 무제한, 깊이 최우선" 철학을 ultracode 에도 일관 적용 — 전면 도입 + 항상 자동 (2026-06-08 인터뷰).

**How to apply**: ultracode 발동을 끄지 말 것. substantive 작업은 워크플로우 우선. effort=max 와 ultracode 둘 다 유지 — 어느 하나를 위해 다른 걸 끄는 변경 전 사용자 확인.

## 히스토리

- 하루 1개 파일, append: `docs/sessions/YYYY-MM-DD.md`
- Stop hook = 매 턴 진행 노트, SessionEnd hook = 확정판 + wip 커밋
- 다른 장비 이어 작업: `git pull` → `claude` → SessionStart hook 이 자동 컨텍스트 주입 → 필요 시 `/resume-session`

## .claude/ git 추적 정책

선택적 추적: `settings.json`, `agents/`, `skills/`, `workflows/`, `hooks/`, `memory/` 만 commit. `settings.local.json`, `worktrees/`, `state/`, 옛 PS 스크립트는 gitignore. (구 `commands/` → skills/ 로 전환됨)

## CLAUDE.md 30줄 하드 제한

`InstructionsLoaded` hook 이 검사. 초과 시 경고. 모든 부가 내용은 docs/harness.md 참고하여 `docs/` 하위로.

## 관련 메모리

- [[user-role]]
- [[feedback-version-format]]
- [[feedback-workflow-rules]]
- [[os-dependent-accept]]
