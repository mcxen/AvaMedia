param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.5',[string]$ProofDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$proof = Join-Path $taskRoot 'artifacts/installer-verification'
if($ProofDirectory){$proof=[IO.Path]::GetFullPath($ProofDirectory)}
$target = Join-Path $proof 'app'
New-Item -ItemType Directory -Path $proof -Force | Out-Null
$setup = Join-Path $taskRoot "artifacts/AvaMedia-$Version-win-x64-setup.exe"
$process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="'+$target+'"'),('/LOG="'+(Join-Path $proof 'install.log')+'"')) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw 'Installer execution failed.' }
$exe = Join-Path $target 'AvaMedia.Desktop.exe'
if (!(Test-Path -LiteralPath $exe) -or (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0] -ne $Version) { throw 'Installed executable/version mismatch.' }
foreach ($name in @('LICENSE','COPYRIGHT','THIRD-PARTY-NOTICES.md','scripts/Install-MediaTools.ps1','AvaMedia.Core.dll','tools/yt-dlp.exe','tools/qjs.exe','tools/download-tools.json')) { if (!(Test-Path -LiteralPath (Join-Path $target $name))) { throw "Installed $name is missing." } }
$manifest=Get-Content -LiteralPath (Join-Path $target 'tools/download-tools.json') -Raw | ConvertFrom-Json
foreach($tool in $manifest.files) {
    $path=Join-Path $target ('tools/'+$tool.name)
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $tool.sha256){throw "Installed $($tool.name) checksum mismatch."}
}
$ytVersion=(& (Join-Path $target 'tools/yt-dlp.exe') --version | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $ytVersion -ne $manifest.ytDlpVersion){throw 'Installed yt-dlp version verification failed.'}
$quickJsVersion=(& (Join-Path $target 'tools/qjs.exe') --version | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $quickJsVersion -ne $manifest.quickJsVersion){throw 'Installed QuickJS-NG version verification failed.'}
$capture = Join-Path $proof 'capture'
$process = Start-Process -FilePath $exe -ArgumentList @('--capture',('"'+$capture+'"'),'--download') -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Installed client startup timed out.' }
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $capture 'main.png')) -or !(Test-Path -LiteralPath (Join-Path $capture 'download.png'))) { throw 'Installed client failed its native startup/download capture.' }
$uninstall = Start-Process -FilePath (Join-Path $target 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
if ($uninstall.ExitCode -ne 0 -or (Test-Path -LiteralPath $exe)) { throw 'Installer uninstall check failed.' }
@{ version=$Version; installation=$true; nativeStartup=$true; nativeDownloadWindow=$true; ytDlpVersion=$manifest.ytDlpVersion; quickJsVersion=$manifest.quickJsVersion; toolHashesVerified=$true; uninstallation=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $proof 'report.json') -Encoding utf8
Write-Output 'Verified Windows installation, native startup, version, licenses and uninstall.'
