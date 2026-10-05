param([string]$Project = 'src/AvaMedia.Desktop/AvaMedia.Desktop.csproj')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $taskRoot ('.tools/dotnet/dotnet' + $(if ($IsWindows -or $env:OS -eq 'Windows_NT') { '.exe' } else { '' }))
if (!(Test-Path -LiteralPath $sdk)) { $sdk = 'dotnet' }
$licenseRoot = Join-Path $taskRoot 'licenses'
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
$metadataText = & $sdk list (Join-Path $taskRoot $Project) package --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'Unable to read package metadata.' }
$metadataText | Set-Content -LiteralPath (Join-Path $licenseRoot 'dependencies.json') -Encoding utf8
$metadata = $metadataText | ConvertFrom-Json
$packages = @($metadata.projects[0].frameworks[0].topLevelPackages) + @($metadata.projects[0].frameworks[0].transitivePackages)
$globalPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$manifest = @()
foreach ($package in $packages) {
    $id = $package.id; $version = $package.resolvedVersion
    $packageDir = Join-Path $globalPackages ($id.ToLowerInvariant() + '/' + $version)
    $nuspec = Get-ChildItem -LiteralPath $packageDir -Filter '*.nuspec' | Select-Object -First 1
    [xml]$xml = Get-Content -LiteralPath $nuspec.FullName
    $info = $xml.package.metadata
    $destination = Join-Path $licenseRoot ($id + '-' + $version)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination $destination
    $licenseFiles = Get-ChildItem -LiteralPath $packageDir -Recurse -File | Where-Object { $_.Name -match '(?i)^(license|licence|notice|third.party.notices|copyright)' }
    foreach ($file in $licenseFiles) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force }
    $type = [string]$info.license.type; $terms = [string]$info.license.'#text'
    if ($type -eq 'expression') {
        if ($terms -match '^[A-Za-z0-9.-]+$') { $url = 'https://raw.githubusercontent.com/spdx/license-list-data/main/text/' + $terms + '.txt' }
        else { throw "Review compound SPDX license manually: $id $terms" }
        $textFile = Join-Path $destination 'SPDX-LICENSE.txt'
        if (!(Test-Path -LiteralPath $textFile) -or ((Get-Content -LiteralPath $textFile -TotalCount 2) -match 'DOCTYPE|<html')) { Invoke-WebRequest -Uri $url -OutFile $textFile }
    }
    elseif ($type -eq 'file') {
        $source = Join-Path $packageDir $terms
        if (!(Test-Path -LiteralPath $source)) { throw "Missing license for $id" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $destination 'PACKAGE-LICENSE.txt')
    }
    else { throw "Review license manually: $id" }
    @("Package: $id $version", "Authors: $($info.authors)", "Copyright: $($info.copyright)", "License: $terms", "Project: $($info.projectUrl)", "Repository: $($info.repository.url)") | Set-Content -LiteralPath (Join-Path $destination 'ATTRIBUTION.txt') -Encoding utf8
    $manifest += [pscustomobject]@{ package=$id; version=$version; license=$terms; authors=[string]$info.authors; copyright=[string]$info.copyright; repository=[string]$info.repository.url }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $licenseRoot 'manifest.json') -Encoding utf8
$originalLicenses = @(
    @{ Name='Avalonia-MIT.txt'; Url='https://raw.githubusercontent.com/AvaloniaUI/Avalonia/11.3.22/licence.md' },
    @{ Name='PDFsharp-MIT.txt'; Url='https://raw.githubusercontent.com/empira/PDFsharp/master/LICENSE' },
    @{ Name='PdfPig-Apache-2.0.txt'; Url='https://raw.githubusercontent.com/UglyToad/PdfPig/master/LICENSE' }
)
$originalRoot = Join-Path $licenseRoot 'upstream'
New-Item -ItemType Directory -Path $originalRoot -Force | Out-Null
foreach ($original in $originalLicenses) {
    $originalPath = Join-Path $originalRoot $original.Name
    if (!(Test-Path -LiteralPath $originalPath)) { Invoke-WebRequest -Uri $original.Url -OutFile $originalPath }
}
Write-Output "Collected $($manifest.Count) package licenses."
