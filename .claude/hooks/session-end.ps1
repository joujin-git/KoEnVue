. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput
$reason = if ($payload) { [string]$payload.reason } else { 'other' }

$root = Get-ProjectRoot
$sessionFile = Get-TodaySessionFile
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'

$committed = $false
$pushResult = 'skipped'
$dirtyBefore = Test-DirtyTree
if ($dirtyBefore) {
    $committed = Invoke-WipCommit -Note "session end ($reason)"
}

# 커밋 = 푸시 항상 같이 — push any commit ahead of upstream (wip from this session,
# or earlier commits that the user made manually without pushing)
$ahead = Get-UnpushedCommitCount
if ($ahead -gt 0) {
    $pushResult = Invoke-Push
}

# Recent commits in this session window (last 10 minutes)
$recentCommits = @()
Push-Location $root
try {
    $recentCommits = git log --since='10 minutes ago' --pretty=format:'%h %s' 2>$null
} finally {
    Pop-Location
}

$block = @()
$block += ''
$block += "## [$stamp] session-end ($reason)"
$block += ''
if ($recentCommits.Count -gt 0) {
    $block += '**이 세션의 커밋:**'
    foreach ($c in $recentCommits) { $block += "- $c" }
    $block += ''
}
if ($dirtyBefore) {
    if ($committed) {
        $block += "**자동 wip 커밋 생성됨** — 다음 세션 시작 시 이어 작업합니다."
    } else {
        $block += "**경고: dirty tree 였으나 자동 커밋 실패** — 수동 확인 필요."
    }
    $block += ''
}
# Auto-push status — "커밋 = 푸시 항상 같이" rule
if ($ahead -gt 0) {
    switch ($pushResult) {
        'pushed' { $block += "**자동 push 완료** ($ahead 개 commit) — 다른 장비에서 즉시 받을 수 있습니다." }
        'no-upstream' { $block += "**push 안 됨**: upstream branch 미설정. 첫 push 시 `git push -u origin <branch>` 필요." }
        'failed' { $block += "**push 실패**: 원격 거부 / 네트워크 문제 등. 수동 `git push` 후 충돌 해결 필요." }
        default { $block += "push 상태 불명 ($pushResult)" }
    }
    $block += ''
} elseif ($ahead -eq 0) {
    $block += "(push 대기 commit 없음 — 이미 upstream 과 동기화 상태)"
    $block += ''
}
$block += '---'

Add-Content -Path $sessionFile -Value ($block -join "`n") -Encoding UTF8
exit 0

}
