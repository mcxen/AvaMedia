param([Parameter(Mandatory=$true)][ValidateSet('win-x64','osx-arm64')][string]$Runtime, [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.5', [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $taskRoot 'src/AvaMedia.Bootstrap'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
if ($Runtime -eq 'osx-arm64') {
    if (!$IsMacOS) { throw 'Build the macOS bootstrap on macOS.' }
    $compilerArguments = @('-arch','arm64','-mmacosx-version-min=13.4','-fobjc-arc','-O2','-Wall','-Wextra','-Werror','-Wno-deprecated-declarations','-framework','Cocoa',(Join-Path $source 'Mac.m'),(Join-Path $source 'Host.c'),'-o',(Join-Path $output 'AvaMedia.Desktop'))
    & xcrun clang @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw 'macOS bootstrap compilation failed.' }
} else {
    if (!$IsWindows) { throw 'Build the Windows bootstrap on Windows.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$visualStudio) { throw 'Visual Studio C++ build tools are required.' }
    $development = Join-Path $visualStudio 'Common7/Tools/VsDevCmd.bat'
    $environmentLines = & $env:ComSpec /d /s /c ('"' + $development + '" -arch=x64 -host_arch=x64 >nul && set')
    if ($LASTEXITCODE -ne 0) { throw 'Visual Studio build environment setup failed.' }
    foreach ($line in $environmentLines) {
        if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
    }
    $temporary = Join-Path $output '.bootstrap-build'
    New-Item -ItemType Directory -Path $temporary -Force | Out-Null
    . (Join-Path $PSScriptRoot 'Branding.ps1')
    $appBrand = Get-AppBrand
    $icon = (Join-Path $taskRoot 'src/AvaMedia.Desktop/Assets/AppIcon/v2/app.ico').Replace('\','\\')
    $manifest = (Join-Path $taskRoot 'src/AvaMedia.Desktop/app.manifest').Replace('\','\\')
    $versionTuple = $Version.Replace('.', ',') + ',0'
    $resource = @"
#include <windows.h>
1 ICON "$icon"
1 RT_MANIFEST "$manifest"
1 VERSIONINFO
FILEVERSION $versionTuple
PRODUCTVERSION $versionTuple
FILETYPE VFT_APP
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "FileDescription", "$($appBrand.DisplayName)\0"
      VALUE "FileVersion", "$Version\0"
      VALUE "ProductName", "$($appBrand.DisplayName)\0"
      VALUE "ProductVersion", "$Version\0"
      VALUE "OriginalFilename", "AvaMedia.Desktop.exe\0"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x0409, 1200
  END
END
"@
    $rc = Join-Path $temporary 'Bootstrap.rc'
    [IO.File]::WriteAllText($rc, $resource, [Text.UTF8Encoding]::new($true))
    $res = Join-Path $temporary 'Bootstrap.res'
    & rc.exe /nologo /c65001 "/fo$res" $rc
    if ($LASTEXITCODE -ne 0) { throw 'Windows bootstrap resources failed.' }
    Push-Location $temporary
    try {
        & cl.exe /nologo /std:c11 /utf-8 /O2 /MT /W4 /WX /wd4191 /D_CRT_SECURE_NO_WARNINGS (Join-Path $source 'Windows.c') (Join-Path $source 'Host.c') $res "/Fe$(Join-Path $output 'AvaMedia.Desktop.exe')" /link /SUBSYSTEM:WINDOWS /MANIFEST:NO user32.lib gdi32.lib shell32.lib shlwapi.lib advapi32.lib comctl32.lib ole32.lib uuid.lib windowscodecs.lib
        if ($LASTEXITCODE -ne 0) { throw 'Windows bootstrap compilation failed.' }
    } finally { Pop-Location }
    Remove-Item -LiteralPath $temporary -Recurse -Force
    # Windows PowerShell 5.1 needs a BOM to read the Chinese messages correctly.
    $installer = Get-Content -LiteralPath (Join-Path $source 'Install-Runtime.ps1') -Raw -Encoding UTF8
    [IO.File]::WriteAllText((Join-Path $output 'Install-Runtime.ps1'), $installer, [Text.UTF8Encoding]::new($true))
}
