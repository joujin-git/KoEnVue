---
name: user-role
description: 사용자는 비개발자 출신으로 Claude Code 로 바이브 코딩하는 1인 개발자. 개발 용어를 잘 모르므로 설명할 때 점진적·구체적·한국어 우선.
metadata: 
  node_type: memory
  type: user
  originSessionId: c492f502-5d0a-450d-853d-101a243df772
---

joujin 은 비개발자 출신으로 Claude Code 를 사용해 KoEnVue (Windows IME 인디케이터, C#/.NET 10) 를 바이브 코딩 스타일로 만들고 있다.

## 함의

- **용어 풀이를 곁들이기**: "subagent", "hook", "permission mode" 같은 Claude Code 용어가 처음 나올 때 한 줄 풀이
- **결정의 결과를 보여주기**: 추상적 설명보다 "이걸 고르면 X 가 자동으로 일어남" 같은 구체적 효과
- **여러 선택지의 트레이드오프 명시**: 둘 중 하나로 결정하는 자리에선 양쪽 결과를 보여줌
- **한국어 우선**: UI/대화는 한국어, 코드/커밋/PR 은 영어 (P2 와 동일 정책)
- **GitHub 자체 익숙도는 보통 수준**: gh CLI 와 git 명령은 통하지만, 고급 기능(rebase --interactive, cherry-pick, signed commits)은 회피
