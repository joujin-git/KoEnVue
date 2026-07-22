# Claude Code 하네스 — KoEnVue

KoEnVue 의 바이브 코딩 워크플로우를 위한 Claude Code 하네스 구성 전체 레퍼런스. CLAUDE.md 는 P1–P6 규칙만 담고, 하네스 운영 규칙은 모두 여기에.

## 0. 첫 사용 가이드 (비개발자 시각)

**이게 뭐예요?** Claude Code 는 명령줄에서 도는 AI 보조 도구입니다. "하네스" 는 그 도구가 KoEnVue 프로젝트에서 일관되게 동작하도록 잡아주는 설정 묶음 — 모델은 항상 Opus 최강, 매 요청은 깊이 추론(ultrathink), 코드 바꾸면 문서 안내 자동 표시, 복잡한 작업은 여러 AI 가 나눠서 처리·교차검증(ultracode), 세션 끝나면 자동 저장 등.

**왜 필요해요?** 매번 같은 설명을 다시 안 해도 되고, 다른 장비로 옮겨도 작업이 이어집니다. 잊을 만한 안전망(자동 wip 커밋, 비밀번호 마스킹) 도 자동으로 잡습니다.

**처음 써보기 — 5단계**:
1. **터미널에서 `claude` 실행** → 화면 아래 statusLine 에 `[opus · max] | git:main* | ultracode · 한/En 하네스 ON` 같은 표시가 보이면 하네스 활성
2. **`/harness-status` 입력** → 모델/effort, 서브에이전트 6명, 오늘 세션 파일, dirty tree, hook 에러 정상 여부를 한눈에 확인
3. **자연어로 작업 요청** — 예: "한 레이블 색을 빨강으로 바꿔줘". 하네스가 알아서 ultrathink 모드로 처리하고, 필요하면 서브에이전트(explorer, planner 등)에 위임
4. **작업 마무리할 때 `/wrap-up`** → 문서 동기화 + 세션 요약을 `docs/sessions/YYYY-MM-DD.md` 에 자동 기록
5. **다른 장비로 옮기기** → `git push` 후 다른 장비에서 `git pull` → `claude` 실행 → SessionStart 가 자동으로 어제 작업을 컨텍스트에 주입

용어 풀이:
- **서브에이전트 (subagent)**: 메인 대화를 깔끔하게 유지하려고 특정 작업을 위임받는 보조 Claude. 예: 코드 검색은 explorer 에게.
- **hook**: 특정 사건(세션 시작, 코드 수정 등) 때 자동 실행되는 PowerShell 스크립트.
- **slash command**: `/이름` 으로 실행하는 미리 정의된 작업.
- **ultracode / 워크플로우**: 복잡한 작업을 여러 보조 Claude 가 병렬로 나눠 처리하고 서로 교차검증하는 멀티에이전트 모드. 항상 켜져 있고 큰 작업에서 자동 발동.

## 1. 디자인 원칙

| 결정 | 내용 | 이유 |
|------|------|------|
| 모델 | `opus` (Opus 4.8) | "비용 무제한, 깊이 최우선". model/effort 는 statusLine payload(`payload.effort.level`)로 전달됨 — 미수신 시 statusline.ps1 이 env 폴백 |
| Effort | `effortLevel: xhigh` (settings) + `CLAUDE_CODE_EFFORT_LEVEL=max` (env, 실효·최우선) | **공식 검증(claude-code-guide, 2026-06-08)**: env > settings > 모델기본 — env=max 가 실효 최우선. settings 의 `effortLevel` 은 공식 키지만 `max`/`ultracode` 값은 **session-only 라 settings 파일에선 무효**, 파일 최대 유효값은 `xhigh` → `xhigh` 로 정정(실효 effort 는 env=max 유지). **ultracode 공식 런타임 모드 = `xhigh` + dynamic workflow 오케스트레이션** — `max > xhigh` 이므로 공식 ultracode 활성화는 오히려 effort 강등 → "env=max + hook 으로 workflow 유도" 현행이 우월. settings.json 으로 ultracode 영속 불가 |
| Thinking | `alwaysThinkingEnabled: true`, `showThinkingSummaries: true` | 모든 작업 ultrathink |
| **ultracode** | **항상 ON** — `inject-turn-context` hook 이 매 턴 키워드+지시 주입, Workflow 멀티에이전트 오케스트레이션 | "비용 무제한·깊이 최우선" 을 멀티에이전트로 확장. effort=max 와 별개 축 — 둘 다 유지 |
| ultrathink 키워드 | UserPromptSubmit hook 으로 매 턴 자동 주입 + **서브에이전트 6개 본문 첫 단락에 "ultrathink + max effort + thinking 모드" 명시 강제** | 사용자 입력에 누락돼도, 위임된 서브에이전트가 inject hook 미경유 경로로 진입해도 동일 effort 보장 |
| 병렬 | 단일 세션 + 서브에이전트 + **Workflow 도구**(ultracode). Agent Team(TeamCreate)만 미사용 | Workflow 는 결정론적·resume·budget 지원이라 도입. Agent Team 은 토큰 3–5배·resume 미지원·동시 1팀만이라 계속 제외 |
| 권한 | `bypassPermissions` 전체 허용 | 사용자가 직접 git 으로 책임. 속도 우선 |
| PR | main 직커밋, PR 없음 | 1인 프로젝트 기존 흐름 존중 |
| **빌드** | **debug + release publish 항상 둘 다** | 한쪽만 하면 release exe outdated — verifier 가 강제 |
| **커밋** | **`git commit` 후 즉시 `git push` 자동** | auto-push hook + SessionEnd 양쪽에서. 다른 장비 즉시 받기 |
| 언어 | UI/대화 한국어, 코드/커밋 메시지/PR 영어 | P2 + 외부 협업 친화 |
| 히스토리 | 세션 요약 + 핵심 결정을 `docs/sessions/YYYY-MM-DD.md` | 다른 장비에서 사람·Claude 모두 읽기 쉬움 |
| .claude/ git | 일부 추적 (settings·agents·skills·hooks 만) | 장비 간 하네스 공유 |
| CLAUDE.md | ≤30 줄 하드 제한 | InstructionsLoaded hook 경고. 줄 제한 상수는 `_common.ps1` 의 `$ClaudeMdLineLimit` 단일 진실원 (size-check hook + harness-status 공유) |

## 2. 파일 구조

