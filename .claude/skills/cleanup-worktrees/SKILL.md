---
description: .claude/worktrees/ 에 누적된 빌드 산출물 정리. 일주일 이상 미사용 worktree 디렉토리를 사용자 승인 후 삭제 — ~GB 단위 디스크 누적 방지.
allowed-tools: Bash, Read
shell: powershell
---

## 현재 상태
- worktree 디렉토리 수: `!`if (Test-Path .claude/worktrees) { (Get-ChildItem .claude/worktrees -Directory | Measure-Object).Count } else { 0 }``
- 합계 크기: `!`if (Test-Path .claude/worktrees) { $b = (Get-ChildItem .claude/worktrees -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum; "{0:N1} MB" -f ($b/1MB) } else { '없음' }``
- 일주일 이상 미사용 디렉토리: `!`if (Test-Path .claude/worktrees) { $cutoff = (Get-Date).AddDays(-7); Get-ChildItem .claude/worktrees -Directory | Where-Object { $_.LastWriteTime -lt $cutoff } | ForEach-Object { '{0} (last={1:yyyy-MM-dd}, {2:N1} MB)' -f $_.Name, $_.LastWriteTime, ((Get-ChildItem $_.FullName -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB) } } else { '없음' }``

## 임무

1. **위 목록을 사용자에게 그대로 제시** — 어떤 디렉토리가 얼마나 오래/크게 쌓였는지 명확히
2. **사용자 승인 필수** — "다음 N개 (합계 X MB) 를 삭제할까요?" 라고 물은 뒤 진행
3. 승인 시 각 디렉토리에 대해:
   - 먼저 `git worktree list` 로 등록된 worktree 인지 확인
   - 등록돼 있으면 `git worktree remove <path> --force`
   - 등록 안 됐으면 (즉, 메타데이터만 남고 git 인지 못함) `Remove-Item -Recurse -Force`
4. 정리 후 남은 합계 크기 재보고

## 안전 규칙
- **현재 진행 중인 worktree** (서브에이전트가 사용 중) 는 LastWriteTime 이 최근일 가능성 — cutoff 7일이 보호
- `git worktree prune` 자동 실행 금지 — 사용자가 명시적으로 요청한 경우만
- 한 디렉토리 삭제 실패해도 다른 디렉토리는 계속 진행 (try/catch)
- 삭제 결과를 한 줄씩 보고: "✅ <name> (X MB)" / "❌ <name>: <reason>"

추가 인자(있다면): $ARGUMENTS
