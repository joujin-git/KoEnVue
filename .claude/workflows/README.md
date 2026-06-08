# .claude/workflows/ — ultracode 워크플로우 작성 계약

ultracode 멀티에이전트 워크플로우(`Workflow` 도구로 실행). 각 `.js` 는 저장 즉시 `/<name>` 슬래시 커맨드로 노출되고 `Workflow({name})` 로 호출됩니다. 이 문서는 워크플로우 런타임이 주입하는 심볼의 **계약** — 새 워크플로우 작성(또는 메인 세션의 즉석 작성) 시 시그니처 추측 오용을 막는 단일 출처입니다.

## 파일 형식

- 첫 줄은 `export const meta = {...}` — **pure literal** (변수/함수호출/스프레드/보간 금지). 필수: `name`, `description`. 선택: `phases` (각 `{ title, detail }`).
- meta 다음이 스크립트 본문 — async 컨텍스트, `await` 직접 사용. (이 `export` + top-level `return`/`await` 혼용은 워크플로우 런타임 전용 포맷이라 `node --check` 로는 검증 불가 — 런타임으로만 검증.)
- meta.phases 의 title 과 본문 `phase('X')` 호출은 1:1 로 맞출 것 (drift 시 진행 표시 어긋남).

## 주입 심볼 계약

| 심볼 | 시그니처 | 반환 / 비고 |
|------|----------|-------------|
| `agent(prompt, opts?)` | opts: `{label?, phase?, schema?, agentType?, model?, isolation?}` | schema 없으면 string, 있으면 검증된 객체. **실패 시 null 로 resolve(reject 아님)** → `.filter(Boolean)` 필수 |
| `pipeline(items, stage1, ...)` | stage 콜백 = `(prevResult, originalItem, index)` | stage 간 barrier 없음. stage throw → 그 item null. 1-stage/N-stage 모두 가능 |
| `parallel(thunks)` | `thunks` = `(() => Promise)[]` | **barrier**. 실패 thunk → null (call 자체는 reject 안 함) → `.filter(Boolean)` |
| `phase(title)` | — | 이후 agent 를 이 진행 그룹에 묶음 (라벨/로그용) |
| `log(msg)` | — | 진행 메시지 1줄 |
| `args` | — | `Workflow({ args })` 로 넘긴 값 (undefined 가능 → 가드) |
| `budget` | `{ total, spent(), remaining() }` | total 은 null 가능. `!budget.total` 단락이 "예산 모르면 무제한"이 되지 않도록 round/개수 hard cap 병행 |
| `agentType` | `explorer`/`planner`/`reviewer`/`docs-keeper`/`historian`/`verifier`/`claude` | KoEnVue 서브에이전트 재사용. `claude` = catch-all 범용. 미지정 = 기본 워크플로우 에이전트 |

## 작성 주의 (실측 함정)

- **비결정 API 금지**: `Date`/`Math.random`/인자없는 `new Date()` 는 호출은 물론 **소스에 문자열 리터럴로 적어도** 정적 검사에서 거부됨(resume 결정성). 설명이 필요하면 "현재 시각 조회/난수 생성" 처럼 우회 표기. (verify-harness 작성 시 실제로 걸림.)
- **null 전파**: `agent().then(x => ({ ...x }))` 는 x 가 null 이어도 truthy 래퍼를 만들어 `filter(Boolean)` 를 무력화한다 → sort/map 에서 null-deref 크래시. `then(x => (x ? { ... } : null))` 로 진짜 null 전파. (design-compare 에서 실제로 발생, 수정함.)
- **fan-out 상한**: 동시 최대 16(초과는 큐잉), 단일 parallel/pipeline 최대 4096 items. finding/제안 수 비례 fan-out 은 `.slice(0, MAX)` 로 절대 상한(release-review `MAX_VERIFY`).
- **무한루프**: `while` 종료를 LLM 출력(비결정 dedup 키)에만 의존하면 안 됨 — 항상 round hard cap 병행(bug-hunt `round < 8`).
- **leaf 는 read-only 지향**: 파괴적 작업(git push/파일삭제)은 워크플로우 종료 후 오케스트레이터(메인 세션)가 수행. leaf 에이전트는 조사/검증만.
- **isolation 미검증**: `agent()` opts 의 `isolation?` 는 계약상 존재하나 현재 모든 워크플로우가 미사용 — 런타임 동작이 검증된 적 없다. 쓰려면 1회 실증 후 사용(미검증 심볼에 의존 금지).

## 워크플로우 카탈로그

카탈로그 단일 진실원은 이 디렉토리(`.claude/workflows/*.js`) — `inject-turn-context.ps1` 이 매 턴 동적으로 광고하고 `/harness-status` 가 수를 점검. 새 `.js` 추가/삭제가 자동 반영되므로 개수를 여기 박지 않는다. 상세는 [docs/harness.md §3](../../docs/harness.md).