```
.claude/
├── settings.json              ✅ committed (팀 공유)
├── settings.local.json        ❌ ignored (개인 override)
├── agents/                    ✅ committed
│   ├── explorer.md            탐색/검색
│   ├── planner.md             설계/PR-XX 초안
│   ├── reviewer.md            P규칙 invariant + 빌드 + 품질
│   ├── docs-keeper.md         docs/ 동기화
│   ├── historian.md           세션 요약
│   └── verifier.md            build/publish/test
├── skills/                    ✅ committed (Skill 형식 슬래시 커맨드)
│   ├── plan/SKILL.md          /plan
│   ├── sync-docs/SKILL.md     /sync-docs
│   ├── resume-session/SKILL.md /resume-session (다른 장비 이어받기)
│   ├── wrap-up/SKILL.md       /wrap-up (세션 마무리)
│   ├── harness-status/SKILL.md /harness-status
│   └── cleanup-worktrees/SKILL.md /cleanup-worktrees (worktree 빌드 산출물 정리)
├── workflows/                 ✅ committed (ultracode 멀티에이전트 워크플로우)
│   ├── release-review.js      릴리즈 전 멀티관점 리뷰
│   ├── bug-hunt.js            버그/레이스 헌트 (loop-until-dry)
│   ├── codebase-audit.js      전체 코드 감사
│   ├── design-compare.js      신규 기능 설계 비교 (judge panel)
│   └── harness-optimize.js    하네스 자체 최적화
├── scratch/                   ❌ ignored (디버깅 임시 ps1 — 현재 비었음, PR-15 권한상승 프로브 잔재 정리됨)
├── hooks/                     ✅ committed
│   ├── lib/_common.ps1        공통 함수 + 공유 상수 ($ClaudeMdLineLimit, Hide-Secrets, Invoke-HookSafely, Write-HookError, Add-SessionBlock, Invoke-Push, Invoke-WipCommit, Sync-Memory, Get-AutoMemoryDir, Test-WorkflowPhaseDrift, Test-WorkflowSyntax, Get-PorcelainStatus)
│   ├── inject-turn-context.ps1 UserPromptSubmit — ultrathink+max effort+ultracode 주입 (워크플로우 카탈로그 동적)
│   ├── session-start.ps1      SessionStart — 이전 요약 주입 + push 안 한 commit 알림 + Sync-Memory
│   ├── pre-compact.ps1        PreCompact — 압축 마커 append + git 스냅샷 additionalContext
│   ├── post-edit-doc-sync.ps1 PostToolUse(Edit/Write) — 문서 동기화 리마인더
│   ├── auto-push.ps1          PostToolUse(Bash|PowerShell git commit) — 커밋 후 자동 push
│   ├── stop-record.ps1        Stop — 진행 상황 append (secret 마스킹)
│   ├── session-end.ps1        SessionEnd — wip 커밋 + auto push + 요약 파이널
│   ├── claude-md-size-check.ps1  InstructionsLoaded — CLAUDE.md 줄 제한 검사 ($ClaudeMdLineLimit)
│   └── statusline.ps1         status line 렌더
├── state/                     ❌ ignored (런타임 상태, hook-errors.log 등)
└── worktrees/                 ❌ ignored

docs/
├── INDEX.md                   문서 인덱스 (이 하네스는 여기서 링크)
├── harness.md                 ← 이 파일
└── sessions/
    ├── README.md              세션 로그 규칙
    └── YYYY-MM-DD.md          하루 1개 append-only
```

## 3. 서브에이전트 + ultracode 워크플로우

메인 세션은 가능한 한 서브에이전트에 위임해서 깔끔하게 유지합니다.

