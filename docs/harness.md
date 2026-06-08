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
| 모델 | `opus` (Opus 4.8) | "비용 무제한, 깊이 최우선" |
| Effort | `max` (settings 명목) + `CLAUDE_CODE_EFFORT_LEVEL=max` (env, 실효) | 문서상 settings 는 xhigh 까지만 공식 수락하나 의도 명시를 위해 `max` 표기. env 가 실제 max 강제 — silent ignore / fallback 시에도 결과 동일 |
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
| CLAUDE.md | ≤30 줄 하드 제한 | InstructionsLoaded hook 경고 |

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
├── scratch/                   ❌ ignored (디버깅 임시 ps1)
├── hooks/                     ✅ committed
│   ├── lib/_common.ps1        공통 함수 (Hide-Secrets, Invoke-HookSafely, Invoke-Push 포함)
│   ├── inject-turn-context.ps1 UserPromptSubmit — ultrathink+max effort+ultracode 주입
│   ├── session-start.ps1      SessionStart — 이전 요약 주입 + push 안 한 commit 알림
│   ├── post-edit-doc-sync.ps1 PostToolUse(Edit/Write) — 문서 동기화 리마인더
│   ├── auto-push.ps1          PostToolUse(Bash git commit) — 커밋 후 자동 push
│   ├── stop-record.ps1        Stop — 진행 상황 append (secret 마스킹)
│   ├── session-end.ps1        SessionEnd — wip 커밋 + auto push + 요약 파이널
│   ├── claude-md-size-check.ps1  InstructionsLoaded — 30줄 검사
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
| **verifier** | release 전 / 큰 변경 후 — `dotnet build`/`publish`/`test` | Bash, Read, Glob, Grep |

