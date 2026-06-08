export const meta = {
  name: 'bug-hunt',
  description: '버그/레이스 헌트 — 여러 finder 가 동시성·레이스·견고성 결함을 더 안 나올 때까지 반복 탐색(loop-until-dry)하고 다관점 렌즈로 교차검증',
  phases: [
    { title: 'Hunt', detail: 'finder 병렬 탐색 (라운드 반복)' },
    { title: 'Verify', detail: '다관점 렌즈 교차검증' },
  ],
}

// 호출: Workflow({ name: 'bug-hunt', args: { target: 'App/UI/' } })
const TARGET = (args && args.target) ? String(args.target) : 'App/ 와 Core/ 의 2-스레드 경로 전체 (메시지 루프 + 탐지 루프 + OS 콜백)'

const BUGS_SCHEMA = {
  type: 'object',
  properties: {
    bugs: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          desc: { type: 'string' },
          file: { type: 'string' },
          location: { type: 'string' },
          kind: { type: 'string', enum: ['race', 'deadlock', 'reentrancy', 'lifetime', 'visibility', 'robustness', 'other'] },
        },
        required: ['desc', 'file', 'kind'],
      },
    },
  },
  required: ['bugs'],
}

const VERDICT_SCHEMA = {
  type: 'object',
  properties: { real: { type: 'boolean' }, reason: { type: 'string' } },
  required: ['real', 'reason'],
}

const FINDERS = [
  { key: 'shared-state', prompt: `KoEnVue (${TARGET}) 에서 lock/volatile 누락한 공유 상태 접근을 찾아라. 두 스레드가 같은 필드를 읽고/쓰는데 동기화가 없는 곳.` },
  { key: 'reentrancy', prompt: `KoEnVue (${TARGET}) 에서 WndProc/콜백 재진입, 타이머 재진입, 이벤트 핸들러 중첩 실행으로 깨지는 곳을 찾아라.` },
  { key: 'lifetime', prompt: `KoEnVue (${TARGET}) 에서 핸들/GDI 객체/창의 수명 관리 결함 — 이중 해제, use-after-free, 누수, dispose 순서 경합을 찾아라.` },
  { key: 'config-race', prompt: `KoEnVue (${TARGET}) 에서 config 리로드와 사용 사이의 경합, 스냅샷 미사용으로 mid-tick 변경이 노출되는 곳을 찾아라.` },
]

const LENSES = ['correctness', 'reproducibility', 'concurrency-theory']

function bugKey(b) {
  return `${(b.file || '').toLowerCase()}::${b.kind || ''}::${(b.desc || '').slice(0, 60).toLowerCase()}`
}

const seen = new Set()
const confirmed = []
let dry = 0
let round = 0

// loop-until-dry: 2라운드 연속 새 결함 0 이면 종료. round<8 hard cap(비결정적 desc 로 인한
// 비종료 방지 — bugKey 가 free-text desc 를 포함해 LLM 변동 시 fresh 가 끝없이 생길 수 있음) + budget 가드.
while (dry < 2 && round < 8 && (!budget.total || budget.remaining() > 60000)) {
  round++
  phase('Hunt')
  const found = (await parallel(FINDERS.map((f) => () =>
    agent(
      `${f.prompt}
라운드 ${round}. 이미 보고됐을 법한 것 말고 새로운 결함에 집중. 각 결함에 file·location·재현 근거.`,
      { label: `hunt:${f.key}#${round}`, phase: 'Hunt', schema: BUGS_SCHEMA }
    )
  ))).filter(Boolean).flatMap((r) => r.bugs || [])

  // dedup 은 seen(라운드 누적) 기준 — judge 가 기각한 것도 재등장 안 하도록 fresh 를 미리 seen 에 등록.
  const fresh = found.filter((b) => !seen.has(bugKey(b)))
  if (fresh.length === 0) {
    dry++
    log(`라운드 ${round}: 새 결함 0 (dry ${dry}/2)`)
    continue
  }
  dry = 0
  fresh.forEach((b) => seen.add(bugKey(b)))
  log(`라운드 ${round}: 새 결함 ${fresh.length}건 → 다관점 검증`)

  phase('Verify')
  const judged = await parallel(fresh.map((b) => () =>
    parallel(LENSES.map((lens) => () =>
      agent(
        `다음 결함을 ${lens} 렌즈로 검증하라. 반증을 시도하고 불확실하면 real=false.
결함: ${JSON.stringify(b)}
파일을 직접 읽어 확인.`,
        { label: `verify:${lens}`, phase: 'Verify', schema: VERDICT_SCHEMA }
      )
    )).then((votes) => {
      const ok = votes.filter(Boolean).filter((v) => v.real).length >= 2
      return { bug: b, real: ok }
    })
  ))
  confirmed.push(...judged.filter(Boolean).filter((j) => j.real).map((j) => j.bug))
}

log(`총 ${round}라운드, 확정 결함 ${confirmed.length}건`)
return { target: TARGET, rounds: round, confirmedCount: confirmed.length, confirmed }
