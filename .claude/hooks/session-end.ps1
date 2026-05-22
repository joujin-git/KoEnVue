. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput
$reason = if ($payload) { [string]$payload.reason } else { 'other' }

$root = Get-ProjectRoot
$sessionFile = Get-TodaySessionFile
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'

# 마무리 블록 append → wip 커밋 의 순서로 진행해야 dirty 잔여물 0.
# (옛 순서: wip 먼저 → 그 후 append → 다음 세션 시작 시 1건 dirty 잔여물.)
$dirtyBefore = Test-DirtyTree

if ($dirtyBefore) {
    # 이번 세션의 실 커밋들 (wip 전 시점) 수집
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
        $block += "- (방금 자동 wip 커밋 — 이 마무리 블록 포함)"
        $block += ''
    }
    $block += "**자동 wip 커밋 생성됨** — 이 마무리 블록까지 포함합니다. 다음 세션 시작 시 이어 작업합니다."
    $block += ''
    $block += '---'

    Add-Content -Path $sessionFile -Value ($block -join "`n") -Encoding UTF8

    # block append 후 wip — dirty + block 한 커밋에 묶임 (잔여물 0)
    Invoke-WipCommit -Note "session end ($reason)" | Out-Null
}

# 커밋 = 푸시 항상 같이 — push any commit ahead of upstream
$ahead = Get-UnpushedCommitCount
if ($ahead -gt 0) {
    Invoke-Push | Out-Null
}

exit 0

}
