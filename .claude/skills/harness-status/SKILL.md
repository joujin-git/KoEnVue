---
description: 현재 하네스 활성화 상태 한눈에 — 모델/effort/thinking, hook 동작, 서브에이전트 목록, 오늘 세션 파일, dirty tree, 최근 hook 에러.
allowed-tools: Bash, Read, Glob
shell: powershell
---

> **주의** — 아래 `!` 백틱 셸 명령이 실행 결과가 아니라 명령 문자열 그대로 보이면 자동 실행되지 않은 것입니다(Skill 도구 호출 경로에서 관측). 그때는 **직접 실행한 뒤** 답하세요 — 추측으로 상태를 보고하지 말 것.

## 모델 / 인텔리전스
- settings.json (**설정값** — 데스크탑 앱에선 effortLevel 이 무시되니 정본 아님): `!`Select-String -Path .claude/settings.json -Pattern 'model|fastMode|effortLevel|alwaysThinkingEnabled' | ForEach-Object { $_.Line.Trim() }``
- **실효 effort (정본 — transcript 실측)**: `!`$p = Get-ChildItem "$env:USERPROFILE\.claude\projects\E--dev-KoEnVue\*.jsonl" -EA SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1; if (-not $p) { '(transcript 없음)' } else { $m = @(Select-String -Path $p.FullName -Pattern '"effort":"(\w+)"'); $e = if ($m) { $m[-1].Matches[0].Groups[1].Value } else { '미기록' }; $t = @(Select-String -Path $p.FullName -Pattern '"entrypoint":"([\w-]+)"'); $n = if ($t) { $t[-1].Matches[0].Groups[1].Value } else { '?' }; "실효 effort=$e / entrypoint=$n — 설정값과 다르면 앱이 무시한 것(2026-07-29 xhigh 확인)" }``
- 환경변수 effort override: `!`$v = $env:CLAUDE_CODE_EFFORT_LEVEL; if ([string]::IsNullOrEmpty($v)) { '(미설정 — 2026-07-24 재구성으로 제거됨. 비어 있는 게 정상. 단 settings 값이 곧 실효는 아님 — 위 transcript 실측이 정본)' } else { "CLAUDE_CODE_EFFORT_LEVEL=$v (override 중 — settings 보다 우선)" }``
- ultracode (멀티에이전트): **큰 작업만 수동 호출** — 코드리뷰·감사·릴리즈·설계비교·버그헌트 때 `/release-review` 등 워크플로우 `/<name>` 으로 호출. 일상 작업은 단일 세션 + 필요 시 서브에이전트. (2026-07-24 재구성으로 매 턴 주입하던 `inject-turn-context` hook 삭제 — 없는 것이 정상.)

## 하네스 파일 존재
- 서브에이전트: `!`(Get-ChildItem .claude/agents/*.md -ErrorAction SilentlyContinue).Name -join ', '``
- 스킬: `!`(Get-ChildItem .claude/skills -Directory -ErrorAction SilentlyContinue).Name -join ', '``
- 워크플로우: `!`(Get-ChildItem .claude/workflows/*.js -ErrorAction SilentlyContinue).BaseName -join ', '``
- hook 스크립트: `!`(Get-ChildItem .claude/hooks/*.ps1 -ErrorAction SilentlyContinue).Name -join ', '``
- scratch (임시 프로브): `!`$n = @(Get-ChildItem .claude/scratch -File -ErrorAction SilentlyContinue).Count; if ($n -eq 0) { '0 (정리됨)' } else { "$n 개 — 보안 민감 프로브 누적 가능, 정리 검토" }``
- _common 로드 (안전망의 안전망): `!`pwsh -NoProfile -Command ". .claude/hooks/lib/_common.ps1; if (Get-Command Invoke-HookSafely -EA SilentlyContinue) { 'OK' } else { '실패 — 모든 hook 무력화 위험' }" 2>$null``

## 워크플로우 무결성
- meta↔phase 정합: `!`. .claude/hooks/lib/_common.ps1; $d = Test-WorkflowPhaseDrift; if ($d) { "⚠ drift: $($d -join '; ')" } else { "✅ 정합 (meta.phases ↔ phase() 일치)" }``

## 오늘 세션
- 파일: `!`$f = "docs/sessions/$(Get-Date -Format yyyy-MM-dd).md"; if (Test-Path $f) { "$f ($((Get-Content $f).Count) 줄)" } else { "없음 — 첫 turn 후 자동 생성" }``
- 가장 최근 블록 헤더: `!`$f = "docs/sessions/$(Get-Date -Format yyyy-MM-dd).md"; if (Test-Path $f) { (Select-String -Path $f -Pattern '^## ' | Select-Object -Last 1).Line } else { '없음' }``

## Git
- 브랜치: `!`git rev-parse --abbrev-ref HEAD``
- dirty (요약): `!`$c = (git status --porcelain | Measure-Object).Count; "$c 건"``
- 최근 wip 커밋 (3일): `!`git log --grep='^wip:' --since='3 days ago' --oneline | Select-Object -First 5``

## 최근 hook 에러
- `!`if (Test-Path .claude/state/hook-errors.log) { Get-Content .claude/state/hook-errors.log -Tail 5 } else { '없음 — 모든 hook 정상' }``

## CLAUDE.md 크기
- `!`. .claude/hooks/lib/_common.ps1; $c = (Get-Content CLAUDE.md).Count; "$c 줄 / $ClaudeMdLineLimit 줄 제한"``

---

위 정보를 받으면 사용자에게 한국어로 친절히 정리해주세요. 이상 신호 (hook error 누적, dirty tree 30건 이상, CLAUDE.md 줄 제한 초과, 서브에이전트 수가 6 미만, 스킬 수가 6 미만, 워크플로우 수가 5 미만, 워크플로우 phase drift) 가 있으면 명시. 정상이면 한 줄로 요약 ("✅ 하네스 정상 — 6명 서브에이전트, 6개 스킬, 5개 워크플로우(큰 작업 수동 호출), phase 정합, 오늘 N건 turn, 최근 wip 없음").

추가 인자(있다면): $ARGUMENTS
