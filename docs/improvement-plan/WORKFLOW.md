# Improvement Plan — Session Workflow

본 문서는 매 세션이 self-contained로 진행되도록 하는 절차. Claude와 사용자 모두 본 문서를 작업 매뉴얼로 참조.

## 1. 핵심 원칙

- **세션 0 (1회)**: 본 디렉토리(`docs/improvement-plan/`)와 메모리(`memory/improvement-plan.md`)를 만든다.
- **세션 N (≥1)**: 이전 세션의 메모리 없이도 시작 가능. INDEX → PR 명세 → branch → 구현 → 검증 → 문서 → 세션 종료.

## 2. 매 세션 시작 절차 (Claude)

고정 7단계:

1. **`docs/improvement-plan/INDEX.md` 읽기** — 현 상태 파악
2. **메모리 `improvement-plan.md` 읽기** — 결정 요약 + 마지막 세션 노트
3. **타겟 `PR-NN-*.md` 읽기** — self-contained 명세 로드
4. **`git status`/`git branch --show-current` 확인** — 시작점 확정
5. **TodoWrite 초기화** — PR 명세 §2 체크리스트를 todo로 변환
6. **git branch 생성/체크아웃** — `feat/pr-NN-<slug>`가 없으면 main에서 분기, 있으면 체크아웃
7. **사용자에게 한 줄 요약** — "PR-NN <Title> 진행. 현재 X/Y 완료. 다음: Z"

## 3. 매 세션 종료 절차 (Claude)

고정 7단계:

1. **`dotnet build`** — 최소 컴파일 확인
2. **PR 명세 §3 Tier-1 검증** 실행
3. **Git commit** — 작업 완료분만. WIP commit 허용
4. **`PR-NN-*.md` §6 세션 로그에 한 줄 추가**
5. **`INDEX.md` Status/Sessions log 갱신**
6. **메모리 한 줄 갱신** — 다음 세션 시작점
7. **사용자에게 한 줄 요약**

## 4. 사용자 진입 패턴

```
"improvement-plan 다음 PR 진행"   ← INDEX의 다음 ⏳ 자동 선택
"PR-NN 진행"                       ← 특정 PR 지정
"PR-NN 검토만"                     ← 명세만 읽고 시작 안 함
"improvement-plan 상태"            ← INDEX 요약만 출력
```

## 5. PR 명세 파일 구조

각 `PR-NN-*.md`는 5+1 섹션 고정:
- §1 목적 (Why)
- §2 변경 범위 (What) — 체크리스트 형식
- §3 검증 기준 (Done When) — Tier 1/2/3
- §4 사이드 이펙트 / 위험
- §5 롤백 절차
- §6 세션 진행 로그

## 6. 검증 3-tier

### Tier 1 — 자동 (반드시)
```bash
dotnet build                                  # Debug
dotnet publish -r win-x64 -c Release          # NativeAOT
git grep "KoEnVue\.App"     Core/             # 0 매치
git grep "ImeState"         Core/             # 0 매치
git grep "NonKoreanImeMode" Core/             # 0 매치
git grep "DllImport"                          # 0 매치
```

### Tier 2 — PR별 grep 가드
각 PR 명세의 §3에 명시. 예: PR-04는 `wc -l App/UI/Tray.cs` < 700.

### Tier 3 — 수동 smoke
PR이 사용자 가시 UI 영향 있을 때만 (PR-07, PR-03 등).

## 7. Branch / Commit / 문서 컨벤션

### Branch
`feat/pr-NN-<kebab-slug>`. 예: `feat/pr-00-mutex-abandoned`.

### Commit
기존 컨벤션 그대로: `refactor:`/`fix:`/`feat:`/`chore:` + 한국어 본문. PR 단위 여러 commit 허용. squash는 머지 시점 결정.

### CHANGELOG
매 PR이 `[Unreleased]`에 항목 추가. PR-12에서 표준 형식으로 정렬.

### dev-notes
의식적 결정이 있을 때 `docs/dev-notes/YYYY-MM-DD-<slug>.md` 신규. 구조: 무엇/왜/대안/회귀 위험/측정 계획.

## 8. 실패 / 차질 대응

### A. 검증 실패
- §6 세션 로그에 기록
- INDEX.md Status는 🚧 유지
- 다음 세션이 같은 PR을 이어서

### B. 새 사이드 이펙트 발견
- 본 PR 명세 §4에 추가
- 본 PR 안에서 해결 가능 → 진행
- 불가 → §6 기록 + 상태 `⏸ blocked`
- 새 PR 필요 → INDEX에 `PR-13` 등 append + 명세 작성

### C. 결정 뒤집기
- `DECISIONS.md` 갱신 + 사유 명시
- 영향받는 PR들의 §1·§2 동기화
- 메모리 한 줄 갱신

### D. 순서 변경
- 본 디렉토리의 dependency graph 확인 → 위반 안 하면 자유

## 9. 메모리 정책

- `memory/improvement-plan.md` — 본 작업 진행 추적 (project 타입). 작업 완료 시 dev-notes로 1건 통합 후 삭제.
- `CLAUDE.md` 갱신 — PR-03 머지 직후 P5 invariant 갱신.
- 기존 메모리 — 변경 없음.

## 10. 시간 예산

| 크기 | 시간 | 어떤 PR |
|---|---|---|
| S | ≤30분 | PR-00, PR-12 |
| M | 1-2시간 | PR-01, 02, 04, 05, 06, 08, 09, 10 |
| L | 반나절+ (다중 세션 가능) | PR-03, 07, 11 |

L 크기는 §2 체크리스트를 sub-task로 쪼개 세션 분할 가능.

## 11. 작업 완료 후 정리

PR-12 완료 시:
1. `docs/dev-notes/2026-MM-DD-improvement-plan-retrospective.md` 신규 — 무엇이 바뀌었고 어떻게 진행됐는지
2. `docs/improvement-plan/` 디렉토리 보존 또는 삭제 (사용자 결정)
3. `memory/improvement-plan.md` 삭제
4. CLAUDE.md final 갱신 (P5 invariant, verification gates 등)
