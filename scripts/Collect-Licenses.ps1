param(
    [string]$Project = 'src/AvaMedia.Desktop/AvaMedia.Desktop.csproj',
    [Parameter(Mandatory)][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BrowserLicenseDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $taskRoot 'licenses'
$licenseRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $licenseRoot) -and (Get-ChildItem -LiteralPath $licenseRoot -Force | Select-Object -First 1)) {
    throw "License output must be empty: $licenseRoot"
}
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
$projectPath = Join-Path $taskRoot $Project
$assets = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $projectPath) 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$targetNames = @($assets.targets.Keys | Where-Object { $_.EndsWith('/' + $Runtime, [StringComparison]::Ordinal) })
if ($targetNames.Count -ne 1) { throw "Restore/publish $Project for $Runtime before collecting licenses." }
$target = $assets.targets[$targetNames[0]]
$packageFolders = @($assets.packageFolders.Keys)
$manifest = @()
foreach ($key in @($target.Keys | Sort-Object)) {
    $entry = $target[$key]
    if ($entry.type -ne 'package') { continue }
    $id, $version = $key.Split('/')
    # RID targets include empty entries for other platforms and build-only packages.
    $shippedAssets = @('runtime','native','contentFiles') | ForEach-Object {
        if ($entry.ContainsKey($_)) { $entry[$_].Keys | Where-Object { !($_.EndsWith('/_._')) } }
    }
    $contentRuntime = $id -eq 'Whisper.net.Runtime' -or $id -eq 'Whisper.net.Runtime.Vulkan' -or
        ($id -eq 'Whisper.net.Runtime.Metal' -and $Runtime.StartsWith('osx-')) -or
        ($id -eq 'Microsoft.AI.DirectML' -and $Runtime.StartsWith('win-')) -or
        ($id -in @('SkiaSharp.NativeAssets.WebAssembly','HarfBuzzSharp.NativeAssets.WebAssembly') -and $Runtime -eq 'browser-wasm')
    if (!$shippedAssets -and !$contentRuntime) { continue }
    $library = $assets.libraries[$key]
    $packageDir = $packageFolders | ForEach-Object { Join-Path $_ $library.path } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$packageDir) { throw "Package not found in restored package folders: $key" }
    $nuspec = Get-ChildItem -LiteralPath $packageDir -Filter '*.nuspec' | Select-Object -First 1
    [xml]$xml = Get-Content -LiteralPath $nuspec.FullName
    $info = $xml.package.metadata
    $destination = Join-Path $licenseRoot ($id + '-' + $version)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination $destination
    # Preserve relative paths; different nested notices must never overwrite one another.
    Get-ChildItem -LiteralPath $packageDir -Recurse -File | Where-Object { $_.Name -match '(?i)^(license|licence|notice|third.?party.?notices|copyright)' } | ForEach-Object {
        $path = Join-Path $destination ([IO.Path]::GetRelativePath($packageDir,$_.FullName))
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $path
    }
    $type = [string]$info.license.type; $terms = [string]$info.license.'#text'
    if ($type -eq 'expression') {
        if ($terms -notmatch '^[A-Za-z0-9.-]+$') { throw "Review compound SPDX license manually: $id $terms" }
        $textFile = Join-Path $destination 'SPDX-LICENSE.txt'
        $cached = Join-Path $sourceRoot "$id-$version/SPDX-LICENSE.txt"
        if (Test-Path -LiteralPath $cached) { Copy-Item -LiteralPath $cached -Destination $textFile }
        else { Invoke-WebRequest -Uri "https://raw.githubusercontent.com/spdx/license-list-data/main/text/$terms.txt" -OutFile $textFile }
        if ((Get-Content -LiteralPath $textFile -TotalCount 2) -match 'DOCTYPE|<html') { throw "Invalid SPDX text: $id" }
    }
    elseif ($type -eq 'file') {
        $source = Join-Path $packageDir $terms
        if (!(Test-Path -LiteralPath $source)) { throw "Missing license for $id" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $destination 'PACKAGE-LICENSE.txt')
    }
    elseif ($id -eq 'System.Memory' -and $version -eq '4.5.5' -and $info.licenseUrl -eq 'https://github.com/dotnet/corefx/blob/master/LICENSE.TXT') {
        $terms = 'MIT'
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'System.Memory-4.5.5/PACKAGE-LICENSE.txt') -Destination (Join-Path $destination 'PACKAGE-LICENSE.txt')
    }
    else { throw "Review license manually: $id" }
    @("Package: $id $version", "Authors: $($info.authors)", "Copyright: $($info.copyright)", "License: $terms", "Project: $($info.projectUrl)", "Repository: $($info.repository.url)") | Set-Content -LiteralPath (Join-Path $destination 'ATTRIBUTION.txt') -Encoding utf8
    $manifest += [pscustomobject]@{ package=$id; version=$version; license=$terms; authors=[string]$info.authors; copyright=[string]$info.copyright; repository=[string]$info.repository.url }
}
if ($BrowserLicenseDirectory) {
    Get-ChildItem -LiteralPath $BrowserLicenseDirectory -Directory | ForEach-Object {
        if (!(Test-Path -LiteralPath (Join-Path $licenseRoot $_.Name))) { Copy-Item -LiteralPath $_.FullName -Destination $licenseRoot -Recurse }
    }
    $browserManifest = Get-Content -LiteralPath (Join-Path $BrowserLicenseDirectory 'manifest.json') -Raw | ConvertFrom-Json
    $manifest = @($manifest) + @($browserManifest) | Sort-Object package,version -Unique
}
# Curated notices cover embedded models, fonts and icons, independently of NuGet.
if ($Runtime -ne 'browser-wasm') {
    foreach ($name in @('chinese-tags','fonts','joytag','lama','local-summary','lucide','nsfw-review','open-images','person-detection','speech','yunet','upstream')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $licenseRoot -Recurse -Force
    }
    if ($Runtime.StartsWith('linux-')) { Remove-Item -LiteralPath (Join-Path $licenseRoot 'upstream/zimg-WTFPL.txt') }
    if ($Runtime -eq 'win-x64') { Copy-Item -LiteralPath (Join-Path $sourceRoot 'installer') -Destination $licenseRoot -Recurse }
    if ($Runtime.StartsWith('win-') -or $Runtime.StartsWith('osx-')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'pdfium') -Destination $licenseRoot -Recurse
        Copy-Item -LiteralPath (Join-Path $sourceRoot 'download-tools') -Destination $licenseRoot -Recurse
        if ($Runtime.StartsWith('osx-')) {
            Get-ChildItem -LiteralPath (Join-Path $licenseRoot 'download-tools') -File | Where-Object { $_.Name -match 'mingw|gcc' } | Remove-Item
        }
        $mediaNotices = Join-Path $sourceRoot "media-tools/$Runtime"
        if (!(Test-Path -LiteralPath $mediaNotices)) { throw "Bundle media tools for $Runtime before collecting licenses." }
        New-Item -ItemType Directory -Path (Join-Path $licenseRoot 'media-tools') -Force | Out-Null
        Copy-Item -LiteralPath $mediaNotices -Destination (Join-Path $licenseRoot 'media-tools') -Recurse
    }
}
# Include only the exact runtime packs resolved by this publish, never every cached version.
$framework = $assets.project.frameworks.Values | Select-Object -First 1
foreach ($dependency in $framework.downloadDependencies) {
    if ($dependency.name -notmatch '^Microsoft\.(NETCore|AspNetCore)\.App\.Runtime\.' -or !$dependency.name.EndsWith('.' + $Runtime)) { continue }
    $version = $dependency.version.Trim('[',']').Split(',')[0].Trim()
    $pack = $packageFolders | ForEach-Object { Join-Path $_ ($dependency.name.ToLowerInvariant() + '/' + $version) } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$pack) { throw "Resolved runtime pack missing: $($dependency.name) $version" }
    $destination = Join-Path $licenseRoot ($dependency.name + '-' + $version)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -match '(?i)license|notice' } | Copy-Item -Destination $destination
    [xml]$packXml = Get-Content -LiteralPath (Get-ChildItem -LiteralPath $pack -Filter '*.nuspec' | Select-Object -First 1).FullName
    $info = $packXml.package.metadata
    $manifest += [pscustomobject]@{ package=$dependency.name; version=$version; license=[string]$info.license.'#text'; authors=[string]$info.authors; copyright=[string]$info.copyright; repository=[string]$info.repository.url }
}
if ($Runtime -eq 'browser-wasm') {
    # WebAssembly resolves its exact Mono runtime pack from the SDK workload, outside NuGet's package folders.
    $sdk = Join-Path $taskRoot ('.tools/dotnet/dotnet' + $(if ($IsWindows) { '.exe' } else { '' }))
    if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
    $runtimeMetadata = & $sdk msbuild $projectPath -nologo -t:ResolveReferences -p:BuildProjectReferences=false -p:Configuration=Release -getItem:ResolvedRuntimePack
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve browser runtime pack notices.' }
    $runtimePacks = ($runtimeMetadata | Out-String | ConvertFrom-Json).Items.ResolvedRuntimePack
    if (!$runtimePacks) { throw 'The published browser runtime pack was not resolved.' }
    foreach ($pack in $runtimePacks) {
        $destination = Join-Path $licenseRoot ($pack.NuGetPackageId + '-' + $pack.NuGetPackageVersion)
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Get-ChildItem -LiteralPath $pack.PackageDirectory -File | Where-Object { $_.Name -match '(?i)license|notice' -or $_.Extension -eq '.nuspec' } | Copy-Item -Destination $destination
        [xml]$packXml = Get-Content -LiteralPath (Get-ChildItem -LiteralPath $pack.PackageDirectory -Filter '*.nuspec' | Select-Object -First 1).FullName
        $info = $packXml.package.metadata
        $manifest += [pscustomobject]@{ package=$pack.NuGetPackageId; version=$pack.NuGetPackageVersion; license=[string]$info.license.'#text'; authors=[string]$info.authors; copyright=[string]$info.copyright; repository=[string]$info.repository.url }
    }
}
# Retain all attribution paths, with references to one copy of each identical declaration.
$common = Join-Path $licenseRoot 'common'
New-Item -ItemType Directory -Path $common -Force | Out-Null
$duplicates = @()
$files = @(Get-ChildItem -LiteralPath $licenseRoot -Recurse -File | Where-Object {
    $_.DirectoryName -ne $common -and $_.Name -notmatch '^(ATTRIBUTION|manifest|dependencies)' -and $_.Extension -ne '.nuspec' -and
        (Get-Content -LiteralPath $_.FullName -TotalCount 1) -notlike 'Identical license/notice text: see *'
})
$groups = $files | Group-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
foreach ($group in $groups) {
    $shared = Join-Path $common ($group.Name.ToLowerInvariant() + '.txt')
    if ($group.Count -lt 2 -and !(Test-Path -LiteralPath $shared)) { continue }
    if (!(Test-Path -LiteralPath $shared)) { Copy-Item -LiteralPath $group.Group[0].FullName -Destination $shared }
    foreach ($file in $group.Group) {
        $relative = [IO.Path]::GetRelativePath($file.DirectoryName,$shared).Replace('\','/')
        "Identical license/notice text: see $relative" | Set-Content -LiteralPath $file.FullName -Encoding utf8
        $duplicates += [pscustomobject]@{ path=[IO.Path]::GetRelativePath($licenseRoot,$file.FullName).Replace('\','/'); text=[IO.Path]::GetRelativePath($licenseRoot,$shared).Replace('\','/') }
    }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $licenseRoot 'manifest.json') -Encoding utf8
@{ runtime=$Runtime; target=$targetNames[0]; packages=@($manifest | Select-Object package,version) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $licenseRoot 'dependencies.json') -Encoding utf8
$duplicates | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $licenseRoot 'shared-texts.json') -Encoding utf8
Write-Output "Collected $($manifest.Count) package licenses for $Runtime."
