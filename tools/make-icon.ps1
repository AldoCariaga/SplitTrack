# Draws the app logo (three white bars on an indigo tile) into src\SplitTrack.App\Assets\SplitTrack.ico.
# Each size is drawn on its own pixel grid so the small ones stay sharp. Run it again after changing the design.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'src\SplitTrack.App\Assets\SplitTrack.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function Get-Even([double]$value) { [math]::Max(2, 2 * [math]::Round($value / 2)) }

function New-RoundedRect([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $d = 2 * $radius
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $width - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $width - $d, $y + $height - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-Logo([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'

    # Tile: full bleed at tray sizes, a small margin from 32 px up.
    $inset = if ($size -ge 32) { [math]::Round($size / 32) } else { 0 }
    $tileSize = $size - (2 * $inset)
    $tile = New-RoundedRect $inset $inset $tileSize $tileSize ($tileSize * 0.22)
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, $inset), [System.Drawing.PointF]::new(0, $size - $inset),
        [System.Drawing.Color]::FromArgb(0x81, 0x7C, 0xF9), [System.Drawing.Color]::FromArgb(0x43, 0x38, 0xCA))
    $g.FillPath($gradient, $tile)

    # Bars: even widths and heights keep every edge on a whole pixel.
    $barWidth = Get-Even ($size * 0.16)
    $gap = [math]::Max(1, [math]::Round($size * 0.09))
    $x = ($size - (3 * $barWidth) - (2 * $gap)) / 2
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    foreach ($ratio in 0.38, 0.66, 0.5) {
        $height = Get-Even ($size * $ratio)
        $bar = New-RoundedRect $x (($size - $height) / 2) $barWidth $height ($barWidth / 2)
        $g.FillPath($white, $bar)
        $bar.Dispose()
        $x += $barWidth + $gap
    }

    $white.Dispose(); $gradient.Dispose(); $tile.Dispose(); $g.Dispose()
    $bitmap
}

# 32-bit DIB for the small sizes (every icon reader takes these), PNG for 256.
function Get-ImageBytes([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $stream = [System.IO.MemoryStream]::new()
    if ($size -ge 256) {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $stream.ToArray()
    }

    $maskRow = [math]::Ceiling($size / 32) * 4
    $writer = [System.IO.BinaryWriter]::new($stream)
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int](2 * $size))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int](($size * $size * 4) + ($maskRow * $size)))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)

    $rect = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
    $data = $bitmap.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = [byte[]]::new($size * 4)
    for ($y = $size - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0, $y * $data.Stride), $row, 0, $row.Length)
        $writer.Write($row)
    }
    $bitmap.UnlockBits($data)
    $writer.Write([byte[]]::new($maskRow * $size))
    $writer.Flush()
    , $stream.ToArray()
}

$images = foreach ($size in $sizes) {
    $bitmap = New-Logo $size
    , (Get-ImageBytes $bitmap)
    $bitmap.Dispose()
}

New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$file = [System.IO.BinaryWriter]::new([System.IO.File]::Create($out))
$file.Write([int16]0); $file.Write([int16]1); $file.Write([int16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $file.Write([byte]$dimension); $file.Write([byte]$dimension); $file.Write([byte]0); $file.Write([byte]0)
    $file.Write([int16]1); $file.Write([int16]32); $file.Write([int]$images[$i].Length); $file.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $file.Write($image) }
$file.Dispose()

Write-Host "Icon: $out ($($sizes -join ', ') px)"
