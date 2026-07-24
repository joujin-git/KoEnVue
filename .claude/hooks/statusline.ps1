. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput

$model = 'opus'
# effort 폴백: payload.effort.level 미수신 시 env(실효 경로)를 반영 — 하드코딩 'max' 보다 실제에 가까움.
# payload 에 effort.level 이 오면 아래에서 덮어씀(우선). env 도 없으면 'max'.
$effort = if ($env:CLAUDE_CODE_EFFORT_LEVEL) { [string]$env:CLAUDE_CODE_EFFORT_LEVEL } else { 'high' }
$branch = ''
$dirty = ''

if ($payload) {
    if ($payload.model -and $payload.model.display_name) { $model = [string]$payload.model.display_name }
    elseif ($payload.model -and $payload.model.id) { $model = [string]$payload.model.id }
    if ($payload.effort -and $payload.effort.level) { $effort = [string]$payload.effort.level }
}

$root = Get-ProjectRoot
# branch 는 .git/HEAD 를 직접 Read — statusline 은 화면 갱신마다 호출돼 가장 빈번하므로 rev-parse 프로세스 생성을 제거.
$headFile = Join-Path $root '.git\HEAD'
if (Test-Path $headFile) {
    $headContent = ([string](Get-Content $headFile -Raw -ErrorAction SilentlyContinue)).Trim()
    if ($headContent -match 'ref:\s*refs/heads/(.+)$') { $branch = $matches[1] }
    elseif ($headContent) { $branch = $headContent.Substring(0, [Math]::Min(7, $headContent.Length)) }  # detached = 짧은 SHA
}
# dirty 표시는 git 1회 유지하되 untracked 제외로 축소(체감 비용 절감, 변경 여부 신호는 보존)
Push-Location $root
try {
    $st = git status --porcelain --untracked-files=no 2>$null
    if (-not [string]::IsNullOrWhiteSpace($st)) { $dirty = '*' }
} finally {
    Pop-Location
}

$parts = @()
$parts += "[$model · $effort]"
if ($branch) { $parts += "git:$branch$dirty" }
$parts += '한/En 하네스'

[Console]::Out.Write(($parts -join ' | '))
exit 0

}
