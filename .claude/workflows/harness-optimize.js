export const meta = {
  name: 'harness-optimize',
  description: '하네스 최적화 — settings·hooks·agents·skills·workflows·docs 를 병렬 점검하고 누락/개선점을 completeness critic 으로 보강해 실행 가능한 개선안으로 종합',
  phases: [
    { title: 'Inspect', detail: '하네스 구성요소 병렬 점검' },
    { title: 'Critic', detail: '누락 점검 (completeness)' },
    { title: 'Synthesize', detail: '개선안 종합' },
  ],
}

// 호출: Workflow({ name: 'harness-optimize', args: { focus: 'hooks 오버헤드' } })
const FOCUS = (args && args.focus) ? String(args.focus) : '전반'

const SUGGESTIONS_SCHEMA = {
  type: 'object',
  properties: {
    suggestions: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          area: { type: 'string' },
          observation: { type: 'string' },
          suggestion: { type: 'string' },
          impact: { type: 'string', enum: ['high', 'medium', 'low'] },
          effort: { type: 'string', enum: ['high', 'medium', 'low'] },
        },
        required: ['area', 'observation', 'suggestion', 'impact'],
      },
    },
  },
  required: ['suggestions'],
}

const COMPONENTS = [
  { key: 'settings', prompt: `.claude/settings.json 을 점검하라. model/effort/thinking/permissions/env/hook 배선이 "비용 무제한·깊이 최우선·ultracode 항상 ON" 정책과 일치하는지, 모순·미사용·위험 설정이 있는지.` },
  { key: 'hooks', prompt: `.claude/hooks/*.ps1 전체를 점검하라. 각 hook 의 역할·견고성(Invoke-HookSafely 래핑), 오버헤드, 누락된 라이프사이클 이벤트, ultracode 와의 정합성.` },
  { key: 'agents', prompt: `.claude/agents/*.md 6개를 점검하라. effort 정책 명시 일관성, 도구 범위, 책임 중복/누락, ultracode 워크플로우와의 역할 분담(leaf vs 오케스트레이터).` },
  { key: 'skills-workflows', prompt: `.claude/skills/ 와 .claude/workflows/ 를 점검하라. 슬래시 커맨드·워크플로우 카탈로그의 발견성, 중복, ultracode 발동 경로의 명확성, 5개 워크플로우 스크립트의 meta/문법 유효성.` },
  { key: 'docs', prompt: `docs/harness.md 와 CLAUDE.md, .claude/memory/ 를 점검하라. 실제 하네스 구성과 문서의 drift, CLAUDE.md 30줄 제한, 메모리 정합성.` },
]

phase('Inspect')
const inspected = await parallel(COMPONENTS.map((c) => () =>
  agent(
    `KoEnVue 하네스 점검 (focus: ${FOCUS}). ${c.prompt}
각 제안에 area·관찰·구체적 개선안·impact·effort.`,
    { label: `inspect:${c.key}`, phase: 'Inspect', agentType: 'explorer', schema: SUGGESTIONS_SCHEMA }
  )
))
const found = inspected.filter(Boolean).flatMap((r) => r.suggestions || [])
const rank = { high: 0, medium: 1, low: 2 }
// critic 에 넘기기 전 impact 우선 정렬 — slice(0,40) 가 임의 부분집합이 아닌 상위 영향 제안을 보게.
found.sort((a, b) => (rank[a.impact] - rank[b.impact]) || (rank[a.effort || 'medium'] - rank[b.effort || 'medium']))
log(`구성요소 점검: ${found.length}건 제안`)

phase('Critic')
const critic = await agent(
  `다음은 KoEnVue 하네스 점검 결과다. 무엇이 빠졌는지 비판하라 — 점검 안 한 구성요소, 검증 안 한 가정, ultracode 도입으로 새로 생긴 리스크(비용 폭증·워크플로우 오용·effort 손실·장비간 연속성/메모리 영향). 빠진 것을 새 제안으로 추가하라.
현재 제안: ${JSON.stringify(found.slice(0, 40))}`,
  { label: 'critic:completeness', phase: 'Critic', schema: SUGGESTIONS_SCHEMA }
)
const extra = (critic && critic.suggestions) || []

phase('Synthesize')
const all = [...found, ...extra]
all.sort((a, b) => (rank[a.impact] - rank[b.impact]) || (rank[a.effort || 'medium'] - rank[b.effort || 'medium']))
log(`총 ${all.length}건 개선안 (critic +${extra.length})`)
return { focus: FOCUS, totalSuggestions: all.length, suggestions: all }
