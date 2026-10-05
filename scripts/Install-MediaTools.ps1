param([string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) '.tools'))
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $target -Force | Out-Null
$archive = Join-Path $target 'ffmpeg-lgpl-download.zip'
# External, replaceable tools. They are not included in the application distribution.
Invoke-WebRequest -Uri 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip' -OutFile $archive
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $target 'ffmpeg') -Force
$ffmpeg = Get-ChildItem -LiteralPath (Join-Path $target 'ffmpeg') -Recurse -Filter ffmpeg.exe | Select-Object -First 1 -ExpandProperty FullName
$audit = & $ffmpeg -version 2>&1 | Out-String
if ($audit -match '--enable-nonfree|--enable-gpl') { throw 'Unexpected FFmpeg license configuration; review before using.' }
$audit | Set-Content -LiteralPath (Join-Path $target 'ffmpeg-build.txt') -Encoding utf8
Get-FileHash -LiteralPath $archive,$ffmpeg -Algorithm SHA256 | Format-List | Out-String | Add-Content -LiteralPath (Join-Path $target 'ffmpeg-build.txt')
Invoke-WebRequest -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile (Join-Path $target 'yt-dlp.exe')
Write-Output "Installed external tools in $target"
Write-Output 'Review actual FFmpeg/yt-dlp component licenses before redistributing these tools.'
