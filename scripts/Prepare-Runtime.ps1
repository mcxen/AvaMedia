param([Parameter(Mandatory=$true)][ValidateSet('win-x64','osx-arm64')][string]$Runtime, [Parameter(Mandatory=$true)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $PublishDirectory 'AvaMedia.Desktop.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$requirements = @($config.runtimeOptions.frameworks)
if ($requirements.Count -ne 2 -or @($requirements | Where-Object { $_.name -in @('Microsoft.NETCore.App','Microsoft.AspNetCore.App') -and $_.version -match '^8\.0\.\d+$' }).Count -ne 2) {
    throw 'Review bootstrap runtime requirements when changing the application target framework.'
}
# Snapshot the official stable release metadata into the package. Users download only archives,
# never a remote script, and verify them against these SHA512 values before extraction.
$metadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json'
$release = $metadata.releases | Where-Object { $_.runtime.version -eq $metadata.'latest-runtime' } | Select-Object -First 1
if (!$release -or $release.runtime.version -notmatch '^8\.0\.\d+$') { throw 'No stable .NET 8 runtime release found.' }
$suffix = if ($Runtime -eq 'win-x64') { '.zip' } else { '.tar.gz' }
$packages = foreach ($component in @('runtime','aspnetcore-runtime')) {
    $required = if ($component -eq 'runtime') { 'Microsoft.NETCore.App' } else { 'Microsoft.AspNetCore.App' }
    $minimum = ($requirements | Where-Object name -eq $required).version
    if ([version]$release.$component.version -lt [version]$minimum) { throw "Official $component version is below the application minimum." }
    $files = @($release.$component.files | Where-Object { $_.rid -eq $Runtime -and $_.url.EndsWith($suffix) })
    if ($files.Count -ne 1 -or $files[0].hash -notmatch '^[a-fA-F0-9]{128}$') { throw "Missing official $component archive/hash for $Runtime." }
    $uri = [Uri]$files[0].url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'builds.dotnet.microsoft.com') { throw 'Unexpected runtime archive origin.' }
    @{ name = $(if ($component -eq 'runtime') { '.NET 8' } else { 'ASP.NET Core 8' }); url = $files[0].url; sha512 = $files[0].hash }
}
$manifest = @{ version = $release.runtime.version; rid = $Runtime; packages = @($packages) } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $PublishDirectory 'runtime-bootstrap.json'), $manifest, [Text.UTF8Encoding]::new($false))
