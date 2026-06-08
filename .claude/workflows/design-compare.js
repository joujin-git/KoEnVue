export const meta = {
  name: 'design-compare',
  description: '신규 기능 설계 비교 — 여러 접근법을 독립 에이전트가 제안하고 병렬 심사로 점수화한 뒤 최선안 + 차선의 좋은 아이디어를 합성',
  phases: [
    { title: 'Propose', detail: '접근법별 독립 설계안' },
    { title: 'Judge', detail: '병렬 심사 점수화' },
    { title: 'Synthesize', detail: '최선안 합성' },
  ],
}

// 호출: Workflow({ name: 'design-compare', args: { feature: '트레이에 테마 토글 추가' } })
const FEATURE = (args && args.feature) ? String(args.feature) : null

const PROPOSAL_SCHEMA = {
  type: 'object',
  properties: {
    summary: { type: 'string' },
    steps: { type: 'array', items: { type: 'string' } },
    files: { type: 'array', items: { type: 'string' } },
    risks: { type: 'array', items: { type: 'string' } },
    pRuleImpact: { type: 'string' },
  },
  required: ['summary', 'steps', 'risks'],
}

const SCORE_SCHEMA = {
  type: 'object',
  properties: {
    correctness: { type: 'number' },
    simplicity: { type: 'number' },
    pRuleFit: { type: 'number' },
    risk: { type: 'number' },
    total: { type: 'number' },
    rationale: { type: 'string' },
    bestIdeas: { type: 'array', items: { type: 'string' } },
  },
  required: ['total', 'rationale'],
}

if (!FEATURE) {
  return { error: '기능 설명이 필요합니다. Workflow({ name: "design-compare", args: { feature: "..." } }) 로 호출하세요.' }
}

const ANGLES = [
  { key: 'mvp-first', lens: '최소 변경으로 동작하는 MVP 우선' },
  { key: 'reuse-first', lens: '기존 Core/ 재사용·중복 최소(P4) 우선' },
  { key: 'risk-first', lens: '리스크·엣지케이스·롤백 안전 우선' },
]

phase('Propose')
const proposals = await parallel(ANGLES.map((a) => () =>
  agent(
    `KoEnVue (Windows IME 인디케이터, C#/.NET 10 AOT, zero-NuGet, P1~P6) 에 다음 기능을 "${a.lens}" 관점에서 설계하라: "${FEATURE}".
구현 단계, 건드릴 파일, 리스크, P규칙 영향을 구체적으로. 코드는 쓰지 말고 설계만.`,
    { label: `propose:${a.key}`, phase: 'Propose', agentType: 'planner', schema: PROPOSAL_SCHEMA }
  ).then((p) => (p ? { angle: a.key, proposal: p } : null))
))

phase('Judge')
const scored = await parallel(proposals.filter(Boolean).map((p) => () =>
  agent(
    `KoEnVue 기능 "${FEATURE}" 의 다음 설계안을 심사하라. correctness/simplicity/pRuleFit/risk 를 0~10 으로 매기고 total 산출, 합리근거와 이 안에서 가져갈 좋은 아이디어를 명시.
접근: ${p.angle}
설계안: ${JSON.stringify(p.proposal)}`,
    { label: `judge:${p.angle}`, phase: 'Judge', schema: SCORE_SCHEMA }
  ).then((s) => (s ? { angle: p.angle, proposal: p.proposal, score: s } : null))
))

phase('Synthesize')
const ranked = scored.filter(Boolean).sort((a, b) => (b.score.total || 0) - (a.score.total || 0))
const winner = ranked[0] || null
// FEATURE 는 정상인데 하위 에이전트(planner proposal / judge)가 전멸한 경우를 '좋은 안 없음'이 아니라
// 명시 실패로 — FEATURE 미입력 가드(위)와 대칭. 안 그러면 winner:null 을 '비교했더니 별로'로 오인.
if (!winner) {
  return { feature: FEATURE, error: '모든 설계안 생성/심사 실패 — planner/judge 노드 전멸(재시도 또는 feature 구체화 필요)', winner: null, ranking: [], graftIdeas: [], coreIdeas: [] }
}
const grafts = ranked.slice(1).flatMap((r) => ((r.score.bestIdeas) || []).map((idea) => ({ from: r.angle, idea })))
log(`최선안: ${winner.angle} (total ${winner.score.total})`)
return {
  feature: FEATURE,
  winner: { angle: winner.angle, proposal: winner.proposal, score: winner.score },
  coreIdeas: (winner.score.bestIdeas) || [],  // winner 자신의 핵심 아이디어(합성 1순위) — graft 는 차선안만이라 누락됐었음
  ranking: ranked.map((r) => ({ angle: r.angle, total: r.score.total })),
  graftIdeas: grafts,
}
