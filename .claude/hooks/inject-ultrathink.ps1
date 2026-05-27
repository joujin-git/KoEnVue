. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput
$prompt = if ($payload) { [string]$payload.prompt } else { '' }

# Skip if user already requested ultrathink in this turn
if ($prompt -match '(?i)ultrathink') {
    exit 0
}

$context = '[harness] 이번 턴은 ultrathink + thinking 모드입니다. 작업 깊이에 맞춰 끝까지 추론하세요. 이 프로젝트의 모든 작업은 항상 max effort 로 수행됩니다 — 단축/생략 없이.'

Write-HookOutput @{
    hookSpecificOutput = @{
        hookEventName = 'UserPromptSubmit'
        additionalContext = $context
    }
}
exit 0

}
