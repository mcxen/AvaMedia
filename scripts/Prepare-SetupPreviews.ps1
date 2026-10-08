param([Parameter(Mandatory=$true)][string]$PublishDirectory, [string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
$output = Join-Path $PublishDirectory 'setup-previews'
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $DotNet
$start.WorkingDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add((Join-Path $start.WorkingDirectory 'AvaMedia.Desktop.dll'))
$start.ArgumentList.Add('--export-setup-previews')
$start.ArgumentList.Add([IO.Path]::GetFullPath($output))
$process = [Diagnostics.Process]::Start($start)
try {
    $standardOutput = $process.StandardOutput.ReadToEndAsync()
    $standardError = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(120000)) { $process.Kill($true); $process.WaitForExit(); throw '皮肤预览资源生成超时。' }
    if ($process.ExitCode -ne 0) { throw ('皮肤预览资源生成失败：' + $standardError.GetAwaiter().GetResult()) }
    foreach ($theme in @('Light', 'Dark', 'MacOS9', 'WindowsXP')) {
        $path = Join-Path $output ($theme + '.png')
        if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "缺少皮肤预览：$theme" }
    }
} finally { $process.Dispose() }
