---
name: record-only-what-was-verified
description: "검증 문서에 상태를 새기기 전, 사용자 지시의 「완료」가 검증 완료인지 추적 종료인지 확인할 것"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 65b5b97e-a7b3-4bbd-ae8a-7f3c5686bfb1
  modified: 2026-08-03T22:34:12.076Z
---

검증 문서(체크리스트·AUDIT·릴리즈 판정)에 상태 표시를 바꿀 때는 **「사람이 확인했다」와 「더 이상 추적하지 않는다」를 절대 같은 기호로 적지 않는다.** 사용자가 "할 일에서 완료로 표시해" 라고 짧게 지시하면 두 뜻 다 가능하므로 새기기 전에 물어본다.

**Why:** 2026-08-04, MANUAL-VERIFICATION 의 미실시 4항목(D-5·E-2·F-1·F-2)에 대해 "완료로 표시" 지시를 받았다. 확인해 보니 **검증한 게 아니라 안 하기로 한 것**이었다 — ✅ 를 넣었다면 다음 실시자가 사람이 확인한 항목으로 읽고 릴리즈 판정 근거로 인용했을 것이다. 사용자는 [[verify-load-bearing-claims]]·[[safety-net-verify-in-failure-state]]처럼 근거 없는 통과 표시를 특히 싫어한다.

**How to apply:** ⓐ 지시가 애매하면 "이미 해서 확인함" vs "안 할 거니 추적 종료" 를 선택지로 물어본다. ⓑ 후자면 ✅ 가 아닌 별도 기호(`⊘ 생략` + 날짜 + "확인된 바 없음")로 적고 절차 본문은 지우지 않는다. ⓒ 하위 상태를 낮추면 **상위 인덱스의 「완료/완주」류 요약이 곧바로 모순**되므로 같은 커밋에서 함께 고친다 — [[normative-doc-blanket-claims]] 와 같은 패턴이고, 실제로 docs/INDEX·improvement-plan/INDEX 2곳이 걸렸다.
