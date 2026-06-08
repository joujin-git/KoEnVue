# AUDIT-3 2026-06-08 — 하네스 3회차 재점검 (harness-optimize, 변경검증+과복잡 focus)

`Workflow({name:'harness-optimize', args:{focus: 오늘 변경검증+과복잡}})` 3회차(445k 토큰, 53건). 1·2차([AUDIT](AUDIT-2026-06-08-harness.md)·[AUDIT-2](AUDIT-2-2026-06-08-harness.md)) 적용 직후 최종 상태 점검. focus 를 "오늘 변경분 새 결함·과복잡·진짜 신규"로 좁히자 **이전 2회보다 구조적 결함을 다수 포착** — critic 이 "42건은 doc/wiring drift 표면에 머물렀다"를 반복 지적.

범례: ✅ 이번 적용 · 🔬 검증완료/유망 · 🟡 부분 · 📋 보류.

## 이번 적용 — 진짜 결함

### 워크플로우 신뢰성 (거짓 클린 = 실패를 "0건 깨끗"으로 묻던 버그)
| # | area | 조치 |
|---|------|------|
| 5 | schema null vs 빈결과 미구분 | ✅ codebase-audit Scope null→error, design-compare winner null→error, release-review dimension null→degraded. `agentsFailed`/`degradedDimensions` 노출로 '0건=깨끗 vs 실패해서 0건' 판별 |
| 44 | release-review SCOPE `main..HEAD` 빈 diff | ✅ 기본값 `HEAD~1..HEAD`(직전 커밋) — main 직커밋 정책이라 main..HEAD 가 거의 항상 비어 거짓 통과하던 것 |
| 37 | codebase-audit 무상한 모듈 fan-out | ✅ `MAX_MODULES=24` slice (release-review MAX_VERIFY 와 대칭 런어웨이 방지) |
| 3 | harness-optimize 자기 결함 | ✅ area dedup(found+critic 중복 제거), critic 입력 slice 40→60(completeness 절단 완화), critic 프롬프트에 self-audit |
| 70 | design-compare graftIdeas 가 winner 본인 아이디어 누락 | ✅ `coreIdeas = winner.bestIdeas` 노출(합성 1순위였는데 빠졌었음) |

### 성능·견고성 (실측 기반)
| # | area | 조치 |
|---|------|------|
| 1 | statusline ~450ms(가장 빈번) + timeout 없음 | ✅ branch 를 `.git/HEAD` 직접 Read(rev-parse 프로세스 제거) + dirty `--untracked-files=no` 축소 + settings statusLine `timeout:5` |
| 19 | session-start/pre-compact git status 3회 | ✅ `Get-PorcelainStatus` 1회 헬퍼(가드+클램프+count 1회 파생) |
| 26 | hook-errors.log 테스트 프로브 잔재 | ✅ TESTERR/PROBE 2줄 제거 + `Write-HookError` 빈줄(`:: `) 기록 거부 가드 |
| 20 | FallbackContext 가 inject 1곳만 | ✅ session-start/pre-compact 확장(컨텍스트 주입 hook 3곳 모두 안전망의 안전망) |
| 286 | Sync-Memory Test-Path→Get-Item TOCTOU | ✅ per-file try/catch(개별 실패가 동기화 루프 중단 안 하게) |

### 에이전트 정합
| # | area | 조치 |
|---|------|------|
| 6/22 | 역할분담·leaf 가 에이전트 본문에 없음 | ✅ 6개 본문에 "호출 경로 & 경계" — 노드 4개(explorer/planner/reviewer/verifier)+위임 2개(docs-keeper/historian), leaf 원칙, Bash read-only, schema 주어지면 그 구조로 반환 |
| 23 | explorer P규칙 토큰 하드코딩(reviewer 단일진실원과 이원화) | ✅ "대표 예시·권위는 conventions.md/reviewer"로 격하 |
| 29/209 | verifier 본문 마크다운표 ↔ BUILD_SCHEMA 충돌 | ✅ verifier 에 schema 전환 + 필드 매핑 명시 |

### 문서 정합 (docs-keeper)
§6 슬래시 통합·§8 budget 사실정정·§10 한계(verify환각·박제hook없음·inject측정값·slug·statusLine)·§11 PR충돌 단일화·README 라우팅·CLAUDE.md INDEX.

## 보류·검토 (다음)

