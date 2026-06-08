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

phase('Scope')
const scoped = await agent(
  `KoEnVue 코드베이스에서 감사 대상 모듈/파일 목록을 만들어라. App/ 와 Core/ 의 .cs 파일을 응집도 있는 단위(파일 또는 하위폴더)로 그룹화해 8~20개 모듈 경로를 반환. Program.cs 도 포함. 테스트(tests/)는 제외.`,
  { label: 'scope', phase: 'Scope', agentType: 'explorer', schema: FILES_SCHEMA }
)
const modules = (scoped && scoped.modules) || []
log(`감사 대상 ${modules.length}개 모듈`)

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
const allIssues = [
  ...audited.filter(Boolean).flatMap((r) => r.issues || []),
  ...((gate && gate.issues) || []),
]
const bySeverity = { high: [], medium: [], low: [] }
for (const i of allIssues) { (bySeverity[i.severity] || bySeverity.low).push(i) }
log(`총 ${allIssues.length}건 (high ${bySeverity.high.length} / medium ${bySeverity.medium.length} / low ${bySeverity.low.length})`)
return {
  moduleCount: modules.length,
  totalIssues: allIssues.length,
  bySeverity,
  note: 'docs/improvement-plan/AUDIT-<날짜>-codebase-review.md 형식으로 기록 권장 — 날짜 스탬프는 메인 세션이 처리',
}
