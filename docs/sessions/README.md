# Sessions log — KoEnVue

이 디렉토리는 KoEnVue 작업 세션의 누적 기록입니다. **장비 간 작업 연속성**을 위해 git 으로 추적되며, 사람과 Claude 양쪽이 읽기 쉬운 형식으로 유지됩니다.

## 규칙

- **하루 1개 파일**: `YYYY-MM-DD.md`. 하루에 여러 세션이 있어도 한 파일에 append
- **append-only**: 기존 블록 수정 금지 — 항상 끝에 추가
- **Claude Code hook** 이 자동 생성·갱신 — 사람이 직접 쓸 필요 없음

## 누가 무엇을 씁니까?

| 블록 종류 | 작성자 | 시점 |
|----------|-------|------|
| 파일 헤더 | `_common.ps1` 의 `Get-TodaySessionFile` | 파일 처음 만들 때 |
| `## [HH:MM] turn` | `stop-record.ps1` | 매 Stop hook (사용자 응답 종료 시) |
| `## [HH:MM] session-end (reason)` | `session-end.ps1` | SessionEnd hook |
| `## [HH:MM] 세션 정리 — <제목>` | `historian` 서브에이전트 | `/wrap-up` 또는 메인 세션이 호출 시 |

## 이어 작업하기

다른 장비에서 시작 시:
1. `git pull`
2. `claude` 실행 → `SessionStart` hook 이 최신 파일을 자동 컨텍스트 주입
3. (선택) `/resume-session` 으로 상세 점검

가장 중요한 정보는 `## [HH:MM] 세션 정리` 블록의 **다음** 섹션 — 자연스럽게 이어질 다음 작업이 1~3개 적혀 있어야 합니다.

## 정리

- 90일 지난 파일은 분기 단위로 묶거나 압축해 별도 보관 — 향후 자동화 예정
- 민감 정보(토큰·비밀번호 등)는 hook 이 발췌 단계에서 제외하지 않으므로, 그런 내용이 transcript 에 들어가지 않게 평소 주의

자세한 하네스 설명은 [../harness.md](../harness.md) 참조.
