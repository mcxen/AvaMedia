param([string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) '.tools'))
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $target -Force | Out-Null
$stage = Join-Path $target ('.ffmpeg-install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$source = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl-shared.zip'
# The command-line programs share codec DLLs. Playback uses our own renderer, not ffplay.
try {
    $archive = Join-Path $stage 'download.zip'
    Invoke-WebRequest -Uri $source -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $stage 'extracted')
    $ffmpeg = Get-ChildItem -LiteralPath (Join-Path $stage 'extracted') -Recurse -Filter ffmpeg.exe | Select-Object -First 1 -ExpandProperty FullName
    $audit = & $ffmpeg -version 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $audit -notmatch '--enable-shared' -or $audit -match '--enable-nonfree|--enable-gpl') { throw 'Unexpected FFmpeg shared build or license configuration.' }
    $files = @(Get-ChildItem -LiteralPath (Split-Path -Parent $ffmpeg) -File | Where-Object { $_.Name -in @('ffmpeg.exe','ffprobe.exe') -or $_.Extension -eq '.dll' })
    $files | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target -Force }
    $audit | Set-Content -LiteralPath (Join-Path $target 'ffmpeg-build.txt') -Encoding utf8
    $notices = Join-Path $target 'ffmpeg-licenses'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent (Split-Path -Parent $ffmpeg)) 'LICENSE.txt') -Destination $notices -Force
    [ordered]@{
        source = $source
        archiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        runtimeBytes = ($files | Measure-Object Length -Sum).Sum
        files = @($files | ForEach-Object { [ordered]@{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'ffmpeg-install.json') -Encoding utf8
} finally {
    $stagePath = [IO.Path]::GetFullPath($stage)
    $targetPrefix = $target.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$stagePath.StartsWith($targetPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary extraction path escaped the tools directory.' }
    Remove-Item -LiteralPath $stagePath -Recurse -Force
}
if (!(Test-Path -LiteralPath (Join-Path $target 'download-tools.json'))) {
    & (Join-Path $PSScriptRoot 'Bundle-DownloadTools.ps1') -Runtime win-x64 -Destination $target
}
Write-Output "Installed external tools in $target"
Write-Output 'Review actual FFmpeg/yt-dlp component licenses before redistributing these tools.'
