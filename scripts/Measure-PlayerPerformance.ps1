param(
    [string]$Fixtures = (Join-Path $PSScriptRoot '../artifacts/player-large-files'),
    [ValidateRange(1,20)][int]$Runs = 3,
    [ValidateRange(1,10)][int]$Seconds = 4,
    [ValidateSet('Light','Dark','MacOS9')][string]$Skin = 'Light',
    [string]$Output = (Join-Path $PSScriptRoot ('../artifacts/player-performance-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [switch]$NativeStartup
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$fixtureRoot = (Resolve-Path -LiteralPath $Fixtures).Path
$outputRoot = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
& $sdk build (Join-Path $taskRoot 'tests/AvaMedia.PlayerTests/AvaMedia.PlayerTests.csproj') -c Release -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Player measurement build failed.' }
$tester = Join-Path $taskRoot 'tests/AvaMedia.PlayerTests/bin/Release/net8.0/AvaMedia.PlayerTests.dll'
$rows = @()
foreach ($name in @('4k-large.avi','1080p60.mp4','two-hour.wav')) {
    $source = Join-Path $fixtureRoot $name
    if (!(Test-Path -LiteralPath $source)) { throw "Missing real fixture: $source. Run Prepare-PlayerLargeFiles.ps1." }
    $case = [IO.Path]::GetFileNameWithoutExtension($name)
    for ($run = 1; $run -le $Runs; $run++) {
        $folder = Join-Path $outputRoot ($case + '-' + $run)
        & $sdk $tester --performance --source $source --output $folder --skin $Skin --seconds $Seconds
        if ($LASTEXITCODE -ne 0) { throw "Measurement failed: $case run $run" }
        $report = Get-Content -LiteralPath (Join-Path $folder 'profile.json') -Raw | ConvertFrom-Json
        $rows += [pscustomobject]@{ case=$case; run=$run; firstFrameMs=$report.firstFrameMs; cpuMs=$report.cpuMs;
            allocatedBytes=$report.allocatedBytes; displayedFps=$report.displayedFps; uiDispatchP95Ms=$report.uiHeartbeatP95Ms;
            seekMs=$report.seekMs; resumeMs=$report.resumeMs; peakWorkingSetBytes=$report.peakWorkingSetBytes; audioPeak=$report.audioPeak }
    }
}
function Median($Values) {
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count % 2) { return $ordered[[int][Math]::Floor($ordered.Count / 2)] }
    return ($ordered[$ordered.Count / 2 - 1] + $ordered[$ordered.Count / 2]) / 2
}
$summary = @($rows | Group-Object case | ForEach-Object {
    [pscustomobject]@{case=$_.Name; firstFrameMs=(Median $_.Group.firstFrameMs); cpuMs=(Median $_.Group.cpuMs);
        allocatedBytes=(Median $_.Group.allocatedBytes); displayedFps=(Median $_.Group.displayedFps);
        uiDispatchP95Ms=(Median $_.Group.uiDispatchP95Ms); seekMs=(Median $_.Group.seekMs); resumeMs=(Median $_.Group.resumeMs)}
})
[pscustomobject]@{revision=(& git -C $taskRoot rev-parse HEAD).Trim(); dirty=[bool](& git -C $taskRoot status --porcelain);
    mode='Real FFmpeg input, headless Skia rendering, PCM test consumer; fresh process with OS cache retained';
    skin=$Skin; seconds=$Seconds; runs=$Runs; summary=$summary; measurements=$rows} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputRoot 'summary.json') -Encoding utf8
if ($NativeStartup) {
    if ([IO.Path]::IsPathRooted($sdk)) { $env:DOTNET_ROOT = Split-Path -Parent $sdk }
    & (Join-Path $PSScriptRoot 'Measure-PlayerStartup.ps1') -Application (Join-Path $taskRoot 'src/AvaMedia.Desktop/bin/Release/net8.0/AvaMedia.Desktop.exe') `
        -Media (Join-Path $fixtureRoot '4k-large-silent.avi') -Skin $Skin -Runs $Runs -Output (Join-Path $outputRoot 'native-startup')
}
$summary | Format-Table
