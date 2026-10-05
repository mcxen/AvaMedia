param([ValidateSet('win-x64','osx-arm64','osx-x64')][string]$Runtime='win-x64', [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$toolRoot=[IO.Path]::GetFullPath($Destination)
$cache=Join-Path $taskRoot '.tools/download-bundle'
New-Item -ItemType Directory -Path $toolRoot,$cache -Force | Out-Null
$ytVersion='2026.08.19'
$denoVersion='2.9.7'
$ytAsset=if($Runtime -eq 'win-x64'){'yt-dlp.exe'}else{'yt-dlp_macos'}
$ytHash=if($Runtime -eq 'win-x64'){'66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a'}else{'0f192b7ec147ab6288885d6351d9ab67367640029b4377576ef46dd79cf7b202'}
$denoAsset=switch($Runtime){'win-x64'{'deno-x86_64-pc-windows-msvc.zip'}'osx-arm64'{'deno-aarch64-apple-darwin.zip'}'osx-x64'{'deno-x86_64-apple-darwin.zip'}}
$denoHash=switch($Runtime){'win-x64'{'a0c3101b4158d1dfb7d6a78a7bf0f3de80c96bb423c152beec8beb22786f2238'}'osx-arm64'{'5cd46d6268f6f78f5d88bdc7159d20bd44cdaa4b3303474839f87ec6fe7ae25c'}'osx-x64'{'95daaff11c116a52ad54785e7914c8e9c9cdcaba793c5ed929c74ca2d8e6259a'}}
function Get-CheckedAsset([string]$Url,[string]$Path,[string]$Hash) {
    if(!(Test-Path -LiteralPath $Path) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash) {
        Invoke-WebRequest -Uri $Url -OutFile $Path
    }
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash){throw "Tool checksum mismatch: $Url"}
    return $Path
}
$ytUrl="https://github.com/yt-dlp/yt-dlp/releases/download/$ytVersion/$ytAsset"
$yt=Get-CheckedAsset $ytUrl (Join-Path $cache "$ytVersion-$ytAsset") $ytHash
$archiveUrl="https://github.com/denoland/deno/releases/download/v$denoVersion/$denoAsset"
$archive=Get-CheckedAsset $archiveUrl (Join-Path $cache "$denoVersion-$denoAsset") $denoHash
$ytName=if($Runtime -eq 'win-x64'){'yt-dlp.exe'}else{'yt-dlp'}
$denoName=if($Runtime -eq 'win-x64'){'deno.exe'}else{'deno'}
Copy-Item -LiteralPath $yt -Destination (Join-Path $toolRoot $ytName) -Force
Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot -Force
$noticeRoot=Join-Path $taskRoot 'licenses/download-tools'
New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
foreach($file in @('LICENSE','THIRD_PARTY_LICENSES.txt')) {
    $noticeName=if($file -eq 'LICENSE'){'LICENSE.txt'}else{$file}
    $target=Join-Path $noticeRoot "yt-dlp-$ytVersion-$noticeName"
    if(!(Test-Path -LiteralPath $target)){Invoke-WebRequest -Uri "https://raw.githubusercontent.com/yt-dlp/yt-dlp/$ytVersion/$file" -OutFile $target}
}
$denoLicense=Join-Path $noticeRoot "deno-$denoVersion-LICENSE.txt"
if(!(Test-Path -LiteralPath $denoLicense)){Invoke-WebRequest -Uri "https://raw.githubusercontent.com/denoland/deno/v$denoVersion/LICENSE.md" -OutFile $denoLicense}
$denoLock=Join-Path $noticeRoot "deno-$denoVersion-Cargo.lock"
if(!(Test-Path -LiteralPath $denoLock)){Invoke-WebRequest -Uri "https://raw.githubusercontent.com/denoland/deno/v$denoVersion/Cargo.lock" -OutFile $denoLock}
$denoNotices=Join-Path $noticeRoot "deno-$denoVersion-THIRD-PARTY-NOTICES.txt"
$sourceNotices=@{
    'deno-v8-15.0.245.4-LICENSE.txt'='https://raw.githubusercontent.com/v8/v8/15.0.245.4/LICENSE'
    'deno-rusty-v8-150.4.0-LICENSE.txt'='https://raw.githubusercontent.com/denoland/rusty_v8/v150.4.0/LICENSE'
    'deno-node-types-LICENSE.txt'="https://raw.githubusercontent.com/denoland/deno/v$denoVersion/cli/tsc/dts/node/LICENSE"
    'deno-undici-LICENSE.txt'="https://raw.githubusercontent.com/denoland/deno/v$denoVersion/cli/tsc/dts/node/undici/LICENSE"
    'deno-typescript-6.0.3-LICENSE.txt'='https://raw.githubusercontent.com/microsoft/TypeScript/v6.0.3/LICENSE.txt'
}
foreach($entry in $sourceNotices.GetEnumerator()) {
    $target=Join-Path $noticeRoot $entry.Key
    if(!(Test-Path -LiteralPath $target)){Invoke-WebRequest -Uri $entry.Value -OutFile $target}
}
$native=($Runtime -eq 'win-x64' -and $env:OS -eq 'Windows_NT') -or ($Runtime.StartsWith('osx-') -and [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX))
if($native) {
    if($Runtime.StartsWith('osx-')){& chmod +x (Join-Path $toolRoot $ytName) (Join-Path $toolRoot $denoName)}
    $denoActual=(& (Join-Path $toolRoot $denoName) --version | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or !$denoActual.StartsWith("deno $denoVersion ")){throw 'Bundled Deno failed version verification.'}
    if(!(Test-Path -LiteralPath $denoNotices)) {
        & (Join-Path $toolRoot $denoName) run --allow-read=$denoLock --allow-write=$denoNotices --allow-net=static.crates.io (Join-Path $PSScriptRoot 'Collect-DenoLicenses.js') $denoLock $denoNotices
        if($LASTEXITCODE -ne 0){throw 'Collecting Deno dependency notices failed.'}
    }
    $ytActual=(& (Join-Path $toolRoot $ytName) --version | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $ytActual -ne $ytVersion){throw 'Bundled yt-dlp failed version verification.'}
}
if(!(Test-Path -LiteralPath $denoNotices)){throw 'Deno dependency notices are required before cross-publishing.'}
$files=@($ytName,$denoName | ForEach-Object {
    $path=Join-Path $toolRoot $_
    [ordered]@{name=$_;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{runtime=$Runtime;ytDlpVersion=$ytVersion;denoVersion=$denoVersion;ytDlpSource=$ytUrl;denoSource=$archiveUrl;denoArchiveSha256=$denoHash;hashStage='upstream-before-app-signing';files=$files} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $toolRoot 'download-tools.json') -Encoding utf8
Write-Output "Bundled yt-dlp $ytVersion and Deno $denoVersion ($Runtime), verified SHA256."
