---
description: docs-keeper 서브에이전트에 docs/ 동기화를 위임. 코드 변경 후 commit 직전에 호출.
allowed-tools: Bash, Read, Edit, Write
---

> **주의** — 아래 `!` 백틱 셸 명령이 실행 결과가 아니라 명령 문자열 그대로 보이면 자동 실행되지 않은 것입니다(Skill 도구 호출 경로에서 관측). 그때는 **직접 실행한 뒤** 답하세요 — 추측으로 상태를 보고하지 말 것.

**docs-keeper 서브에이전트**에 다음을 위임:

## 컨텍스트
- 현재 git diff: `!`git diff HEAD --stat``
- 현재 git status: `!`git status --short``
- 최근 5개 커밋: `!`git log --oneline -5``

## 임무
1. 위 변경에 비추어 어느 docs 가 갱신돼야 하는지 매핑 (docs-keeper 의 매핑 테이블 참조)
2. 각 문서에 필요한 최소 패치 적용
3. CHANGELOG.md `## [Unreleased]` 섹션에 사용자 가시 변경 사항 추가
4. 갱신 결과 보고

추가 인자(있다면): $ARGUMENTS
