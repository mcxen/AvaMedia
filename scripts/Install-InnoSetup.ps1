param([string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) '.tools/innosetup'))
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath (Join-Path $target 'ISCC.exe')) { Write-Output (Join-Path $target 'ISCC.exe'); return }
New-Item -ItemType Directory -Path $target -Force | Out-Null
$setup = Join-Path $target 'compiler-setup.exe'
Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_4_3/innosetup-6.4.3.exe' -OutFile $setup
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne 'F3C42116542C4CC57263C5BA6C4FEABFC49FE771F2F98A79D2F7628B8762723B') { throw 'Inno Setup download checksum mismatch.' }
$process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/NOICONS',('/DIR="' + $target + '"')) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Inno Setup installation failed: $($process.ExitCode)" }
Write-Output (Join-Path $target 'ISCC.exe')
