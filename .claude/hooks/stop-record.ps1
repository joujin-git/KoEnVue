. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput

$root = Get-ProjectRoot
$sessionFile = Get-TodaySessionFile
$stamp = Get-Date -Format 'HH:mm'

# Pull last assistant turn from transcript (Tail-clamped so huge transcripts don't OOM)
$lastAssistantText = ''
if ($payload -and $payload.transcript_path -and (Test-Path $payload.transcript_path)) {
    $lines = Get-Content -Path $payload.transcript_path -Encoding UTF8 -Tail 1000 -ErrorAction SilentlyContinue
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $line = $lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $entry = $line | ConvertFrom-Json -ErrorAction Stop
        } catch { continue }
        if ($entry.type -eq 'assistant' -and $entry.message -and $entry.message.content) {
            foreach ($block in $entry.message.content) {
                if ($block.type -eq 'text' -and $block.text) {
                    $lastAssistantText = [string]$block.text
                    break
                }
            }
            if ($lastAssistantText) { break }
        }
    }
}

# Redact common secret patterns before writing to git-tracked file
$lastAssistantText = Hide-Secrets $lastAssistantText

# Escape triple-backticks so the blockquote stays well-formed inside markdown renderers
$lastAssistantText = $lastAssistantText -replace '```', "'''"

# Clamp to 400 chars to keep daily log readable
if ($lastAssistantText.Length -gt 400) {
    $lastAssistantText = $lastAssistantText.Substring(0, 400).TrimEnd() + '…'
}

# Pending doc-sync entries from this turn
$pendingDocs = @()
$stateFile = Join-Path (Get-StateDir) 'pending-docs.txt'
if (Test-Path $stateFile) {
    $pendingDocs = Get-Content -Path $stateFile -Encoding UTF8 -ErrorAction SilentlyContinue
    Remove-Item -Path $stateFile -ErrorAction SilentlyContinue
}

# Git status snapshot (clamped)
$dirty = Test-DirtyTree
$dirtySummary = ''
if ($dirty) {
    Push-Location $root
    try {
        $dirtySummary = (git status --porcelain 2>$null | Select-Object -First 30) -join "`n"
    } finally {
        Pop-Location
    }
}

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
    # 빈 발췌 침묵 실패 가시화 — text 블록 없는 턴(도구 위임/구조화 출력)과 transcript 스키마 변경을 구분.
    $block += '**최근 응답 발췌:** (text 응답 없음 — 도구 위임/구조화 출력으로 종료된 턴. 매 턴 이 마커면 transcript 파싱 점검 필요)'
    $block += ''
}
if ($pendingDocs.Count -gt 0) {
    $block += '**문서 동기화 대기:**'
    foreach ($p in $pendingDocs) { $block += "- $p" }
    $block += ''
}
if ($dirty) {
    $block += '**커밋되지 않은 변경:**'
    $block += '```'
    $block += $dirtySummary
    $block += '```'
    $block += ''
}

Add-Content -Path $sessionFile -Value ($block -join "`n") -Encoding UTF8
exit 0

}
