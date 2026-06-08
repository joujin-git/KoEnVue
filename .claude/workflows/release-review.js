export const meta = {
  name: 'release-review',
  description: '릴리즈 전 멀티관점 코드 리뷰 — correctness·보안·P1~P6·동시성을 병렬 검토 후 발견을 적대적 교차검증해 확정 결함만 종합',
  phases: [
    { title: 'Review', detail: '4개 차원 병렬 리뷰' },
    { title: 'Verify', detail: '발견을 적대적 교차검증' },
    { title: 'Synthesize', detail: '확정 결함 종합' },
  ],
}

// 호출: Workflow({ name: 'release-review', args: { scope: 'PR-XX 변경' } })
const SCOPE = (args && args.scope) ? String(args.scope) : 'git diff main..HEAD 의 변경 (없으면 최근 커밋의 변경 파일)'

const FINDINGS_SCHEMA = {
  type: 'object',
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          title: { type: 'string' },
          file: { type: 'string' },
          line: { type: 'string' },
          severity: { type: 'string', enum: ['critical', 'high', 'medium', 'low'] },
          description: { type: 'string' },
        },
        required: ['title', 'file', 'severity', 'description'],
      },
    },
  },
  required: ['findings'],
}

const VERDICT_SCHEMA = {
  type: 'object',
  properties: {
    real: { type: 'boolean' },
    reason: { type: 'string' },
  },
  required: ['real', 'reason'],
}

const DIMENSIONS = [
  {
    key: 'correctness',
    prompt: `KoEnVue (Windows IME 인디케이터, C#/.NET 10 NativeAOT) 의 다음 범위를 correctness 관점에서 리뷰하라: ${SCOPE}.
로직 버그, off-by-one, null/빈값 처리 누락, 잘못된 분기, 리소스 누수(핸들/GDI/메모리), 예외 경로의 상태 일관성을 본다.
각 발견에 file:line 과 근거를 명시. 추측이면 severity 를 낮게, 확실한 것만 critical/high.`,
  },
  {
    key: 'security',
    prompt: `KoEnVue 의 다음 범위를 보안 관점에서 리뷰하라: ${SCOPE}.
중점: Win32 P/Invoke(LibraryImport) 시그니처의 marshalling 안전성, asInvoker 권한(P5 — requireAdministrator 금지), 권한 상승(PR-15) 경로, 신뢰 경계, 입력 검증. KoEnVue 는 zero-NuGet(P1)·asInvoker 정책이다.
각 발견에 file:line 과 근거. 확실한 것만 high 이상.`,
  },
  {
    key: 'invariant',
    agentType: 'reviewer',
    prompt: `KoEnVue 의 P1~P6 invariant 와 docs/conventions.md 의 grep 게이트를 전수 점검하라. 위반을 finding 으로 보고(없으면 빈 배열).
범위: ${SCOPE}. 너의 §0 추출 규칙대로 conventions.md 를 새로 Read 해 grep 을 전수 실행하고, 기대값(0/1/4 등 주석)과 다른 결과를 위반으로. 각 위반에 grep 명령과 매치 위치를 file:line 으로.`,
  },
  {
    key: 'concurrency',
    prompt: `KoEnVue 의 다음 범위를 동시성/레이스 관점에서 리뷰하라: ${SCOPE}.
KoEnVue 는 2-스레드(메시지 루프 + 탐지 루프) + OS 이벤트 콜백 경로다. 공유 상태 접근(lock/volatile 누락), config 스냅샷 경합, WndProc 재진입, 타이머/딤 애니메이션 경합, cross-thread 필드 가시성을 본다.
각 발견에 file:line 과 재현 시나리오. 확실한 것만 high 이상.`,
  },
]

phase('Review')
const results = await pipeline(
  DIMENSIONS,
  (d) => agent(d.prompt, { label: `review:${d.key}`, phase: 'Review', agentType: d.agentType, schema: FINDINGS_SCHEMA }),
  (review, d) => parallel(((review && review.findings) || []).map((f) => () =>
    agent(
      `다음 코드 리뷰 발견이 실재하는 결함인지 적대적으로 검증하라. 반증을 적극 시도하고, 불확실하면 real=false.
차원: ${d.key}
발견: ${JSON.stringify(f)}
해당 파일을 직접 읽어 확인하라.`,
      { label: `verify:${d.key}`, phase: 'Verify', schema: VERDICT_SCHEMA }
    ).then((v) => ({ ...f, dimension: d.key, verdict: v }))
  ))
)

phase('Synthesize')
const all = results.flat().filter(Boolean)
const confirmed = all.filter((f) => f.verdict && f.verdict.real)
const sevRank = { critical: 0, high: 1, medium: 2, low: 3 }
confirmed.sort((a, b) => (sevRank[a.severity] ?? 9) - (sevRank[b.severity] ?? 9))
log(`리뷰 ${all.length}건 발견 → 적대적 검증 후 ${confirmed.length}건 확정`)
return {
  scope: SCOPE,
  confirmedCount: confirmed.length,
  totalFindings: all.length,
  confirmed,
  dismissed: all
    .filter((f) => !(f.verdict && f.verdict.real))
    .map((f) => ({ title: f.title, dimension: f.dimension, reason: f.verdict && f.verdict.reason })),
}
