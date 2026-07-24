# Claude Code 하네스 — KoEnVue

KoEnVue 의 바이브 코딩 워크플로우를 위한 Claude Code 하네스 구성 전체 레퍼런스. CLAUDE.md 는 P1–P6 규칙만 담고, 하네스 운영 규칙은 모두 여기에.

## 0. 첫 사용 가이드 (비개발자 시각)

**이게 뭐예요?** Claude Code 는 명령줄에서 도는 AI 보조 도구입니다. "하네스" 는 그 도구가 KoEnVue 프로젝트에서 일관되게 동작하도록 잡아주는 설정 묶음 — 모델은 Opus(빠른 출력 모드), 작업 깊이는 상황에 맞춰 조정(기본 effort high), 코드 바꾸면 문서 동기화 안내, 큰 작업만 여러 AI 가 나눠서 교차검증(ultracode 수동 호출), 세션 끝나면 자동 저장 등.

**왜 필요해요?** 매번 같은 설명을 다시 안 해도 되고, 다른 장비로 옮겨도 작업이 이어집니다. 잊을 만한 안전망(자동 wip 커밋, 비밀번호 마스킹) 도 자동으로 잡습니다.

**처음 써보기 — 5단계**:
1. **터미널에서 `claude` 실행** → 화면 아래 statusLine 에 `[opus · high] | git:main* | 한/En 하네스` 같은 표시가 보이면 하네스 활성
2. **`/harness-status` 입력** → 모델/effort, 서브에이전트 6명, 오늘 세션 파일, dirty tree, hook 에러 정상 여부를 한눈에 확인
3. **자연어로 작업 요청** — 예: "한 레이블 색을 빨강으로 바꿔줘". 하네스가 effort high 로 처리하고, 필요하면 서브에이전트(explorer, planner 등)에 위임. 코드리뷰·감사·릴리즈 같은 큰 작업은 `/release-review` 처럼 워크플로우를 수동 호출
4. **작업 마무리할 때 `/wrap-up`** → 문서 동기화 + 세션 요약을 `docs/sessions/YYYY-MM-DD.md` 에 자동 기록
5. **다른 장비로 옮기기** → `git push` 후 다른 장비에서 `git pull` → `claude` 실행 → SessionStart 가 자동으로 어제 작업을 컨텍스트에 주입

용어 풀이:
- **서브에이전트 (subagent)**: 메인 대화를 깔끔하게 유지하려고 특정 작업을 위임받는 보조 Claude. 예: 코드 검색은 explorer 에게.
- **hook**: 특정 사건(세션 시작, 코드 수정 등) 때 자동 실행되는 PowerShell 스크립트.
- **slash command**: `/이름` 으로 실행하는 미리 정의된 작업.
- **ultracode / 워크플로우**: 복잡한 작업을 여러 보조 Claude 가 병렬로 나눠 처리하고 서로 교차검증하는 멀티에이전트 모드. 큰 작업에서 `/<name>` 으로 **수동 호출**(2026-07-24 재구성 전엔 매 턴 자동 발동).

## 1. 디자인 원칙

