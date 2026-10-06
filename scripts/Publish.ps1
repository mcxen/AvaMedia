param([ValidateSet('win-x64','osx-arm64','osx-x64')][string]$Runtime = 'win-x64', [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.5', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
function Remove-LegacyDownloadFiles([string]$Root) {
    foreach ($relative in @('tools/deno.exe','tools/deno','scripts/Collect-DenoLicenses.js')) {
        $path=Join-Path $Root $relative
        if(Test-Path -LiteralPath $path -PathType Leaf){Remove-Item -LiteralPath $path -Force}
    }
    $notices=Join-Path $Root 'licenses/download-tools'
    if(Test-Path -LiteralPath $notices -PathType Container) {
        Get-ChildItem -LiteralPath $notices -File -Filter 'deno-*' | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    }
}
$sdk = Join-Path $taskRoot ('.tools/dotnet/dotnet' + $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { '.exe' } else { '' }))
if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$publishRoot = Join-Path $taskRoot ('artifacts/release/' + $Version + '/' + $Runtime)
if ($OutputDirectory) { $publishRoot = [IO.Path]::GetFullPath($OutputDirectory) }
[string[]]$startupOptions = if ($Runtime -eq 'win-x64') { @('-p:PublishReadyToRun=true') } else { @() }
& $sdk publish (Join-Path $taskRoot 'src/AvaMedia.Desktop/AvaMedia.Desktop.csproj') -c Release -r $Runtime --self-contained true -o $publishRoot "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false @startupOptions --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
& (Join-Path $PSScriptRoot 'Collect-Licenses.ps1')
& (Join-Path $PSScriptRoot 'Bundle-DownloadTools.ps1') -Runtime $Runtime -Destination (Join-Path $publishRoot 'tools')
$runtimePackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$runtimePack = Join-Path $runtimePackages ('microsoft.netcore.app.runtime.' + $Runtime)
if (Test-Path -LiteralPath $runtimePack) {
    $runtimeNotices = Join-Path $taskRoot 'licenses/dotnet-runtime'
    New-Item -ItemType Directory -Path $runtimeNotices -Force | Out-Null
    Get-ChildItem -LiteralPath $runtimePack -Recurse -File | Where-Object { $_.Name -match '(?i)license|notice' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $runtimeNotices -Force }
}
foreach ($name in @('LICENSE','COPYRIGHT','README.md','UISPEC.MD','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $taskRoot $name) -Destination $publishRoot -Force }
Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses') -Destination $publishRoot -Recurse -Force
$publishDocs = Join-Path $publishRoot 'docs'
New-Item -ItemType Directory -Path $publishDocs -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'docs') -Filter '*.md' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $publishDocs -Force }
if (Test-Path -LiteralPath (Join-Path $taskRoot 'docs/assets')) { Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/assets') -Destination $publishDocs -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $taskRoot 'scripts') -Destination $publishRoot -Recurse -Force
Remove-LegacyDownloadFiles $publishRoot
$zip = Join-Path $taskRoot ('artifacts/AvaMedia-' + $Version + '-' + $Runtime + '.zip')
New-Item -ItemType Directory -Path (Split-Path -Parent $zip) -Force | Out-Null
if ($Runtime.StartsWith('osx-')) {
    $bundleRoot = Join-Path $taskRoot ('artifacts/release/' + $Version + '/' + $Runtime + '-bundle')
    $bundle = Join-Path $bundleRoot 'AvaMedia.app'
    $nativeRoot = Join-Path $bundle 'Contents/MacOS'
    $resources = Join-Path $bundle 'Contents/Resources'
    New-Item -ItemType Directory -Path $nativeRoot,$resources -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'src/AvaMedia.Desktop/Assets/AppIcon/v2/app.icns') -Destination (Join-Path $resources 'AvaMedia.icns') -Force
    Get-ChildItem -LiteralPath $publishRoot -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $nativeRoot -Force }
    Get-ChildItem -LiteralPath $publishRoot -Directory | Where-Object { $_.Name -notin @('licenses','docs','scripts') } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $nativeRoot -Recurse -Force }
    foreach ($directory in @('licenses','docs','scripts')) { Copy-Item -LiteralPath (Join-Path $publishRoot $directory) -Destination $resources -Recurse -Force }
    foreach ($name in @('LICENSE','COPYRIGHT','README.md','UISPEC.MD','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $publishRoot $name) -Destination $resources -Force }
    Remove-LegacyDownloadFiles $nativeRoot
    Remove-LegacyDownloadFiles $resources
    $plist = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'macos/Info.plist') -Raw).Replace('__VERSION__',$Version)
    [IO.File]::WriteAllText((Join-Path $bundle 'Contents/Info.plist'),$plist,[Text.UTF8Encoding]::new($false))
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) {
        foreach ($tool in @('yt-dlp','qjs')) { & chmod +x (Join-Path $nativeRoot ('tools/'+$tool)) }
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
            $executable = $_.Name -in @('AvaMedia.Desktop','yt-dlp','qjs','createdump') -or $_.Extension -eq '.dylib' -or $_.Extension -eq '.sh'
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