전체 정의는 [.claude/agents/*.md](../.claude/agents/) 참조. **invariant grep 단일 진실원**: reviewer 는 grep 명령을 자체 보유하지 않고 [docs/conventions.md](conventions.md) 를 매 호출마다 새로 Read 해 전수 추출 (방법 A) — 현재 알려진 5 위치 (§P6 verification invariants, §P6 Additional sub-rule, §Silent catch §8 Core↔Logger, §Silent catch §9 Debug "failed", §AOT Verification). 자세한 추출 규칙은 [.claude/agents/reviewer.md §0](../.claude/agents/reviewer.md) 참조 — drift 방지.

### ultracode — 멀티에이전트 워크플로우 (항상 ON)

서브에이전트가 "단일 작업 위임"이라면, **ultracode 는 한 작업을 여러 에이전트로 쪼개 병렬·교차검증하는 오케스트레이션**입니다. 2026-06-08 전면 도입 (인터뷰: "비용 무제한·깊이 최우선" 을 멀티에이전트로 확장).

**effort 와 별개 축**: ultracode 는 effort 레벨(low/…/max)이 아닙니다. `CLAUDE_CODE_EFFORT_LEVEL=max` 는 그대로 유지되고, ultracode 는 그 위에서 Workflow 오케스트레이션을 켭니다. **env 를 `ultracode` 로 바꾸지 마세요** — max 를 잃을 수 있어, 키워드 주입 방식으로 둘 다 유지합니다.

**발동 — 항상 자동**: `inject-turn-context.ps1` hook 이 매 턴 "ultracode" 키워드 + 행동 지시를 주입. substantive 작업(다중 파일 변경·코드 리뷰·릴리즈 점검·버그/레이스 헌트·설계 비교·하네스 변경)은 Workflow 도구로 오케스트레이션하고, trivial 편집·단순 대화·단일 사실 조회만 solo.

**Agent Team 과의 구분**: Workflow 도구 ≠ Agent Team(TeamCreate). Workflow 는 결정론적 제어흐름(loop/조건/fan-out)·resume(`resumeFromRunId`)·토큰 budget 을 지원해 도입. Agent Team 은 토큰 3–5배·resume 미지원이라 **계속 거부** — "단일 세션" 철학과 충돌 없음.

**저장 워크플로우 5개** (`.claude/workflows/*.js`). 저장 즉시 `/<name>` 슬래시 커맨드로 자동 노출되며, 메인 세션은 `Workflow({ name })` 로 호출:

| 워크플로우 | 무엇을 | 패턴 |
|-----------|--------|------|
| `release-review` | 릴리즈 전 correctness·보안·P1~P6·동시성 병렬 리뷰 → 적대적 검증 | pipeline + adversarial verify |
| `bug-hunt` | 동시성·레이스·견고성 결함을 안 나올 때까지 반복 탐색 | loop-until-dry + 다관점 렌즈 |
| `codebase-audit` | App/·Core/ 모듈 전수 병렬 점검 → P규칙 게이트 → AUDIT 종합 | scope→audit→gate |
| `design-compare` | 기능 설계를 N접근법 제안 → 점수화 → 합성 (`args.feature` 필수) | judge panel |
| `harness-optimize` | 하네스 구성요소 점검 → completeness critic | inspect + critic |

각 워크플로우는 KoEnVue 서브에이전트(explorer/planner/reviewer)를 `agentType` 으로 재사용하고 `schema` 로 구조화 출력을 강제합니다. 예: `Workflow({ name: 'release-review', args: { scope: 'PR-26 변경' } })`.

**leaf vs 오케스트레이터**: 6개 서브에이전트의 `tools:` 에는 위임 도구가 없습니다(leaf). 오케스트레이션은 메인 세션 또는 워크플로우 스크립트가 담당하고, 서브에이전트는 워크플로우의 노드로 호출됩니다.

**⚠️ 검증 상태**: 워크플로우의 `/<name>` 자동 노출은 확인됨. 다만 hook 의 키워드 주입이 ultracode **런타임 플래그**를 켜는지는 미검증(ultrathink 와 달리 ultracode 는 세션 설정일 수 있음). 명시적 한국어 지시가 fallback 이라 행동은 보장되지만, 새 세션에서 statusLine 의 `ultracode` 표시로 확인하세요. 런타임 활성화가 안 되면 세션 시작 시 `/effort ultracode` 수동 입력이 대안(단 effort=max 와의 우선순위는 별도 확인).

## 4. Hook 라이프사이클

각 hook 의 역할:

### `SessionStart` → `session-start.ps1`
- 가장 최근 `docs/sessions/YYYY-MM-DD.md` 에서 **`## [HH:MM] 세션 정리` 블록만 추출**해 `additionalContext` 로 주입 (정리 블록 없으면 마지막 turn 헤더 3개만 표시 — 잡음 최소화)
- 최근 3일 내 wip 커밋 알림 (5건까지)
- dirty tree 면 알림 (30건 클램프)
- 최근 hook 에러 3건 (있으면)
- P1–P6 규칙과 서브에이전트 활용 권장사항 reminder

### `UserPromptSubmit` → `inject-turn-context.ps1`
- 사용자 입력에 `ultrathink` 가 없으면 — **"ultrathink + thinking 모드"** + **"항상 max effort — 단축/생략 없이"** 주입
- 사용자 입력에 `ultracode` 가 없으면 — **ultracode 멀티에이전트 모드** 주입 (키워드 + 행동 지시 + 워크플로우 5종 카탈로그). 두 축 독립 — 해당 키워드가 입력에 있으면 그 축만 skip
- effort=max 는 env(`CLAUDE_CODE_EFFORT_LEVEL=max`)로 별도 강제 — ultracode 가 effort 를 대체하지 않음. 서브에이전트 본문 강제 명시와 함께 effort 단축 경로 0 보장

### `PostToolUse(Edit|Write|NotebookEdit)` → `post-edit-doc-sync.ps1`
- 변경된 파일 경로를 매핑 테이블에 대조. rules 배열은 **first-match-wins** — 더 좁은 패턴 `^Core/Native/` 가 일반 `^Core/` 보다 먼저 정의돼 우선 매칭됨.
- 영향받는 docs 목록을 이번 턴 컨텍스트로 추가
- **보안 민감 매핑**: `Core/Native/` (P/Invoke 시그니처), `app.manifest` (UAC 레벨), `KoEnVue.csproj` (NuGet 추가/제거), `NuGet.config` (외부 피드) 변경 시 Reason 메시지에 `/security-review` 권장/필수 문구 자동 포함 — Claude Code 의 built-in 슬래시 커맨드로 보안 점검 유도.
- `.claude/state/pending-docs.txt` 에 기록 → Stop hook 에서 사용
- **중복 reminder 억제**: 같은 매핑(예: `App/*` → `docs/architecture.md`)이 한 턴에 여러 번 trigger 되면, 첫 번째만 컨텍스트 reminder. pending-docs.txt 에는 모든 파일 기록 (Stop hook 에서 다 표시).

### `PostToolUse(Bash git commit *)` → `auto-push.ps1`
- "**커밋 = 푸시 항상 같이**" 규칙 구현
- Claude 가 `git commit` 명령을 호출하면 즉시 발동 → `git push` 자동 실행
- upstream 미설정 시 (`-u origin <branch>` 없이 처음 push) → skip + 사용자에게 알림
- push 실패 (원격 거부 / 네트워크) → 컨텍스트에 실패 알림
- `--dry-run`, `--allow-empty-message`, `--no-edit` 같은 비-실 커밋 패턴은 skip

### `Stop` → `stop-record.ps1`
- transcript `Get-Content -Tail 1000` 클램프 후 마지막 assistant 응답 발췌 (400자)
- **secret 마스킹** (Hide-Secrets 함수) 후 기록 — 자세한 한계는 §9 참조
- 이번 턴의 pending-docs 와 dirty tree 상태 정리 (30건 클램프)
- `docs/sessions/YYYY-MM-DD.md` 끝에 `## [HH:MM] turn` 블록 append

### `SessionEnd` → `session-end.ps1`
- dirty tree 가 있으면:
  1. **먼저** 오늘 세션 파일에 `## [HH:MM] session-end (reason)` 블록 append (이 세션의 최근 10분 커밋 목록 + "방금 wip — 이 마무리 블록 포함" 한 줄)
  2. **그 다음** `wip: session YYYY-MM-DD HH:MM — session end (reason)` 커밋 — block 변경분 + 기존 dirty 가 같은 wip 커밋에 묶임 (다음 세션 시작 시 dirty 잔여물 0 보장)
- dirty tree 가 없으면 nothing — 마무리 블록도 안 적고 wip 커밋도 만들지 않음 (잡음 0)
- 위 처리 후 unpushed commit 이 있으면 `git push` 자동 시도 ("커밋 = 푸시 항상 같이" fallback)

### `InstructionsLoaded` → `claude-md-size-check.ps1`
- 로드된 파일이 `CLAUDE.md` 이면 줄 수 검사
- 30줄 초과면 경고 컨텍스트 주입

**안전망**: 모든 hook 은 `_common.ps1` 의 `Invoke-HookSafely { ... }` 로 감싸져 있어 에러가 transcript 에 새지 않고 `.claude/state/hook-errors.log` 에 기록 (100줄 초과 시 자동 rotation — 마지막 50줄만 유지).

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

## 6. 슬래시 커맨드

`/<name> [args]` 로 호출:

| 커맨드 | 무엇을 함 |
|--------|----------|
| `/plan <작업 설명>` | planner 서브에이전트로 설계 위임 |
| `/sync-docs` | docs-keeper 서브에이전트로 문서 동기화 |
| `/resume-session` | 다른 장비에서 이어 작업 시 — 최근 세션과 git 상태 정리 후 다음 작업 제안 |
| `/wrap-up` | 세션 마무리 — docs-keeper + historian 호출, dirty tree 정리 |
| `/harness-status` | 하네스 현재 상태 한눈에 — 모델/effort, hook 동작, 서브에이전트 수, 오늘 세션, dirty tree, 최근 hook 에러 |
| `/cleanup-worktrees` | `.claude/worktrees/` 의 일주일 이상 미사용 디렉토리 정리 (빌드 산출물 ~GB 누적 방지) |

각 명령은 `.claude/skills/<name>/SKILL.md` 에 정의. Skill 형식은 Claude Code 의 최신 권장. 향후 supporting files 가 필요해지면 같은 디렉토리에 추가 가능 (예: `.claude/skills/release/scripts/build.ps1`).

### Claude Code built-in 명령 — 함께 활용

위 5개 외에 Claude Code 표준 명령도 사용 가능:

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

`bypassPermissions` + Opus + max effort + thinking + 매 턴 ultrathink/ultracode 주입 = 보통 코딩 작업의 약 8~15배, **ultracode 워크플로우가 도는 substantive 작업은 추가로 수 배~수십 배**(워크플로우당 최대 16 동시 / 1,000 누적 에이전트) 토큰 소비. 사용자가 "비용 무제한, 깊이 최우선" 선택했음 (인터뷰 결과). 하네스는 자동 하향 조정 안 함. 워크플로우는 `budget` 가드로 과소비를 일부 제어 (bug-hunt 등).

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
- **inject-turn-context hook 오버헤드**: 매 UserPromptSubmit 마다 PowerShell 프로세스 생성. 측정(Win): `Measure-Command { '{"prompt":""}' | pwsh -NoProfile -ExecutionPolicy Bypass -File .claude/hooks/inject-turn-context.ps1 }`. 실측 **~381 ms** (Opus 4.7, 2026-05-22, inject-ultrathink 시절 측정, fresh pwsh 시동 1회). ultrathink+ultracode 매 턴 주입 안전망이지만, 빠른 응답을 원할 때 부담.
- **`.claude/worktrees/` 의 빌드 산출물 누적**: 서브에이전트가 publish 를 돌리면 worktree 안에 ~150 MB 산출물이 남고 정리 안 함. 주기적으로 `/cleanup-worktrees` SKILL 로 일주일 이상 미사용 worktree 제거 권장.
- **ultracode 런타임 활성화 미검증**: `inject-turn-context.ps1` 의 키워드 주입이 ultracode "런타임 플래그"를 켜는지만 미확인 — 워크플로우 `/<name>` 자동 노출과 `Workflow({name})` 실제 다중 에이전트 fan-out 은 **확인됨**(2026-06-08 release-review/harness-optimize 실행 시 각 6 에이전트). 행동은 명시적 지시 + 워크플로우 실행으로 보장됨. ⚠️ statusLine 의 `ultracode` 는 항상 하드코딩 표시라 **런타임 검증 신호가 아님** — 검증은 `Workflow` 실제 fan-out 로그로. (memory `feedback-harness-design` 참조)
- **ultracode 비용 급증**: substantive 작업마다 워크플로우 fan-out → 토큰 급증. 빠르고 싼 처리를 원하는 turn 은 작업이 trivial 함을 프롬프트에 명시하거나 워크플로우를 건너뛰도록 지시.

## 11. PR 분리 시 충돌 회피 정책

여러 PR 이 동시에 open 상태로 진행되는 분기에서, 같은 파일의 인접 영역을 건드리면 머지 충돌이 발생합니다. 본 정책은 docs-keeper [§ Step 0](../.claude/agents/docs-keeper.md) 의 사전 점검을 하네스 수준으로 끌어올린 것입니다 — 학습 트리거: 한 세션에서 PR #3 vs PR #4 의 dev-note 충돌이 본 점검 부재로 발생.

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

**그러나 2026-06-08 harness-optimize 점검에서 이 설정이 실제로는 무시되고 있음을 발견**했습니다 — `${CLAUDE_PROJECT_DIR}` 가 빈 값으로 전개돼(`autoMemoryDirectory` 가 무효) Claude Code 가 기본 위치 **C: 에 읽기/쓰기 중**입니다. 즉 E: 의 `.claude/memory/` 는 git 백업이고 Claude 가 직접 읽는 건 C: 입니다. **이 split-brain 은 아래 `Sync-Memory` hook 으로 해결**(2026-06-08) — C: 의 최신 메모리가 매 세션 E:(git) 로 백업되고, 복원 시 E: 에서 C: 로 자동 복구됩니다.

| 항목 | 실태 (2026-06-08) |
|------|------|
| 실제 auto-memory 위치 | **C:** `C:\Users\<user>\.claude\projects\e--dev-KoEnVue\memory\` (설정 무시됨) |
| E: `.claude/memory/` | git 추적 백업 — Claude 가 직접 읽진 않음 |
| `autoMemoryDirectory` 설정 | `${CLAUDE_PROJECT_DIR}/.claude/memory` — 전개 실패로 **무효** |
| 복원 영향 | ✅ `Sync-Memory` hook 으로 보호 — 복원된 C: 는 매 SessionStart 에 E:(git)에서 복구 |

**임시 구제 (적용됨)**: C: 에만 있던 `os-dependent-accept.md` 를 E: 로 복사 + MEMORY.md 갱신 + commit (소실 방지). 새 메모리 저장 시 수동으로 E: 에도 반영 권장.

**근본 해결 (적용됨 2026-06-08)**: SessionStart hook 의 `Sync-Memory`(`_common.ps1`)가 매 세션 C:↔E: 동기화 — **E: 가 truth**(복원 무관), C: 의 더 새 파일만 E: 로 흡수(복원된 옛 C: 가 최신 E: 를 못 덮게 mtime UTC 비교), 그 뒤 E:→C: 미러로 복원된 C: 를 최신 복구. slug=`e--dev-KoEnVue`·`Copy-Item` mtime 보존 검증 완료, 최초 실행 시 `restored=5` 로 현재 split-brain 해소(C:=E: 해시 일치). `absorbed>0`(C: 에 새 메모리) 시 SessionStart 가 커밋 권장 알림 → 다음 commit 에 E: 백업 포함.

### 영구 보존되는 메모리

- **`user-role`** — 사용자 프로필 (비개발자 + 바이브 코딩)
- **`feedback-harness-design`** — 하네스 디자인 결정
- **`feedback-workflow-rules`** — 빌드 = 둘 다, 커밋 = push 까지
- **`feedback-version-format`** — 4-part 버전 형식
- **추가 메모리** — 새 결정/규칙을 Claude 가 자동 또는 사용자가 명시적으로 저장

### ⚠️ 주의 — 민감 정보 저장 금지

메모리가 **git 추적 + push 시 GitHub 노출**. 다음은 절대 메모리에 저장하지 마세요:
- 회사 기밀 / 고객 데이터 / PII
- 비밀번호 / API 키 / 토큰
- 내부 URL / IP 주소

`Hide-Secrets` 는 transcript 발췌만 마스킹하고 **메모리 본문은 마스킹 대상 아님**. 사용자 책임.

### C: 복원 후 복구 흐름 (`Sync-Memory` hook 으로 자동)

1. C: 복원 — C: 의 auto-memory 가 14일 전으로 되돌아가거나 소실
2. (다른 장비 작업분 있으면) `git pull` 로 E: 백업 최신화
3. `claude` 실행 → **SessionStart 의 `Sync-Memory` 가 E:(truth) → C: 미러로 자동 복구** (옛 C: 는 mtime 비교로 흡수 안 됨 → 최신 E: 안전)
4. SessionStart hook 컨텍스트와 함께 사용자 프로필/규칙 복원

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
