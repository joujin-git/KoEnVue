. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput

# InstructionsLoaded payload schema is not fully documented; try known/likely keys
$path = ''
if ($payload) {
    foreach ($key in @('file_path', 'path', 'file', 'relative_path', 'instructions_path', 'loaded_file')) {
        $v = [string]$payload.$key
        if (-not [string]::IsNullOrWhiteSpace($v)) { $path = $v; break }
    }
}

# Fallback — if payload is missing or schema differs from our guesses, check root/CLAUDE.md
# directly so the 30-line guard never goes silently dark.
if ([string]::IsNullOrWhiteSpace($path)) {
    $path = Join-Path (Get-ProjectRoot) 'CLAUDE.md'
}

if ($path -notmatch 'CLAUDE\.md$') { exit 0 }

if (-not (Test-Path $path)) { exit 0 }

# (Get-Content).Count returns the actual line-array length; Measure-Object -Line counts newline
# characters and undercounts the trailing line.
$lineCount = (Get-Content -Path $path -Encoding UTF8).Count
$limit = $ClaudeMdLineLimit  # 단일 진실원: _common.ps1 (harness-status 와 공유)

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
