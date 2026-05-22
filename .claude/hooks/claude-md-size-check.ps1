. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput
if (-not $payload) { exit 0 }

# InstructionsLoaded payload schema is not fully documented; try known/likely keys
$path = ''
foreach ($key in @('file_path', 'path', 'file', 'relative_path', 'instructions_path', 'loaded_file')) {
    $v = [string]$payload.$key
    if (-not [string]::IsNullOrWhiteSpace($v)) { $path = $v; break }
}
if ([string]::IsNullOrWhiteSpace($path)) { exit 0 }

if ($path -notmatch 'CLAUDE\.md$') { exit 0 }

if (-not (Test-Path $path)) { exit 0 }

$lineCount = (Get-Content -Path $path -Encoding UTF8 | Measure-Object -Line).Lines
$limit = 30

if ($lineCount -le $limit) { exit 0 }

$context = "[harness] CLAUDE.md is $lineCount lines (limit: $limit). 추가 내용은 docs/ 하위로 분리하고 링크로 연결해야 합니다. 본 세션 중 수정 기회가 생기면 정리하세요."

Write-HookOutput @{
    hookSpecificOutput = @{
        hookEventName = 'InstructionsLoaded'
        additionalContext = $context
    }
}
exit 0

}
