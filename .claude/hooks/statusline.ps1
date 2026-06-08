. (Join-Path $PSScriptRoot 'lib\_common.ps1')

Invoke-HookSafely {

$payload = Read-HookInput

$model = 'opus'
# effort 폴백: payload.effort.level 미수신 시 env(실효 경로)를 반영 — 하드코딩 'max' 보다 실제에 가까움.
# payload 에 effort.level 이 오면 아래에서 덮어씀(우선). env 도 없으면 'max'.
$effort = if ($env:CLAUDE_CODE_EFFORT_LEVEL) { [string]$env:CLAUDE_CODE_EFFORT_LEVEL } else { 'max' }
$branch = ''
$dirty = ''

if ($payload) {
    if ($payload.model -and $payload.model.display_name) { $model = [string]$payload.model.display_name }
    elseif ($payload.model -and $payload.model.id) { $model = [string]$payload.model.id }
    if ($payload.effort -and $payload.effort.level) { $effort = [string]$payload.effort.level }
}

$root = Get-ProjectRoot
Push-Location $root
try {
    $branch = (git rev-parse --abbrev-ref HEAD 2>$null) -as [string]
    if (Test-DirtyTree) { $dirty = '*' }
} finally {
    Pop-Location
}

$parts = @()
$parts += "[$model · $effort]"
if ($branch) { $parts += "git:$branch$dirty" }
$parts += 'ultracode · 한/En 하네스 ON'

[Console]::Out.Write(($parts -join ' | '))
exit 0

}
