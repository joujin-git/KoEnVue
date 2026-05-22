---
description: 현재 하네스 활성화 상태 한눈에 — 모델/effort/thinking, hook 동작, 서브에이전트 목록, 오늘 세션 파일, dirty tree, 최근 hook 에러.
allowed-tools: Bash, Read, Glob
shell: powershell
---

## 모델 / 인텔리전스
- 환경변수 effort: `!`echo "CLAUDE_CODE_EFFORT_LEVEL=$env:CLAUDE_CODE_EFFORT_LEVEL"``
- settings.json: `!`Select-String -Path .claude/settings.json -Pattern 'model|effortLevel|alwaysThinkingEnabled' | ForEach-Object { $_.Line.Trim() }``

## 하네스 파일 존재
- 서브에이전트: `!`(Get-ChildItem .claude/agents/*.md -ErrorAction SilentlyContinue).Name -join ', '``
- 슬래시 커맨드: `!`(Get-ChildItem .claude/commands/*.md -ErrorAction SilentlyContinue).Name -join ', '``
- hook 스크립트: `!`(Get-ChildItem .claude/hooks/*.ps1 -ErrorAction SilentlyContinue).Name -join ', '``

## 오늘 세션
- 파일: `!`$f = "docs/sessions/$(Get-Date -Format yyyy-MM-dd).md"; if (Test-Path $f) { "$f ($((Get-Content $f | Measure-Object -Line).Lines) 줄)" } else { "없음 — 첫 turn 후 자동 생성" }``
- 가장 최근 블록 헤더: `!`$f = "docs/sessions/$(Get-Date -Format yyyy-MM-dd).md"; if (Test-Path $f) { (Select-String -Path $f -Pattern '^## ' | Select-Object -Last 1).Line } else { '없음' }``

## Git
- 브랜치: `!`git rev-parse --abbrev-ref HEAD``
- dirty (요약): `!`$c = (git status --porcelain | Measure-Object).Count; "$c 건"``
- 최근 wip 커밋 (3일): `!`git log --grep='^wip:' --since='3 days ago' --oneline | Select-Object -First 5``

## 최근 hook 에러
- `!`if (Test-Path .claude/state/hook-errors.log) { Get-Content .claude/state/hook-errors.log -Tail 5 } else { '없음 — 모든 hook 정상' }``

## CLAUDE.md 크기
- `!`$c = (Get-Content CLAUDE.md | Measure-Object -Line).Lines; "$c 줄 / 30 줄 제한"``

---

위 정보를 받으면 사용자에게 한국어로 친절히 정리해주세요. 이상 신호 (hook error 누적, dirty tree 30건 이상, CLAUDE.md 30줄 초과, 서브에이전트 수가 6 미만) 가 있으면 명시. 정상이면 한 줄로 요약 ("✅ 하네스 정상 — 6명 서브에이전트, 오늘 N건 turn, 최근 wip 없음").

추가 인자(있다면): $ARGUMENTS
