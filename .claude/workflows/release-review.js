export const meta = {
  name: 'release-review',
  description: '릴리즈 전 멀티관점 코드 리뷰 — correctness·보안·P1~P6·동시성을 병렬 검토 후 발견을 적대적 교차검증해 확정 결함만 종합',
  phases: [
    { title: 'Review', detail: '4개 차원 병렬 리뷰' },
    { title: 'Verify', detail: '발견을 적대적 교차검증' },
    { title: 'Build', detail: 'verifier 빌드·publish·테스트 게이트' },
    { title: 'Synthesize', detail: '확정 결함 + 빌드 종합' },
  ],
}

// 호출: Workflow({ name: 'release-review', args: { scope: 'PR-XX 변경' } })
const SCOPE = (args && args.scope) ? String(args.scope) : 'git diff main..HEAD 의 변경 (없으면 최근 커밋의 변경 파일)'

// 한 차원이 환각성으로 finding 을 과다 생성해도 verify fan-out 폭주를 막는 절대 상한 (동시 16 cap 과 별개)
const MAX_VERIFY = 25

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

const BUILD_SCHEMA = {
  type: 'object',
  properties: {
    passed: { type: 'boolean' },
    debug: { type: 'string' },
    publish: { type: 'string' },
    test: { type: 'string' },
    issues: { type: 'array', items: { type: 'string' } },
  },
  required: ['passed'],
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
범위: ${SCOPE}. 너의 §0 추출 규칙대로 conventions.md 를 새로 Read 해 grep 을 전수 실행하고, 기대값(0/1/4 등 주석)과 다른 결과를 위반으로. 각 위반에 grep 명령과 매치 위치를 file:line 으로.
추가 — 이 프로젝트 고유의 릴리즈 리스크 2건을 명시 점검: (1) 버전 4-part — KoEnVue.csproj 버전·최신 git 태그·CHANGELOG 최상단이 모두 major.minor.build.revision 4-part 로 일치하는지(메모리 규칙), (2) P6 단방향 — App/ 이 Core/ 만 참조하고 Core/ 가 App/ 을 역참조하지 않는지(git grep KoEnVue\\.App Core/ → 0).`,
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
  (review, d) => {
    phase('Verify')
    return parallel(((review && review.findings) || []).slice(0, MAX_VERIFY).map((f) => () =>
      agent(
        `다음 코드 리뷰 발견이 실재하는 결함인지 적대적으로 검증하라. 반증을 적극 시도하고, 불확실하면 real=false.
차원: ${d.key}
발견: ${JSON.stringify(f)}
해당 파일을 직접 읽어 확인하라.`,
        { label: `verify:${d.key}`, phase: 'Verify', schema: VERDICT_SCHEMA }
      ).then((v) => ({ ...f, dimension: d.key, verdict: v }))
    ))
  }
)

phase('Build')
// 릴리즈 게이트 — 코드 리뷰만으론 '실제로 빌드·publish 되는가'를 못 봄. verifier 노드로 빌드 검증 포함.
const build = await agent(
  `KoEnVue 릴리즈 빌드 게이트. 순서대로 실행하고 결과 보고:
1. dotnet build (debug) — 경고/에러 수
2. dotnet publish -r win-x64 -c Release (AOT) — 경고/에러, 산출물 크기
3. dotnet test tests\\KoEnVue.Tests\\KoEnVue.Tests.csproj — 통과/실패 수 (bare dotnet test 금지)
셋 다 성공+경고0 이면 passed=true. 하나라도 실패/경고면 passed=false 와 issues 에 사유.`,
  { label: 'build-gate', phase: 'Build', agentType: 'verifier', schema: BUILD_SCHEMA }
)

phase('Synthesize')
const all = results.flat().filter(Boolean)
const confirmed = all.filter((f) => f.verdict && f.verdict.real)
const sevRank = { critical: 0, high: 1, medium: 2, low: 3 }
confirmed.sort((a, b) => (sevRank[a.severity] ?? 9) - (sevRank[b.severity] ?? 9))
log(`리뷰 ${all.length}건 발견 → 적대적 검증 후 ${confirmed.length}건 확정. 빌드 게이트: ${build && build.passed ? 'PASS' : 'FAIL'}`)
return {
  scope: SCOPE,
  build,
  confirmedCount: confirmed.length,
  totalFindings: all.length,
  confirmed,
  dismissed: all
    .filter((f) => !(f.verdict && f.verdict.real))
    .map((f) => ({ title: f.title, dimension: f.dimension, reason: f.verdict && f.verdict.reason })),
}
