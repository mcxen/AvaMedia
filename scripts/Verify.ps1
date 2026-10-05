param([ValidateSet('All','Ablation')][string]$Suite = 'All')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $taskRoot ('.tools/dotnet/dotnet' + $(if ($env:OS -eq 'Windows_NT') { '.exe' } else { '' }))
if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$reportRoot = Join-Path $taskRoot ('artifacts/test-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$suites = if ($Suite -eq 'Ablation') { @('AblationTests') } else {
    @('SmokeTests','FunctionTests','QuickClipTests','AudioOptionsTests','SubtitleTests',
      'BatchCropTests','UiChecks','BatchTests','BatchUiTests','AblationTests','SkinTests')
}
$results = @()
$status = 'failed'
Push-Location -LiteralPath $taskRoot
try {
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read Git revision.' }
    $dirty = [bool](& git status --porcelain)
    $build = & $sdk build AvaMedia.sln -c Release --nologo 2>&1
    $build | Set-Content -LiteralPath (Join-Path $reportRoot 'build.log') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; see build.log.' }
    foreach ($name in $suites) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $arguments = @('run','--project',('tests/AvaMedia.' + $name),'-c','Release','--no-build')
        if ($name -eq 'AblationTests') { $arguments += @('--',(Join-Path $reportRoot 'ablation')) }
        $output = & $sdk @arguments 2>&1
        $exitCode = $LASTEXITCODE
        $timer.Stop()
        $log = $name + '.log'
        $output | Set-Content -LiteralPath (Join-Path $reportRoot $log) -Encoding utf8
        $results += [pscustomobject]@{ suite=$name; exitCode=$exitCode; seconds=$timer.Elapsed.TotalSeconds; log=$log }
        Write-Output ($name + ': ' + ($output | Select-Object -Last 1))
        if ($exitCode -ne 0) { throw ($name + ' failed; see ' + $log) }
    }
    $status = 'passed'
} finally {
    [pscustomobject]@{ gitRevision=$revision; workingTreeDirty=$dirty; status=$status; suites=$results } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding utf8
    Pop-Location
    Write-Output $reportRoot
}
