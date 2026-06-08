export const meta = {
  name: 'codebase-audit',
  description: '전체 코드베이스 감사 — App/·Core/ 모듈을 병렬 전수 점검 후 P1~P6 invariant 게이트로 교차검증하고 AUDIT 문서용으로 종합',
  phases: [
    { title: 'Scope', detail: '감사 대상 모듈 목록 수집' },
    { title: 'Audit', detail: '모듈별 병렬 점검' },
    { title: 'Gate', detail: 'P1~P6 invariant 전수 게이트' },
    { title: 'Synthesize', detail: 'AUDIT 문서용 종합' },
  ],
}

const FILES_SCHEMA = {
  type: 'object',
  properties: { modules: { type: 'array', items: { type: 'string' } } },
  required: ['modules'],
}

const ISSUES_SCHEMA = {
  type: 'object',
  properties: {
    issues: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          title: { type: 'string' },
          file: { type: 'string' },
          line: { type: 'string' },
          category: { type: 'string', enum: ['bug', 'race', 'robustness', 'duplication', 'magic-number', 'naming', 'dead-code', 'perf', 'doc-drift'] },
          severity: { type: 'string', enum: ['high', 'medium', 'low'] },
          suggestion: { type: 'string' },
        },
        required: ['title', 'file', 'category', 'severity'],
      },
    },
  },
  required: ['issues'],
}

// LLM 출력이라 모듈 수가 무경계 — release-review MAX_VERIFY 와 동형의 절대 상한(런어웨이 방지).
const MAX_MODULES = 24

phase('Scope')
const scoped = await agent(
  `KoEnVue 코드베이스에서 감사 대상 모듈/파일 목록을 만들어라. App/ 와 Core/ 의 .cs 파일을 응집도 있는 단위(파일 또는 하위폴더)로 그룹화해 최대 ${MAX_MODULES}개 모듈 경로를 반환(초과 시 응집도 높은 단위로 병합). Program.cs 도 포함. 테스트(tests/)는 제외.`,
  { label: 'scope', phase: 'Scope', agentType: 'explorer', schema: FILES_SCHEMA }
)
// null(Scope 에이전트 실패/schema 위반)과 빈 결과(정상 0개)를 구분 — null 을 []로 흡수하면 'modules 0개'로
// 조용히 진행해 Audit/Gate 가 빈 입력으로 돌고 '이슈 0건'을 거짓 클린으로 반환한다.
if (!scoped || !scoped.modules) {
  log('Scope 실패 — 감사 미수행 (Scope 에이전트 null)')
  return { error: 'Scope 단계 실패 — 감사 대상 모듈을 못 얻음. 재시도 필요(거짓 클린 아님)', moduleCount: 0, totalIssues: 0, bySeverity: { high: [], medium: [], low: [] } }
}
const modules = scoped.modules.slice(0, MAX_MODULES)
log(`감사 대상 ${modules.length}개 모듈${scoped.modules.length > MAX_MODULES ? ` (${scoped.modules.length}개 중 상위 ${MAX_MODULES})` : ''}`)

phase('Audit')
const audited = await pipeline(
  modules,
  (m) => agent(
    `KoEnVue 의 다음 모듈을 정독 감사하라: ${m}.
버그·레이스·견고성·중복(P4)·매직넘버(P3)·명명·죽은 코드·성능·문서 drift 를 본다. 각 issue 에 file:line·근거·수정 제안.
P1~P6: zero-NuGet, UI한국어/로그영어, no-magic-number, 단일구현(공유는 Core/), asInvoker, App→Core 단방향.`,
    { label: `audit:${m}`, phase: 'Audit', schema: ISSUES_SCHEMA }
  )
)

phase('Gate')
const gate = await agent(
  `KoEnVue 의 P1~P6 invariant 를 전수 검증하라. docs/conventions.md 를 새로 Read 해 모든 grep 게이트를 실행하고, 기대값과 다른 위반을 issue 로 보고(없으면 빈 배열). 각 위반에 grep 명령·매치 위치를 file:line 으로.`,
  { label: 'gate:P1-P6', phase: 'Gate', agentType: 'reviewer', schema: ISSUES_SCHEMA }
)

phase('Synthesize')
// null(에이전트 실패)과 빈 결과(정상 0건)를 구분 — 실패 노드 수를 결과에 실어 '0건=깨끗 vs 실패해서 0건' 판별.
const okAudits = audited.filter(Boolean)
const auditsFailed = audited.length - okAudits.length
const gateFailed = gate ? 0 : 1
const allIssues = [
  ...okAudits.flatMap((r) => r.issues || []),
  ...((gate && gate.issues) || []),
]
const bySeverity = { high: [], medium: [], low: [] }
for (const i of allIssues) { (bySeverity[i.severity] || bySeverity.low).push(i) }
log(`총 ${allIssues.length}건 (high ${bySeverity.high.length} / medium ${bySeverity.medium.length} / low ${bySeverity.low.length})${auditsFailed || gateFailed ? ` ⚠ 실패 노드 audit ${auditsFailed}/gate ${gateFailed}` : ''}`)
return {
  moduleCount: modules.length,
  agentsFailed: auditsFailed + gateFailed,
  totalIssues: allIssues.length,
  bySeverity,
  note: 'docs/improvement-plan/AUDIT-<날짜>-codebase-review.md 형식으로 기록 권장 — 날짜 스탬프는 메인 세션이 처리. agentsFailed>0 이면 그만큼 커버리지 누락(0건이 깨끗을 보장 안 함).',
}
