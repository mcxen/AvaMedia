param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.3')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$proof = Join-Path $taskRoot 'artifacts/installer-verification'
$target = Join-Path $proof 'app'
New-Item -ItemType Directory -Path $proof -Force | Out-Null
$setup = Join-Path $taskRoot "artifacts/AvaMedia-$Version-win-x64-setup.exe"
$process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="'+$target+'"'),('/LOG="'+(Join-Path $proof 'install.log')+'"')) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw 'Installer execution failed.' }
$exe = Join-Path $target 'AvaMedia.Desktop.exe'
if (!(Test-Path -LiteralPath $exe) -or (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0] -ne $Version) { throw 'Installed executable/version mismatch.' }
foreach ($name in @('LICENSE','COPYRIGHT','THIRD-PARTY-NOTICES.md','scripts/Install-MediaTools.ps1','AvaMedia.Core.dll')) { if (!(Test-Path -LiteralPath (Join-Path $target $name))) { throw "Installed $name is missing." } }
$capture = Join-Path $proof 'capture'
$process = Start-Process -FilePath $exe -ArgumentList @('--capture',('"'+$capture+'"')) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Installed client startup timed out.' }
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $capture 'main.png'))) { throw 'Installed client failed its native startup capture.' }
$uninstall = Start-Process -FilePath (Join-Path $target 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
if ($uninstall.ExitCode -ne 0 -or (Test-Path -LiteralPath $exe)) { throw 'Installer uninstall check failed.' }
@{ version=$Version; installation=$true; nativeStartup=$true; uninstallation=$true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $proof 'report.json') -Encoding utf8
Write-Output 'Verified Windows installation, native startup, version, licenses and uninstall.'
