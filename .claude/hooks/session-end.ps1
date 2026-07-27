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
    # 최근 10분 커밋 (wip 전 시점) — 세션 길이를 알 수 없어 고정 창을 쓰는 근사치다.
    # 10분 넘게 이어진 세션은 그 앞 커밋이 목록에서 빠지므로 아래 제목도 '최근 10분'으로 표기.
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
        $block += '**최근 10분 커밋 (이 세션 추정 — 더 긴 세션은 앞 커밋이 빠질 수 있음):**'
        foreach ($c in $recentCommits) { $block += "- $c" }
        $block += "- (방금 자동 wip 커밋 — 이 마무리 블록 포함)"
        $block += ''
    }
    $block += "**자동 wip 커밋 생성됨** — 이 마무리 블록까지 포함합니다. 다음 세션 시작 시 이어 작업합니다."
    $block += ''
    $block += '---'

    Add-SessionBlock -Path $sessionFile -Content ($block -join "`n")

    # block append 후 wip — dirty + block 한 커밋에 묶임 (잔여물 0)
    Invoke-WipCommit -Note "session end ($reason)" | Out-Null
}

# 커밋 = 푸시 항상 같이 — push any commit ahead of upstream
$ahead = Get-UnpushedCommitCount
if ($ahead -gt 0) {
    $pushResult = Invoke-Push
    if ($pushResult -ne 'pushed') {
        # SessionEnd 시점엔 additionalContext 를 사용자에게 못 띄우므로
        # hook-errors.log 에 기록 → 다음 SessionStart 의 "최근 hook 에러" 섹션에서 노출
        $reasonMsg = switch ($pushResult) {
            'no-upstream' { 'upstream branch 미설정 — git push -u origin <branch> 필요' }
            'failed'      { '원격 거부 / 네트워크 실패 — 수동 git push 로 확인' }
            default       { "unknown ($pushResult)" }
        }
        Write-HookError -HookName 'session-end.ps1' -Message "auto-push $pushResult (ahead=$ahead) — $reasonMsg"
    }
}

exit 0

}
