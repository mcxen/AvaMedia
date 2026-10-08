param([Parameter(Mandatory=$true)][string]$Application, [Parameter(Mandatory=$true)][string]$RuntimeBase, [Parameter(Mandatory=$true)][string]$ErrorFile, [Parameter(Mandatory=$true)][string]$ProgressFile)
$ErrorActionPreference = 'Stop'
$stage = $null
$lock = $null
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    New-Item -ItemType Directory -Path $RuntimeBase -Force | Out-Null
    function Set-InstallStatus([string]$Message) {
        $temporary = $ProgressFile + '.' + $PID + '.tmp'
        [IO.File]::WriteAllText($temporary, $Message, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $ProgressFile -Force
    }
    Set-InstallStatus '正在准备安装…'
    # Serialize concurrent first launches without exposing a partially installed runtime.
    $deadline = [DateTime]::UtcNow.AddMinutes(15)
    while (!$lock) {
        try { $lock = [IO.File]::Open((Join-Path $RuntimeBase '.install.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] { if ([DateTime]::UtcNow -ge $deadline) { throw '另一窗口仍在安装运行时，请稍后重试。' }; Set-InstallStatus '正在等待另一窗口完成安装…'; Start-Sleep -Milliseconds 500 }
    }
    $manifest = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $Application) 'runtime-bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.rid -ne 'win-x64' -or $manifest.version -notmatch '^8\.0\.\d+$' -or @($manifest.packages).Count -ne 2) { throw '运行时安装信息无效。' }
    $target = Join-Path $RuntimeBase ($manifest.version + '-' + $manifest.rid)
    function Test-Runtime([string]$Path) {
        $check = Start-Process -FilePath $Application -ArgumentList @('--bootstrap-check-root', ('"' + $Path + '"')) -PassThru -Wait
        return $check.ExitCode -eq 0
    }
    $existing = Start-Process -FilePath $Application -ArgumentList '--bootstrap-check' -PassThru -Wait
    if ($existing.ExitCode -eq 0) { exit 0 }
    $stage = Join-Path $RuntimeBase ('.stage-' + [Guid]::NewGuid().ToString('N'))
    $content = Join-Path $stage 'runtime'
    New-Item -ItemType Directory -Path $content -Force | Out-Null
    foreach ($package in $manifest.packages) {
        $uri = [Uri]$package.url
        if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'builds.dotnet.microsoft.com' -or $package.sha512 -notmatch '^[a-fA-F0-9]{128}$') { throw '运行时下载信息无效。' }
        $archive = Join-Path $stage ([Guid]::NewGuid().ToString('N') + '.zip')
        Set-InstallStatus ('正在下载 ' + $package.name + '…')
        $client = [Net.WebClient]::new()
        try { $client.DownloadFile($uri, $archive) } finally { $client.Dispose() }
        Set-InstallStatus ('正在校验 ' + $package.name + '…')
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $package.sha512) { throw '运行时校验失败，请重试下载。' }
        Set-InstallStatus ('正在安装 ' + $package.name + '…')
        Expand-Archive -LiteralPath $archive -DestinationPath $content -Force
    }
    Set-InstallStatus '正在确认安装结果…'
    if (!(Test-Runtime $content)) { throw '下载的运行时无法启动此版本软件。' }
    # A damaged completed cache can be replaced; active compatible caches are reused above.
    if (Test-Path -LiteralPath $target) { $target += '-' + [Guid]::NewGuid().ToString('N') }
    [IO.Directory]::Move($content, $target)
    $record = Start-Process -FilePath $Application -ArgumentList @('--bootstrap-record-root', ('"' + $target + '"')) -PassThru -Wait
    if ($record.ExitCode -ne 0) { throw '无法保存运行时安装信息。' }
    Set-InstallStatus '运行环境已就绪'
    if (Test-Path -LiteralPath $ErrorFile) { Remove-Item -LiteralPath $ErrorFile -Force }
} catch {
    [IO.File]::WriteAllText($ErrorFile, $_.Exception.Message, [Text.UTF8Encoding]::new($false))
    exit 1
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if ($lock) { $lock.Dispose() }
}
