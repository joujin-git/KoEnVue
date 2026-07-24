. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely -EventName 'Stop' -FallbackContext '' {

# Stop hook — 턴 끝 1회. 구조: (1) 마지막 응답 발췌 (2) git status 1회 (3) doc-sync 리마인더
# (4) 워크플로우 정합검사 (5) auto-push (6) 세션 로그 append (7) additionalContext 시도.
# 재구성(2026-07-24): 구 post-edit-doc-sync(매 편집)·auto-push(매 셸) PostToolUse 두 hook을 이
# Stop 하나로 통합 — 매 tool call pwsh 콜드스타트(~245ms)를 제거하고 턴당 1회로 모음.

$payload = Read-HookInput

$root = Get-ProjectRoot
$sessionFile = Get-TodaySessionFile
$stamp = Get-Date -Format 'HH:mm'

# ── (1) transcript 에서 마지막 assistant text 발췌 (Tail-clamp) ──
$lastAssistantText = ''
if ($payload -and $payload.transcript_path -and (Test-Path $payload.transcript_path)) {
    $lines = Get-Content -Path $payload.transcript_path -Encoding UTF8 -Tail 1000 -ErrorAction SilentlyContinue
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $line = $lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $entry = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($entry.type -eq 'assistant' -and $entry.message -and $entry.message.content) {
            foreach ($block in $entry.message.content) {
                if ($block.type -eq 'text' -and $block.text) { $lastAssistantText = [string]$block.text; break }
            }
            if ($lastAssistantText) { break }
        }
    }
}
$lastAssistantText = Hide-Secrets $lastAssistantText
$lastAssistantText = $lastAssistantText -replace '```', "'''"
if ($lastAssistantText.Length -gt 400) { $lastAssistantText = $lastAssistantText.Substring(0, 400).TrimEnd() + '…' }

# ── (2) git status 1회 — dirty 여부 + 변경 파일 목록 (doc-sync·표시 공용) ──
Push-Location $root
try {
    $porcelainLines = @(git status --porcelain 2>$null)
} finally {
    Pop-Location
}
$dirty = $porcelainLines.Count -gt 0
$dirtySummary = ''
$changedFiles = @()
if ($dirty) {
    $dirtySummary = (($porcelainLines | Select-Object -First 30) -join "`n")
    foreach ($pl in $porcelainLines) {
        # porcelain "XY path" → path. rename("R  old -> new")은 도착 경로만.
        $p = $pl
        if ($p.Length -gt 3) { $p = $p.Substring(3) }
        if ($p -match ' -> ') { $p = ($p -split ' -> ')[-1] }
        $changedFiles += $p.Trim().Trim('"')
    }
}

# ── (3) doc-sync 리마인더 — 구 post-edit-doc-sync 를 턴 끝 1회로 통합 ──
$docReminders = @()
if ($changedFiles.Count -gt 0) { $docReminders = @(Get-DocSyncReminders -Files $changedFiles) }

# ── (4) 워크플로우 .js 변경 시 phase drift / 문법 정합 검사 (구 post-edit 통합) ──
$wfWarnings = @()
$changedWf = @($changedFiles | Where-Object { $_ -match '^\.claude/workflows/.*\.js$' })
if ($changedWf.Count -gt 0) {
    $drift = Test-WorkflowPhaseDrift
    if ($drift) { $wfWarnings += "phase drift: $($drift -join '; ')" }
    foreach ($js in $changedWf) {
        $syntaxErr = Test-WorkflowSyntax -JsPath (Join-Path $root $js)
        if ($syntaxErr) { $wfWarnings += "문법오류 ($js): $syntaxErr" }
    }
}

# ── (5) auto-push — 구 auto-push.ps1(매 셸)을 턴 끝 1회로 통합. 미푸시 커밋 있으면 push ──
$pushNote = ''
$ahead = Get-UnpushedCommitCount
if ($ahead -gt 0) {
    $pushResult = Invoke-Push
    switch ($pushResult) {
        'pushed'      { $pushNote = "auto-push: $ahead 개 커밋 푸시 완료" }
        'no-upstream' { $pushNote = "auto-push 건너뜀: upstream 미설정 (git push -u origin <branch> 필요)" }
        'failed'      { $pushNote = "auto-push 실패: 원격 거부/네트워크 — 수동 git push 확인" }
        default       { $pushNote = "auto-push 상태 불명 ($pushResult)" }
    }
}

# ── (6) 세션 로그 turn 블록 append ──
$block = @()
$block += ''
$block += "## [$stamp] turn"
$block += ''
if ($lastAssistantText) {
    $block += '**최근 응답 발췌:**'
    $block += ''
    $block += "> $($lastAssistantText -replace "`r?`n", ' ')"
    $block += ''
} else {
    $block += '**최근 응답 발췌:** (text 응답 없음 — 도구 위임/구조화 출력으로 종료된 턴. 매 턴 이 마커면 transcript 파싱 점검 필요)'
    $block += ''
}
if ($docReminders.Count -gt 0) {
    $block += '**문서 동기화 대기:**'
    foreach ($d in $docReminders) { $block += "- $($d.Reason) → $($d.Docs -join ', ')" }
    $block += ''
}
if ($wfWarnings.Count -gt 0) {
    $block += '**워크플로우 경고:**'
    foreach ($w in $wfWarnings) { $block += "- ⚠ $w" }
    $block += ''
}
if ($pushNote) {
    $block += "**$pushNote**"
    $block += ''
}
if ($dirty) {
    $block += '**커밋되지 않은 변경:**'
    $block += '```'
    $block += $dirtySummary
    $block += '```'
    $block += ''
}
Add-SessionBlock -Path $sessionFile -Content ($block -join "`n")

# ── (7) 메인 세션에 doc-sync/경고/push 실패를 additionalContext 로 시도 ──
# (Stop hook 이 additionalContext 를 지원하면 다음 턴에 노출됨. 미지원이어도 무해 —
#  세션 로그(6)가 확실한 기록. 재구성 후 스모크로 실제 노출 여부 확인 대상.)
$ctxParts = @()
if ($docReminders.Count -gt 0) {
    $items = (@($docReminders | ForEach-Object { $_.Docs -join ', ' }) | Select-Object -Unique) -join ' / '
    $ctxParts += "[harness] 이번 턴 코드/설정 변경 — 커밋 전 문서 동기화 확인: $items. sync-docs 스킬(docs-keeper) 고려."
}
if ($wfWarnings.Count -gt 0) { $ctxParts += "[harness] 워크플로우 정합 경고: $($wfWarnings -join ' | ')" }
if ($pushNote -and $pushNote -notmatch '완료') { $ctxParts += "[harness] $pushNote" }
if ($ctxParts.Count -gt 0) {
    Write-HookOutput @{
        hookSpecificOutput = @{
            hookEventName = 'Stop'
            additionalContext = ($ctxParts -join "`n")
        }
    }
}
exit 0

}