| # | area | 상태 |
|---|------|------|
| 18 | **PreToolUse → permissions.deny 로 파괴명령 차단** | 🔬 **유망** — 공식문서: "deny rules are evaluated regardless of hook" + bypass 에서도 rm -rf circuit breaker. 사용자가 감수한 force-push/reset --hard 차단의 진짜 해법일 수 있음. 단 (a)deny 문법 `Bash(cmd:*)` (b)**PowerShell 도구 매칭**(KoEnVue 는 PowerShell 도구 주사용 — Bash() 패턴이 PowerShell 호출을 잡는지) (c)settings 런타임 반영 (d)명령 변형 우회를 **실험 검증 필요**. 검증되면 적용 권장 |
| 2 | budget 코드가드 4개 워크플로우 | 📋 사용자 비용상한 명시 제외 — §8 문서 정정만 적용 |
| 4 | verify 공통 환각(동일 model·동일 코드) | 🟡 한계 문서화 + 빌드게이트 보완. verify model 다양성(opts.model)은 후속 실증 |
| 8/9 | PostToolUse Workflow / SubagentStop hook | 📋 SDK 가 해당 이벤트를 emit 하는지 미검증 — 박제는 메인세션 규율 유지(hook 안전망 없음을 §10 한계로) |
| 12/14 | workflow 정적 게이트(node --check) | 📋 top-level await 때문에 ESM/CJS 모두 오탐 — 효용 한정, smoke-load shim 은 후속 |
| 10 | auto-push 60s 동기 차단(오프라인 시) | 📋 OS/네트워크 의존 감수 대상 — 필요 시 timeout 30s |
| 11/314 | 문서 매핑 3중·PR충돌 3안 중복 | 🟡 §11 정본화는 적용, 매핑 단일화는 후속 |
| — | bug-hunt round 0 / skill argument-hint / 도구순서 / 호출주석 등 low | 📋 cosmetic — 후속 |

## 메타 관찰 (focus 효과)

3회차에서 focus 를 "변경검증+과복잡+self-비판"으로 좁힌 것이 적중 — 1·2차가 doc/wiring drift(표면)에 머문 반면 3차는 거짓 클린·self dedup·verify 환각 같은 **구조적 신뢰성 결함**을 잡았다. harness-optimize 의 self-audit(이번 적용)이 이를 상시화한다. 단 harness-optimize 를 하루 3회 돌린 것은 수렴 곡선의 끝 — 다음 점검은 실제 코드/하네스 변경이 누적된 뒤가 ROI 높다.

## 후속 적용 — permissions.deny (PreToolUse 대안 실현, #18)

위 보류 #18 을 같은 세션에 **실험·적용 완료** — AUDIT-2 2부에서 사용자가 "감수" 한 파괴명령 차단을 Claude Code 레벨에서 실현:
- **echo deny 실험**: `Bash(echo KEVDENYTEST:*)`·`PowerShell(echo …:*)` deny 후 두 도구로 echo 시도 → **둘 다 차단**. 이로써 (a) bypassPermissions 에서도 `permissions.deny` 작동(PreToolUse hook 의 deny 가 무시되는 것과 **정반대** — 공식문서 "deny rules evaluated regardless of hook" 확증), (b) settings 런타임 즉시 반영(Edit 직후), (c) **PowerShell 도구 매칭**(KoEnVue 주사용 도구) 3가지 동시 확인.
- **deny 적용**: `git push --force`/`-f`/`--force-with-lease`(+`git push * --force` origin 뒤 변형)·`git reset --hard`·`git filter-branch` — Bash·PowerShell 각각. `git push --force --dry-run` → 차단, 정상 `git status` → 통과(정상 commit/push 무영향) 검증.
- **한계·부작용**: (1) prefix 매칭이라 flag 순서 변형(force 가 명령 끝)·env var 우회는 일부 미포착 — 흔한 형태는 차단하는 1차 방어. (2) **중간 와일드카드 패턴(`git push * --force`)은 명령 문자열 어디든 키워드가 있으면 매칭 → 커밋 메시지에 차단 키워드가 들어간 정상 커밋까지 오탐 차단**(이 발견을 커밋하려다 실제로 걸림). force 직후 prefix 패턴만 유지하고 중간 와일드카드는 제거. 완벽 enforcement 아닌 1차 방어.
- → PreToolUse 가 bypassPermissions 에서 무효(AUDIT-2 도구제약)였던 것을 `permissions.deny` 로 우회·실현. **메모리 정책의 "도구 제약 감수" 가 실은 더 강한 메커니즘으로 해결 가능했던 사례.**
