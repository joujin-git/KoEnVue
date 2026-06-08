---
name: os-dependent-accept
description: OS(Win32/셸) 동작에 의존하고 제어 불가능한 버그는 무리하게 고치지 말고 감수하는 것을 선호
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 492e1a7e-ee5d-4878-9b09-9db207650950
---

사용자는 KoEnVue 가 제어할 수 없는 OS 동작에 의존하는 버그는 무리한 고정값/휴리스틱 수정보다 **감수**하는 것을 선호한다 (2026-06-08 명시: "OS 의존적인 문제는 이후에도 감수하기로 함").

**Why:** OS 업데이트마다 타이밍/동작이 바뀌면 어렵게 맞춘 수정이 다시 깨지고, 실사용과 구분할 신뢰 신호가 없으면 회귀 위험이 크다. (사례: 설정앱 cold start 시 작업표시줄(Shell_TrayWnd)/바탕화면(Progman)이 foreground 를 2~4초 점유 → 인디 완전소멸. grace period 를 구현했으나 OS 의 cold start foreground 지연을 흡수 못 해 롤백, 감수. → docs/improvement-plan/PR-25 보류 보존. 후속 재관찰(2026-06-08, PR-23 코드, koenvue.log 11:30 세션)에서 **무조작 통제 실험**으로 확정: 설정앱 켜두고 무조작 시 소멸 0건(`Filter HIDE deferred` 조차 없음, 56초 무이벤트), 작업표시줄/바탕화면을 **클릭한 순간에만** `Filter triggered HIDE streak=3, fgClass=Shell_TrayWnd` → 즉 무조작 자동 소멸은 존재하지 않고 사용자 조작 동반이며 이는 SystemFilter(셸 위 인디 숨김)의 **의도된 동작**이라 추가 수정 없이 종결. cold start 완전소멸은 첫 클릭이 `streak=1`에서 리셋되면 미발생 = 간헐·OS 속도 의존 재확증.)

**How to apply:** 근본 원인이 OS/Win32 동작 의존으로 규명되면, 무리한 수정을 강행하기 전에 "감수 vs 수정" 을 사용자에게 확인한다. 수정이 OS 버전 가변 타이밍에 의존하거나 실사용과 구분 불가하면 감수를 우선 제안. 단 원인 규명·기록(제안서 보류 보존)은 충실히 — 향후 OS 변화 시 재검토 출발점이 되도록.