| 결정 | 내용 | 이유 |
|------|------|------|
| 모델 | `opus` (Opus 4.8) + `fastMode: true` | 최고 성능 모델을 유지하되 **fast mode 로 빠른 출력**(모델 다운그레이드 아님 — schemastore 스키마의 boolean 키로 확인). model/effort 는 statusLine payload(`payload.effort.level`)로 전달됨 — 미수신 시 statusline.ps1 이 env→`high` 폴백 |
| Effort | `effortLevel: high` (settings) | 2026-07-24 재구성으로 **`max`→`high`**, env `CLAUDE_CODE_EFFORT_LEVEL=max` **제거**. 스키마 재확인: `max`/`ultracode` 값은 **session-only 라 settings 파일에선 무효**(파일 최대 유효값 `xhigh`) — 파일로는 영속 불가라 종전엔 env 로 max 를 강제했으나, 15K 라인 유지보수 단계엔 상시 최대가 과해 `high` 기본으로 낮춤. 특정 작업에 더 깊이가 필요하면 그때 승격 |
| Thinking | `alwaysThinkingEnabled: true`, `showThinkingSummaries: true` | thinking 은 유지하되 **매 턴 ultrathink 강제 주입은 제거** — effort high 에 맞춘 **적응형**(단순 작업은 가볍게, 복잡한 작업은 깊게) |
| **ultracode** | **큰 작업만 수동** — 매 턴 주입하던 `inject-turn-context` hook **삭제**, 큰 작업(리뷰·감사·릴리즈·설계비교·버그헌트)만 워크플로우 `/<name>` 수동 호출 | 매 작업 6+ 에이전트 fan-out 은 이 규모(1인·유지보수)에 과잉. 일상은 solo + 필요 시 서브에이전트 |
| 서브에이전트 effort | 본문 첫 단락 문구 + **모델 차등**(agents `model:`) | explorer=haiku·verifier=sonnet 은 균형 문구, planner/reviewer/docs-keeper/historian 은 opus(inherit) 유지. 매 턴 주입 hook 이 없어져 본문 문구가 서브에이전트 깊이의 단일 보장 |
| 병렬 | 단일 세션 + 서브에이전트 + **Workflow 도구**(ultracode). Agent Team(TeamCreate)만 미사용 | Workflow 는 결정론적·resume·budget 지원이라 도입. Agent Team 은 토큰 3–5배·resume 미지원·동시 1팀만이라 계속 제외 |
| 권한 | `bypassPermissions` 전체 허용 | 사용자가 직접 git 으로 책임. 속도 우선 |
| PR | main 직커밋, PR 없음 | 1인 프로젝트 기존 흐름 존중 |
| **빌드** | **debug + release publish 항상 둘 다** | 한쪽만 하면 release exe outdated — verifier 가 강제 |
| **커밋** | **`git commit` 후 즉시 `git push` 자동** | Stop hook(턴 끝 1회) + SessionEnd 양쪽에서. 다른 장비 즉시 받기 |
| 언어 | UI/대화 한국어, 코드/커밋 메시지/PR 영어 | P2 + 외부 협업 친화 |
| 히스토리 | 세션 요약 + 핵심 결정을 `docs/sessions/YYYY-MM-DD.md` | 다른 장비에서 사람·Claude 모두 읽기 쉬움 |
| .claude/ git | 일부 추적 (settings·agents·skills·hooks 만) | 장비 간 하네스 공유 |
| CLAUDE.md | ≤30 줄 하드 제한 | InstructionsLoaded hook 경고. 줄 제한 상수는 `_common.ps1` 의 `$ClaudeMdLineLimit` 단일 진실원 (size-check hook + harness-status 공유) |

> **2026-07-24 균형 재구성**: 프로젝트가 15K 라인 성숙 유지보수 단계(v0.9.9.x)에 들어서며 "작업 시간이 너무 오래 걸린다"는 사용자 요청 → AskUserQuestion 으로 "균형" 방향 승인. effort max→high, fast mode 추가, ultracode 상시→큰 작업 수동, 매 tool call hook→턴당 1회(Stop) 통합, 서브에이전트 모델 차등(explorer=haiku·verifier=sonnet). 기술은 3자 검증(claude-code-guide + schemastore 공식 스키마 + 기존 메모리).

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
│   ├── lib/_common.ps1        공통 함수 + 공유 상수 ($ClaudeMdLineLimit, Hide-Secrets, Invoke-HookSafely, Write-HookError, Add-SessionBlock, Invoke-Push, Invoke-WipCommit, Sync-Memory, Get-AutoMemoryDir, Get-DocSyncReminders, Test-WorkflowPhaseDrift, Test-WorkflowSyntax, Get-PorcelainStatus)
│   ├── session-start.ps1      SessionStart — 이전 요약 주입 + push 안 한 commit 알림 + Sync-Memory
│   ├── pre-compact.ps1        PreCompact — 압축 마커 append + git 스냅샷 additionalContext
│   ├── stop-record.ps1        Stop — 턴 끝 1회: 발췌 append + doc-sync 리마인더 + auto-push + 워크플로우 정합검사 (구 post-edit-doc-sync·auto-push 통합)
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

