. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

# PreCompact — 대화가 압축(컴팩션)되기 직전 실행. ultracode 멀티에이전트가 컨텍스트를
# 빠르게 채워 컴팩션 빈도가 높아진 환경에서 작업 연속성을 보강한다. 두 축으로 동작:
#   (1) 세션 로그에 compaction 마커 append — 압축 지점을 영구 기록(다음 SessionStart/사람이 추적).
#   (2) additionalContext 주입 — 압축 직후 새 컨텍스트에 git 스냅샷 + 세션파일 포인터를 실어,
#       진행 중이던 미커밋 작업의 연속성을 즉시 복원(SessionStart 와 동일 메커니즘).
# matcher '*' 라 auto(컨텍스트 한도)·manual(/compact) 둘 다 트리거. payload.trigger 로 구분 기록.

$payload = Read-HookInput
$trigger = if ($payload -and $payload.trigger) { [string]$payload.trigger } else { 'auto' }
$stamp = Get-Date -Format 'HH:mm'
$root = Get-ProjectRoot

# (1) 세션 로그에 압축 마커 박제
$sessionFile = Get-TodaySessionFile
$marker = @(
    ''
    "## [$stamp] compaction (trigger=$trigger)"
    ''
    '> 이 지점에서 대화가 압축됐습니다. 위쪽 turn 기록이 압축 후 세션의 상세 컨텍스트 원본입니다.'
    ''
)
Add-SessionBlock -Path $sessionFile -Content ($marker -join "`n")

# (2) 연속성 컨텍스트 — 압축 후에도 살아남도록 additionalContext 로 주입
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("[harness] 방금 대화가 압축(컴팩션, trigger=$trigger)됐습니다 — 직전까지의 상세 맥락이 요약으로 축약됐습니다. 진행 중이던 작업의 연속성을 위해 아래 스냅샷을 확인하세요.")
$lines.Add('')

# 미커밋 변경 — 압축 직전 진행하던 작업일 가능성이 높음
if (Test-DirtyTree) {
    Push-Location $root
    try {
        $st = (git status --porcelain 2>$null | Select-Object -First 30) -join "`n"
        $count = (git status --porcelain 2>$null | Measure-Object).Count
        $lines.Add("## 미커밋 변경 ($count 건) — 압축 직전 진행 작업일 수 있음")
        $lines.Add('```')
        $lines.Add($st)
        if ($count -gt 30) { $lines.Add("…(나머지 $($count - 30)건 생략)…") }
        $lines.Add('```')
        $lines.Add('완료 여부와 다음 단계를 확인하세요.')
        $lines.Add('')
    } finally { Pop-Location }
}

# 최근 커밋 — 어디까지 진행됐는지 앵커
Push-Location $root
try {
    $recent = git log --pretty=format:'%h %s' -5 2>$null
    if ($recent) {
        $lines.Add('## 최근 커밋 5개')
        foreach ($c in $recent) { $lines.Add("- $c") }
        $lines.Add('')
    }
} finally { Pop-Location }

# 상세 복원 포인터
$today = Get-Date -Format 'yyyy-MM-dd'
$lines.Add('## 상세 복원')
$lines.Add("압축 전 turn 기록은 docs/sessions/$today.md 에 남아 있습니다. 미완 작업의 구체 맥락이 필요하면 그 파일의 마지막 turn 블록들을 참조하세요. 큰 작업 중이었다면 /resume-session 도 가능합니다.")

Write-HookOutput @{
    hookSpecificOutput = @{
        hookEventName = 'PreCompact'
        additionalContext = ($lines -join "`n")
    }
}
exit 0

}
