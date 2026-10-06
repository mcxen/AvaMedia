param([switch]$SourceOnly)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$lock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'quickjs-source.lock.json') -Raw | ConvertFrom-Json
$cache=Join-Path $taskRoot '.tools/download-bundle'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive=Join-Path $cache "$($lock.version)-quickjs-source.tar.gz"
if (!(Test-Path -LiteralPath $archive)) {
    $temporary=$archive+'.part'
    & curl --fail --location --connect-timeout 20 --max-time 180 --retry 2 --retry-all-errors --retry-max-time 90 --proto '=https' --proto-redir '=https' --output $temporary $lock.url
    if ($LASTEXITCODE -ne 0) { throw 'QuickJS source download failed.' }
    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.sha256) { throw 'QuickJS source checksum mismatch.' }
    Move-Item -LiteralPath $temporary -Destination $archive -Force
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.sha256) { throw 'Cached QuickJS source checksum mismatch.' }
Write-Host "Verified QuickJS-NG $($lock.version) source SHA256."
if ($SourceOnly) { return $archive }
if (![Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX) -or
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::Arm64) {
    throw 'QuickJS must be built on native macOS ARM64. Use -SourceOnly elsewhere; the upstream binary requires macOS 26.'
}
$target=Join-Path $cache "$($lock.version)-qjs-osx-arm64-macos$($lock.deploymentTarget)"
$binary=Join-Path $target 'qjs'
$manifestPath=Join-Path $target 'build.json'
$recipeHash=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
function Assert-QuickJSBinary {
    $architecture=(& /usr/bin/lipo -archs $binary | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $architecture -ne $lock.architecture) { throw 'QuickJS architecture mismatch.' }
    $commands=(& /usr/bin/otool -l $binary | Out-String)
    if ($LASTEXITCODE -ne 0 -or $commands -notmatch '(?m)^\s+minos\s+([0-9.]+)\s*$') { throw 'QuickJS deployment version is absent.' }
    $minimum=$Matches[1]
    if ([version]$minimum -gt [version]::new(13,4,0)) { throw "QuickJS requires unsupported macOS $minimum." }
    $dependencies=@(& /usr/bin/otool -L $binary)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read QuickJS dependencies.' }
    foreach ($line in $dependencies | Select-Object -Skip 1) {
        $dependency=($line.Trim() -split ' \(')[0]
        if (!$dependency.StartsWith('/usr/lib/') -and !$dependency.StartsWith('/System/Library/')) { throw "Unbundled QuickJS dependency: $dependency" }
    }
    & /usr/bin/codesign --verify --strict $binary
    if ($LASTEXITCODE -ne 0) { throw 'QuickJS signature verification failed.' }
    $version=(& $binary --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $version -ne $lock.version) { throw 'QuickJS version verification failed.' }
    $result=(& $binary -e 'Promise.resolve(6 * 7).then(value => console.log(value))' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $result -ne '42') { throw 'QuickJS JavaScript execution failed.' }
    Write-Host "Verified ARM64 QuickJS $version, macOS $minimum, system dependencies and JavaScript execution."
}
if ((Test-Path -LiteralPath $binary) -and (Test-Path -LiteralPath $manifestPath)) {
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.sourceSha256 -eq $lock.sha256 -and $manifest.recipeSha256 -eq $recipeHash -and
        $manifest.sha256 -eq (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()) {
        Assert-QuickJSBinary
        return $binary
    }
}
if (!(Get-Command cmake -ErrorAction SilentlyContinue)) { throw 'Install cmake to build QuickJS.' }
$work=Join-Path $taskRoot ('artifacts/quickjs-build-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work,$target -Force | Out-Null
& /usr/bin/tar -xzf $archive -C $work
if ($LASTEXITCODE -ne 0) { throw 'QuickJS source extraction failed.' }
$source=Join-Path $work "quickjs-$($lock.version)"
$build=Join-Path $work 'build'
& cmake -S $source -B $build -DCMAKE_BUILD_TYPE=MinSizeRel -DCMAKE_OSX_ARCHITECTURES=arm64 "-DCMAKE_OSX_DEPLOYMENT_TARGET=$($lock.deploymentTarget)" -DBUILD_SHARED_LIBS=OFF -DQJS_BUILD_EXAMPLES=OFF -DQJS_ENABLE_INSTALL=OFF -DQJS_BUILD_CLI_WITH_MIMALLOC=OFF -DQJS_BUILD_CLI_WITH_STATIC_MIMALLOC=OFF | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'QuickJS configure failed.' }
& cmake --build $build --target qjs_exe --parallel 3 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'QuickJS build failed.' }
Copy-Item -LiteralPath (Join-Path $build 'qjs') -Destination $binary -Force
& /usr/bin/strip -x $binary
if ($LASTEXITCODE -ne 0) { throw 'QuickJS strip failed.' }
& /bin/chmod +x $binary
if ($LASTEXITCODE -ne 0) { throw 'QuickJS executable permission failed.' }
& /usr/bin/codesign --force --sign - --timestamp=none $binary
if ($LASTEXITCODE -ne 0) { throw 'QuickJS signing failed.' }
Assert-QuickJSBinary
[ordered]@{
    version=$lock.version;architecture=$lock.architecture;deploymentTarget=$lock.deploymentTarget
    source=$lock.url;sourceSha256=$lock.sha256;recipeSha256=$recipeHash;license=$lock.license
    sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
    bytes=(Get-Item -LiteralPath $binary).Length;compiler=(& /usr/bin/clang --version | Out-String).Trim()
} | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
return $binary
