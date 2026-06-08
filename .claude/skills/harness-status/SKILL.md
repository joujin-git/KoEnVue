---
description: 현재 하네스 활성화 상태 한눈에 — 모델/effort/thinking, hook 동작, 서브에이전트 목록, 오늘 세션 파일, dirty tree, 최근 hook 에러.
allowed-tools: Bash, Read, Glob
shell: powershell
---

## 모델 / 인텔리전스
- 환경변수 effort: `!`echo "CLAUDE_CODE_EFFORT_LEVEL=$env:CLAUDE_CODE_EFFORT_LEVEL"``
- settings.json: `!`Select-String -Path .claude/settings.json -Pattern 'model|effortLevel|alwaysThinkingEnabled' | ForEach-Object { $_.Line.Trim() }``
- ultracode (멀티에이전트): 항상 ON — `inject-turn-context` hook 이 매 턴 키워드+지시 주입. effort=max 와 별개 축으로 둘 다 유지. statusLine 에 `ultracode` 표시.

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

위 정보를 받으면 사용자에게 한국어로 친절히 정리해주세요. 이상 신호 (hook error 누적, dirty tree 30건 이상, CLAUDE.md 줄 제한 초과, 서브에이전트 수가 6 미만, 스킬 수가 6 미만, 워크플로우 수가 5 미만, 워크플로우 phase drift, `inject-turn-context` hook 누락) 가 있으면 명시. 정상이면 한 줄로 요약 ("✅ 하네스 정상 — 6명 서브에이전트, 6개 스킬, 5개 워크플로우, ultracode ON, phase 정합, 오늘 N건 turn, 최근 wip 없음").

추가 인자(있다면): $ARGUMENTS
