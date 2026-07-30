. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely -EventName 'SessionStart' -FallbackContext '[harness] session-start 실패 — effort(데스크탑 앱 실측 xhigh) + thinking mode 적용(fast mode 미사용), P1-P6 준수. 큰 작업만 워크플로우. 이전 세션 컨텍스트는 docs/sessions 최신 파일 참조.' {

$payload = Read-HookInput
$source = if ($payload) { [string]$payload.source } else { 'startup' }

$root = Get-ProjectRoot
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("[harness] KoEnVue 하네스 활성화 — 작업은 thinking mode + effort(적응형)로 수행됩니다. 단순 작업은 가볍게, 복잡한 작업은 깊게.")
$lines.Add("기본 규칙: model=opus(fast mode 미사용 — 품질 우선), effort=xhigh(데스크탑 앱 실측 — settings 의 high 는 앱에서 미반영), language=korean. P1–P6 규칙 준수가 필수입니다.")
$lines.Add("멀티에이전트(Workflow)는 큰 작업에만 수동 호출 — 코드리뷰·감사·릴리즈·설계비교·버그헌트. 일상 작업은 단일 세션 + 필요 시 서브에이전트(explorer/verifier 등).")
$lines.Add('')

# 메모리 동기화 (§12): E: 가 **실경로이자 truth**(런타임이 autoMemoryDirectory 로 E: 를 직접
# 읽고 씀 — 2026-07-29 확정), C: 는 설정 무효화 대비 보험 사본. 복원된 옛 C: 는 흡수 안 되고
# 최신 E: 로 복구됨. absorbed>0 = 새 메모리가 E: 로 백업됨(커밋 대상).
# errors: 종전엔 실패를 삼켜 "실패"와 "할 일 없음"이 구분되지 않았다(2026-07-29 침묵 사례) — 반드시 노출.
$memSync = Sync-Memory
$memErrs = @($memSync.errors)
if ($memSync.absorbed -gt 0 -or $memSync.restored -gt 0 -or $memSync.created -or $memErrs.Count -gt 0) {
    $lines.Add("## 메모리 동기화 (C:↔E:)")
    if ($memSync.created) {
        $lines.Add("⚠ C: auto-memory 디렉토리가 없어 새로 생성했습니다 — **C: 복원/초기화 감지**. 아래 복구 건수를 확인하세요.")
    }
    if ($memErrs.Count -gt 0) {
        $lines.Add("❌ **동기화 실패 $($memErrs.Count)건** — 아래 사유. 상세는 ``.claude/state/hook-errors.log``. E: 가 실경로이므로 메모리 회상 자체는 정상이나, 보험 사본이 깨진 상태입니다.")
        foreach ($e in ($memErrs | Select-Object -First 3)) { $lines.Add("  - $e") }
        if ($memErrs.Count -gt 3) { $lines.Add("  - (외 $($memErrs.Count - 3)건)") }
    }
    $lines.Add("C:→E: $($memSync.absorbed)건 흡수, E:→C: $($memSync.restored)건 복구. absorbed>0 이면 git 백업 위해 커밋 필요.")
    $lines.Add('')
}

# Resume context: prefer the file containing a "세션 정리" wrap-up block (richer);
# fall back to the most recent file's headers if no wrap-up exists.
$latest = Get-LatestSessionFile
$wrapupFile = Get-LatestSessionFileWithWrapup

function Add-WrapupExcerpt {
    param([string]$path, [System.Collections.Generic.List[string]]$lines, [string]$heading)
    if (-not $path -or -not (Test-Path $path)) { return }
    try {
        $content = Get-Content -Path $path -Raw -Encoding UTF8
        $name = Split-Path $path -Leaf
        $pattern = '(?ms)(## \[\d{2}:\d{2}\] 세션 정리.*?)(?=\n## |\Z)'
        $matches = [regex]::Matches($content, $pattern)
        if ($matches.Count -gt 0) {
            $excerpt = $matches[$matches.Count - 1].Value.TrimEnd()
            $lines.Add("## $heading ($name)")
            $lines.Add('')
            $lines.Add($excerpt)
            $lines.Add('')
        }
    } catch { }
}

function Add-HeadersOnly {
    param([string]$path, [System.Collections.Generic.List[string]]$lines, [string]$heading)
    if (-not $path -or -not (Test-Path $path)) { return }
    try {
        $content = Get-Content -Path $path -Raw -Encoding UTF8
        $name = Split-Path $path -Leaf
        # 스탬프 폭을 가리지 않는다 — turn/세션정리 는 `[HH:MM]`, session-end/compaction 은
        # `[yyyy-MM-dd HH:mm]` (session-end.ps1·pre-compact.ps1 이 날짜까지 찍음). \d{2}:\d{2}
        # 로 좁히면 파일 마지막 블록인 session-end 가 매번 누락된다.
        $headerMatches = [regex]::Matches($content, '(?m)^## \[[^\]]+\][^\n]*')
        $lastHeaders = @($headerMatches | Select-Object -Last 3 | ForEach-Object { $_.Value })
        $lines.Add("## $heading ($name)")
        $lines.Add('')
        if ($lastHeaders.Count -gt 0) {
            $lines.Add('마지막 turn/session-end 헤더 (상세 컨텍스트 없음 — `/wrap-up` 미수행):')
            foreach ($h in $lastHeaders) { $lines.Add("- $h") }
            $lines.Add('')
            $lines.Add("상세는 `docs/sessions/$name` 직접 열거나 `/resume-session` 호출.")
            $lines.Add('')
        } else {
            $lines.Add('(빈 파일 — 이전 세션이 어떤 turn 도 기록 안 함)')
            $lines.Add('')
        }
    } catch { }
}

if ($wrapupFile -and $latest -and ($wrapupFile -eq $latest)) {
    # Most-recent file has a wrap-up block — clean resume
    Add-WrapupExcerpt -path $wrapupFile -lines $lines -heading '이전 세션 정리'
    $lines.Add('이어가는 컨텍스트: 위 정리를 참고해 이전 작업을 이어서 진행하세요. 새로운 요청이 있으면 그것을 우선합니다.')
} elseif ($wrapupFile -and $latest -and ($wrapupFile -ne $latest)) {
    # Most-recent file has no wrap-up — show that file's headers + older wrap-up for richer context
    Add-WrapupExcerpt -path $wrapupFile -lines $lines -heading '이전 정리 (정리 블록 있는 가장 최근 파일)'
    Add-HeadersOnly -path $latest -lines $lines -heading '그 이후 작업'
    $lines.Add('이어가는 컨텍스트: 위 정리는 더 옛 세션의 마지막 wrap-up. 이후 작업(`그 이후 작업`)은 상세가 없습니다. `/wrap-up` 으로 정리하면 다음 세션부터 깔끔히 받습니다.')
} elseif ($latest) {
    # No wrap-up exists anywhere — show most recent file's headers
    Add-HeadersOnly -path $latest -lines $lines -heading '이전 세션 — 정리 블록 없음'
    $lines.Add('이어가는 컨텍스트: 위 헤더만 보고 이전 작업을 추론하거나 `/resume-session` 으로 상세 확인.')
}

# Unpushed commits (cross-device sync hint)
$ahead = Get-UnpushedCommitCount
if ($ahead -gt 0) {
    $lines.Add('')
    $lines.Add("## push 안 한 commit ($ahead 개)")
    Push-Location $root
    try {
        $aheadLog = git log '@{u}..HEAD' --pretty=format:'%h %s' --date=short 2>$null | Select-Object -First 5
        if ($aheadLog) { foreach ($l in $aheadLog) { $lines.Add("- $l") } }
    } finally { Pop-Location }
    $lines.Add('다른 장비에서 작업 이어가려면 `git push` 필요합니다.')
}

# Recent wip commits — surface even when tree is clean (e.g. after pull from another machine)
Push-Location $root
try {
    $wipCommits = git log --grep='^wip:' --since='3 days ago' --pretty=format:'%h %ad %s' --date=short 2>$null
    if ($wipCommits) {
        $lines.Add('')
        $lines.Add('## 최근 wip 커밋 (3일 내)')
        foreach ($c in ($wipCommits | Select-Object -First 5)) { $lines.Add("- $c") }
        $lines.Add('이 커밋들은 이전 세션 종료 시 자동 wip 커밋입니다. 의미 있는 커밋으로 묶거나 그대로 두세요.')
    }
} finally {
    Pop-Location
}

# Surface uncommitted changes (clamped) — git status 1회(Get-PorcelainStatus)
$porcelain = Get-PorcelainStatus
if ($porcelain.Dirty) {
    $count = $porcelain.Count
    $lines.Add('')
    $lines.Add("## 주의: 커밋되지 않은 변경 ($count 건)")
    $lines.Add('```')
    $lines.Add($porcelain.Clamped)
    if ($count -gt 30) { $lines.Add("…(나머지 $($count - 30)건 생략)…") }
    $lines.Add('```')
    $lines.Add('이전 세션의 임시 변경일 수 있습니다. 작업 이어가기 전에 상태를 확인하세요.')
}

# Recent hook errors (silent fail log)
$errLog = Join-Path (Get-StateDir) 'hook-errors.log'
if (Test-Path $errLog) {
    try {
        $errLines = Get-Content -Path $errLog -Tail 3 -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($errLines.Count -gt 0) {
            $lines.Add('')
            $lines.Add('## 최근 hook 에러 (마지막 3건)')
            foreach ($e in $errLines) { $lines.Add("- $e") }
            $lines.Add('상세 로그: `.claude/state/hook-errors.log`')
        }
    } catch { }
}

$lines.Add('')
$lines.Add('## 항상 적용 규칙')
$lines.Add('1. **서브에이전트 재사용**: 탐색은 explorer, 설계는 planner, 검증은 reviewer, 문서는 docs-keeper, 빌드는 verifier, 세션 정리는 historian. 메인 세션을 깔끔하게 유지하세요.')
$lines.Add('2. **코드 변경 → 문서 동기화**: App/, Core/, *.csproj, app.manifest 수정 시 docs-keeper가 docs/ 변경 필수.')
$lines.Add('3. **P1–P6 불변식**: docs/conventions.md 의 각 grep 이 우측 주석의 기대값과 일치해야 합니다 (주석 없으면 0 매치, 있으면 1+/3/4 등 — "전부 0" 아님).')
$lines.Add('4. **세션 종료**: dirty tree면 자동 wip 커밋 + docs/sessions/ 요약이 추가됩니다. 의미 있게 종료하려면 `/wrap-up`.')
$lines.Add('5. **빌드 = 항상 둘 다**: `dotnet build` (debug) + `dotnet publish -r win-x64 -c Release` (AOT). 한쪽만 하면 release exe outdated — verifier 서브에이전트 권장.')
$lines.Add('6. **커밋 = 항상 푸시까지**: `git commit` 후 즉시 `git push`. Stop hook(턴 끝)이 자동 처리하지만, 다른 장비에서 즉시 받을 수 있도록 확인하세요.')

$context = ($lines -join "`n")

Write-HookOutput @{
    hookSpecificOutput = @{
        hookEventName = 'SessionStart'
        additionalContext = $context
    }
}
exit 0

}
