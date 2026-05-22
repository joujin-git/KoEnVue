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

- **모델**: `opus` (Opus 4.7). `effortLevel: "max"` (settings 명목 — schema 가 silent ignore / clamp 시에도 env 가 fallback). `CLAUDE_CODE_EFFORT_LEVEL=max` (env 로 실효 max 강제). 검증: statusline payload 가 `effort.level=max` 로 받음.
- **Thinking 항상**: `alwaysThinkingEnabled: true`. ultrathink 키워드는 `UserPromptSubmit` hook 으로 매 턴 자동 주입
- **단일 세션 + 항상 서브에이전트**: Agent Team 안 씀 (토큰 3–5배, resume 미지원, 동시 1팀만)
- **권한**: `bypassPermissions` 전체 — 사용자가 git 으로 책임짐. 속도 우선
- **PR 없음**: main 직커밋 (1인 프로젝트 흐름 유지)
- **언어**: 대화 한국어, 코드/커밋 영어 (P2 정책)

**Why**: 사용자가 명시한 "비용 무제한, 깊이 최우선" + "단일 세션 + 항상 서브에이전트" + "main 직커밋 유지" 결정. 인터뷰 4라운드 결과.

**How to apply**: 하네스 관련 결정을 다시 묻지 말 것. 이 결정과 충돌하는 변경(예: agent team 활성화, 자동 PR 생성)을 제안하기 전엔 사용자에게 명시적으로 확인 받기.

## 서브에이전트 6명

- **explorer**: 탐색/검색 (Read/Glob/Grep/Bash)
- **planner**: 설계 (구현 안 함). 다중 파일/새 기능/P규칙 변경 자동 위임
- **reviewer**: 코드 변경 후 P규칙 invariant + 빌드 + 품질
- **docs-keeper**: docs/ 동기화 (PostToolUse hook 신호 받음)
- **historian**: 세션 요약 → `docs/sessions/YYYY-MM-DD.md`
- **verifier**: build/publish/test (UI 동작은 검증 불가)

전체 정의는 `.claude/agents/*.md`.

## 히스토리

- 하루 1개 파일, append: `docs/sessions/YYYY-MM-DD.md`
- Stop hook = 매 턴 진행 노트, SessionEnd hook = 확정판 + wip 커밋
- 다른 장비 이어 작업: `git pull` → `claude` → SessionStart hook 이 자동 컨텍스트 주입 → 필요 시 `/resume-session`

## .claude/ git 추적 정책

선택적 추적: `settings.json`, `agents/`, `commands/`, `hooks/` 만 commit. `settings.local.json`, `worktrees/`, `state/`, 옛 PS 스크립트는 gitignore.

## CLAUDE.md 30줄 하드 제한

`InstructionsLoaded` hook 이 검사. 초과 시 경고. 모든 부가 내용은 [[harness-design]] 참고하여 `docs/` 하위로.

## 관련 메모리

- [[user-role]]
- [[version-format]]
