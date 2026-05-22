---
name: feedback-workflow-rules
description: "KoEnVue 의 두 워크플로우 규칙 — 빌드는 debug + release publish 항상 둘 다, 커밋은 push 까지 항상 같이."
metadata: 
  node_type: memory
  type: feedback
  originSessionId: c492f502-5d0a-450d-853d-101a243df772
---

KoEnVue 의 워크플로우 규칙 2건 (2026-05-22 사용자 지시).

## 규칙 1 — 빌드는 항상 둘 다

`dotnet build` (debug) + `dotnet publish -r win-x64 -c Release` (AOT) 항상 둘 다 실행. 한쪽만 하면 release exe 가 outdated 가 됩니다.

**Why**: 사용자 명시 — "빌드할 때 릴리즈 빌드도 항상 같이 해야 해". 과거에 debug 만 돌고 release 가 stale 상태로 commit/push 된 사고 발생 가능성을 차단.

**How to apply**: 
- 코드 변경 후 `dotnet build` 호출했다면 즉시 `dotnet publish -r win-x64 -c Release` 도 실행
- verifier 서브에이전트가 두 단계를 모두 실행 — 가능하면 verifier 에 위임
- "debug 만 통과했어요" 라고 보고하지 말 것 — release publish 까지 마쳐야 검증 완료

## 규칙 2 — 커밋은 푸시 항상 같이

`git commit` 후 즉시 `git push`. Claude 가 `git commit` 호출하면 PostToolUse hook (`auto-push.ps1`) 가 자동 push. SessionEnd hook 도 fallback 으로 push.

**Why**: 사용자 명시 — "커밋, 푸시 항상 같이 해야 해". 다른 장비에서 즉시 받을 수 있게 보장.

**How to apply**:
- `git commit` 호출하면 자동 push 가 일어남 — 별도 호출 불필요
- 사용자가 직접 commit 한 경우엔 자동 push 안 일어남 — `/harness-status` 또는 SessionStart 의 "push 안 한 commit" 알림으로 확인
- upstream 미설정 시 (`-u origin <branch>` 없이 첫 push) 한 번은 수동 필요
- push 실패 시 (원격 거부) 충돌 해결 후 수동 push

## 관련 메모리
- [[harness-design]]