**effort 정책 — 본문 명시 + 모델 차등**: 매 턴 주입 hook(inject-turn-context)이 재구성으로 사라져, 각 서브에이전트의 깊이는 [.claude/agents/*.md](../.claude/agents/) **본문 첫 단락 문구**와 **frontmatter `model:`** 로 정해집니다. 모델은 작업 성격에 맞춰 차등 — **explorer=haiku**(경량 탐색)·**verifier=sonnet**(빌드/테스트 판독)은 균형 문구, **planner/reviewer/docs-keeper/historian=inherit(opus)** 는 "깊이 우선" 문구 유지. 이전의 "6개 전부 ultrathink+max effort 강제" 규약은 이 모델 차등으로 완화됐습니다.

| 에이전트 | 호출 시점 | 도구 |
|---------|----------|------|
| **explorer** | "X 가 어디?" 같은 read-only 조사가 3쿼리 이상이거나 여러 위치 | Read, Glob, Grep, Bash |
| **planner** | 다중 파일 / 새 기능 / P규칙 영향 변경 전 (구현 안 함) | Read, Glob, Grep, Bash |
| **reviewer** | 코드 변경 후 commit 직전 — P규칙 invariant + 빌드 + 품질 | Read, Glob, Grep, Bash |
| **docs-keeper** | 코드/설정 변경 후 docs/ 동기화 — Stop hook(턴 끝)의 doc-sync 리마인더 또는 `/sync-docs` 가 신호 | Read, Edit, Write, Glob, Grep, Bash |
| **historian** | 세션 정리 — `/wrap-up` 또는 SessionEnd 후속 | Read, Write, Edit, Bash |
| **verifier** | release 전 / 큰 변경 후 — `dotnet build`/`publish`/`test` (release-review 의 Build phase 노드로도 호출) | Bash, Read, Glob, Grep |

전체 정의는 [.claude/agents/*.md](../.claude/agents/) 참조. **invariant grep 단일 진실원**: reviewer 는 grep 명령을 자체 보유하지 않고 [docs/conventions.md](conventions.md) 를 매 호출마다 새로 Read 해 전수 추출 (방법 A) — 현재 알려진 5 위치 (§P6 verification invariants, §P6 Additional sub-rule, §Silent catch §8 Core↔Logger, §Silent catch §9 Debug "failed", §AOT Verification). 자세한 추출 규칙은 [.claude/agents/reviewer.md §0](../.claude/agents/reviewer.md) 참조 — drift 방지.

### ultracode — 멀티에이전트 워크플로우 (큰 작업만 수동)

서브에이전트가 "단일 작업 위임"이라면, **ultracode 는 한 작업을 여러 에이전트로 쪼개 병렬·교차검증하는 오케스트레이션**입니다. 2026-06-08 전면 도입 (인터뷰: "비용 무제한·깊이 최우선" 을 멀티에이전트로 확장).

**effort 와 별개 축**: ultracode 는 effort 레벨(low/…/high/xhigh)이 아니라 그 위의 Workflow 오케스트레이션입니다. 기본 effort 는 `high`(settings) — 워크플로우를 돌려도 이 축은 그대로입니다. (재구성 전엔 env `CLAUDE_CODE_EFFORT_LEVEL=max` + 매 턴 키워드 주입으로 둘 다 상시 유지했으나, 지금은 effort=high 기본 + 워크플로우 수동 호출로 분리.)

**발동 — 큰 작업만 수동**: 재구성으로 매 턴 주입하던 `inject-turn-context.ps1` hook 이 삭제돼, 워크플로우는 이제 **메인 세션(또는 사용자)이 판단해 수동 호출**합니다 — 코드 리뷰·전체 감사·릴리즈 점검·버그/레이스 헌트·설계 비교처럼 여러 관점의 교차검증이 실익인 큰 작업만. 일상의 편집·대화·단일 조회는 solo(필요 시 단일 서브에이전트). 워크플로우 카탈로그는 여전히 `.claude/workflows/*.js` 파일시스템이 **단일 진실원** — 저장 즉시 `/<name>` 슬래시로 노출됩니다(§6).

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

**호출 경로 — 역할분담**: 저장된 워크플로우가 `agentType` 으로 실제 노드 호출하는 서브에이전트는 **explorer / planner / reviewer / verifier**(explorer=harness-optimize Inspect·codebase-audit Scope, planner=design-compare Propose, reviewer=release-review Review·codebase-audit Gate, **verifier=release-review Build**). bug-hunt 의 `agent()` 는 `agentType` 미지정(기본 워크플로우 에이전트). **docs-keeper / historian 만 워크플로우 노드가 아닌 메인 세션 위임 전용**(Stop hook doc-sync 리마인더·`/sync-docs` / `/wrap-up` 등)입니다 — `README.md` 의 `agentType` enum 에 전 서브에이전트가 열거돼 있어도 노드로 쓰이는 건 위 4개. "서브에이전트는 워크플로우 노드로 호출"을 전체로 일반화하지 마세요. (1차에선 verifier 도 메인세션 위임 전용으로 적었으나, release-review 에 Build 게이트가 추가되며 노드로 승격.)

**meta↔phase 자동 가드**: `.claude/workflows/README.md` 의 "meta.phases 의 title ↔ 본문 `phase('X')` 1:1" 규약을 `_common.ps1` 의 `Test-WorkflowPhaseDrift` 가 정규식 휴리스틱으로 기계 검증합니다. `/harness-status` 의 `## 워크플로우 무결성` 섹션이 매 진단 시 호출 — 불일치 워크플로우(meta-only / body-only phase)를 보고하고, 전부 일치면 "✅ 정합"(현재 drift 0).

**JS 정적 문법 가드**: phase drift(의미 정합)와 별개로 `_common.ps1` 의 `Test-WorkflowSyntax` 가 워크플로우 `.js` 의 **순수 문법**을 검사합니다 — `check-workflow-syntax.cjs` 가 node 로 본문을 `AsyncFunction`(async 함수 본문)으로 파싱(실행 안 함)해 SyntaxError 만 검출. 워크플로우 본문은 런타임이 async 로 실행하므로 top-level `await`/`return` 이 합법인데 `node --check` 는 이를 오탐 → AsyncFunction 파싱은 await/return 둘 다 허용하고 ESM `export` 만 제거하면 포맷이 일치해 **오탐 0**. 이로써 종전 "정적 문법검사 불가" 한계는 해소(단 phase 실제 실행·`agent()` 호출 등 **런타임 의미검증은 여전히 런타임 전용**). node/스크립트 부재 시 침묵 skip.

**결과 반환 즉시 고정**: 워크플로우 산출(release-review/codebase-audit/harness-optimize 등)은 여러 에이전트 fan-out 으로 생성한 고비용 결과이므로, 메인 세션은 반환 즉시 git-tracked 파일(예: `docs/improvement-plan/AUDIT-YYYY-MM-DD-*.md`)로 박제해 컨텍스트 휘발을 막습니다. [AUDIT-2026-06-08-harness.md](improvement-plan/AUDIT-2026-06-08-harness.md) 가 첫 적용 사례.

**⚠️ 검증 상태**: 워크플로우의 `/<name>` 자동 노출과 `Workflow({name})` 실제 다중 에이전트 fan-out 은 확인됨(2026-06-08 release-review/harness-optimize 실행 시 각 6 에이전트). ultracode "런타임 플래그" 자체를 켜는지는 여전히 미검증이나, 재구성 후엔 큰 작업에서 워크플로우를 **명시적으로 수동 호출**하므로 매 턴 자동 발동에 의존하지 않습니다 — 행동은 수동 호출 + 명시 지시로 보장.

## 4. Hook 라이프사이클

hook 이벤트 5개 (SessionStart · PreCompact · Stop · SessionEnd · InstructionsLoaded) + statusLine 렌더 = pwsh 스크립트 6개. 2026-07-24 재구성으로 `UserPromptSubmit`(inject-turn-context)·`PostToolUse×2`(post-edit-doc-sync·auto-push) 세 hook 을 제거하고 Stop 하나로 통합. 각 hook 의 역할:

### `SessionStart` → `session-start.ps1`
- 가장 최근 `docs/sessions/YYYY-MM-DD.md` 에서 **`## [HH:MM] 세션 정리` 블록만 추출**해 `additionalContext` 로 주입 (정리 블록 없으면 마지막 turn 헤더 3개만 표시 — 잡음 최소화)
- 최근 3일 내 wip 커밋 알림 (5건까지)
- dirty tree 면 알림 (30건 클램프) — `Get-PorcelainStatus`(git status --porcelain **1회**)로 가드+클램프+count 를 한 번에 처리 (이전엔 git 3회 호출)
- 최근 hook 에러 3건 (있으면)
- `Sync-Memory` 로 C:↔E: 메모리 동기화 (§12 참조)
- P1–P6 규칙과 서브에이전트 활용 권장사항 reminder
- **`-FallbackContext`/`-EventName` 안전망**: `Write-HookOutput` 직전에 죽어도 catch 경로가 최소 fallback 컨텍스트("effort high + fast mode + thinking, 큰 작업만 워크플로우" + 이전 세션 포인터) 1줄을 주입 — SessionStart·PreCompact 2곳이 사용(재구성 전엔 삭제된 inject-turn-context 포함 3곳)

### `PreCompact` → `pre-compact.ps1`
- 대화 압축(컴팩션, 자동 컨텍스트 한도 / 수동 `/compact`) **직전** 실행. 긴 작업·큰 워크플로우로 컨텍스트가 빠르게 찰 때 작업 연속성을 보강. matcher `*` 라 auto·manual 둘 다 트리거, `payload.trigger` 로 구분 기록
- **(1) 압축 마커 박제**: 오늘 세션 파일에 `## [HH:MM] compaction (trigger=auto|manual)` 블록 append (`Add-SessionBlock` mutex 로 직렬화) → 압축 지점을 영구 기록 (압축 전 turn 기록이 상세 컨텍스트 원본임을 명시)
- **(2) 연속성 컨텍스트**: `additionalContext` 로 git 스냅샷(미커밋 변경 30건 클램프 + 최근 커밋 5개) + 세션파일 복원 포인터를 주입 → 압축 직후 새 컨텍스트에서 진행 중이던 미커밋 작업의 연속성 즉시 복원 (SessionStart 와 동일 메커니즘). 미커밋 변경은 `Get-PorcelainStatus`(git **1회**)로 축소
- **`-FallbackContext`/`-EventName` 안전망**: SessionStart 와 동형 — `Write-HookOutput` 직전 사망 시 catch 경로가 "압축됨 — 연속성 확인, effort high 유지, 큰 작업만 워크플로우" 1줄 주입 (SessionStart·PreCompact 2곳)

### `Stop` → `stop-record.ps1` (턴 끝 1회 — 재구성 2026-07-24 통합)
턴이 끝날 때 **1회** 실행. 재구성 전 매 편집마다 돌던 `post-edit-doc-sync`(PostToolUse)와 매 셸 호출마다 돌던 `auto-push`(PostToolUse) 두 hook 을 이 Stop 하나로 흡수 — pwsh 콜드스타트(실측 ~245 ms)를 **매 tool call → 턴당 1회**로 줄이는 게 재구성의 핵심 속도 개선. 순서:
1. **마지막 응답 발췌**: transcript `Get-Content -Tail 1000` 클램프 후 마지막 assistant text (400자, Hide-Secrets 마스킹 — 한계는 §9). text 없이 끝난 턴(도구 위임/구조화 출력)은 `(text 응답 없음 …)` 마커를 명시 append — 침묵 실패와 도구-위임 턴 구분. 매 턴 이 마커면 transcript 파싱 점검 신호.
2. **git status 1회**: `git status --porcelain` 한 번으로 dirty 여부 + 변경 파일 목록(30건 클램프)을 뽑아 아래 (3)(4) 가 공용.
3. **doc-sync 리마인더**: 변경 파일을 `_common.ps1` 의 `Get-DocSyncReminders`(구 post-edit-doc-sync 매핑을 흡수·단일화, §5)에 넣어 동기화 대상 docs 산출 — Docs-key 로 중복 제거. 보안 민감 매핑(`Core/Native/`·`app.manifest`·`csproj`·`NuGet.config`)은 Reason 에 `/security-review` 권장/필수 문구 포함.
4. **워크플로우 정합검사**: 변경 파일에 `.claude/workflows/*.js` 가 있으면 `Test-WorkflowPhaseDrift`(meta.phases ↔ 본문 `phase()` 불일치) + `Test-WorkflowSyntax`(변경된 js 만 AsyncFunction 파싱으로 SyntaxError) 즉시 검사.
5. **auto-push**: 미푸시 커밋이 있으면(`Get-UnpushedCommitCount>0`) `Invoke-Push` — "커밋 = 푸시 항상 같이" 를 턴 끝 1회로 구현. 결과(pushed / no-upstream / failed)를 세션 로그·컨텍스트에 기록. upstream 미설정이면 `git push -u origin <branch>` 안내.
6. **세션 로그 append**: `docs/sessions/YYYY-MM-DD.md` 끝에 `## [HH:MM] turn` 블록 append (`Add-SessionBlock` mutex 로 직렬화 — PreCompact/SessionEnd 동시 append 시 블록 인터리브 방지). 발췌 + 문서 동기화 대기 + 워크플로우 경고 + push 결과 + 커밋되지 않은 변경을 담음.
7. **additionalContext 시도**: doc-sync 대상·워크플로우 경고·push 실패를 `hookSpecificOutput.additionalContext` 로 내보내 다음 턴 메인 세션에 노출 시도(Stop 이 미지원이어도 세션 로그(6)가 확실한 기록 — 재구성 후 스모크로 실제 노출 여부 확인 대상).
- **한계**: 세션 발췌는 transcript JSONL 내부 스키마(`type`/`message`/`content`)에 의존 — Claude Code 버전업으로 스키마가 바뀌면 발췌가 깨질 수 있음(위 빈 발췌 마커가 그 조기 신호).

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

**안전망의 안전망 — `-FallbackContext`/`-EventName`**: context-injecting hook 이 `Write-HookOutput` 직전에 죽으면 그 턴의 주입(effort/연속성 컨텍스트)이 통째 증발합니다. `Invoke-HookSafely` 에 `-FallbackContext`(+`-EventName`)를 주면 catch 경로에서 최소 fallback 컨텍스트 1줄을 `hookSpecificOutput.additionalContext` 로 내보내 방어합니다. **session-start·pre-compact 2곳이 이 옵션 사용**(재구성 전엔 삭제된 inject-turn-context 포함 3곳).

## 5. 매핑 — 코드 변경 → 동기화 문서

`_common.ps1` 의 `Get-DocSyncReminders` 매핑 규칙 (Stop hook 이 턴 끝 변경 파일을 넣어 호출 — 재구성 전엔 post-edit-doc-sync.ps1 소유):

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

규칙 순서는 더 좁은 패턴(`Core/Native/`)이 더 넓은 패턴(`Core/`)보다 먼저 매칭되도록 [_common.ps1](../.claude/hooks/lib/_common.ps1) 의 `Get-DocSyncReminders` 에서 보장됨 (first-match-wins, Docs-key 중복 제거).

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
2. Claude 의 모든 `git commit` 은 Stop hook(턴 끝 1회)이 미푸시 커밋을 자동 push
3. 다른 장비에서 `git pull` → 최신 상태
4. `claude` 실행 → `SessionStart` hook 이 최근 세션 요약 + push 안 한 commit 알림 (잊은 경우 대비)
5. (선택) `/resume-session` 으로 상세 컨텍스트 확인

**예외 — 자동 push 안 되는 경우**:
- upstream branch 미설정 (새 branch 첫 push) — 사용자가 `git push -u origin <branch>` 한 번 실행 필요
- 원격이 fast-forward 거부 — 충돌 해결 후 수동 push

**중요한 결정 / 다음 작업 / 위험 신호**는 `historian` 서브에이전트가 `docs/sessions/YYYY-MM-DD.md` 의 `## [HH:MM] 세션 정리` 블록에 기록. 이 블록만 보면 어디서든 이어받을 수 있게.

## 8. 비용 모니터링

2026-07-24 균형 재구성으로 **일상 작업 비용이 대폭 낮아졌습니다** — `bypassPermissions` + Opus(fast mode) + effort **high** + thinking 은 solo 로 도는 보통 작업 기준 완만한 배수이고, 매 턴 ultrathink/ultracode 자동 주입과 매 tool call hook 오버헤드가 사라졌습니다. **비용이 튀는 건 큰 작업에서 워크플로우를 수동 호출할 때뿐** — 그 substantive 작업은 fan-out 으로 수 배~수십 배(워크플로우당 최대 16 동시 / 1,000 누적 에이전트). 일상은 solo·high 로 가볍게, 깊이가 필요한 큰 작업만 의식적으로 워크플로우로.

**상한의 실제 — `budget` 가드는 거의 무실효**: `budget`(total/spent/remaining) 을 실제 읽는 워크플로우는 **bug-hunt 1개뿐**이고, 그조차 `budget.total` 미주입 시(현재 호출은 total 안 넘김) round hard cap(`round < 8`)만 실효 상한입니다. 나머지 4개(release-review·codebase-audit·design-compare·harness-optimize)는 budget 을 안 읽으며 **per-parallel-item cap** 이 유일 상한 — `MAX_VERIFY=25`(release-review)·`MAX_MODULES=24`(codebase-audit)·동시 16(전역). 즉 토큰 총량 가드가 아니라 fan-out 폭주 방지일 뿐. **⚠️ 미검증 단서**: 재구성으로 env `CLAUDE_CODE_EFFORT_LEVEL` 은 제거됐고 effort 는 settings `high` 뿐 — 워크플로우 노드(`agent()`)가 이 effort 를 어느 레벨로 상속하는지는 미검증. 노드 effort 가 낮으면 위 "수 배~수십 배" 추정이 과대일 수 있음. 비용 추정 시 이 단서를 감안.

가시화 수단:
- **`statusLine`**: 매 턴 `[opus · high] | git:main* | 한/En 하네스` 표시
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
- **`.claude/worktrees/` 의 빌드 산출물 누적**: 서브에이전트가 publish 를 돌리면 worktree 안에 ~150 MB 산출물이 남고 정리 안 함. 주기적으로 `/cleanup-worktrees` SKILL 로 일주일 이상 미사용 worktree 제거 권장.
- **ultracode 런타임 활성화 미검증 (큰 작업 수동 호출 시에만 해당)**: 워크플로우 `/<name>` 자동 노출과 `Workflow({name})` 다중 에이전트 fan-out 은 **확인됨**(2026-06-08 각 6 에이전트). ultracode "런타임 플래그" 자체를 켜는지는 미확인이나, 재구성으로 매 턴 자동 발동을 제거하고 큰 작업에서 **수동 호출**하므로 이 미검증에 의존하지 않음. (memory `feedback-harness-design` 참조)
- **워크플로우 비용 (수동 호출 시에만)**: 큰 작업에서 워크플로우를 호출하면 fan-out 으로 토큰이 급증. 재구성 후엔 매 턴 자동이 아니라 **수동 호출 때만** 발생하므로, 비용이 부담되면 워크플로우 대신 solo 또는 단일 서브에이전트(`/plan`·`/sync-docs`)로 처리.
- **statusLine 하드코딩 폴백**: `model`/`effort` 는 statusLine payload(`payload.effort.level`)가 오면 그 값을 쓰되, **payload 미수신 시 하드코딩 폴백이라 런타임 진실이 아님** — model 은 `'opus'` 고정, effort 는 `$env:CLAUDE_CODE_EFFORT_LEVEL` 반영 후 없으면 `'high'`(재구성 후 env 는 미설정이 정상이라 실질 폴백은 `high`; model 은 항상 하드코딩 폴백). statusLine 은 화면 갱신마다 호출돼 가장 빈번하므로 **git 호출을 축소**: branch 는 `.git/HEAD` 를 직접 Read(rev-parse 프로세스 제거 — `ref: refs/heads/X` 파싱, detached 면 짧은 SHA), dirty `*` 는 `git status --porcelain --untracked-files=no` 1회(untracked 제외로 체감 비용 절감, 변경 신호는 보존). settings.json 의 statusLine 에 `timeout: 5` 추가로 렌더 지연 시 조기 차단.
- **PreCompact ↔ Stop 세션파일 동시 append 경합**: 두 hook 모두 `docs/sessions/YYYY-MM-DD.md` 끝에 `Add-Content` 로 블록을 붙임. 컴팩션과 턴 종료가 거의 동시에 발생하면 같은 파일 동시 쓰기로 블록이 섞이거나 누락될 가능성(append-only·저빈도라 실측 피해 미관측, OS 파일락 의존이라 §OS 감수 정책 대상).
- **transcript JSONL 스키마 의존**: stop-record 의 세션 발췌는 transcript 내부 스키마(`type`/`message`/`content`)에 의존 — Claude Code 버전업 시 깨질 수 있음(§4 Stop 의 빈 발췌 마커가 조기 신호).
- **verify 공통 환각 (적대적 검증의 사각)**: release-review·bug-hunt 의 적대적 검증은 **동일 model·동일 코드 재독** 기반이라, finder 가 코드를 잘못 읽어 생긴 **공통 환각**(예: 실제로 없는 레이스를 양쪽 다 "있다"고 봄)은 못 거른다 — 검증자도 같은 오독을 반복하기 때문. **빌드 게이트 같은 객관 신호**(컴파일/테스트 통과)가 이 사각을 일부 보완하지만, 동작 차원의 공통 환각은 사람 손(`/run`·`/verify`)이 최종 방어선.
- **워크플로우 결과 박제는 순수 메인세션 규율 — hook 안전망 없음**: §3 의 "결과 반환 즉시 git-tracked 파일로 박제"는 메인 세션의 규율일 뿐 hook 으로 강제·백업되지 않는다. 박제 **전에 컴팩션**이 일어나면 fan-out 으로 생성한 고비용 워크플로우 결과가 통째 휘발(전손) 가능 — PreCompact 의 연속성 컨텍스트는 git 스냅샷·세션 포인터만 담고 in-flight 워크플로우 산출은 못 살린다. 고비용 결과는 **받는 즉시** 박제(컴팩션 기다리지 말 것).
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
2. **Stop hook(턴 끝)의 doc-sync 리마인더가 이 파일(docs/harness.md) 갱신 신호** → 이 파일도 같이 업데이트
3. `git diff` 로 변경 확인 후 commit
4. 다른 장비에서 `git pull` 하면 자동 적용
