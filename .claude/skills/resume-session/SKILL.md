---
description: 다른 장비에서 이어 작업할 때. 최근 세션 기록과 git 상태를 보고 이어갈 컨텍스트 정리.
allowed-tools: Bash, Read, Glob
---

## 현재 상태
- 브랜치: `!`git rev-parse --abbrev-ref HEAD``
- 작업 트리: `!`git status --short``
- 최근 10 커밋: `!`git log --oneline -10``
- wip 커밋 검색: `!`git log --oneline --grep='^wip:' -10``

## 가장 최근 세션 기록
다음 파일을 Read 해서 사용자에게 요약 제시:

오늘과 어제 (있다면) 의 `docs/sessions/YYYY-MM-DD.md` 둘 다.

## 임무
1. **상태 보고**: 어디까지 했고, 무엇이 dirty 인지
2. **다음 작업 후보**: 가장 최근 세션의 "다음" 섹션과 wip 커밋 메시지에서
3. **위험 신호**: dirty tree 가 있으면 이전 wip 커밋 인지 / 임 변경인지 분별
4. 사용자에게 "이어서 X 할까요? 아니면 다른 거 하실래요?" 라고 묻기 — 단정해서 시작하지 말 것

추가 인자(있다면): $ARGUMENTS
