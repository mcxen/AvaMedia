param([int]$ParentProcessId, [string]$Kind, [string]$Package, [string]$Target, [string]$Stage, [string]$Backup, [string]$ErrorFile)
$ErrorActionPreference = 'Stop'
$moved = $false
$installed = $false
try {
    $parentProcess = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
    if ($parentProcess -and !$parentProcess.WaitForExit(300000)) { throw 'Application has not exited.' }
    $executable = Join-Path $Target 'AvaMedia.Desktop.exe'
    $instances = @(Get-Process -Name 'AvaMedia.Desktop' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable })
    if ($instances.Count -gt 0) { throw 'Close all application instances before updating.' }
    if ($Kind -eq 'installer') {
        # Inno Setup's own rollback handles installer failures. Never close another running app.
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', ('/DIR="' + $Target + '"'), ('/LOG="' + (Join-Path (Split-Path -Parent $Package) 'setup.log') + '"'))
        $setup = Start-Process -FilePath $Package -ArgumentList $arguments -Wait -PassThru
        if ($setup.ExitCode -ne 0) { throw "Installer exit code: $($setup.ExitCode)" }
        Remove-Item -LiteralPath $Stage -Recurse -Force
    } elseif ($Kind -eq 'portable') {
        if (!(Test-Path -LiteralPath (Join-Path $Stage 'AvaMedia.Desktop.exe'))) { throw 'Application missing from update.' }
        Move-Item -LiteralPath $Target -Destination $Backup
        $moved = $true
        Move-Item -LiteralPath $Stage -Destination $Target
        $installed = $true
        # Keep the previous portable directory: users may have stored media beside the executable.
        # It also provides a recoverable copy of the previous version.
    } else { throw 'Unsupported update package.' }
    if (Test-Path -LiteralPath $ErrorFile) { Remove-Item -LiteralPath $ErrorFile -Force }
    Remove-Item -LiteralPath (Split-Path -Parent $Package) -Recurse -Force
} catch {
    if ($moved -and !$installed -and !(Test-Path -LiteralPath $Target)) {
        Move-Item -LiteralPath $Backup -Destination $Target -ErrorAction SilentlyContinue
    }
    [IO.File]::WriteAllText($ErrorFile, ('更新安装失败，请从发布页手动安装。' + [Environment]::NewLine + $_.Exception.Message), [Text.UTF8Encoding]::new($false))
    exit 1
}
