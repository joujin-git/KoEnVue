. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput
if (-not $payload) { exit 0 }

# Inspect the Bash command — only push after a real git commit succeeded
$command = [string]$payload.tool_input.command
if ([string]::IsNullOrWhiteSpace($command)) { exit 0 }

# Match `git commit` as a real commit (with or without flags), but skip dry-run / message-edit shortcuts
if ($command -notmatch '\bgit\s+commit\b') { exit 0 }
if ($command -match '--dry-run|--allow-empty-message|--no-edit\s*$') { exit 0 }

# PostToolUse only fires after success per Claude Code docs, so we don't need to re-check exit code.
$pushResult = Invoke-Push

switch ($pushResult) {
    'pushed' {
        $msg = '[harness] auto-push 완료 — 커밋 + 푸시 규칙 적용. 다른 장비에서 즉시 받을 수 있습니다.'
    }
    'no-upstream' {
        $msg = '[harness] auto-push 건너뜀 — upstream branch 미설정. 첫 push 시 `git push -u origin <branch>` 필요.'
    }
    'failed' {
        $msg = '[harness] auto-push 실패 — 원격 거부 / 네트워크 문제. 수동 `git push` 로 상태 확인 후 처리하세요.'
    }
    default {
        $msg = "[harness] auto-push 상태 불명 ($pushResult)."
    }
}

Write-HookOutput @{
    hookSpecificOutput = @{
        hookEventName = 'PostToolUse'
        additionalContext = $msg
    }
}
exit 0

}
