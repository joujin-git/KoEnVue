# Claude Code 하네스 — KoEnVue

KoEnVue 의 바이브 코딩 워크플로우를 위한 Claude Code 하네스 구성 전체 레퍼런스. CLAUDE.md 는 P1–P6 규칙만 담고, 하네스 운영 규칙은 모두 여기에.

## 0. 첫 사용 가이드 (비개발자 시각)

**이게 뭐예요?** Claude Code 는 명령줄에서 도는 AI 보조 도구입니다. "하네스" 는 그 도구가 KoEnVue 프로젝트에서 일관되게 동작하도록 잡아주는 설정 묶음 — 모델은 항상 Opus 최강, 매 요청은 깊이 추론(ultrathink), 코드 바꾸면 문서 안내 자동 표시, 세션 끝나면 자동 저장 등.

**왜 필요해요?** 매번 같은 설명을 다시 안 해도 되고, 다른 장비로 옮겨도 작업이 이어집니다. 잊을 만한 안전망(자동 wip 커밋, 비밀번호 마스킹) 도 자동으로 잡습니다.

**처음 써보기 — 5단계**:
1. **터미널에서 `claude` 실행** → 화면 아래 statusLine 에 `[opus · max] | git:main* | 한/En 하네스 ON` 같은 표시가 보이면 하네스 활성
2. **`/harness-status` 입력** → 모델/effort, 서브에이전트 6명, 오늘 세션 파일, dirty tree, hook 에러 정상 여부를 한눈에 확인
3. **자연어로 작업 요청** — 예: "한 레이블 색을 빨강으로 바꿔줘". 하네스가 알아서 ultrathink 모드로 처리하고, 필요하면 서브에이전트(explorer, planner 등)에 위임
4. **작업 마무리할 때 `/wrap-up`** → 문서 동기화 + 세션 요약을 `docs/sessions/YYYY-MM-DD.md` 에 자동 기록
5. **다른 장비로 옮기기** → `git push` 후 다른 장비에서 `git pull` → `claude` 실행 → SessionStart 가 자동으로 어제 작업을 컨텍스트에 주입

용어 풀이:
- **서브에이전트 (subagent)**: 메인 대화를 깔끔하게 유지하려고 특정 작업을 위임받는 보조 Claude. 예: 코드 검색은 explorer 에게.
- **hook**: 특정 사건(세션 시작, 코드 수정 등) 때 자동 실행되는 PowerShell 스크립트.
- **slash command**: `/이름` 으로 실행하는 미리 정의된 작업.

## 1. 디자인 원칙

| 결정 | 내용 | 이유 |
|------|------|------|
| 모델 | `opus` (Opus 4.7) | "비용 무제한, 깊이 최우선" |
| Effort | `max` (settings 명목) + `CLAUDE_CODE_EFFORT_LEVEL=max` (env, 실효) | 문서상 settings 는 xhigh 까지만 공식 수락하나 의도 명시를 위해 `max` 표기. env 가 실제 max 강제 — silent ignore / fallback 시에도 결과 동일 |
| Thinking | `alwaysThinkingEnabled: true`, `showThinkingSummaries: true` | 모든 작업 ultrathink |
| ultrathink 키워드 | UserPromptSubmit hook 으로 매 턴 자동 주입 | 사용자 입력에 누락돼도 보장 |
| 병렬 | 단일 세션 + 항상 서브에이전트 (Agent Team 미사용) | Agent Team 은 토큰 3–5배, resume 미지원, 동시 1팀만 — KoEnVue 규모에 과함 |
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
├── scratch/                   ❌ ignored (디버깅 임시 ps1)
├── hooks/                     ✅ committed
│   ├── lib/_common.ps1        공통 함수 (Hide-Secrets, Invoke-HookSafely, Invoke-Push 포함)
│   ├── inject-ultrathink.ps1  UserPromptSubmit
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

## 3. 서브에이전트 (단일 세션 + 항상 위임)

메인 세션은 가능한 한 서브에이전트에 위임해서 깔끔하게 유지합니다.

| 에이전트 | 호출 시점 | 도구 |
|---------|----------|------|
| **explorer** | "X 가 어디?" 같은 read-only 조사가 3쿼리 이상이거나 여러 위치 | Read, Glob, Grep, Bash |
| **planner** | 다중 파일 / 새 기능 / P규칙 영향 변경 전 (구현 안 함) | Read, Glob, Grep, Bash |
| **reviewer** | 코드 변경 후 commit 직전 — P규칙 invariant + 빌드 + 품질 | Read, Glob, Grep, Bash |
| **docs-keeper** | Edit/Write 후 docs/ 동기화 — PostToolUse hook 이 신호 | Read, Edit, Write, Glob, Grep, Bash |
| **historian** | 세션 정리 — `/wrap-up` 또는 SessionEnd 후속 | Read, Write, Edit, Bash |
| **verifier** | release 전 / 큰 변경 후 — `dotnet build`/`publish`/`test` | Bash, Read, Glob, Grep |

