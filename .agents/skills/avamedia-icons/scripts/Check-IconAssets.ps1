param([string]$ProjectRoot = (Join-Path $PSScriptRoot '../../../..'))
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Pixel and binary inspection uses Windows System.Drawing. On other hosts use IconPreview and the native icon utilities.' }
Add-Type -AssemblyName System.Drawing
$taskRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$featureRoot = Join-Path $taskRoot 'src/AvaMedia.Desktop/Assets/FeatureIcons/v2'
$appRoot = Join-Path $taskRoot 'src/AvaMedia.Desktop/Assets/AppIcon/v2'
$checks = [Collections.Generic.List[string]]::new()
function Assert-Icon([bool]$condition,[string]$message) {
    if (!$condition) { throw $message }
    $checks.Add($message)
}
function Read-Be32([byte[]]$bytes,[int]$offset) {
    $part = [byte[]]::new(4); [Array]::Copy($bytes,$offset,$part,0,4)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($part) }
    return [BitConverter]::ToUInt32($part,0)
}
function Assert-Png([byte[]]$bytes,[int]$offset,[int]$size,[string]$label) {
    Assert-Icon ($offset+24 -le $bytes.Length) "$label has a PNG header"
    $signature = @(137,80,78,71,13,10,26,10)
    for ($i=0;$i -lt 8;$i++) { if ($bytes[$offset+$i] -ne $signature[$i]) { throw "$label is not PNG-backed." } }
    Assert-Icon ((Read-Be32 $bytes ($offset+16)) -eq $size -and (Read-Be32 $bytes ($offset+20)) -eq $size) "$label has the expected dimensions"
}
function Check-PngFile([string]$path,[string]$label,[string]$sha256) {
    $bitmap = [Drawing.Bitmap]::new($path)
    try {
        Assert-Icon ($bitmap.Width -eq $bitmap.Height) "$label is square"
        Assert-Icon ($bitmap.GetPixel(0,0).A -eq 0 -and $bitmap.GetPixel($bitmap.Width-1,0).A -eq 0 -and $bitmap.GetPixel(0,$bitmap.Height-1).A -eq 0 -and $bitmap.GetPixel($bitmap.Width-1,$bitmap.Height-1).A -eq 0) "$label has transparent corners"
    } finally { $bitmap.Dispose() }
    Assert-Icon ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $sha256) "$label matches its manifest hash"
}
$manifest = Get-Content -LiteralPath (Join-Path $featureRoot 'manifest.json') -Raw | ConvertFrom-Json
$loader = Get-Content -LiteralPath (Join-Path $taskRoot 'src/AvaMedia.Desktop/Controls/FeatureIconAssets.cs') -Raw
$allowedKinds = @([regex]::Matches($loader,'"([a-z][a-z-]*)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
Assert-Icon ($allowedKinds.Count -eq $manifest.assets.Count) 'The functional icon manifest and loader cover the same number of kinds'
foreach ($asset in $manifest.assets) {
    Assert-Icon ($allowedKinds -contains $asset.kind) "$($asset.kind) is registered in the loader"
    Check-PngFile (Join-Path $featureRoot $asset.file) $asset.kind $asset.sha256
}
$appManifest = Get-Content -LiteralPath (Join-Path $appRoot 'manifest.json') -Raw | ConvertFrom-Json
Check-PngFile (Join-Path $appRoot $appManifest.file) 'Application icon source' $appManifest.sha256
$ico = [IO.File]::ReadAllBytes((Join-Path $appRoot 'app.ico'))
Assert-Icon ($ico.Length -ge 6 -and [BitConverter]::ToUInt16($ico,0) -eq 0 -and [BitConverter]::ToUInt16($ico,2) -eq 1) 'ICO has the icon-container header'
$frameCount = [BitConverter]::ToUInt16($ico,4); $sizes = @()
Assert-Icon (6+16*$frameCount -le $ico.Length) 'ICO frame directory is complete'
for ($i=0;$i -lt $frameCount;$i++) {
    $entry=6+16*$i; $size=if($ico[$entry] -eq 0){256}else{[int]$ico[$entry]}
    $length=[BitConverter]::ToUInt32($ico,$entry+8); $offset=[BitConverter]::ToUInt32($ico,$entry+12)
    Assert-Icon ($offset -ge 6+16*$frameCount -and $offset+$length -le $ico.Length) "ICO $size frame fits inside the container"
    Assert-Png $ico $offset $size "ICO $size"
    $sizes += $size
}
foreach($size in @(16,20,24,32,40,48,64,128,256)) { Assert-Icon ($sizes -contains $size) "ICO contains the $size pixel frame" }
$icns=[IO.File]::ReadAllBytes((Join-Path $appRoot 'app.icns'))
Assert-Icon ($icns.Length -ge 8 -and [Text.Encoding]::ASCII.GetString($icns,0,4) -eq 'icns' -and (Read-Be32 $icns 4) -eq $icns.Length) 'ICNS header length matches the file'
$expected=@{icp4=16;icp5=32;icp6=64;ic07=128;ic08=256;ic09=512;ic10=1024;ic11=32;ic12=64;ic13=256;ic14=512}
$types=@(); $position=8
while($position -lt $icns.Length) {
    Assert-Icon ($position+8 -le $icns.Length) 'ICNS chunk header is complete'
    $type=[Text.Encoding]::ASCII.GetString($icns,$position,4); $length=Read-Be32 $icns ($position+4)
    Assert-Icon ($length -ge 32 -and $position+$length -le $icns.Length -and $expected.ContainsKey($type)) "ICNS $type is a supported complete chunk"
    Assert-Png $icns ($position+8) $expected[$type] "ICNS $type"
    $types += $type; $position += $length
}
foreach($type in $expected.Keys) { Assert-Icon ($types -contains $type) "ICNS contains $type" }
foreach($size in @(16,32,128,256,512)) {
    foreach($retina in @($false,$true)) {
        $suffix=if($retina){'@2x'}else{''}; $pixels=if($retina){$size*2}else{$size}
        $path=Join-Path $appRoot "AvaMedia.iconset/icon_${size}x${size}${suffix}.png"
        Assert-Png ([IO.File]::ReadAllBytes($path)) 0 $pixels "iconset $size$suffix"
    }
}
[pscustomobject]@{status='passed';functionalKinds=$manifest.assets.Count;icoSizes=$sizes;icnsTypes=$types;checks=$checks.Count;results=$checks.ToArray()}
