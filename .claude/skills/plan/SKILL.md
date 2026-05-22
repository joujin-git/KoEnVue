---
description: Planner 서브에이전트에 설계를 위임. 다중 파일/새 기능/P규칙 영향 작업 전에 사용.
argument-hint: [작업 설명]
allowed-tools: Bash, Read, Glob, Grep
---

다음 작업의 설계를 **planner 서브에이전트**에게 위임하세요:

> $ARGUMENTS

위임 시 다음을 planner 에게 전달:
- 위 작업 설명
- 현재 git 상태 (`!`git status --short``)
- 현재 브랜치 (`!`git rev-parse --abbrev-ref HEAD``)
- 이미 진행 중인 PR-XX 가 있다면 [docs/improvement-plan/INDEX.md](docs/improvement-plan/INDEX.md) 참조

planner 가 반환한 계획을 받으면:
1. 사용자에게 그대로 제시
2. 큰 변경이면 `docs/improvement-plan/PR-XX-<slug>.md` 신규 파일을 만들지 사용자에게 묻기
3. 사용자 승인 후에야 구현 시작

**중요**: 사용자 승인 전까지 Edit/Write 도구로 코드 변경 금지.
