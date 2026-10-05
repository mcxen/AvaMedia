param([ValidateSet('osx-arm64','osx-x64')][string]$Runtime, [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.3')
$ErrorActionPreference = 'Stop'
if (![Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) { throw 'macOS is required to generate PKG and DMG files.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$bundle = Join-Path $taskRoot "artifacts/release/$Version/$Runtime-bundle/AvaMedia.app"
if (!(Test-Path -LiteralPath $bundle)) { throw 'Publish the macOS application first.' }
& /usr/bin/codesign --force --deep --sign - $bundle
if ($LASTEXITCODE -ne 0) { throw 'Ad-hoc application signing failed.' }
$pkg = Join-Path $taskRoot "artifacts/AvaMedia-$Version-$Runtime.pkg"
& /usr/bin/pkgbuild --component $bundle --install-location /Applications --identifier app.avamedia.desktop --version $Version $pkg
if ($LASTEXITCODE -ne 0) { throw 'PKG build failed.' }
$stage = Join-Path $taskRoot "artifacts/dmg-$Runtime"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath $bundle -Destination $stage -Recurse -Force
& /bin/ln -s /Applications (Join-Path $stage 'Applications')
if ($LASTEXITCODE -ne 0) { throw 'DMG Applications shortcut failed.' }
& /usr/bin/hdiutil create -volname "AvaMedia $Version" -srcfolder $stage -format UDZO (Join-Path $taskRoot "artifacts/AvaMedia-$Version-$Runtime.dmg")
if ($LASTEXITCODE -ne 0) { throw 'DMG build failed.' }
