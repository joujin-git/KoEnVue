---
description: .claude/worktrees/ 에 누적된 빌드 산출물 정리. 미사용 worktree 디렉토리를 사용자 승인 후 삭제 — ~GB 단위 디스크 누적 방지.
allowed-tools: Bash, Read
shell: powershell
argument-hint: "[--days N | --all]"
---

## 인자 (선택)

| 인자 | 동작 |
|------|------|
| (없음) | 기본 7일 cutoff. 활성 worktree 보호 우선. |
| `--days N` | cutoff 를 N 일로 변경. `--days 0` 은 사실상 `--all`. |
| `--all` | cutoff 무시, 모든 worktree 정리. **현재 작업 중인 서브에이전트가 없을 때만 안전**. |

**왜 cutoff 가 필요한가**: Claude Code 의 EnterWorktree 가 worktree 만들고 빌드 실행하면 그 안에 ~155 MB 산출물(.NET publish artifacts) 잔류. ExitWorktree 가 항상 cleanup 안 함 — LastWriteTime 만으로 "사용 중" 판단 부정확. 디스크 회수가 시급하면 `--all` (활성 서브에이전트 없을 때) 권장.

## 현재 상태
- worktree 디렉토리 수: `!`if (Test-Path .claude/worktrees) { (Get-ChildItem .claude/worktrees -Directory | Measure-Object).Count } else { 0 }``
- 합계 크기: `!`if (Test-Path .claude/worktrees) { $b = (Get-ChildItem .claude/worktrees -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum; "{0:N1} MB" -f ($b/1MB) } else { '없음' }``
- 일주일 이상 미사용 디렉토리: `!`if (Test-Path .claude/worktrees) { $cutoff = (Get-Date).AddDays(-7); Get-ChildItem .claude/worktrees -Directory | Where-Object { $_.LastWriteTime -lt $cutoff } | ForEach-Object { '{0} (last={1:yyyy-MM-dd}, {2:N1} MB)' -f $_.Name, $_.LastWriteTime, ((Get-ChildItem $_.FullName -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB) } } else { '없음' }``
- 모든 worktree (--all 시 대상): `!`if (Test-Path .claude/worktrees) { Get-ChildItem .claude/worktrees -Directory | ForEach-Object { '{0} (last={1:yyyy-MM-dd}, {2:N1} MB)' -f $_.Name, $_.LastWriteTime, ((Get-ChildItem $_.FullName -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB) } } else { '없음' }``

## 임무

1. **$ARGUMENTS 파싱**: `--all` / `--days N` / (없음=7) 로 cutoff 결정.
2. **대상 목록을 사용자에게 그대로 제시** — 어떤 디렉토리가 얼마나 오래/크게 쌓였는지 명확히.
3. **사용자 승인 필수** — "다음 N개 (합계 X MB) 를 삭제할까요?" 라고 물은 뒤 진행. `--all` 인 경우 "활성 서브에이전트가 없음을 확인하셨나요?" 추가 확인.
4. 승인 시 각 디렉토리에 대해:
   - 먼저 `git worktree list` 로 등록된 worktree 인지 확인
   - 등록돼 있으면 `git worktree remove <path> --force`
   - 등록 안 됐으면 (즉, 메타데이터만 남고 git 인지 못함) `Remove-Item -Recurse -Force`
5. 정리 후 남은 합계 크기 재보고.

## 안전 규칙

- **활성 worktree 보호 (기본 7일 cutoff)**: 서브에이전트가 사용 중일 가능성. `--all` 시에는 이 보호가 사라지므로 사용자가 활성 서브에이전트 없음을 명시 확인해야 함.
- `git worktree prune` 자동 실행 금지 — 사용자가 명시적으로 요청한 경우만.
- 한 디렉토리 삭제 실패해도 다른 디렉토리는 계속 진행 (try/catch).
- 삭제 결과를 한 줄씩 보고: "✅ \<name\> (X MB)" / "❌ \<name\>: \<reason\>".
- 작업 완료 후 남은 .claude/worktrees/ 합계가 0 MB 이면 정상. 한 자릿수 MB 잔여는 git 메타데이터 — 무해.
