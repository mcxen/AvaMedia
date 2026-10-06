param(
    [Parameter(Mandatory)][string]$Application,
    [Parameter(Mandatory)][string]$Media,
    [ValidateRange(1,20)][int]$Runs = 5,
    [ValidateSet('Light','Dark','MacOS9')][string]$Skin = 'Light',
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/player-startup')
)
$ErrorActionPreference = 'Stop'
$appPath = (Resolve-Path -LiteralPath $Application).Path
$mediaPath = (Resolve-Path -LiteralPath $Media).Path
$outputPath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$measurements = @()
for ($run = 1; $run -le $Runs; $run++) {
    $folder = Join-Path $outputPath ('run-' + $run)
    $start = [DateTimeOffset]::UtcNow
    $info = [Diagnostics.ProcessStartInfo]::new($appPath)
    $info.UseShellExecute = $false
    $info.ArgumentList.Add('--play')
    $info.ArgumentList.Add($mediaPath)
    $info.ArgumentList.Add('--player-benchmark')
    $info.ArgumentList.Add($folder)
    $info.ArgumentList.Add('--player-benchmark-no-capture')
    if ($Skin -eq 'Light') { $info.ArgumentList.Add('--light') }
    if ($Skin -eq 'Dark') { $info.ArgumentList.Add('--dark') }
    if ($Skin -eq 'MacOS9') { $info.ArgumentList.Add('--macos9') }
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (!$process.WaitForExit(15000)) { $process.Kill($true); throw 'Player startup exceeded 15 seconds.' }
        if ($process.ExitCode -ne 0) { throw ('Player failed; see ' + $folder) }
        $report = Get-Content -LiteralPath (Join-Path $folder 'startup.json') -Raw | ConvertFrom-Json
        if (!$report.firstFrameUtc -or $report.error) { throw ('First frame unavailable: ' + $report.error) }
        $measurements += [pscustomobject]@{
            run = $run
            processToWindowMs = ([DateTimeOffset]$report.windowOpenedUtc - $start).TotalMilliseconds
            processToFirstFrameMs = ([DateTimeOffset]$report.firstFrameUtc - $start).TotalMilliseconds
            openToFirstFrameMs = $report.openToFirstFrameMs
        }
    } finally { $process.Dispose() }
}
$ordered = @($measurements.processToFirstFrameMs | Sort-Object)
$median = if ($ordered.Count % 2) { $ordered[[int][Math]::Floor($ordered.Count / 2)] } else { ($ordered[$ordered.Count / 2 - 1] + $ordered[$ordered.Count / 2]) / 2 }
[pscustomobject]@{
    mode = 'Fresh application process per run; OS file cache is retained'
    application = $appPath; media = $mediaPath; skin = $Skin
    gitRevision = (& git -C (Split-Path -Parent $PSScriptRoot) rev-parse HEAD).Trim()
    workingTreeDirty = [bool](& git -C (Split-Path -Parent $PSScriptRoot) status --porcelain)
    medianFirstFrameMs = $median; p95FirstFrameMs = $ordered[[int][Math]::Ceiling($ordered.Count * .95) - 1]
    measurements = $measurements
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'summary.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $outputPath 'summary.json')
