param([ValidateSet('win-x64','osx-arm64')][string]$Runtime='win-x64', [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$toolRoot=[IO.Path]::GetFullPath($Destination)
$cache=Join-Path $taskRoot '.tools/download-bundle'
New-Item -ItemType Directory -Path $toolRoot,$cache -Force | Out-Null
$ytVersion='2026.08.19'
$quickJsVersion='0.17.0'
$ytAsset=if($Runtime -eq 'win-x64'){'yt-dlp.exe'}else{'yt-dlp_macos'}
$ytHash=if($Runtime -eq 'win-x64'){'66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a'}else{'0f192b7ec147ab6288885d6351d9ab67367640029b4377576ef46dd79cf7b202'}
function Get-CheckedAsset([string]$Url,[string]$Path,[string]$Hash) {
    if(!(Test-Path -LiteralPath $Path) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash) {
        Invoke-WebRequest -Uri $Url -OutFile $Path
    }
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash){throw "Tool checksum mismatch: $Url"}
    return $Path
}
$ytUrl="https://github.com/yt-dlp/yt-dlp/releases/download/$ytVersion/$ytAsset"
$yt=Get-CheckedAsset $ytUrl (Join-Path $cache "$ytVersion-$ytAsset") $ytHash
$quickJsBuild=$null
if($Runtime -eq 'osx-arm64') {
    $quickJs=& (Join-Path $PSScriptRoot 'macos/Build-QuickJS.ps1')
    $quickJsBuild=Get-Content -LiteralPath (Join-Path (Split-Path -Parent $quickJs) 'build.json') -Raw | ConvertFrom-Json
    if($quickJsBuild.version -ne $quickJsVersion){throw 'QuickJS recipe version mismatch.'}
    $quickJsUrl=$quickJsBuild.source
    $quickJsHash=$quickJsBuild.sha256
} else {
    $quickJsAsset='qjs-windows-x86_64.exe'
    $quickJsHash='2aeabf0092c3262d6b2609824418f7dd7ed1f1df939f73b2b15645230cac0d77'
    $quickJsUrl="https://github.com/quickjs-ng/quickjs/releases/download/v$quickJsVersion/$quickJsAsset"
    $quickJs=Get-CheckedAsset $quickJsUrl (Join-Path $cache "$quickJsVersion-$quickJsAsset") $quickJsHash
}
$ytName=if($Runtime -eq 'win-x64'){'yt-dlp.exe'}else{'yt-dlp'}
$quickJsName=if($Runtime -eq 'win-x64'){'qjs.exe'}else{'qjs'}
Copy-Item -LiteralPath $yt -Destination (Join-Path $toolRoot $ytName) -Force
Copy-Item -LiteralPath $quickJs -Destination (Join-Path $toolRoot $quickJsName) -Force
# Only the runner is shipped; no compiler, SDK or full JavaScript platform.
$noticeRoot=Join-Path $taskRoot 'licenses/download-tools'
New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
foreach($file in @('LICENSE','THIRD_PARTY_LICENSES.txt')) {
    $noticeName=if($file -eq 'LICENSE'){'LICENSE.txt'}else{$file}
    $target=Join-Path $noticeRoot "yt-dlp-$ytVersion-$noticeName"
    if(!(Test-Path -LiteralPath $target)){Invoke-WebRequest -Uri "https://raw.githubusercontent.com/yt-dlp/yt-dlp/$ytVersion/$file" -OutFile $target}
}
$sourceNotices=@{
    "quickjs-ng-$quickJsVersion-LICENSE.txt"="https://raw.githubusercontent.com/quickjs-ng/quickjs/v$quickJsVersion/LICENSE"
    'quickjs-ng-mimalloc-LICENSE.txt'='https://raw.githubusercontent.com/microsoft/mimalloc/v3.0.10/LICENSE'
    'quickjs-ng-mingw-w64-COPYING.txt'='https://raw.githubusercontent.com/mingw-w64/mingw-w64/v13.0.0/COPYING'
    'quickjs-ng-gcc-COPYING.RUNTIME.txt'='https://raw.githubusercontent.com/gcc-mirror/gcc/releases/gcc-15.2.0/COPYING.RUNTIME'
    'quickjs-ng-gcc-COPYING3.txt'='https://raw.githubusercontent.com/gcc-mirror/gcc/releases/gcc-15.2.0/COPYING3'
}
foreach($entry in $sourceNotices.GetEnumerator()) {
    $target=Join-Path $noticeRoot $entry.Key
    if(!(Test-Path -LiteralPath $target)){Invoke-WebRequest -Uri $entry.Value -OutFile $target}
}
$native=($Runtime -eq 'win-x64' -and $env:OS -eq 'Windows_NT') -or ($Runtime.StartsWith('osx-') -and [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX))
if($native) {
    if($Runtime.StartsWith('osx-')){& chmod +x (Join-Path $toolRoot $ytName) (Join-Path $toolRoot $quickJsName)}
    $quickJsActual=(& (Join-Path $toolRoot $quickJsName) --version | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $quickJsActual -ne $quickJsVersion){throw 'Bundled QuickJS-NG failed version verification.'}
    $ytActual=(& (Join-Path $toolRoot $ytName) --version | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $ytActual -ne $ytVersion){throw 'Bundled yt-dlp failed version verification.'}
}
$files=@($ytName,$quickJsName | ForEach-Object {
    $path=Join-Path $toolRoot $_
    [ordered]@{name=$_;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{runtime=$Runtime;ytDlpVersion=$ytVersion;quickJsVersion=$quickJsVersion;jsRuntime='quickjs';ytDlpSource=$ytUrl;quickJsSource=$quickJsUrl;quickJsSha256=$quickJsHash;quickJsBuild=$quickJsBuild;hashStage='before-app-signing';files=$files} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $toolRoot 'download-tools.json') -Encoding utf8
Write-Output "Bundled yt-dlp $ytVersion and QuickJS-NG $quickJsVersion ($Runtime), verified SHA256."
