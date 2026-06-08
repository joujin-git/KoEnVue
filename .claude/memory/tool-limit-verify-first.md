---
name: tool-limit-verify-first
description: 도구/SDK/설정 등 제어 가능한 제약은 "못 한다/감수" 단정 전에 저비용 실험으로 재확인
metadata:
  type: feedback
---

도구·SDK·하네스 설정처럼 **제어 가능한** 제약은 "안 된다 / 도구 제약이라 감수" 로 단정하기 전에 **저비용 실험으로 직접 확인**한다. 제어 불가한 OS 의존([[os-dependent-accept]])과 정반대 — 이쪽은 감수 전에 한 번 찔러본다.

**Why:** "감수" 로 적어둔 제약 셋이 실제로는 실험하니 가능했다 — (1) PreToolUse 파괴명령 차단이 bypassPermissions 에서 무효라 감수했으나 `permissions.deny` 로 실현(2026-06-08). (2) 워크플로우 JS 정적 문법검사가 `node --check` 의 top-level await 오탐 때문에 "효용 한정" 보류였으나, AsyncFunction 으로 async 본문 파싱하면 오탐 0 으로 해결·적용. (3) `SubagentStop` hook 부재 + 공식문서 "결과 포함 불확실" 이라 서브에이전트 자동기록을 미검증 보류했으나, PostToolUse matcher `Task`(tool_name=Agent) 가 발화 + tool_response 에 결과·토큰·소요시간 완전 포함됨을 probe 로 확인(실현 가능 확정). 공식문서/직관이 "불가/불확실" 이어도 실측이 우월한 경우가 반복됐다.

**How to apply:** 도구·SDK·설정 제약에 막히면, 감수 결론 전에 echo-deny·probe hook·probe 워크플로우 같은 **격리된 최소 실험**(부작용 0, 직후 원복)으로 실제 동작을 확인한다. 단 "가능함" 과 "해야 함" 은 별개 — 실현 가능해도 과복잡 등으로 미구현 결정할 수 있다(PostToolUse 자동기록은 가능하나 hook 과복잡으로 미구현). 가능 여부는 실험으로, 적용 여부는 트레이드오프·사용자 결정으로.
