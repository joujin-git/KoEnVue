. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput
$reason = if ($payload) { [string]$payload.reason } else { 'other' }
$transcriptPath = if ($payload) { [string]$payload.transcript_path } else { '' }
$sessionId = if ($payload) { [string]$payload.session_id } else { '' }

$root = Get-ProjectRoot
$sessionFile = Get-TodaySessionFile
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'

# 마무리 블록 append → wip 커밋 의 순서로 진행해야 dirty 잔여물 0.
# (옛 순서: wip 먼저 → 그 후 append → 다음 세션 시작 시 1건 dirty 잔여물.)
$dirtyBefore = Test-DirtyTree
# 다른 세션의 미커밋 변경을 대신 커밋·push 하지 않는다(Get-OtherSessionStates 주석):
#   Live(최근 기록) → 커밋 생략 / Idle(쉬는 중일 수도) → 이 세션이 편집한 파일만 / 없음 → 전체.
$states = Get-OtherSessionStates -TranscriptPath $transcriptPath -SessionId $sessionId
# `if` 식은 빈 배열을 $null 로 펼치므로 단항 콤마로 감싼다 — $null 은 '판단 불가'(transcript_path 없음) 전용.
$others = if ($null -ne $states) { ,@($states.Live) } else { $null }
$idleOthers = if ($null -ne $states) { ,@($states.Idle) } else { ,@() }
$ownFiles = @()
$leftPaths = @()
$decision = 'clean'

if ($dirtyBefore -and $null -ne $others -and $others.Count -gt 0) {
    $block = @()
    $block += ''
    $block += "## [$stamp] session-end ($reason)"
    $block += ''
    $block += "**wip 커밋 생략** — 같은 프로젝트에서 다른 세션이 작업 중입니다: $($others -join ', '). 남은 변경은 그 세션이 커밋하거나 그 세션이 끝날 때 처리됩니다."
    $block += ''
    $block += '---'
    Add-SessionBlock -Path $sessionFile -Content ($block -join "`n")
    $decision = 'skip-other-live'
} elseif ($dirtyBefore -and $idleOthers.Count -gt 0) {
    $sessionRel = [System.IO.Path]::GetRelativePath($root, $sessionFile).Replace('\', '/')
    $edited = Get-SessionEditedFiles -TranscriptPath $transcriptPath -SessionId $sessionId
    $dirtyPaths = Get-DirtyPaths
    $ownFiles = @($dirtyPaths | Where-Object { $edited -contains $_ -and $_ -ne $sessionRel })
    $leftPaths = @($dirtyPaths | Where-Object { $ownFiles -notcontains $_ -and $_ -ne $sessionRel })

    $block = @()
    $block += ''
    $block += "## [$stamp] session-end ($reason)"
    $block += ''
    $block += "**이 세션이 편집한 파일만 wip 커밋** — 쉬는 중일 수 있는 다른 세션이 있습니다: $($idleOthers -join ', '). 그 세션의 미커밋 변경을 대신 커밋하지 않도록 이 세션의 Edit/Write 기록에 있는 파일만 커밋합니다."
    if ($ownFiles.Count -gt 0) {
        $block += ''
        $block += "**커밋한 파일 ($($ownFiles.Count)):**"
        foreach ($f in ($ownFiles | Select-Object -First 30)) { $block += "- ``$f``" }
    }
    if ($leftPaths.Count -gt 0) {
        $block += ''
        $block += "**남겨 둔 변경 ($($leftPaths.Count)) — 다른 세션의 것이거나 Bash 로 편집해 이 세션 기록에 없는 것:**"
        foreach ($f in ($leftPaths | Select-Object -First 30)) { $block += "- ``$f``" }
    }
    $block += ''
    $block += '---'
    Add-SessionBlock -Path $sessionFile -Content ($block -join "`n")

    # 세션 로그는 hook 이 쓰는 공용 기록이라 함께 커밋(이 마무리 블록 포함).
    Invoke-WipCommitPaths -Paths (@($ownFiles) + $sessionRel) -Note "session end ($reason), own files only" | Out-Null
    $decision = 'committed-own-only'
} elseif ($dirtyBefore) {
    $decision = if ($null -eq $others) { 'committed-sessions-unknown' } else { 'committed' }
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

# 판정 흔적 — "커밋 안 됨"이 다른 세션 보호인지 hook 무동작인지 가르는 런타임 산출물.
$trace = [ordered]@{
    at = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    session_id = $sessionId
    transcript_path = $transcriptPath
    reason = $reason
    dirty = [bool]$dirtyBefore
    others = $others
    idle_others = $idleOthers
    own_files = $ownFiles
    left_paths = $leftPaths
    decision = $decision
}
[System.IO.File]::WriteAllText((Join-Path (Get-StateDir) 'session-end-last.json'), ($trace | ConvertTo-Json -Compress -Depth 4), [System.Text.UTF8Encoding]::new($false))

exit 0

}
