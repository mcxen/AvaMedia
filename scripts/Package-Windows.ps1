param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.5', [string]$PublishDirectory, [string]$Compiler)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Branding.ps1')
$appBrand = Get-AppBrand
if (!$PublishDirectory) { $PublishDirectory = Join-Path $taskRoot "artifacts/release/$Version/win-x64" }
$publish = [IO.Path]::GetFullPath($PublishDirectory)
if (!(Test-Path -LiteralPath (Join-Path $publish 'AvaMedia.Desktop.exe'))) { throw 'Run Publish.ps1 -Runtime win-x64 first.' }
if (!$Compiler) { $Compiler = & (Join-Path $PSScriptRoot 'Install-InnoSetup.ps1') }
$output = Join-Path $taskRoot 'artifacts'
& $Compiler "/DAppVersion=$Version" "/DAppChineseName=$($appBrand.ChineseName)" "/DAppDisplayName=$($appBrand.DisplayName)" "/DPublishDir=$publish" "/DOutputDir=$output" (Join-Path $PSScriptRoot 'installer/AvaMedia.iss')
if ($LASTEXITCODE -ne 0) { throw 'Windows installer compilation failed.' }
Write-Output (Join-Path $output "AvaMedia-$Version-win-x64-setup.exe")
