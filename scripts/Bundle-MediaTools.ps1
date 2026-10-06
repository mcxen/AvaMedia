param([ValidateSet('win-x64','osx-arm64')][string]$Runtime, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$toolRoot=[IO.Path]::GetFullPath($Destination)
$version=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'macos/ffmpeg-sources.lock.json') -Raw | ConvertFrom-Json).ffmpegVersion
$base="AvaMedia-FFmpeg-$version"
$archiveName=if($Runtime -eq 'win-x64'){"$base-win-x64.zip"}else{"$base-osx-arm64.tar.gz"}
$checksumName=if($Runtime -eq 'win-x64'){"$base-win-x64-SHA256SUMS.txt"}else{"$base-SHA256SUMS.txt"}
$archive=Join-Path $taskRoot "artifacts/$archiveName"
$checksums=Get-Content -LiteralPath (Join-Path $taskRoot "artifacts/$checksumName")
$expected=@($checksums | Where-Object { $_.EndsWith('  '+$archiveName,[StringComparison]::Ordinal) })
if($expected.Count -ne 1 -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[0].Split(' ')[0]){throw 'Bundled FFmpeg archive checksum mismatch.'}
$stage=Join-Path $taskRoot ('artifacts/media-bundle-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage,$toolRoot -Force | Out-Null
try {
    if($Runtime -eq 'win-x64'){Expand-Archive -LiteralPath $archive -DestinationPath $stage}
    else{& tar -xzf $archive -C $stage; if($LASTEXITCODE -ne 0){throw 'FFmpeg archive extraction failed.'}}
    $folders=@(Get-ChildItem -LiteralPath $stage -Directory)
    if($folders.Count -ne 1){throw 'Unexpected FFmpeg runtime archive layout.'}
    $runtimeRoot=$folders[0].FullName
    foreach($name in @('ffmpeg','ffprobe')){
        $file=Join-Path $runtimeRoot ($name+$(if($Runtime -eq 'win-x64'){'.exe'}else{''}))
        if(!(Test-Path -LiteralPath $file)){throw "Bundled $name is missing."}
    }
    Get-ChildItem -LiteralPath $runtimeRoot -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $toolRoot -Force }
    if(Test-Path -LiteralPath (Join-Path $runtimeRoot 'lib')){Copy-Item -LiteralPath (Join-Path $runtimeRoot 'lib') -Destination $toolRoot -Recurse -Force}
    $notices=Join-Path $taskRoot "licenses/media-tools/$Runtime"
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $runtimeRoot 'licenses') -Destination $notices -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $runtimeRoot 'NOTICE.txt') -Destination $notices -Force
    $build=Get-Content -LiteralPath (Join-Path $runtimeRoot 'build.json') -Raw | ConvertFrom-Json
    foreach($item in $build.files){
        $file=Join-Path $toolRoot $item.name
        if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.sha256){throw "Bundled FFmpeg file hash mismatch: $($item.name)"}
    }
    [ordered]@{runtime=$Runtime;version=$version;license=$build.license;archive=$archiveName;archiveSha256=$expected[0].Split(' ')[0];hashStage='before-app-signing';files=$build.files} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $toolRoot 'ffmpeg-bundle.json') -Encoding utf8
} finally {
    $resolved=[IO.Path]::GetFullPath($stage)
    $allowed=[IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts'))+[IO.Path]::DirectorySeparatorChar
    if(!$resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Media bundle stage escaped artifacts.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output "Bundled FFmpeg / FFprobe $version and runtime libraries ($Runtime)."
