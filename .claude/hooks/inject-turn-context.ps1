. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

# UserPromptSubmit — 매 턴 effort/thinking/ultracode 컨텍스트 주입.
# 두 축을 독립 주입: (1) ultrathink + max effort, (2) ultracode 멀티에이전트 모드.
# 사용자가 프롬프트에 해당 키워드를 직접 쓰면 그 축만 skip (중복 방지).
# effort=max 는 env(CLAUDE_CODE_EFFORT_LEVEL=max)로 별도 강제 — ultracode 가 effort 를
# 대체하지 않는다. ultracode 키워드를 컨텍스트에 넣어 런타임 활성화를 시도하되,
# 명시적 한국어 지시를 함께 주입해 활성화 경로와 무관하게 행동(Workflow 오케스트레이션)을 보장.

$payload = Read-HookInput
$prompt = if ($payload) { [string]$payload.prompt } else { '' }

$lines = New-Object System.Collections.Generic.List[string]

if ($prompt -notmatch '(?i)ultrathink') {
    $lines.Add('[harness] 이번 턴은 ultrathink + thinking 모드입니다. 작업 깊이에 맞춰 끝까지 추론하세요. 이 프로젝트의 모든 작업은 항상 max effort 로 수행됩니다 — 단축/생략 없이.')
}

if ($prompt -notmatch '(?i)ultracode') {
    $lines.Add('[harness] ultracode 모드 (항상 ON — 하네스 정책). substantive 작업(다중 파일 변경·코드 리뷰·릴리즈 점검·버그/레이스 헌트·설계 비교·하네스 변경)은 Workflow 도구로 멀티에이전트 오케스트레이션하세요. trivial 편집·단순 대화·단일 사실 조회만 solo. 저장된 워크플로우 — release-review · bug-hunt · codebase-audit · design-compare · harness-optimize (.claude/workflows/). 적합하면 Workflow({name}) 로 호출, 없으면 작업에 맞게 즉석 작성. effort 는 max 그대로 유지됩니다.')
}

if ($lines.Count -eq 0) { exit 0 }

Write-HookOutput @{
    hookSpecificOutput = @{
        hookEventName = 'UserPromptSubmit'
        additionalContext = ($lines -join "`n")
    }
}
exit 0

}