전체 정의는 [.claude/agents/*.md](../.claude/agents/) 참조. **invariant grep 단일 진실원**: reviewer 는 grep 명령을 자체 보유하지 않고 [docs/conventions.md](conventions.md) §P6 invariants 를 참조 — drift 방지.

## 4. Hook 라이프사이클

각 hook 의 역할:

### `SessionStart` → `session-start.ps1`
- 가장 최근 `docs/sessions/YYYY-MM-DD.md` 에서 **`## [HH:MM] 세션 정리` 블록만 추출**해 `additionalContext` 로 주입 (정리 블록 없으면 마지막 turn 헤더 3개만 표시 — 잡음 최소화)
- 최근 3일 내 wip 커밋 알림 (5건까지)
- dirty tree 면 알림 (30건 클램프)
- 최근 hook 에러 3건 (있으면)
- P1–P6 규칙과 서브에이전트 활용 권장사항 reminder

### `UserPromptSubmit` → `inject-ultrathink.ps1`
- 사용자 입력에 `ultrathink` 가 없으면 한국어 컨텍스트로 추가
- "모든 작업은 ultrathink + max effort" 보장

### `PostToolUse(Edit|Write|NotebookEdit)` → `post-edit-doc-sync.ps1`
- 변경된 파일 경로를 매핑 테이블에 대조
- 영향받는 docs 목록을 이번 턴 컨텍스트로 추가
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

`bypassPermissions` + Opus + max effort + thinking + 매 턴 ultrathink 주입 = 보통 코딩 작업의 약 8~15배 토큰 소비. 사용자가 "비용 무제한, 깊이 최우선" 선택했음 (인터뷰 결과). 하네스는 자동 하향 조정 안 함.

가시화 수단:
- **`statusLine`**: 매 턴 `[opus · max] | git:main* | 한/En 하네스 ON` 표시
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
- **inject-ultrathink hook 오버헤드**: 매 UserPromptSubmit 마다 PowerShell 프로세스 생성 (~200-500ms). 측정하려면 `Measure-Command { pwsh -NoProfile -File .claude/hooks/inject-ultrathink.ps1 < /dev/null }` (Win 에선 `$null` 입력). 빠른 응답을 원할 때 부담이지만, "항상 ultrathink" 요구사항을 위한 안전망.
- **`.claude/worktrees/` 의 빌드 산출물 누적**: 서브에이전트가 publish 를 돌리면 worktree 안에 ~150 MB 산출물이 남고 정리 안 함. 주기적으로 `/cleanup-worktrees` SKILL 로 일주일 이상 미사용 worktree 제거 권장.

## 11. Memory 시스템 — E: 드라이브 영구화

이 컴퓨터의 **C: 드라이브는 보안 정책상 수시로 14일 전 시점으로 복원**됩니다. 기본 Claude Code memory 위치 (`~/.claude/projects/<project>/memory/` — C: 드라이브) 는 복원될 때마다 사라지므로, 본 하네스는 **메모리를 프로젝트 트리(E: 드라이브)로 옮겼습니다**.

| 항목 | 위치 |
|------|------|
| 메모리 디렉토리 | `.claude/memory/` (프로젝트 트리 안) |
| 설정 | `settings.json` 의 `"autoMemoryDirectory": "${CLAUDE_PROJECT_DIR}/.claude/memory"` |
| git 추적 | ✅ commit 됨 — 다른 장비도 동일 메모리 |
| 복원 영향 | 0 — E: 드라이브는 복원 대상 아님 |

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

### C: 복원 후 복구 흐름

1. C: 복원 — `.claude/memory/` 는 E: 드라이브라 무손실
2. (다른 장비에서 갱신했다면) `git pull` 로 최신 메모리 받기
3. `claude` 실행 — `autoMemoryDirectory` 가 E: 위치 가리키므로 자동 로드
4. SessionStart hook 의 컨텍스트와 함께 사용자 프로필/규칙 즉시 복원

### 사라지지만 docs/sessions/ 가 보완하는 것

C: 복원 시 사라지는 다른 항목 — 본 하네스는 처리하지 않으며 사용자가 의식적으로 관리:
- `~/.claude/projects/.../*.jsonl` (과거 대화 transcript) — docs/sessions/ 요약이 컨텍스트 회복용 대체
- Claude Code 로그인 (`credentials.json`) — 재로그인
- Claude Code 본체, PowerShell 7+, .NET 10 SDK, git — 재설치 (winget)

## 12. 변경 절차

이 하네스 자체를 수정할 때:
1. `.claude/settings.json` 이나 `.claude/agents/*` / `.claude/skills/*` / `.claude/hooks/*` 변경
2. **post-edit-doc-sync hook 이 이 파일(docs/harness.md) 갱신 reminder 발생** → 이 파일도 같이 업데이트
3. `git diff` 로 변경 확인 후 commit
4. 다른 장비에서 `git pull` 하면 자동 적용
