param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This exporter uses Windows System.Drawing. On macOS, package the exported iconset with iconutil.' }
Add-Type -AssemblyName System.Drawing
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$targetPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $targetPath -Force | Out-Null
$sourceImage = [Drawing.Bitmap]::new($sourcePath)
try {
    if ($sourceImage.Width -ne $sourceImage.Height) { throw 'The source application icon must be square.' }
    function ConvertTo-PngBytes([int]$size) {
        $image = [Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($image)
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        $buffer = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([Drawing.Color]::Transparent)
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($sourceImage,[Drawing.Rectangle]::new(0,0,$size,$size),0,0,$sourceImage.Width,$sourceImage.Height,[Drawing.GraphicsUnit]::Pixel,$attributes)
            $image.Save($buffer,[Drawing.Imaging.ImageFormat]::Png)
            return ,$buffer.ToArray()
        } finally { $buffer.Dispose(); $attributes.Dispose(); $graphics.Dispose(); $image.Dispose() }
    }
    $frames = @{}
    foreach ($size in @(16,20,24,32,40,48,64,128,256,512,1024)) {
        $frames[$size] = ConvertTo-PngBytes $size
        [IO.File]::WriteAllBytes((Join-Path $targetPath "app-$size.png"),$frames[$size])
    }
    $icoSizes = @(16,20,24,32,40,48,64,128,256)
    $icoPath = Join-Path $targetPath 'app.ico'
    $stream = [IO.File]::Create($icoPath)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$icoSizes.Count)
        $offset = 6 + 16 * $icoSizes.Count
        foreach ($size in $icoSizes) {
            $writer.Write([byte]($size % 256)); $writer.Write([byte]($size % 256))
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$size].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$size].Length
        }
        foreach ($size in $icoSizes) { $writer.Write([byte[]]$frames[$size]) }
    } finally { $writer.Dispose(); $stream.Dispose() }

    # PNG-backed ICNS chunks for modern macOS, with standard and Retina sizes.
    # Chunk map: https://github.com/fiahfy/icns (format parser / builder).
    $chunks = @(
        @{type='icp4';size=16}, @{type='icp5';size=32}, @{type='icp6';size=64},
        @{type='ic07';size=128}, @{type='ic08';size=256}, @{type='ic09';size=512}, @{type='ic10';size=1024},
        @{type='ic11';size=32}, @{type='ic12';size=64}, @{type='ic13';size=256}, @{type='ic14';size=512}
    )
    function Write-BigEndian([IO.BinaryWriter]$writer,[uint32]$number) {
        $bytes = [BitConverter]::GetBytes($number)
        if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($bytes) }
        $writer.Write($bytes)
    }
    $icnsPath = Join-Path $targetPath 'app.icns'
    $stream = [IO.File]::Create($icnsPath)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $length = 8
        foreach ($chunk in $chunks) { $length += 8 + $frames[$chunk.size].Length }
        $writer.Write([Text.Encoding]::ASCII.GetBytes('icns')); Write-BigEndian $writer $length
        foreach ($chunk in $chunks) {
            $writer.Write([Text.Encoding]::ASCII.GetBytes($chunk.type))
            Write-BigEndian $writer (8 + $frames[$chunk.size].Length)
            $writer.Write([byte[]]$frames[$chunk.size])
        }
    } finally { $writer.Dispose(); $stream.Dispose() }
    $iconsetPath = Join-Path $targetPath 'AvaMedia.iconset'
    New-Item -ItemType Directory -Path $iconsetPath -Force | Out-Null
    foreach ($size in @(16,32,128,256,512)) {
        [IO.File]::WriteAllBytes((Join-Path $iconsetPath "icon_${size}x${size}.png"),$frames[$size])
        [IO.File]::WriteAllBytes((Join-Path $iconsetPath "icon_${size}x${size}@2x.png"),$frames[$size*2])
    }
    [pscustomobject]@{
        source=$sourcePath;sourceSha256=(Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        originalWidth=$sourceImage.Width;originalHeight=$sourceImage.Height
        ico=$icoPath;icoSizes=$icoSizes;icns=$icnsPath;icnsTypes=@($chunks | ForEach-Object { $_.type })
        iconset=$iconsetPath
    }
} finally { $sourceImage.Dispose() }
