. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput
$source = if ($payload) { [string]$payload.source } else { 'startup' }

$root = Get-ProjectRoot
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("[harness] KoEnVue 하네스 활성화 — 모든 작업은 ultrathink + max effort + thinking mode 로 수행됩니다.")
$lines.Add("기본 규칙: model=opus, effort=max, language=korean. P1–P6 규칙 준수가 필수입니다.")
$lines.Add('')

# 메모리 split-brain 동기화 (§12): E:(git truth, C: 복원 무관) ↔ C:(Claude 작업 사본).
# 복원된 옛 C: 는 흡수 안 되고 최신 E: 로 복구됨. absorbed>0 = 새 메모리가 E: 로 백업됨(커밋 대상).
$memSync = Sync-Memory
if ($memSync.absorbed -gt 0 -or $memSync.restored -gt 0) {
    $lines.Add("## 메모리 동기화 (C:↔E:)")
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
        $headerMatches = [regex]::Matches($content, '(?m)^## \[\d{2}:\d{2}\][^\n]*')
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

# Surface uncommitted changes (clamped)
if (Test-DirtyTree) {
    Push-Location $root
    try {
        $st = (git status --porcelain 2>$null | Select-Object -First 30) -join "`n"
        $count = (git status --porcelain 2>$null | Measure-Object).Count
        $lines.Add('')
        $lines.Add("## 주의: 커밋되지 않은 변경 ($count 건)")
        $lines.Add('```')
        $lines.Add($st)
        if ($count -gt 30) { $lines.Add("…(나머지 $($count - 30)건 생략)…") }
        $lines.Add('```')
        $lines.Add('이전 세션의 임시 변경일 수 있습니다. 작업 이어가기 전에 상태를 확인하세요.')
    } finally {
        Pop-Location
    }
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
$lines.Add('3. **P1–P6 불변식**: docs/conventions.md 의 grep 명령이 모두 0건이어야 합니다.')
$lines.Add('4. **세션 종료**: dirty tree면 자동 wip 커밋 + docs/sessions/ 요약이 추가됩니다. 의미 있게 종료하려면 `/wrap-up`.')
$lines.Add('5. **빌드 = 항상 둘 다**: `dotnet build` (debug) + `dotnet publish -r win-x64 -c Release` (AOT). 한쪽만 하면 release exe outdated — verifier 서브에이전트 권장.')
$lines.Add('6. **커밋 = 항상 푸시까지**: `git commit` 후 즉시 `git push`. PostToolUse hook 이 자동 처리하지만, 다른 장비에서 즉시 받을 수 있도록 확인하세요.')

$context = ($lines -join "`n")

Write-HookOutput @{
    hookSpecificOutput = @{
        hookEventName = 'SessionStart'
        additionalContext = $context
    }
}
exit 0

}
