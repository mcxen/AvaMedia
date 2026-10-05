param([string]$Version='1.0.2')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
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
$packages=@()
foreach ($runtime in @('osx-arm64','osx-x64')) {
    $path=Join-Path $taskRoot "artifacts/AvaMedia-$Version-$runtime.zip"
    $expected=if ($runtime -eq 'osx-arm64') { 0x100000c } else { 0x1000007 }
    $archive=[IO.Compression.ZipFile]::OpenRead($path)
    try {
        $root='AvaMedia.app/Contents/'
        foreach ($name in @('AvaMedia.Desktop','libhostfxr.dylib','libAvaloniaNative.dylib','libSkiaSharp.dylib','libHarfBuzzSharp.dylib')) {
            $entry=$archive.GetEntry($root+'MacOS/'+$name)
            Assert-Package ($null -ne $entry) "$runtime includes $name"
            $stream=$entry.Open()
            try { $header=[byte[]]::new(512); $null=$stream.Read($header,0,512) } finally { $stream.Dispose() }
            Assert-Package ((Get-MachArchitectures $header) -contains $expected) "$runtime $name contains the target Mach-O architecture"
            $mode=($entry.ExternalAttributes -shr 16) -band 511
            Assert-Package (($mode -band 73) -eq 73) "$runtime $name carries Unix execute permissions"
        }
        $reader=[IO.StreamReader]::new($archive.GetEntry($root+'Info.plist').Open())
        try { [xml]$plist=$reader.ReadToEnd() } finally { $reader.Dispose() }
        $values=@{}
        $nodes=@($plist.plist.dict.ChildNodes)
        for ($i=0; $i -lt $nodes.Length-1; $i+=2) { $values[$nodes[$i].InnerText]=$nodes[$i+1].InnerText }
        Assert-Package ($values.CFBundleExecutable -eq 'AvaMedia.Desktop' -and $values.CFBundleShortVersionString -eq $Version) "$runtime plist matches executable and version"
        Assert-Package ($null -ne $archive.GetEntry($root+'Resources/scripts/Install-MediaTools-macOS.sh')) "$runtime includes the macOS tool installer"
        Assert-Package ($null -ne $archive.GetEntry($root+'Resources/THIRD-PARTY-NOTICES.md')) "$runtime includes third-party notices"
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
