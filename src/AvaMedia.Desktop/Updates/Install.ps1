param([string]$Kind, [string]$Package, [string]$Target, [string]$Stage, [string]$Backup, [string]$ErrorFile,
    [string]$Version, [long]$Size, [string]$Sha256, [string]$InstallLock)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$work = Split-Path -Parent $Package
$log = Join-Path $work 'install.log'
$cancelFile = Join-Path $work 'cancel'
$guardian = $null
$installation = $null
$moved = $false
$installed = $false
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-InstallLog([string]$Message) {
    [IO.File]::AppendAllText($log, ([DateTime]::UtcNow.ToString('O') + ' ' + $Message + [Environment]::NewLine), $utf8)
}
function Wait-ApplicationInstances {
    $executable = Join-Path $Target 'AvaMedia.Desktop.exe'
    do {
        if (Test-Path -LiteralPath $cancelFile) { Write-InstallLog 'Update cancelled.'; exit 0 }
        $instances = @(Get-Process -Name 'AvaMedia.Desktop' -ErrorAction SilentlyContinue | Where-Object { !$_.Path -or $_.Path -eq $executable })
        if ($instances.Count -gt 0) { Start-Sleep -Seconds 1 }
    } while ($instances.Count -gt 0)
}
function Get-InstalledVersion {
    return [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Target 'AvaMedia.Desktop.dll')).Version
}
function Complete-Installation {
    $receipt = Join-Path $work 'installed-version'
    [IO.File]::WriteAllText(($receipt + '.tmp'), $Version, $utf8)
    Move-Item -LiteralPath ($receipt + '.tmp') -Destination $receipt -Force
    if (Test-Path -LiteralPath $ErrorFile) { Remove-Item -LiteralPath $ErrorFile -Force }
    foreach ($name in @($Package, (Join-Path $work 'pending.json'), (Join-Path $work 'helper.json'), (Join-Path $work 'ready'), (Join-Path $work 'armed'))) {
        if (Test-Path -LiteralPath $name) { Remove-Item -LiteralPath $name -Force }
    }
    Write-InstallLog "Update $Version complete."
    $restart = Join-Path $work 'restart'
    if (Test-Path -LiteralPath $restart) {
        Remove-Item -LiteralPath $restart -Force
        $script:installation.Dispose(); $script:installation = $null
        $script:guardian.Dispose(); $script:guardian = $null
        try { Start-Process -FilePath (Join-Path $Target 'AvaMedia.Desktop.exe') -WorkingDirectory $Target }
        catch { Write-InstallLog ('Update installed, but application relaunch failed: ' + $_.Exception.Message) }
    }
}
try {
    if ($Kind -notin @('installer', 'portable') -or !(Test-Path -LiteralPath $Package) -or !(Test-Path -LiteralPath $Stage)) { throw 'Prepared update is missing.' }
    try { $guardian = [IO.File]::Open((Join-Path $work 'guardian.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch [IO.IOException] { exit 0 } # Another helper owns this prepared update.
    [IO.File]::WriteAllText((Join-Path $work 'ready'), $PID.ToString(), $utf8)
    Write-InstallLog "Update $Version guardian ready; waiting for application exit."
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (!(Test-Path -LiteralPath (Join-Path $work 'armed'))) {
        if (Test-Path -LiteralPath $cancelFile) { exit 0 }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Prepared update was not armed.' }
        Start-Sleep -Seconds 1
    }
    Wait-ApplicationInstances
    while (!$installation) {
        if (Test-Path -LiteralPath $cancelFile) { exit 0 }
        try { $installation = [IO.File]::Open($InstallLock, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] { Start-Sleep -Seconds 1 }
    }
    Wait-ApplicationInstances
    if (Test-Path -LiteralPath $cancelFile) { exit 0 }
    if ((Get-InstalledVersion) -ge [Version]$Version) {
        Write-InstallLog 'An equal or newer version is already installed; skipping replacement.'
        Remove-Item -LiteralPath $Stage -Recurse -Force
        Complete-Installation
        exit 0
    }
    if ((Get-Item -LiteralPath $Package).Length -ne $Size -or (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash -ne $Sha256) { throw 'Update package size or SHA256 changed.' }
    Write-InstallLog "Installing $Version into $Target."
    if ($Kind -eq 'installer') {
        # Inno Setup owns installer rollback. Never close or restart another application instance.
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', ('/DIR="' + $Target + '"'), ('/LOG="' + (Join-Path $work 'setup.log') + '"'))
        $setup = Start-Process -FilePath $Package -ArgumentList $arguments -Wait -PassThru
        if ($setup.ExitCode -ne 0) { throw "Installer exit code: $($setup.ExitCode)" }
        if ((Get-InstalledVersion) -lt [Version]$Version) { throw 'Installer did not replace the application version.' }
        Remove-Item -LiteralPath $Stage -Recurse -Force
    } else {
        if (!(Test-Path -LiteralPath (Join-Path $Stage 'AvaMedia.Desktop.exe')) -or (Test-Path -LiteralPath $Backup)) { throw 'Prepared application or backup path is invalid.' }
        if (([Reflection.AssemblyName]::GetAssemblyName((Join-Path $Stage 'AvaMedia.Desktop.dll')).Version) -lt [Version]$Version) { throw 'Prepared application version is invalid.' }
        if (Test-Path -LiteralPath $cancelFile) { exit 0 }
        Move-Item -LiteralPath $Target -Destination $Backup
        $moved = $true
        Move-Item -LiteralPath $Stage -Destination $Target
        if ((Get-InstalledVersion) -lt [Version]$Version) { throw 'Replacement application version is invalid.' }
        $installed = $true
        # Preserve the old portable directory, which may contain the user's media.
    }
    Complete-Installation
} catch {
    $failure = $_.Exception.Message
    if ($moved -and !$installed -and (Test-Path -LiteralPath $Backup)) {
        if ((Test-Path -LiteralPath $Target) -and !(Test-Path -LiteralPath $Stage)) {
            Move-Item -LiteralPath $Target -Destination $Stage -ErrorAction SilentlyContinue
        }
        if (!(Test-Path -LiteralPath $Target)) { Move-Item -LiteralPath $Backup -Destination $Target -ErrorAction SilentlyContinue }
    }
    Write-InstallLog $failure
    [IO.File]::WriteAllText($ErrorFile, ('更新安装失败，待安装文件已保留。' + [Environment]::NewLine + $failure + [Environment]::NewLine + '日志：' + $log), $utf8)
    exit 1
} finally {
    if ($installation) { $installation.Dispose() }
    if ($guardian) { $guardian.Dispose() }
}
