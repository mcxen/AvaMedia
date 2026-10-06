param(
    [string]$FFmpeg = (Join-Path $PSScriptRoot '../.tools/ffmpeg/ffmpeg-master-latest-win64-lgpl/bin/ffmpeg.exe'),
    [string]$Output = (Join-Path $PSScriptRoot '../artifacts/player-large-files')
)
$ErrorActionPreference = 'Stop'
$tool = (Resolve-Path -LiteralPath $FFmpeg).Path
$fixtureRoot = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
function Generate([string]$Name, [string[]]$Arguments) {
    $target = Join-Path $fixtureRoot $Name
    if (Test-Path -LiteralPath $target) { throw "Fixture already exists: $target" }
    & $tool -v error -nostdin @Arguments -n $target
    if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed: $Name" }
    Get-Item -LiteralPath $target | Select-Object Name,Length
}
# Real indexed video packets exceed 4 GiB; no sparse filler or fake movie extension.
Generate '4k-large.avi' @('-f','lavfi','-i','testsrc2=size=3840x2160:rate=30','-f','lavfi','-i','sine=sample_rate=48000','-t','14','-c:v','rawvideo','-pix_fmt','yuv420p','-c:a','pcm_s16le')
# Two hours of physical PCM validates that opening never reads the entire soundtrack.
Generate 'two-hour.wav' @('-f','lavfi','-i','sine=frequency=660:sample_rate=48000','-t','7200','-ac','2','-c:a','pcm_s16le')
Generate '1080p60.mp4' @('-f','lavfi','-i','testsrc2=size=1920x1080:rate=60','-f','lavfi','-i','sine=sample_rate=48000','-t','14','-c:v','mpeg4','-q:v','3','-c:a','aac','-movflags','+faststart')
Generate '4k-large-silent.avi' @('-i',(Join-Path $fixtureRoot '4k-large.avi'),'-map','0:v:0','-c','copy','-an')
$chapters = [Text.StringBuilder]::new()
[void]$chapters.AppendLine(';FFMETADATA1')
for ($index = 0; $index -lt 2000; $index++) {
    [void]$chapters.AppendLine('[CHAPTER]')
    [void]$chapters.AppendLine('TIMEBASE=1/1000')
    [void]$chapters.AppendLine('START=' + ($index * 7))
    [void]$chapters.AppendLine('END=' + (($index + 1) * 7))
    [void]$chapters.AppendLine('title=Chapter ' + $index)
}
$metadata = Join-Path $fixtureRoot 'many-chapters.ffmeta'
[IO.File]::WriteAllText($metadata,$chapters.ToString(),[Text.UTF8Encoding]::new($false))
Generate 'many-chapters.mp4' @('-i',(Join-Path $fixtureRoot '1080p60.mp4'),'-f','ffmetadata','-i',$metadata,'-map','0','-map_metadata','1','-map_chapters','1','-c','copy')
Get-ChildItem -LiteralPath $fixtureRoot -File | Select-Object Name,Length | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot 'fixtures.json') -Encoding utf8
