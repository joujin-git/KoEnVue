. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput
if (-not $payload) { exit 0 }

$toolInput = $payload.tool_input
if (-not $toolInput) { exit 0 }

$filePath = [string]$toolInput.file_path
if ([string]::IsNullOrWhiteSpace($filePath)) {
    $filePath = [string]$toolInput.path
}
if ([string]::IsNullOrWhiteSpace($filePath)) { exit 0 }

# Normalize path relative to project root
$root = (Get-ProjectRoot).Replace('\', '/')
$normalized = $filePath.Replace('\', '/')
if ($normalized.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
    $normalized = $normalized.Substring($root.Length).TrimStart('/')
}

# Build mapping rules: source pattern → required docs to sync.
# More specific patterns (Core/Native/) MUST precede their generalizations (Core/) — first match wins.
$rules = @(
    @{ Pattern = '^Core/Native/'; Docs = @('docs/architecture.md', 'docs/conventions.md'); Reason = 'Core/Native/ 변경 (P/Invoke 시그니처 영역, 보안 민감). 새 P/Invoke 면 `/security-review` 권장.' }
    @{ Pattern = '^App/'; Docs = @('docs/architecture.md', 'docs/implementation-notes.md'); Reason = 'App/ 변경 (애플리케이션 레이어)' }
    @{ Pattern = '^Core/'; Docs = @('docs/architecture.md'); Reason = 'Core/ 변경 (재사용 인프라)' }
    @{ Pattern = '^Program.*\.cs$'; Docs = @('docs/architecture.md', 'docs/implementation-notes.md'); Reason = '엔트리포인트/부트스트랩 변경' }
    @{ Pattern = '^app\.manifest$'; Docs = @('CLAUDE.md', 'docs/conventions.md'); Reason = 'manifest 변경 (P5 영향). UAC/권한 레벨 변경이면 `/security-review` 권장.' }
    @{ Pattern = 'KoEnVue\.csproj$'; Docs = @('docs/architecture.md', 'docs/release-procedure.md'); Reason = 'csproj 변경 (빌드/버전). NuGet 추가/제거면 `/security-review` 필수 (P1 영향).' }
    @{ Pattern = '^Directory\.Build\.targets$'; Docs = @('docs/architecture.md', 'docs/conventions.md'); Reason = '빌드 타깃 변경' }
    @{ Pattern = '^NuGet\.config$'; Docs = @('docs/conventions.md'); Reason = 'NuGet 소스 변경 (P1 정신 영향). 외부 피드 추가면 `/security-review` 필수.' }
    @{ Pattern = '^tests/'; Docs = @('docs/conventions.md'); Reason = '테스트 변경 (CONTRIBUTING 영향)' }
    @{ Pattern = '^\.github/'; Docs = @('CONTRIBUTING.md'); Reason = 'CI 변경' }
    @{ Pattern = '^\.claude/'; Docs = @('docs/harness.md'); Reason = '하네스 설정 변경' }
)

$matched = $null
foreach ($rule in $rules) {
    if ($normalized -match $rule.Pattern) { $matched = $rule; break }
}

if (-not $matched) { exit 0 }

# Check whether this same mapping was already reminded this turn — dedup to avoid noise
$stateFile = Join-Path (Get-StateDir) 'pending-docs.txt'
$docsKey = $matched.Docs -join ','
$alreadyReminded = $false
if (Test-Path $stateFile) {
    $existing = Get-Content -Path $stateFile -Encoding UTF8 -ErrorAction SilentlyContinue
    foreach ($line in $existing) {
        $parts = $line -split '\|'
        if ($parts.Count -ge 3 -and $parts[2] -eq $docsKey) {
            $alreadyReminded = $true
            break
        }
    }
}

# Always record this entry so Stop hook lists all touched paths (even when reminder is suppressed)
$entry = ("{0}|{1}|{2}" -f (Get-Date -Format 'HH:mm:ss'), $normalized, $docsKey)
Add-Content -Path $stateFile -Value $entry -Encoding UTF8

# .claude/workflows/*.js 편집 시 meta↔phase 정합 + 문법 정합 자동 검사(런타임 호출 전 조기 경고).
# Test-WorkflowPhaseDrift / Test-WorkflowSyntax 는 _common 에 존재 — 호출만 추가. dedup 과 무관하게 항상 검사.
$driftWarning = ''
$syntaxWarning = ''
if ($normalized -match '^\.claude/workflows/.*\.js$') {
    $drift = Test-WorkflowPhaseDrift
    if ($drift) { $driftWarning = " ⚠ 워크플로우 phase drift: $($drift -join '; ') — meta.phases ↔ phase() 를 1:1 로 맞추세요." }
    $syntaxErr = Test-WorkflowSyntax -JsPath (Join-Path (Get-ProjectRoot) $normalized)
    if ($syntaxErr) { $syntaxWarning = " ⚠ 워크플로우 문법오류 ($normalized): $syntaxErr — 런타임 실행 전 수정 필요." }
}

# Suppress duplicate reminder for the same mapping in the same turn — 단 phase drift/문법 경고가 있으면 내보낸다
if ($alreadyReminded -and -not $driftWarning -and -not $syntaxWarning) { exit 0 }

$docList = $matched.Docs -join ', '
$context = "[harness] $($matched.Reason): 이번 턴이 끝나기 전에 다음 문서 동기화 필요 — $docList. docs-keeper 서브에이전트 사용을 고려하세요.$driftWarning$syntaxWarning"

Write-HookOutput @{
    hookSpecificOutput = @{
        hookEventName = 'PostToolUse'
        additionalContext = $context
    }
}
exit 0

}
