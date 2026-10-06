param([string]$Version='1.0.5')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Branding.ps1')
$appBrand=Get-AppBrand
$checks=[Collections.Generic.List[string]]::new()
function Assert-Package($Condition,[string]$Message) {
    if (!$Condition) { throw $Message }
    $checks.Add($Message)
    Write-Output "PASS $Message"
}
function Read-BigEndian([byte[]]$Bytes,[int]$Offset) {
    return ([uint32]$Bytes[$Offset]*16777216)+([uint32]$Bytes[$Offset+1]*65536)+([uint32]$Bytes[$Offset+2]*256)+[uint32]$Bytes[$Offset+3]
}
function Get-MachArchitectures([byte[]]$Bytes) {
    if ([BitConverter]::ToUInt32($Bytes,0) -eq 4277009103) { return @([BitConverter]::ToUInt32($Bytes,4)) }
    $magic=Read-BigEndian $Bytes 0
    if ($magic -ne 3405691582 -and $magic -ne 3405691583) { throw 'Expected a Mach-O binary.' }
    $count=Read-BigEndian $Bytes 4
    $step=if ($magic -eq 3405691583) { 32 } else { 20 }
    return @(for ($i=0; $i -lt $count; $i++) { Read-BigEndian $Bytes (8+$i*$step) })
}
function Get-MachMinimumVersion([byte[]]$Bytes,[uint32]$Architecture) {
    $start=0
    $magic=Read-BigEndian $Bytes 0
    if ($magic -eq 3405691582 -or $magic -eq 3405691583) {
        $count=Read-BigEndian $Bytes 4
        $step=if ($magic -eq 3405691583) { 32 } else { 20 }
        $found=$false
        for ($i=0; $i -lt $count; $i++) {
            $position=8+$i*$step
            if ((Read-BigEndian $Bytes $position) -eq $Architecture) {
                if ($step -eq 32) {
                    if ((Read-BigEndian $Bytes ($position+8)) -ne 0) { throw 'Mach-O slice exceeds supported archive size.' }
                    $start=Read-BigEndian $Bytes ($position+12)
                } else { $start=Read-BigEndian $Bytes ($position+8) }
                $found=$true
                break
            }
        }
        if (!$found) { throw 'Target Mach-O architecture is absent.' }
    }
    if ($start+32 -gt $Bytes.Length -or [BitConverter]::ToUInt32($Bytes,$start) -ne 4277009103 -or
        [BitConverter]::ToUInt32($Bytes,$start+4) -ne $Architecture) { throw 'Expected a target 64-bit Mach-O header.' }
    $count=[BitConverter]::ToUInt32($Bytes,$start+16)
    $end=$start+32+[BitConverter]::ToUInt32($Bytes,$start+20)
    if ($end -gt $Bytes.Length) { throw 'Truncated Mach-O load commands.' }
    $position=$start+32
    for ($i=0; $i -lt $count; $i++) {
        if ($position+8 -gt $end) { throw 'Truncated Mach-O load command.' }
        $command=[BitConverter]::ToUInt32($Bytes,$position)
        $size=[BitConverter]::ToUInt32($Bytes,$position+4)
        if ($size -lt 8 -or $position+$size -gt $end) { throw 'Invalid Mach-O load command size.' }
        if ($command -eq 0x32 -or $command -eq 0x24) {
            $offset=if ($command -eq 0x32) { 12 } else { 8 }
            if ($size -lt $offset+4) { throw 'Truncated Mach-O deployment version.' }
            if ($command -eq 0x32 -and [BitConverter]::ToUInt32($Bytes,$position+8) -ne 1) { throw 'Expected macOS deployment platform.' }
            $value=[BitConverter]::ToUInt32($Bytes,$position+$offset)
            return [version]::new(($value -shr 16),(($value -shr 8) -band 255),($value -band 255))
        }
        $position+=$size
    }
    throw 'Mach-O deployment version is absent.'
}
function Read-ArchiveBytes($Entry) {
    $stream=$Entry.Open()
    $buffer=[IO.MemoryStream]::new()
    try { $stream.CopyTo($buffer); return ,$buffer.ToArray() } finally { $stream.Dispose(); $buffer.Dispose() }
}
$packages=@()
foreach ($runtime in @('osx-arm64')) {
    $path=Join-Path $taskRoot "artifacts/AvaMedia-$Version-$runtime.zip"
    $expected=0x100000c
    $archive=[IO.Compression.ZipFile]::OpenRead($path)
    try {
        $root=$appBrand.MacBundleName+'/Contents/'
        $reader=[IO.StreamReader]::new($archive.GetEntry($root+'Info.plist').Open())
        try { [xml]$plist=$reader.ReadToEnd() } finally { $reader.Dispose() }
        $values=@{}
        $nodes=@($plist.plist.dict.ChildNodes)
        for ($i=0; $i -lt $nodes.Length-1; $i+=2) { $values[$nodes[$i].InnerText]=$nodes[$i+1].InnerText }
        Assert-Package ($values.LSMinimumSystemVersion -eq '13.4') "$runtime declares macOS 13.4 or later"
        $minimum=[version]$values.LSMinimumSystemVersion
        foreach ($name in @('AvaMedia.Desktop','libhostfxr.dylib','libcoreclr.dylib','libhostpolicy.dylib',
            'libAvaloniaNative.dylib','libSkiaSharp.dylib','libHarfBuzzSharp.dylib','libonnxruntime.dylib')) {
            $entry=$archive.GetEntry($root+'MacOS/'+$name)
            Assert-Package ($null -ne $entry) "$runtime includes $name"
            $header=Read-ArchiveBytes $entry
            Assert-Package ((Get-MachArchitectures $header) -contains $expected) "$runtime $name contains the target Mach-O architecture"
            Assert-Package ((Get-MachMinimumVersion $header $expected) -le [version]::new($minimum.Major,$minimum.Minor,0)) "$runtime $name supports the declared macOS minimum"
            $mode=($entry.ExternalAttributes -shr 16) -band 511
            Assert-Package (($mode -band 73) -eq 73) "$runtime $name carries Unix execute permissions"
        }
        Assert-Package ($values.CFBundleExecutable -eq 'AvaMedia.Desktop' -and $values.CFBundleShortVersionString -eq $Version) "$runtime plist matches executable and version"
        Assert-Package ($values.CFBundleIconFile -eq 'AvaMedia.icns') "$runtime plist selects the AvaMedia application icon"
        $iconEntry=$archive.GetEntry($root+'Resources/AvaMedia.icns')
        Assert-Package ($null -ne $iconEntry) "$runtime includes the application ICNS"
        $iconStream=$iconEntry.Open(); $iconHeader=[byte[]]::new(8)
        try { $read=$iconStream.Read($iconHeader,0,8) } finally { $iconStream.Dispose() }
        Assert-Package ($read -eq 8 -and [Text.Encoding]::ASCII.GetString($iconHeader,0,4) -eq 'icns' -and (Read-BigEndian $iconHeader 4) -eq $iconEntry.Length) "$runtime application icon has a complete ICNS header"
        Assert-Package ($null -ne $archive.GetEntry($root+'Resources/scripts/Install-MediaTools-macOS.sh')) "$runtime includes the macOS tool installer"
        Assert-Package ($null -ne $archive.GetEntry($root+'Resources/THIRD-PARTY-NOTICES.md')) "$runtime includes third-party notices"
        foreach ($tool in @('yt-dlp','qjs','ffmpeg','ffprobe')) {
            $entry=$archive.GetEntry($root+'MacOS/tools/'+$tool)
            Assert-Package ($null -ne $entry) "$runtime includes bundled $tool"
            $header=Read-ArchiveBytes $entry
            Assert-Package ((Get-MachArchitectures $header) -contains $expected) "$runtime $tool contains the target Mach-O architecture"
            Assert-Package ((Get-MachMinimumVersion $header $expected) -le [version]::new($minimum.Major,$minimum.Minor,0)) "$runtime $tool supports the declared macOS minimum"
            Assert-Package (((($entry.ExternalAttributes -shr 16) -band 511) -band 73) -eq 73) "$runtime $tool carries Unix execute permissions"
        }
        Assert-Package ($null -ne $archive.GetEntry($root+'MacOS/tools/ffmpeg-bundle.json')) "$runtime includes bundled FFmpeg manifest"
        Assert-Package ($null -ne $archive.GetEntry($root+'Resources/licenses/media-tools/osx-arm64/NOTICE.txt')) "$runtime includes bundled FFmpeg notices"
        foreach($entry in $archive.Entries | Where-Object { $_.FullName.StartsWith($root+'MacOS/tools/lib/') -and $_.FullName.EndsWith('.dylib') }){
            $header=Read-ArchiveBytes $entry
            Assert-Package ((Get-MachArchitectures $header) -contains $expected) "$runtime $($entry.Name) is a target media library"
            Assert-Package ((Get-MachMinimumVersion $header $expected) -le [version]::new($minimum.Major,$minimum.Minor,0)) "$runtime $($entry.Name) supports the declared macOS minimum"
            Assert-Package (((($entry.ExternalAttributes -shr 16) -band 511) -band 73) -eq 73) "$runtime $($entry.Name) carries Unix execute permissions"
        }
        Assert-Package ($null -ne $archive.GetEntry($root+'MacOS/tools/download-tools.json')) "$runtime includes pinned downloader manifest"
        $reader=[IO.StreamReader]::new($archive.GetEntry($root+'MacOS/tools/download-tools.json').Open())
        try { $downloader=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        $quickJsLock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'macos/quickjs-source.lock.json') -Raw | ConvertFrom-Json
        Assert-Package ($downloader.quickJsBuild.version -eq $quickJsLock.version -and
            $downloader.quickJsBuild.sourceSha256 -eq $quickJsLock.sha256 -and
            $downloader.quickJsBuild.deploymentTarget -eq $values.LSMinimumSystemVersion -and
            $downloader.quickJsBuild.architecture -eq 'arm64') "$runtime downloader records the pinned ARM64 QuickJS source build"
    } finally { $archive.Dispose() }
    $bytes=[IO.File]::ReadAllBytes($path)
    $end=-1
    for ($offset=$bytes.Length-22; $offset -ge [Math]::Max(0,$bytes.Length-65557); $offset--) {
        if ([BitConverter]::ToUInt32($bytes,$offset) -eq 0x06054b50 -and $offset+22+[BitConverter]::ToUInt16($bytes,$offset+20) -eq $bytes.Length) { $end=$offset; break }
    }
    Assert-Package ($end -ge 0) "$runtime ZIP has a valid central directory"
    $count=[BitConverter]::ToUInt16($bytes,$end+10); $position=[BitConverter]::ToUInt32($bytes,$end+16); $unix=$true
    for ($i=0; $i -lt $count; $i++) {
        if ([BitConverter]::ToUInt32($bytes,$position) -ne 0x02014b50 -or $bytes[$position+5] -ne 3) { $unix=$false; break }
        $position+=46+[BitConverter]::ToUInt16($bytes,$position+28)+[BitConverter]::ToUInt16($bytes,$position+30)+[BitConverter]::ToUInt16($bytes,$position+32)
    }
    Assert-Package $unix "$runtime ZIP marks every entry as Unix for executable extraction"
    $packages+=@{runtime=$runtime;path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;bytes=$bytes.Length;entries=$count}
}
$reportRoot=Join-Path $taskRoot ('artifacts/mac-packages-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
@{platform=[Runtime.InteropServices.RuntimeInformation]::OSDescription;checks=$checks.Count;results=$checks.ToArray();packages=$packages;macOSRuntimeVerified=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportRoot 'report.json') -Encoding utf8
Write-Output "Verified $($checks.Count) package checks. $reportRoot"
