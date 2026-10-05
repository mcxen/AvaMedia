param([ValidateSet('win-x64','osx-arm64','osx-x64')][string]$Runtime = 'win-x64', [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.2')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $taskRoot ('.tools/dotnet/dotnet' + $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { '.exe' } else { '' }))
if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$publishRoot = Join-Path $taskRoot ('artifacts/release/' + $Version + '/' + $Runtime)
& $sdk publish (Join-Path $taskRoot 'src/AvaMedia.Desktop/AvaMedia.Desktop.csproj') -c Release -r $Runtime --self-contained true -o $publishRoot -p:DebugType=None -p:DebugSymbols=false --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
& (Join-Path $PSScriptRoot 'Collect-Licenses.ps1')
$runtimePackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$runtimePack = Join-Path $runtimePackages ('microsoft.netcore.app.runtime.' + $Runtime)
if (Test-Path -LiteralPath $runtimePack) {
    $runtimeNotices = Join-Path $taskRoot 'licenses/dotnet-runtime'
    New-Item -ItemType Directory -Path $runtimeNotices -Force | Out-Null
    Get-ChildItem -LiteralPath $runtimePack -Recurse -File | Where-Object { $_.Name -match '(?i)license|notice' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $runtimeNotices -Force }
}
foreach ($name in @('LICENSE','README.md','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $taskRoot $name) -Destination $publishRoot -Force }
Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses') -Destination $publishRoot -Recurse -Force
$publishDocs = Join-Path $publishRoot 'docs'
New-Item -ItemType Directory -Path $publishDocs -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'docs') -Filter '*.md' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $publishDocs -Force }
Copy-Item -LiteralPath (Join-Path $taskRoot 'scripts') -Destination $publishRoot -Recurse -Force
$zip = Join-Path $taskRoot ('artifacts/AvaMedia-' + $Version + '-' + $Runtime + '.zip')
if ($Runtime.StartsWith('osx-')) {
    $bundleRoot = Join-Path $taskRoot ('artifacts/release/' + $Version + '/' + $Runtime + '-bundle')
    $bundle = Join-Path $bundleRoot 'AvaMedia.app'
    $nativeRoot = Join-Path $bundle 'Contents/MacOS'
    $resources = Join-Path $bundle 'Contents/Resources'
    New-Item -ItemType Directory -Path $nativeRoot,$resources -Force | Out-Null
    Get-ChildItem -LiteralPath $publishRoot -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $nativeRoot -Force }
    foreach ($directory in @('licenses','docs','scripts')) { Copy-Item -LiteralPath (Join-Path $publishRoot $directory) -Destination $resources -Recurse -Force }
    foreach ($name in @('LICENSE','README.md','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $publishRoot $name) -Destination $resources -Force }
    $plist = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'macos/Info.plist') -Raw).Replace('__VERSION__',$Version)
    [IO.File]::WriteAllText((Join-Path $bundle 'Contents/Info.plist'),$plist,[Text.UTF8Encoding]::new($false))
    # ZIP stores Unix permission bits even when assembled on Windows.
    Add-Type -AssemblyName System.IO.Compression
    $archiveStream = [IO.File]::Open($zip,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($archiveStream,[IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $bundle -Recurse -File | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($bundleRoot,$_.FullName).Replace('\','/')
            $entry = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$_.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)
            $executable = $_.Name -eq 'AvaMedia.Desktop' -or $_.Extension -eq '.dylib' -or $_.Extension -eq '.sh' -or $_.Name -eq 'createdump'
            $mode = if ($executable) { 33261 } else { 33188 } # regular file: 0755 / 0644
            $entry.ExternalAttributes = [int]($mode -shl 16)
        }
    } finally { $archive.Dispose(); $archiveStream.Dispose() }
} else { Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zip -Force }
Write-Output $zip
