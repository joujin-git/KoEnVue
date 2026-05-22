---
description: 세션 마무리 — historian 서브에이전트로 docs/sessions/ 정리, dirty 시 의미 있는 커밋, push 까지 확인.
allowed-tools: Bash, Read, Edit, Write
---

## 현재 상태
- 변경 요약: `!`git diff HEAD --stat``
- 상태: `!`git status --short``
- 이번 세션 커밋: `!`git log --since='2 hours ago' --pretty=format:'%h %s'``
- push 안 한 commit: `!`git rev-list --count '@{u}..HEAD' 2>/dev/null``

## 임무

1. **docs-keeper** 호출 — 미동기화 docs 정리
2. **historian** 호출 — 오늘의 세션 파일에 정리 블록 append
3. dirty tree 가 남아있으면 사용자에게 묻기:
   - "남은 변경: …. wip 커밋할까요, 아니면 의미 있는 커밋 메시지로 묶을까요?"
4. 사용자 확정 후 `git commit` 실행 — **PostToolUse hook 이 자동으로 push 까지 실행**합니다 ("커밋 = 푸시 항상 같이" 규칙)
5. push 결과를 사용자에게 확인:
   - 성공: "✅ push 완료 — 다른 장비에서 즉시 받을 수 있습니다"
   - 실패: 메시지에 따라 대응 (upstream 미설정 → `git push -u origin <branch>`, 거부 → 충돌 해결)
6. 만약 commit 안 하는 경우에도 push 안 한 commit 이 있으면 사용자에게 `git push` 확인

추가 인자(있다면): $ARGUMENTS

**중요**: 이 명령 끝나면 세션 종료 가능 + 다른 장비에서 즉시 이어받을 수 있는 상태가 돼야 합니다. SessionEnd hook 이 어차피 wip + push fallback 을 잡지만, 의미 있는 정리는 이 명령에서.

## `docs/sessions/YYYY-MM-DD.md` 쓰기 단일 진실원 규약

같은 파일을 hook + subagent + 메인 세션이 동시에 건드리면 race condition (`Add-Content` 가 file lock 비보장) 으로 데이터 손실 가능. 따라서:

| 주체 | 허용된 작업 |
|------|------------|
| `stop-record.ps1` hook | `## [HH:MM] turn` 블록 append (이번 턴 transcript 발췌) |
| `session-end.ps1` hook | `## [HH:MM] session-end` 블록 append (마무리) |
| `historian` subagent | `## [HH:MM] 세션 정리` 블록 append |
| **메인 세션** | **Read 만**. 직접 Edit/Write 금지 — 필요하면 historian 에 위임. |

`/wrap-up` 흐름은 이 순서를 보장:
1. docs-keeper (다른 docs/ 만 건드림 — sessions/ 는 안 건드림)
2. historian (sessions/ 단독 쓰기)
3. 사용자 확정 후 commit (메인 세션은 git 만 — sessions/ 는 안 건드림)
