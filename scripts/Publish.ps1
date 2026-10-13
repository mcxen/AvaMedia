param([ValidateSet('win-x64','osx-arm64')][string]$Runtime = 'win-x64', [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.5', [string]$OutputDirectory, [switch]$SkipBrowser, [string]$BrowserLicenseDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Branding.ps1')
$appBrand = Get-AppBrand
function Assert-EmptyPublishDirectory([string]$Path) {
    if((Test-Path -LiteralPath $Path) -and (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1)) {
        throw "发布目录必须为空，请使用新的输出目录：$Path"
    }
}
$sdk = Join-Path $taskRoot ('.tools/dotnet/dotnet' + $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { '.exe' } else { '' }))
if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$publishRoot = Join-Path $taskRoot ('artifacts/release/' + $Version + '/' + $Runtime)
if ($OutputDirectory) { $publishRoot = [IO.Path]::GetFullPath($OutputDirectory) }
Assert-EmptyPublishDirectory $publishRoot
if ($Runtime.StartsWith('osx-')) {
    $bundleRoot = $publishRoot + '-bundle'
    Assert-EmptyPublishDirectory $bundleRoot
}
$browserRoot = Join-Path $taskRoot 'src/AvaMedia.Browser/bin/Release/net8.0-browser/publish/wwwroot'
if (!$SkipBrowser) {
    & $sdk publish (Join-Path $taskRoot 'src/AvaMedia.Browser/AvaMedia.Browser.csproj') -c Release "-p:Version=$Version" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Browser publish failed.' }
    $BrowserLicenseDirectory = Join-Path $publishRoot 'browser-licenses'
    & (Join-Path $PSScriptRoot 'Collect-Licenses.ps1') -Project 'src/AvaMedia.Browser/AvaMedia.Browser.csproj' -Runtime browser-wasm -OutputDirectory $BrowserLicenseDirectory
}
if (!(Test-Path -LiteralPath (Join-Path $browserRoot 'index.html')) -or !$BrowserLicenseDirectory -or !(Test-Path -LiteralPath (Join-Path $BrowserLicenseDirectory 'manifest.json'))) {
    throw 'Browser wwwroot and its generated licenses are required (download the shared browser artifact when using -SkipBrowser).'
}
[string[]]$startupOptions = if ($Runtime -eq 'win-x64') { @('-p:PublishReadyToRun=true') } else { @() }
& $sdk publish (Join-Path $taskRoot 'src/AvaMedia.Desktop/AvaMedia.Desktop.csproj') -c Release -r $Runtime --self-contained false -o $publishRoot "-p:Version=$Version" -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false @startupOptions --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
& (Join-Path $PSScriptRoot 'Build-Bootstrap.ps1') -Runtime $Runtime -Version $Version -OutputDirectory $publishRoot
& (Join-Path $PSScriptRoot 'Prepare-Runtime.ps1') -Runtime $Runtime -PublishDirectory $publishRoot
& (Join-Path $PSScriptRoot 'Bundle-DownloadTools.ps1') -Runtime $Runtime -Destination (Join-Path $publishRoot 'tools')
& (Join-Path $PSScriptRoot 'Bundle-MediaTools.ps1') -Runtime $Runtime -Destination (Join-Path $publishRoot 'tools')
& (Join-Path $PSScriptRoot 'Prepare-SetupPreviews.ps1') -PublishDirectory $publishRoot -DotNet $sdk
& (Join-Path $PSScriptRoot 'Collect-Licenses.ps1') -Runtime $Runtime -OutputDirectory (Join-Path $publishRoot 'licenses-generated') -BrowserLicenseDirectory $BrowserLicenseDirectory
# SDK content may already have supplied font notices. Replace it with the complete RID-specific bundle.
if (Test-Path -LiteralPath (Join-Path $publishRoot 'licenses')) { Remove-Item -LiteralPath (Join-Path $publishRoot 'licenses') -Recurse -Force }
Move-Item -LiteralPath (Join-Path $publishRoot 'licenses-generated') -Destination (Join-Path $publishRoot 'licenses')
if (!$SkipBrowser) { Remove-Item -LiteralPath $BrowserLicenseDirectory -Recurse -Force }
& (Join-Path $PSScriptRoot 'Bundle-UserDocuments.ps1') -Destination $publishRoot
$zipSuffix = if ($Runtime -eq 'win-x64') { '-portable.zip' } else { '.zip' }
$zip = Join-Path $taskRoot ('artifacts/AvaMedia-' + $Version + '-' + $Runtime + $zipSuffix)
New-Item -ItemType Directory -Path (Split-Path -Parent $zip) -Force | Out-Null
if ($Runtime.StartsWith('osx-')) {
    $bundle = Join-Path $bundleRoot $appBrand.MacBundleName
    $nativeRoot = Join-Path $bundle 'Contents/MacOS'
    $resources = Join-Path $bundle 'Contents/Resources'
    New-Item -ItemType Directory -Path $nativeRoot,$resources -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'src/AvaMedia.Desktop/Assets/AppIcon/v2/app.icns') -Destination (Join-Path $resources 'AvaMedia.icns') -Force
    Get-ChildItem -LiteralPath $publishRoot -File | Where-Object { $_.Name -notin @('Branding.props','LICENSE','COPYRIGHT','README.md','THIRD-PARTY-NOTICES.md') } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $nativeRoot -Force }
    Get-ChildItem -LiteralPath $publishRoot -Directory | Where-Object { $_.Name -notin @('licenses','docs') } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $nativeRoot -Recurse -Force }
    foreach ($directory in @('licenses','docs')) { Copy-Item -LiteralPath (Join-Path $publishRoot $directory) -Destination $resources -Recurse -Force }
    foreach ($name in @('Branding.props','LICENSE','COPYRIGHT','README.md','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $publishRoot $name) -Destination $resources -Force }
    $plist = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'macos/Info.plist') -Raw -Encoding UTF8).Replace('__VERSION__',$Version).Replace('__APPLICATION_NAME__',[Security.SecurityElement]::Escape($appBrand.EnglishName))
    [IO.File]::WriteAllText((Join-Path $bundle 'Contents/Info.plist'),$plist,[Text.UTF8Encoding]::new($false))
    & python3 (Join-Path $PSScriptRoot 'macos/Declare-MediaTypes.py') $bundle
    if ($LASTEXITCODE -ne 0) { throw 'macOS media file association declaration failed.' }
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) {
        foreach ($tool in @('yt-dlp','qjs','ffmpeg','ffprobe')) { & chmod +x (Join-Path $nativeRoot ('tools/'+$tool)) }
        & /usr/bin/codesign --force --deep --sign - $bundle
        if ($LASTEXITCODE -ne 0) { throw 'Ad-hoc application signing failed.' }
    }
    # ZIP stores Unix permission bits even when assembled on Windows.
    Add-Type -AssemblyName System.IO.Compression
    $archiveStream = [IO.File]::Open($zip,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($archiveStream,[IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $bundle -Recurse -File | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($bundleRoot,$_.FullName).Replace('\','/')
            $entry = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$_.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)
            $executable = $_.Name -in @('AvaMedia.Desktop','yt-dlp','qjs','ffmpeg','ffprobe','createdump') -or $_.Extension -eq '.dylib' -or $_.Extension -eq '.sh'
            $mode = if ($executable) { 33261 } else { 33188 } # regular file: 0755 / 0644
            $entry.ExternalAttributes = [int]($mode -shl 16)
        }
    } finally { $archive.Dispose(); $archiveStream.Dispose() }
    # Windows ZipArchive records the Windows creator OS. Mark central-directory
    # entries as Unix so macOS extractors honor the stored execute permissions.
    $zipStream = [IO.File]::Open($zip,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $tailLength = [int][Math]::Min($zipStream.Length,65557)
        $tail = [byte[]]::new($tailLength)
        $zipStream.Position = $zipStream.Length - $tailLength
        $null = $zipStream.Read($tail,0,$tailLength)
        $end = -1
        for ($offset=$tailLength-22; $offset -ge 0; $offset--) {
            if ([BitConverter]::ToUInt32($tail,$offset) -eq 0x06054b50 -and $offset+22+[BitConverter]::ToUInt16($tail,$offset+20) -eq $tailLength) { $end=$offset; break }
        }
        if ($end -lt 0) { throw 'ZIP central directory was not found.' }
        $count = [BitConverter]::ToUInt16($tail,$end+10)
        $zipStream.Position = [BitConverter]::ToUInt32($tail,$end+16)
        for ($entryIndex=0; $entryIndex -lt $count; $entryIndex++) {
            $position = $zipStream.Position
            $header = [byte[]]::new(46)
            $null = $zipStream.Read($header,0,46)
            if ([BitConverter]::ToUInt32($header,0) -ne 0x02014b50) { throw 'Invalid ZIP central directory entry.' }
            $zipStream.Position = $position+5
            $zipStream.WriteByte(3)
            $zipStream.Position = $position+46+[BitConverter]::ToUInt16($header,28)+[BitConverter]::ToUInt16($header,30)+[BitConverter]::ToUInt16($header,32)
        }
    } finally { $zipStream.Dispose() }
} else { Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zip -Force }
Write-Output $zip