**effort 정책 — 본문 명시 강제**: 6개 서브에이전트 모두 [.claude/agents/*.md](../.claude/agents/) 본문 첫 단락에 `**모든 작업은 ultrathink + max effort + thinking 모드로 수행합니다** — 하네스 정책 (메인 세션과 동일)` 한 줄을 박아두어, UserPromptSubmit hook 주입 경로 미경유 시에도 동일 깊이로 추론하도록 보장합니다.

| 에이전트 | 호출 시점 | 도구 |
|---------|----------|------|
| **explorer** | "X 가 어디?" 같은 read-only 조사가 3쿼리 이상이거나 여러 위치 | Read, Glob, Grep, Bash |
| **planner** | 다중 파일 / 새 기능 / P규칙 영향 변경 전 (구현 안 함) | Read, Glob, Grep, Bash |
| **reviewer** | 코드 변경 후 commit 직전 — P규칙 invariant + 빌드 + 품질 | Read, Glob, Grep, Bash |
| **docs-keeper** | Edit/Write 후 docs/ 동기화 — PostToolUse hook 이 신호 | Read, Edit, Write, Glob, Grep, Bash |
| **historian** | 세션 정리 — `/wrap-up` 또는 SessionEnd 후속 | Read, Write, Edit, Bash |
| **verifier** | release 전 / 큰 변경 후 — `dotnet build`/`publish`/`test` (release-review 의 Build phase 노드로도 호출) | Bash, Read, Glob, Grep |

전체 정의는 [.claude/agents/*.md](../.claude/agents/) 참조. **invariant grep 단일 진실원**: reviewer 는 grep 명령을 자체 보유하지 않고 [docs/conventions.md](conventions.md) 를 매 호출마다 새로 Read 해 전수 추출 (방법 A) — 현재 알려진 5 위치 (§P6 verification invariants, §P6 Additional sub-rule, §Silent catch §8 Core↔Logger, §Silent catch §9 Debug "failed", §AOT Verification). 자세한 추출 규칙은 [.claude/agents/reviewer.md §0](../.claude/agents/reviewer.md) 참조 — drift 방지.

### ultracode — 멀티에이전트 워크플로우 (항상 ON)

서브에이전트가 "단일 작업 위임"이라면, **ultracode 는 한 작업을 여러 에이전트로 쪼개 병렬·교차검증하는 오케스트레이션**입니다. 2026-06-08 전면 도입 (인터뷰: "비용 무제한·깊이 최우선" 을 멀티에이전트로 확장).

**effort 와 별개 축**: ultracode 는 effort 레벨(low/…/max)이 아닙니다. `CLAUDE_CODE_EFFORT_LEVEL=max` 는 그대로 유지되고, ultracode 는 그 위에서 Workflow 오케스트레이션을 켭니다. **env 를 `ultracode` 로 바꾸지 마세요** — max 를 잃을 수 있어, 키워드 주입 방식으로 둘 다 유지합니다.

**발동 — 항상 자동**: `inject-turn-context.ps1` hook 이 매 턴 "ultracode" 키워드 + 행동 지시를 주입. substantive 작업(다중 파일 변경·코드 리뷰·릴리즈 점검·버그/레이스 헌트·설계 비교·하네스 변경)은 Workflow 도구로 오케스트레이션하고, trivial 편집·단순 대화·단일 사실 조회만 solo. 주입되는 워크플로우 카탈로그는 `.claude/workflows/*.js` 파일시스템을 **단일 진실원**으로 동적 나열 — 워크플로우 추가/삭제가 매 턴 주입에 자동 반영(디렉토리 못 읽으면 수치 없는 '`.claude/workflows/` 참조' 안내로 fallback — 하드코딩 사본을 안 두어 drift 0).

**Agent Team 과의 구분**: Workflow 도구 ≠ Agent Team(TeamCreate). Workflow 는 결정론적 제어흐름(loop/조건/fan-out)·resume(`resumeFromRunId`)·토큰 budget 을 지원해 도입. Agent Team 은 토큰 3–5배·resume 미지원이라 **계속 거부** — "단일 세션" 철학과 충돌 없음.

**저장 워크플로우** (`.claude/workflows/*.js` 가 단일 진실원 — 추가/삭제 자동 반영). 저장 즉시 `/<name>` 슬래시 커맨드로 자동 노출되며, 메인 세션은 `Workflow({ name })` 로 호출:

| 워크플로우 | 무엇을 | 패턴 |
|-----------|--------|------|
| `release-review` | 릴리즈 전 correctness·보안·P1~P6·동시성 4차원 병렬 리뷰 → 적대적 검증 → **Build 게이트**(verifier 가 build·publish·test 실행). invariant 차원이 버전 4-part 일관성(csproj·git태그·CHANGELOG)·P6 단방향(`git grep KoEnVue.App Core/`=0)도 명시 점검. ⚠️ 적대적 검증은 **동일 model·동일 코드 재독** 기반이라 finder 의 공통 환각은 못 거름 — 객관 신호인 **Build 게이트**가 보완. SCOPE 기본값은 `HEAD~1..HEAD`(직전 커밋 — main 직커밋이라 `main..HEAD` 는 보통 빔), 차원 에이전트 실패 시 그 차원을 `degraded` 로 분기해 `degradedDimensions` 로 노출(거짓 클린 방지) | pipeline + adversarial verify + build gate |
| `bug-hunt` | 동시성·레이스·견고성 결함을 안 나올 때까지 반복 탐색 | loop-until-dry + 다관점 렌즈 |
| `codebase-audit` | App/·Core/ 모듈 전수 병렬 점검 → P규칙 게이트 → AUDIT 종합. Scope 가 `MAX_MODULES=24` 절대상한(초과 시 응집도 병합)·Scope null 은 `error` 반환(거짓 클린 방지)·실패 노드 수를 `agentsFailed` 로 노출(0건이 깨끗을 보장 안 함) | scope→audit→gate |
| `design-compare` | 기능 설계를 N접근법(현재 3 angle 고정) 제안 → judge panel 점수화 → 합성 (`args.feature` 필수). winner null 은 명시 `error`(planner/judge 전멸을 '별로'로 오인 방지), `coreIdeas`(=winner 자신의 bestIdeas, 합성 1순위) 노출 | judge panel |
| `harness-optimize` | 하네스 구성요소 점검 → completeness critic. area 기준 dedup(found+critic 양쪽), critic 입력 slice 40→60(절단 완화), self-audit 프롬프트(harness-optimize.js 자신의 설계 결함도 점검) | inspect + critic |

각 워크플로우는 KoEnVue 서브에이전트(explorer/planner/reviewer/verifier)를 `agentType` 으로 재사용하고 `schema` 로 구조화 출력을 강제합니다. 예: `Workflow({ name: 'release-review', args: { scope: 'PR-26 변경' } })`.

**라우팅 — 저비용 단일 위임 vs 고비용 워크플로우**: 모든 substantive 작업을 Workflow 로 보내지 마세요. **단일 관점이면 충분한 작업은 스킬 슬래시(저비용)** — 설계 한 건은 `/plan`(planner 1명), 문서 동기화는 `/sync-docs`(docs-keeper 1명). **여러 관점의 교차검증이 실익일 때만 Workflow**(fan-out 토큰 수 배~수십 배, §8). 특히 `/plan`(planner 단독, 저비용) vs `design-compare`(3 angle 제안 + judge panel 점수화 + 합성, 고비용)는 한 기능을 **여러 설계안으로 경쟁시켜 비교**할 때만 후자 — 단일 합리안이면 `/plan` 으로 충분.

**선택 기준 — release-review vs bug-hunt** (동시성 점검 시 모호함 해소): `release-review` 는 릴리즈 직전 diff 를 1-pass 로 4차원(correctness·보안·P규칙·**동시성**)+빌드게이트로 훑어 동시성을 이미 커버한다. `bug-hunt` 는 레이스 의심이 깊을 때 전체 범위를 안 나올 때까지(loop-until-dry) 반복 탐색하는 더 무거운 도구 — release-review 가 1차로 동시성을 보고, 그래도 레이스 의심이 잔존하면 bug-hunt 를 추가로 돌린다.

**leaf vs 오케스트레이터**: 서브에이전트의 `tools:` 에는 위임 도구가 없습니다(leaf). 오케스트레이션은 메인 세션 또는 워크플로우 스크립트가 담당합니다.

**호출 경로 — 역할분담**: 저장된 워크플로우가 `agentType` 으로 실제 노드 호출하는 서브에이전트는 **explorer / planner / reviewer / verifier**(explorer=harness-optimize Inspect·codebase-audit Scope, planner=design-compare Propose, reviewer=release-review Review·codebase-audit Gate, **verifier=release-review Build**). bug-hunt 의 `agent()` 는 `agentType` 미지정(기본 워크플로우 에이전트). **docs-keeper / historian 만 워크플로우 노드가 아닌 메인 세션 위임 전용**(PostToolUse 신호 / `/wrap-up` 등)입니다 — `README.md` 의 `agentType` enum 에 전 서브에이전트가 열거돼 있어도 노드로 쓰이는 건 위 4개. "서브에이전트는 워크플로우 노드로 호출"을 전체로 일반화하지 마세요. (1차에선 verifier 도 메인세션 위임 전용으로 적었으나, release-review 에 Build 게이트가 추가되며 노드로 승격.)

**meta↔phase 자동 가드**: `.claude/workflows/README.md` 의 "meta.phases 의 title ↔ 본문 `phase('X')` 1:1" 규약을 `_common.ps1` 의 `Test-WorkflowPhaseDrift` 가 정규식 휴리스틱으로 기계 검증합니다. `/harness-status` 의 `## 워크플로우 무결성` 섹션이 매 진단 시 호출 — 불일치 워크플로우(meta-only / body-only phase)를 보고하고, 전부 일치면 "✅ 정합"(현재 drift 0).

**JS 정적 문법 가드**: phase drift(의미 정합)와 별개로 `_common.ps1` 의 `Test-WorkflowSyntax` 가 워크플로우 `.js` 의 **순수 문법**을 검사합니다 — `check-workflow-syntax.cjs` 가 node 로 본문을 `AsyncFunction`(async 함수 본문)으로 파싱(실행 안 함)해 SyntaxError 만 검출. 워크플로우 본문은 런타임이 async 로 실행하므로 top-level `await`/`return` 이 합법인데 `node --check` 는 이를 오탐 → AsyncFunction 파싱은 await/return 둘 다 허용하고 ESM `export` 만 제거하면 포맷이 일치해 **오탐 0**. 이로써 종전 "정적 문법검사 불가" 한계는 해소(단 phase 실제 실행·`agent()` 호출 등 **런타임 의미검증은 여전히 런타임 전용**). node/스크립트 부재 시 침묵 skip.

**결과 반환 즉시 고정**: 워크플로우 산출(release-review/codebase-audit/harness-optimize 등)은 max effort 로 생성한 고비용 결과이므로, 메인 세션은 반환 즉시 git-tracked 파일(예: `docs/improvement-plan/AUDIT-YYYY-MM-DD-*.md`)로 박제해 컨텍스트 휘발을 막습니다. [AUDIT-2026-06-08-harness.md](improvement-plan/AUDIT-2026-06-08-harness.md) 가 첫 적용 사례.

**⚠️ 검증 상태**: 워크플로우의 `/<name>` 자동 노출은 확인됨. 다만 hook 의 키워드 주입이 ultracode **런타임 플래그**를 켜는지는 미검증(ultrathink 와 달리 ultracode 는 세션 설정일 수 있음). 명시적 한국어 지시가 fallback 이라 행동은 보장되지만, 새 세션에서 statusLine 의 `ultracode` 표시로 확인하세요. 런타임 활성화가 안 되면 세션 시작 시 `/effort ultracode` 수동 입력이 대안(단 effort=max 와의 우선순위는 별도 확인).

## 4. Hook 라이프사이클

hook 이벤트 8개 (SessionStart · PreCompact · UserPromptSubmit · PostToolUse×2 · Stop · SessionEnd · InstructionsLoaded). 각 hook 의 역할:

### `SessionStart` → `session-start.ps1`
- 가장 최근 `docs/sessions/YYYY-MM-DD.md` 에서 **`## [HH:MM] 세션 정리` 블록만 추출**해 `additionalContext` 로 주입 (정리 블록 없으면 마지막 turn 헤더 3개만 표시 — 잡음 최소화)
- 최근 3일 내 wip 커밋 알림 (5건까지)
- dirty tree 면 알림 (30건 클램프) — `Get-PorcelainStatus`(git status --porcelain **1회**)로 가드+클램프+count 를 한 번에 처리 (이전엔 git 3회 호출)
- 최근 hook 에러 3건 (있으면)
- `Sync-Memory` 로 C:↔E: 메모리 동기화 (§12 참조)
- P1–P6 규칙과 서브에이전트 활용 권장사항 reminder
- **`-FallbackContext`/`-EventName` 안전망**: `Write-HookOutput` 직전에 죽어도 catch 경로가 최소 fallback 컨텍스트(ultrathink/max/ultracode + 이전 세션 포인터) 1줄을 주입 — 이전엔 inject-turn-context 단독이었으나 SessionStart/PreCompact 로 확장(3곳)

### `PreCompact` → `pre-compact.ps1`
- 대화 압축(컴팩션, 자동 컨텍스트 한도 / 수동 `/compact`) **직전** 실행. ultracode 멀티에이전트가 컨텍스트를 빠르게 채워 컴팩션 빈도가 높아진 환경에서 작업 연속성을 보강. matcher `*` 라 auto·manual 둘 다 트리거, `payload.trigger` 로 구분 기록
- **(1) 압축 마커 박제**: 오늘 세션 파일에 `## [HH:MM] compaction (trigger=auto|manual)` 블록 append (`Add-SessionBlock` mutex 로 직렬화) → 압축 지점을 영구 기록 (압축 전 turn 기록이 상세 컨텍스트 원본임을 명시)
- **(2) 연속성 컨텍스트**: `additionalContext` 로 git 스냅샷(미커밋 변경 30건 클램프 + 최근 커밋 5개) + 세션파일 복원 포인터를 주입 → 압축 직후 새 컨텍스트에서 진행 중이던 미커밋 작업의 연속성 즉시 복원 (SessionStart 와 동일 메커니즘). 미커밋 변경은 `Get-PorcelainStatus`(git **1회**)로 축소
- **`-FallbackContext`/`-EventName` 안전망**: SessionStart 와 동형 — `Write-HookOutput` 직전 사망 시 catch 경로가 "압축됨 — 연속성 확인 + ultrathink/max/ultracode 유지" 1줄 주입 (inject-turn-context 단독 → SessionStart/PreCompact 포함 3곳)

### `UserPromptSubmit` → `inject-turn-context.ps1`
- 사용자 입력에 `ultrathink` 가 없으면 — **"ultrathink + thinking 모드"** + **"항상 max effort — 단축/생략 없이"** 주입
- 사용자 입력에 `ultracode` 가 없으면 — **ultracode 멀티에이전트 모드** 주입 (키워드 + 행동 지시 + 워크플로우 카탈로그). 카탈로그는 `.claude/workflows/*.js` BaseName 동적 나열 (단일 진실원, 디렉토리 못 읽으면 '`.claude/workflows/` 참조' 안내로 fallback). 두 축 독립 — 해당 키워드가 입력에 있으면 그 축만 skip
- effort=max 는 env(`CLAUDE_CODE_EFFORT_LEVEL=max`)로 별도 강제 — ultracode 가 effort 를 대체하지 않음. 서브에이전트 본문 강제 명시와 함께 effort 단축 경로 0 보장

### `PostToolUse(Edit|Write|NotebookEdit)` → `post-edit-doc-sync.ps1`
- 변경된 파일 경로를 매핑 테이블에 대조. rules 배열은 **first-match-wins** — 더 좁은 패턴 `^Core/Native/` 가 일반 `^Core/` 보다 먼저 정의돼 우선 매칭됨.
- 영향받는 docs 목록을 이번 턴 컨텍스트로 추가
- **보안 민감 매핑**: `Core/Native/` (P/Invoke 시그니처), `app.manifest` (UAC 레벨), `KoEnVue.csproj` (NuGet 추가/제거), `NuGet.config` (외부 피드) 변경 시 Reason 메시지에 `/security-review` 권장/필수 문구 자동 포함 — Claude Code 의 built-in 슬래시 커맨드로 보안 점검 유도.
- `.claude/state/pending-docs.txt` 에 기록 → Stop hook 에서 사용
- **중복 reminder 억제**: 같은 매핑(예: `App/*` → `docs/architecture.md`)이 한 턴에 여러 번 trigger 되면, 첫 번째만 컨텍스트 reminder. pending-docs.txt 에는 모든 파일 기록 (Stop hook 에서 다 표시).
- **워크플로우 phase-drift + 문법 자동가드**: 편집 파일이 `.claude/workflows/*.js` 면 `Test-WorkflowPhaseDrift`(meta.phases ↔ 본문 `phase()` 불일치)와 `Test-WorkflowSyntax`(편집된 파일만 AsyncFunction 파싱으로 SyntaxError 검출)를 즉시 호출해 그 자리에서 경고(런타임/`/harness-status` 호출 전 조기 검출). drift/문법 경고가 있으면 중복 억제와 무관하게 항상 내보냄.

### `PostToolUse(Bash|PowerShell git commit *)` → `auto-push.ps1`
- "**커밋 = 푸시 항상 같이**" 규칙 구현
- **matcher 는 두 셸 도구 모두** — `env.CLAUDE_CODE_USE_POWERSHELL_TOOL=1` 이라 이 프로젝트의 주 셸 도구는 PowerShell 인데 matcher 가 `Bash` 뿐이어서 **PowerShell 로 커밋하면 hook 이 발화하지 않았다**(2026-07-22 발견·수정). `auto-push.ps1` 은 `tool_input.command` 를 읽으므로 두 도구 모두 동일하게 동작 — matcher 만 좁았던 침묵 실패
- Claude 가 `git commit` 명령을 호출하면 즉시 발동 → `git push` 자동 실행
- upstream 미설정 시 (`-u origin <branch>` 없이 처음 push) → skip + 사용자에게 알림
- push 실패 (원격 거부 / 네트워크) → 컨텍스트에 실패 알림
- `--dry-run`, `--allow-empty-message`, `--no-edit` 같은 비-실 커밋 패턴은 skip

### `Stop` → `stop-record.ps1`
- transcript `Get-Content -Tail 1000` 클램프 후 마지막 assistant 응답 발췌 (400자)
- **빈 발췌 마커**: text 응답 없이 끝난 턴(도구 위임 / 구조화 출력)은 `(text 응답 없음 …)` 마커를 명시 append — 침묵 실패와 도구-위임 턴을 구분. 매 턴 이 마커면 transcript 파싱 점검 신호.
- **secret 마스킹** (Hide-Secrets 함수) 후 기록 — 자세한 한계는 §9 참조
- 이번 턴의 pending-docs 와 dirty tree 상태 정리 (30건 클램프)
- `docs/sessions/YYYY-MM-DD.md` 끝에 `## [HH:MM] turn` 블록 append (`Add-SessionBlock` mutex 로 직렬화 — PreCompact/SessionEnd 와 동시 append 시 블록 인터리브 방지)
- **한계**: 세션 발췌는 transcript JSONL 의 내부 스키마(`type`/`message`/`content`)에 의존 — Claude Code 버전업으로 스키마가 바뀌면 발췌가 깨질 수 있음(위 빈 발췌 마커가 그 조기 신호).

### `SessionEnd` → `session-end.ps1`
- dirty tree 가 있으면:
  1. **먼저** 오늘 세션 파일에 `## [HH:MM] session-end (reason)` 블록 append (`Add-SessionBlock` mutex 로 직렬화; 이 세션의 최근 10분 커밋 목록 + "방금 wip — 이 마무리 블록 포함" 한 줄)
  2. **그 다음** `wip: session YYYY-MM-DD HH:MM — session end (reason)` 커밋 — block 변경분 + 기존 dirty 가 같은 wip 커밋에 묶임 (다음 세션 시작 시 dirty 잔여물 0 보장)
- dirty tree 가 없으면 nothing — 마무리 블록도 안 적고 wip 커밋도 만들지 않음 (잡음 0)
- 위 처리 후 unpushed commit 이 있으면 `git push` 자동 시도 ("커밋 = 푸시 항상 같이" fallback)

### `InstructionsLoaded` → `claude-md-size-check.ps1`
- 로드된 파일이 `CLAUDE.md` 이면 줄 수 검사
- `_common.ps1` 의 `$ClaudeMdLineLimit`(현재 30) 초과면 경고 컨텍스트 주입 — 한계값은 harness-status 스킬과 공유하는 단일 진실원

**안전망**: 모든 hook 은 `_common.ps1` 의 `Invoke-HookSafely { ... }` 로 감싸져 있어 에러가 transcript 에 새지 않고 `.claude/state/hook-errors.log` 에 기록 (100줄 초과 시 자동 rotation — 마지막 50줄만 유지). `Write-HookError` 는 `HookName`/`Message` 가 빈 값이면 기록을 거부해 `' :: '` 같은 프로브성 잡음 라인이 로그에 안 쌓이게 가드.

**안전망의 안전망 — `-FallbackContext`/`-EventName`**: context-injecting hook 이 `Write-HookOutput` 직전에 죽으면 그 턴의 주입(ultrathink/ultracode/effort 또는 연속성 컨텍스트)이 통째 증발 — ultracode 항상-ON 의 단일 실패점입니다. `Invoke-HookSafely` 에 `-FallbackContext`(+`-EventName`)를 주면 catch 경로에서 최소 fallback 컨텍스트 1줄을 `hookSpecificOutput.additionalContext` 로 내보내 깊이 손실을 방어합니다. **inject-turn-context·session-start·pre-compact 3곳이 이 옵션 사용**(이전엔 inject 단독 — context-injecting hook 전체로 확장).

## 5. 매핑 — 코드 변경 → 동기화 문서

post-edit-doc-sync.ps1 의 매핑 규칙:

| 코드 영역 | 동기화 docs |
|----------|------------|
| `Core/Native/*` | docs/architecture.md, docs/conventions.md (+ `/security-review` 권장) |
| `App/*.cs` | docs/architecture.md, docs/implementation-notes.md |
| `Core/*.cs` | docs/architecture.md |
| `Program*.cs` | docs/architecture.md, docs/implementation-notes.md |
| `app.manifest` | CLAUDE.md, docs/conventions.md (UAC 변경 시 `/security-review`) |
| `KoEnVue.csproj` | docs/architecture.md, docs/release-procedure.md (NuGet 추가 시 `/security-review` 필수) |
| `Directory.Build.targets` | docs/architecture.md, docs/conventions.md |
| `NuGet.config` | docs/conventions.md (외부 피드 추가 시 `/security-review` 필수) |
| `tests/` | docs/conventions.md |
| `.github/` | CONTRIBUTING.md |
| `.claude/` | docs/harness.md |

규칙 순서는 더 좁은 패턴(`Core/Native/`)이 더 넓은 패턴(`Core/`)보다 먼저 매칭되도록 [post-edit-doc-sync.ps1](../.claude/hooks/post-edit-doc-sync.ps1) 에서 보장됨 (first-match-wins).

**모든 사용자 가시 변경**은 추가로 `CHANGELOG.md` 의 `## [Unreleased]` 섹션에 항목 추가.

## 6. 슬래시 커맨드 (발견성 단일 진실원)

`/<name>` 슬래시는 **두 종류**가 같은 네임스페이스에 노출됩니다 — **skills**(대화형 단일 위임, `.claude/skills/<name>/SKILL.md`)와 **workflows**(ultracode 멀티에이전트, `.claude/workflows/<name>.js` 가 저장 즉시 `/<name>` 으로 자동 노출, §3). 본 표가 전체 슬래시의 발견성 정본입니다.

**skills — 대화형 단일 위임** (`/<name> [args]`):

| 커맨드 | 무엇을 함 |
|--------|----------|
| `/plan <작업 설명>` | planner 서브에이전트로 설계 위임 (단일 합리안 — 저비용; 여러 설계안 경쟁은 `/design-compare`) |
| `/sync-docs` | docs-keeper 서브에이전트로 문서 동기화 |
| `/resume-session` | 다른 장비에서 이어 작업 시 — 최근 세션과 git 상태 정리 후 다음 작업 제안 |
| `/wrap-up` | 세션 마무리 — docs-keeper + historian 호출, dirty tree 정리 |
| `/harness-status` | 하네스 현재 상태 한눈에 — 모델/effort, hook 동작, 서브에이전트 수, 워크플로우 meta↔phase 정합, 오늘 세션, dirty tree, 최근 hook 에러 |
| `/cleanup-worktrees` | `.claude/worktrees/` 의 일주일 이상 미사용 디렉토리 정리 (빌드 산출물 ~GB 누적 방지) |

각 skill 은 `.claude/skills/<name>/SKILL.md` 에 정의. Skill 형식은 Claude Code 의 최신 권장. 향후 supporting files 가 필요해지면 같은 디렉토리에 추가 가능 (예: `.claude/skills/release/scripts/build.ps1`).

**ultracode 워크플로우 슬래시 — 멀티에이전트 교차검증** (각 `/<name>` = `Workflow({name})` 와 동일; 고비용 fan-out, §8). 상세 동작·선택 기준·라우팅은 §3:

| 슬래시 | 무엇을 함 |
|--------|----------|
| `/release-review` | 릴리즈 전 4차원 병렬 리뷰 + 적대적 검증 + Build 게이트 |
| `/bug-hunt` | 동시성·레이스·견고성 결함을 안 나올 때까지 반복 탐색 (loop-until-dry) |
| `/codebase-audit` | App/·Core/ 모듈 전수 병렬 감사 → P규칙 게이트 |
| `/design-compare` | 기능 설계 3 angle 제안 → judge panel 점수화 → 합성 (`args.feature` 필수) |
| `/harness-optimize` | 하네스 구성요소 점검 → completeness critic |

워크플로우 카탈로그의 추가/삭제는 `.claude/workflows/*.js` 파일시스템이 단일 진실원 — 위 표는 발견성 편의이고, 매 턴 주입(§3)과 `/harness-status` 점검은 디렉토리를 동적으로 읽습니다.

### Claude Code built-in 명령 — 함께 활용

위 슬래시 커맨드(6개) 외에 Claude Code 표준 명령도 사용 가능:

| 명령 | 용도 |
|------|------|
| `/security-review` | 현재 branch 의 보안 점검 — Windows P/Invoke, asInvoker 변경, NuGet 추가 같은 변경 시 |
| `/verify` | 변경이 실제로 동작하는지 앱을 실행해 확인 (verifier subagent 보다 더 일반) |
| `/simplify` | 변경 코드의 중복·품질·효율 검토 후 자동 정리 |
| `/run` | 앱을 실행해 변경 결과를 사람 손으로 확인 |
| `/loop <interval> <command>` | 정기 실행 — 예: `/loop 5m /verify` 로 5분마다 검증 |
| `/status` | 누적 토큰/비용, 계정 정보 |
| `/init` | CLAUDE.md 자동 생성 — KoEnVue 는 이미 있어 불필요 |
| `/review` | PR 리뷰 — KoEnVue 는 main 직커밋이라 무관 |

## 7. 장비 간 연속성

git 만이 유일한 교봉점. **"커밋 = 푸시 항상 같이"** 규칙으로 push 도 자동.

1. 작업 중 `Ctrl+C` / 시스템 종료 → `SessionEnd` hook 이 자동 wip 커밋 + **자동 push**
2. Claude 의 모든 `git commit` 도 PostToolUse hook 이 자동 push
3. 다른 장비에서 `git pull` → 최신 상태
4. `claude` 실행 → `SessionStart` hook 이 최근 세션 요약 + push 안 한 commit 알림 (잊은 경우 대비)
5. (선택) `/resume-session` 으로 상세 컨텍스트 확인

**예외 — 자동 push 안 되는 경우**:
- upstream branch 미설정 (새 branch 첫 push) — 사용자가 `git push -u origin <branch>` 한 번 실행 필요
- 원격이 fast-forward 거부 — 충돌 해결 후 수동 push

**중요한 결정 / 다음 작업 / 위험 신호**는 `historian` 서브에이전트가 `docs/sessions/YYYY-MM-DD.md` 의 `## [HH:MM] 세션 정리` 블록에 기록. 이 블록만 보면 어디서든 이어받을 수 있게.

## 8. 비용 모니터링

`bypassPermissions` + Opus + max effort + thinking + 매 턴 ultrathink/ultracode 주입 = 보통 코딩 작업의 약 8~15배, **ultracode 워크플로우가 도는 substantive 작업은 추가로 수 배~수십 배**(워크플로우당 최대 16 동시 / 1,000 누적 에이전트) 토큰 소비. 사용자가 "비용 무제한, 깊이 최우선" 선택했음 (인터뷰 결과). 하네스는 자동 하향 조정 안 함.

**상한의 실제 — `budget` 가드는 거의 무실효**: `budget`(total/spent/remaining) 을 실제 읽는 워크플로우는 **bug-hunt 1개뿐**이고, 그조차 `budget.total` 미주입 시(현재 호출은 total 안 넘김) round hard cap(`round < 8`)만 실효 상한입니다. 나머지 4개(release-review·codebase-audit·design-compare·harness-optimize)는 budget 을 안 읽으며 **per-parallel-item cap** 이 유일 상한 — `MAX_VERIFY=25`(release-review)·`MAX_MODULES=24`(codebase-audit)·동시 16(전역). 즉 토큰 총량 가드가 아니라 fan-out 폭주 방지일 뿐. **⚠️ 미검증 단서**: 워크플로우 노드(`agent()`)가 메인 세션의 `CLAUDE_CODE_EFFORT_LEVEL=max` env 를 상속해 max effort 로 도는지는 미검증 — 노드가 effort 를 상속 안 하면 위 "수 배~수십 배" 추정이 과대일 수 있음(반대로 상속하면 추정대로). 비용 추정 시 이 단서를 감안.

가시화 수단:
- **`statusLine`**: 매 턴 `[opus · max] | git:main* | ultracode · 한/En 하네스 ON` 표시
- **`showTurnDuration: true`**: 매 응답 후 소요 시간 노출 → 비용 자각
- **`awaySummaryEnabled: true`**: 자리 비운 동안의 진행 요약
- **`/status`**: 누적 토큰/비용 (Claude Code built-in)
- **`/harness-status`**: 하네스 상태 + 최근 hook 에러

월 사용량 80% 도달 시 알림은 Claude.ai 계정 설정에서 별도 관리. 하네스 안에서 자동 알림 없음.

## 9. 보안 — Hide-Secrets 1차 방어선

`stop-record.ps1` 은 transcript 의 마지막 응답을 `docs/sessions/` 에 기록하기 전 `Hide-Secrets` 함수로 일부 secret 패턴을 마스킹합니다.

**잡힘**:
- GitHub PAT (`gh[psouru]_*`), Anthropic/OpenAI 키 (`sk-*`, `sk-ant-*`)
- AWS access key (`AKIA*`)
- Bearer 토큰 (Authorization 헤더)
- Slack 토큰 (`xox[abprs]-*`)
- 일반 `api_key=`, `password=`, `secret=`, `token=`, `access_key=` 형식 (16자 이상 값)

**안 잡힘 — 사용자 책임**:
- **PII** (사람 이름, 주소, 이메일, 전화번호)
- **16자 미만의 짧은 토큰**
- **임의 base64 데이터**
- **비표준 형식의 비밀** (예: 환경변수 이름 없이 단독으로 등장하는 hash)
- **commit 직전의 git diff** — `git add -A` 가 잡은 파일 내용은 마스킹 안 됨

**권장**: transcript 에 절대 노출돼선 안 될 정보(고객 데이터, 회사 기밀)는 평소부터 Claude Code 세션에 입력하지 마세요. 하네스는 1차 방어선이지 만능 방패가 아닙니다. `.gitignore` 에 `*.env`, `secrets/`, `credentials*`, `*.pfx` 등 secret 파일 패턴이 등록돼 있어 우발적 commit 도 막지만, 위 한계는 동일합니다.

## 10. 알려진 한계

- `SessionEnd` 의 wip 커밋은 의미 없는 메시지로 묶이므로, 가능하면 `/wrap-up` 으로 의미 있는 커밋 만들고 종료
- `SessionEnd` 의 auto-push 실패는 `additionalContext` 로 사용자에게 노출 불가 — `.claude/state/hook-errors.log` 에 기록되고 다음 `SessionStart` 의 "최근 hook 에러" 섹션에서 노출. 즉시 알고 싶다면 종료 직전 `/wrap-up` 사용.
- `historian` 의 자동 호출은 hook 으로 불가능 (hook 은 단순 PowerShell 만 — subagent 위임 불가). 사용자가 `/wrap-up` 실행하거나 메인 세션이 wrap-up 판단 시 위임
- `verifier` 는 UI 동작 검증 불가 — KoEnVue 가 IME 인디케이터라 실제 한/En 전환은 사람 손이 필요
- `.claude/state/` 는 gitignored — 다른 장비 동기화 안 됨 (의도된 동작, 런타임 상태)
- **Hooks 는 PowerShell 7+ 의존** — 다음 절차로 설치 + 검증:
  ```powershell
  # Windows
  winget install --id Microsoft.PowerShell --source winget
  ```
  ```bash
  # macOS
  brew install --cask powershell
  # Linux (Ubuntu/Debian)
  sudo apt update && sudo apt install -y powershell
  ```
  ```bash
  pwsh --version   # 7.x 이상 확인
  ```
  Windows PowerShell 5.x (기본 내장) 만으로는 hook 전부 fail. 단 KoEnVue 는 `net10.0-windows` 타깃이라 빌드/실행은 Windows 전용 — Mac/Linux 는 documentation·planning 작업에만 한정.
- **`/wrap-up` 의 race condition**: `docs/sessions/YYYY-MM-DD.md` 의 쓰기는 hook(stop-record / session-end) 과 historian subagent 만 수행 — 메인 세션이 직접 같은 파일을 Edit/Write 하면 충돌 가능. [skills/wrap-up/SKILL.md](../.claude/skills/wrap-up/SKILL.md) 의 "쓰기 단일 진실원" 규약 참조.
- **inject-turn-context hook 오버헤드**: 매 UserPromptSubmit 마다 PowerShell 프로세스 생성. 측정(Win): `Measure-Command { '{"prompt":""}' | pwsh -NoProfile -ExecutionPolicy Bypass -File .claude/hooks/inject-turn-context.ps1 }`. **⚠️ 측정값 stale**: 기록된 **~381 ms** 는 Opus 4.7·2026-05-22·**inject-ultrathink 시절** 측정값으로, 이후 ultracode 워크플로우 카탈로그 동적 나열(`.claude/workflows/` 디렉토리 enum)이 추가돼 현 오버헤드와 다를 수 있음 — 빠른 응답이 중요하면 위 명령으로 재측정 권장. ultrathink+ultracode 매 턴 주입 안전망이지만, 빠른 응답을 원할 때 부담.
- **`.claude/worktrees/` 의 빌드 산출물 누적**: 서브에이전트가 publish 를 돌리면 worktree 안에 ~150 MB 산출물이 남고 정리 안 함. 주기적으로 `/cleanup-worktrees` SKILL 로 일주일 이상 미사용 worktree 제거 권장.
- **ultracode 런타임 활성화 미검증**: `inject-turn-context.ps1` 의 키워드 주입이 ultracode "런타임 플래그"를 켜는지만 미확인 — 워크플로우 `/<name>` 자동 노출과 `Workflow({name})` 실제 다중 에이전트 fan-out 은 **확인됨**(2026-06-08 release-review/harness-optimize 실행 시 각 6 에이전트). 행동은 명시적 지시 + 워크플로우 실행으로 보장됨. ⚠️ statusLine 의 `ultracode` 는 항상 하드코딩 표시라 **런타임 검증 신호가 아님** — 검증은 `Workflow` 실제 fan-out 로그로. (memory `feedback-harness-design` 참조)
- **ultracode 비용 급증**: substantive 작업마다 워크플로우 fan-out → 토큰 급증. 빠르고 싼 처리를 원하는 turn 은 작업이 trivial 함을 프롬프트에 명시하거나 워크플로우를 건너뛰도록 지시.
- **statusLine 하드코딩 폴백**: `model`/`effort` 는 statusLine payload(`payload.effort.level`)가 오면 그 값을 쓰되, **payload 미수신 시 하드코딩 폴백이라 런타임 진실이 아님** — model 은 `'opus'` 고정, effort 는 `$env:CLAUDE_CODE_EFFORT_LEVEL`(실효 경로) 반영 후 없으면 `'max'`(특히 model 은 항상 하드코딩 폴백). statusLine 은 화면 갱신마다 호출돼 가장 빈번하므로 **git 호출을 축소**: branch 는 `.git/HEAD` 를 직접 Read(rev-parse 프로세스 제거 — `ref: refs/heads/X` 파싱, detached 면 짧은 SHA), dirty `*` 는 `git status --porcelain --untracked-files=no` 1회(untracked 제외로 체감 비용 절감, 변경 신호는 보존). settings.json 의 statusLine 에 `timeout: 5` 추가로 렌더 지연 시 조기 차단.
- **PreCompact ↔ Stop 세션파일 동시 append 경합**: 두 hook 모두 `docs/sessions/YYYY-MM-DD.md` 끝에 `Add-Content` 로 블록을 붙임. 컴팩션과 턴 종료가 거의 동시에 발생하면 같은 파일 동시 쓰기로 블록이 섞이거나 누락될 가능성(append-only·저빈도라 실측 피해 미관측, OS 파일락 의존이라 §OS 감수 정책 대상).
- **transcript JSONL 스키마 의존**: stop-record 의 세션 발췌는 transcript 내부 스키마(`type`/`message`/`content`)에 의존 — Claude Code 버전업 시 깨질 수 있음(§4 Stop 의 빈 발췌 마커가 조기 신호).
- **verify 공통 환각 (적대적 검증의 사각)**: release-review·bug-hunt 의 적대적 검증은 **동일 model·동일 코드 재독** 기반이라, finder 가 코드를 잘못 읽어 생긴 **공통 환각**(예: 실제로 없는 레이스를 양쪽 다 "있다"고 봄)은 못 거른다 — 검증자도 같은 오독을 반복하기 때문. **빌드 게이트 같은 객관 신호**(컴파일/테스트 통과)가 이 사각을 일부 보완하지만, 동작 차원의 공통 환각은 사람 손(`/run`·`/verify`)이 최종 방어선.
- **워크플로우 결과 박제는 순수 메인세션 규율 — hook 안전망 없음**: §3 의 "결과 반환 즉시 git-tracked 파일로 박제"는 메인 세션의 규율일 뿐 hook 으로 강제·백업되지 않는다. 박제 **전에 컴팩션**이 일어나면 max effort 로 생성한 고비용 워크플로우 결과가 통째 휘발(전손) 가능 — PreCompact 의 연속성 컨텍스트는 git 스냅샷·세션 포인터만 담고 in-flight 워크플로우 산출은 못 살린다. 고비용 결과는 **받는 즉시** 박제(컴팩션 기다리지 말 것).
- **메모리 slug 장비간 가정**: `Sync-Memory` 의 `Get-AutoMemoryDir` slug(`e--dev-KoEnVue`)는 **리포 절대경로 기반**이라, C: 미러가 E: 와 정합하려면 **모든 장비에서 리포 경로가 같아야** 한다(예: 어디서나 `E:\dev\KoEnVue`). 경로가 다르면 slug 가 달라져 각 장비의 C: auto-memory 는 서로 독립되고 — E:(git)만 공유 truth로 남는다(C:↔E: 미러는 장비별 로컬). 다른 경로의 새 장비를 쓸 땐 메모리는 git pull 로만 받고 C: 미러는 그 장비 로컬임을 인지.

## 11. PR 분리 시 충돌 회피 정책

여러 PR 이 동시에 open 상태로 진행되는 분기에서, 같은 파일의 인접 영역을 건드리면 머지 충돌이 발생합니다. **본 §11 이 충돌 회피 정책(특히 아래 3안 표)의 정본**이며, docs-keeper [§ Step 0](../.claude/agents/docs-keeper.md) 은 절차만 갖고 3안은 여기를 참조합니다(planner 가 §11 을 참조하는 방식과 통일 — 표 중복 제거로 드리프트 방지). 학습 트리거: 한 세션에서 PR #3 vs PR #4 의 dev-note 충돌이 본 점검 부재로 발생.

**적용 시점**: 새 변경(코드/문서 모두)을 시작하기 전, 그리고 docs-keeper 위임 시 자동으로.

**절차**:

1. `gh pr list --state open --json number,title,headRefName,files` 로 open PR 의 파일 목록 확인
2. 이번 변경 대상 파일이 open PR 의 `files` 에 있으면 `gh pr diff <N>` 로 변경 영역 (라인/섹션) 확인
3. 본인 변경이 **인접 영역**인지 판단:
   - 같은 섹션 끝/시작, 같은 list block, 같은 파일 끝 append 는 인접
   - 완전히 다른 섹션, 동일 파일이라도 충분히 격리되면 비인접

**인접 영역 시 — 3안 중 선택**:

| 안 | 설명 | 적합 케이스 |
|----|------|-------------|
| **(a) 한 PR 로 묶기** | 두 변경을 같은 branch 에서 합쳐 단일 PR 로 | 변경이 논리적으로 연관 (같은 기능의 GUI + docs) |
| **(b) base 를 PR #N head 로 재기준** | `git rebase origin/<PR-N-branch>` 후 작업 | 변경이 독립적이나 PR #N 머지 전 의존 |
| **(c) 멀리 떨어진 다른 위치 선택** | 같은 파일이라도 인접하지 않은 영역으로 분리 (예: dev-note 다른 섹션, CHANGELOG 의 Added 대신 Changed) | 두 변경이 완전 독립 + 빠른 분리 머지 우선 |

**비인접 영역 시**: 보고에 "PR #N 충돌 위험 없음" 명시 후 진행.

**책임 분담**:
- docs-keeper: 본 점검을 자기 작업 흐름 Step 0 으로 자동 실행
- 메인 세션: 코드 변경에 대해서도 동일 점검 (필요 시 explorer 위임)
- 사용자: 3안 선택은 사용자 결정 — 서브에이전트는 보고만

## 12. Memory 시스템 — C:↔E: 동기화 (2026-06-08 split-brain 발견·해결)

이 컴퓨터의 **C: 드라이브는 보안 정책상 수시로 14일 전 시점으로 복원**됩니다. 기본 Claude Code memory 위치 (`C:\Users\<user>\.claude\projects\<project>\memory\`) 는 복원될 때마다 사라지므로, 본 하네스는 메모리를 프로젝트 트리(E:)로 옮기려 `autoMemoryDirectory` 를 설정했습니다.

**현 실태(2026-07-22 갱신)**: `autoMemoryDirectory` 는 `${CLAUDE_PROJECT_DIR}` 전개 실패로 무효였고, Claude Code 는 기본 위치 C: 를 읽기/쓰기해 왔습니다. 2026-07-22 에 이 키를 **절대경로로 교체**해 런타임이 E: 를 직접 쓰도록 시도했습니다 — 다만 **효력 확인은 다음 세션 시작 시** 시스템 컨텍스트가 안내하는 memory 경로로만 가능합니다(무효로 판명돼도 `Sync-Memory` hook 이 C:↔E: 를 보전하므로 실제 영향은 0, 즉 실패 모드가 안전).

> ⚠ **2026-06-09 세션 기록의 판단은 오류**입니다 — "C: 0파일이니 실제 활성 store 는 E:, system-reminder 경로 안내는 오안내" 라고 결론냈으나, E: 에 파일이 쌓인 건 `Sync-Memory` 가 **백업**한 결과지 런타임이 직접 쓴 증거가 아닙니다. 2026-07-22 세션 컨텍스트가 다시 C: 경로를 memory 위치로 안내해 06-08 판단(설정 무효 → C: 사용)이 옳음이 확인됐습니다. 즉 06-09 시점에도 이미 recall 이 깨져 있었습니다.

| 항목 | 실태 (2026-07-22) |
|------|------|
| 실제 auto-memory 위치 | **C:** `C:\Users\<user>\.claude\projects\E--dev-KoEnVue\memory\` — 드라이브문자 대소문자는 Claude Code 버전에 따라 변동(2026-07 이전 `e--`, 이후 `E--`). Windows 는 대소문자 무시라 접근엔 무영향 |
| E: `.claude/memory/` | git 추적 **truth** — 9파일(메모리 8 + `MEMORY.md` 인덱스, 2026-07-22 기준), C: 복원과 무관하게 보존 |
| `autoMemoryDirectory` 설정 | `E:/dev/KoEnVue/.claude/memory` (절대경로, 2026-07-22 변경 — AUDIT-2 #51 처리) — **효력 미검증**, 다음 세션에서 확인 |
| 복원 영향 | ✅ `Sync-Memory` hook — 디렉토리째 소실돼도 생성 후 E:→C: 복구 (2026-07-22 수정) |

**임시 구제 (적용됨)**: C: 에만 있던 `os-dependent-accept.md` 를 E: 로 복사 + MEMORY.md 갱신 + commit (소실 방지). 새 메모리 저장 시 수동으로 E: 에도 반영 권장.

**근본 해결 (적용됨 2026-06-08)**: SessionStart hook 의 `Sync-Memory`(`_common.ps1`)가 매 세션 C:↔E: 동기화 — **E: 가 truth**(복원 무관), C: 의 더 새 파일만 E: 로 흡수(복원된 옛 C: 가 최신 E: 를 못 덮게 mtime UTC 비교), 그 뒤 E:→C: 미러로 복원된 C: 를 최신 복구. slug=`e--dev-KoEnVue`·`Copy-Item` mtime 보존 검증 완료, 최초 실행 시 `restored=5` 로 현재 split-brain 해소(C:=E: 해시 일치). `absorbed>0`(C: 에 새 메모리) 시 SessionStart 가 커밋 권장 알림 → 다음 commit 에 E: 백업 포함. 각 파일 복사는 **per-file try/catch** 로 감싸 TOCTOU(`Test-Path` 후 복사 직전 파일 삭제·잠금)나 단일 파일 I/O 오류가 나머지 동기화를 중단시키지 않게 함.

**2026-07-22 재발과 수정 — 디렉토리째 부재 케이스**: 6주 공백 뒤 첫 세션에서 C: 프로젝트 디렉토리가 통째로 새로 생성돼 있었고(`memory` 서브디렉토리 **자체가 부재**), hook 이 복구를 전혀 하지 않았습니다. 원인은 `Get-AutoMemoryDir` 이 `Test-Path` 실패 시 `$null` 을 반환하고 `Sync-Memory` 가 즉시 `return` 하던 **비대칭** — E: 쪽은 없으면 `New-Item` 으로 만들면서 C: 쪽은 포기했습니다. 06-08 검증(`restored=5`)은 C: 디렉토리가 살아있는 *부분* 복원 상태여서 이 경로를 못 봤고, 결과적으로 **복구가 가장 필요한 순간(완전 소실)에 정확히 무동작**했습니다. 그동안 메모리 recall 은 0건이었습니다.

수정 3점: ① `Get-AutoMemoryDir` 은 부재 시에도 **원형 slug 경로를 반환**(존재하는 대소문자 변형이 있으면 그쪽 우선) — `$null` 을 주지 않는 것이 핵심. ② `Sync-Memory` 는 C: 부재 시 **생성**하고(E: 와 대칭) 결과에 `created` 를 포함. ③ `session-start.ps1` 은 `created` 일 때 "C: 복원/초기화 감지" 경고를 띄움(이전엔 `absorbed`/`restored` 가 둘 다 0 이면 **침묵**해서, 무동작과 정상 무변화가 구분되지 않았음). 실측: `created=True`·`restored=7`·`absorbed=0`, E:↔C: **7/7 SHA256 MATCH**, `Copy-Item` mtime 보존 재확인.

### 영구 보존되는 메모리

> 표기 주의 — 아래 이름은 wikilink/표시용 **정규화 형태(hyphen)** 이고, 초기 4개만 **실제 파일이 underscore**(`feedback_harness_design.md`, `user_role.md`, `feedback_workflow_rules.md`, `feedback_version_format.md`). Claude Code 의 memory wikilink 가 둘을 정규화해 동일시하므로 본문은 hyphen 으로 적는다. 이후 추가된 메모리는 실파일도 hyphen 이다.

- **`user-role`** — 사용자 프로필 (비개발자 + 바이브 코딩)
- **`feedback-harness-design`** — 하네스 디자인 결정
- **`feedback-workflow-rules`** — 빌드 = 둘 다, 커밋 = push 까지
- **`feedback-version-format`** — 4-part 버전 형식
- **`os-dependent-accept`** — 제어 불가 OS(Win32/셸) 동작 의존 버그는 무리한 수정보다 감수
- **`tool-limit-verify-first`** — 제어 **가능한** 제약은 "못 한다" 단정 전 저비용 실험으로 재확인
- **`safety-net-verify-in-failure-state`** — hook·복구 로직은 발동 조건(실패 상태)을 만들어 end-to-end 검증
- **`verify-load-bearing-claims`** — 권고를 떠받치는 정량 주장(서브에이전트 실측·코드 주석)은 메인이 조건 바꿔 재현 (2026-07-22 추가)
- **추가 메모리** — 새 결정/규칙을 Claude 가 자동 또는 사용자가 명시적으로 저장

### ⚠️ 주의 — 민감 정보 저장 금지

메모리가 **git 추적 + push 시 GitHub 노출**. 다음은 절대 메모리에 저장하지 마세요:
- 회사 기밀 / 고객 데이터 / PII
- 비밀번호 / API 키 / 토큰
- 내부 URL / IP 주소

`Hide-Secrets` 는 transcript 발췌만 마스킹하고 **메모리 본문은 마스킹 대상 아님**. 사용자 책임.

### C: 복원 후 복구 흐름 (`Sync-Memory` hook 으로 자동)

1. C: 복원 — C: 의 auto-memory 가 14일 전으로 되돌아가거나, **디렉토리째 소실**(프로젝트 디렉토리 전체가 새로 생성되는 경우 포함 — 2026-07-22 실제 발생)
2. (다른 장비 작업분 있으면) `git pull` 로 E: 백업 최신화
3. `claude` 실행 → **SessionStart 의 `Sync-Memory` 가 E:(truth) → C: 미러로 자동 복구** — 디렉토리가 없으면 **생성부터** 하고 복구 (옛 C: 는 mtime 비교로 흡수 안 됨 → 최신 E: 안전)
4. SessionStart hook 컨텍스트와 함께 사용자 프로필/규칙 복원. `created=$true` 면 "C: 복원/초기화 감지" 경고가 함께 표시됨 — 복구 건수를 확인할 것

### 사라지지만 docs/sessions/ 가 보완하는 것

C: 복원 시 사라지는 다른 항목 — 본 하네스는 처리하지 않으며 사용자가 의식적으로 관리:
- `~/.claude/projects/.../*.jsonl` (과거 대화 transcript) — docs/sessions/ 요약이 컨텍스트 회복용 대체
- Claude Code 로그인 (`credentials.json`) — 재로그인
- Claude Code 본체, PowerShell 7+, .NET 10 SDK, git — 재설치 (winget)

## 13. 변경 절차

이 하네스 자체를 수정할 때:
1. `.claude/settings.json` 이나 `.claude/agents/*` / `.claude/skills/*` / `.claude/hooks/*` 변경
2. **post-edit-doc-sync hook 이 이 파일(docs/harness.md) 갱신 reminder 발생** → 이 파일도 같이 업데이트
3. `git diff` 로 변경 확인 후 commit
4. 다른 장비에서 `git pull` 하면 자동 적용
