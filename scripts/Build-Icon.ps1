[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$assetDir = Join-Path $root "assets"
New-Item -ItemType Directory -Path $assetDir -Force | Out-Null

function New-IconBitmap([int]$size) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $scale = $size / 1024.0

    function Rect([double]$x, [double]$y, [double]$w, [double]$h, [double]$radius, [Drawing.Color]$color) {
        $path = [Drawing.Drawing2D.GraphicsPath]::new()
        $d = 2 * $radius * $scale
        $path.AddArc($x * $scale, $y * $scale, $d, $d, 180, 90)
        $path.AddArc(($x + $w - 2 * $radius) * $scale, $y * $scale, $d, $d, 270, 90)
        $path.AddArc(($x + $w - 2 * $radius) * $scale, ($y + $h - 2 * $radius) * $scale, $d, $d, 0, 90)
        $path.AddArc($x * $scale, ($y + $h - 2 * $radius) * $scale, $d, $d, 90, 90)
        $path.CloseFigure()
        $brush = [Drawing.SolidBrush]::new($color)
        $graphics.FillPath($brush, $path)
        $brush.Dispose()
        $path.Dispose()
    }

    function Circle([double]$cx, [double]$cy, [double]$radius, [Drawing.Color]$color) {
        $brush = [Drawing.SolidBrush]::new($color)
        $graphics.FillEllipse($brush, ($cx - $radius) * $scale, ($cy - $radius) * $scale,
            2 * $radius * $scale, 2 * $radius * $scale)
        $brush.Dispose()
    }

    Rect 0 0 1024 1024 184 ([Drawing.ColorTranslator]::FromHtml("#171B22"))
    Rect 142 164 740 696 96 ([Drawing.ColorTranslator]::FromHtml("#F5F7FA"))
    Rect 194 216 636 592 58 ([Drawing.ColorTranslator]::FromHtml("#232933"))
    $rows = @(
        @{ Y = 318; Cx = 618; Color = "#E5484D" },
        @{ Y = 485; Cx = 454; Color = "#25A56A" },
        @{ Y = 652; Cx = 562; Color = "#3584E4" }
    )
    foreach ($row in $rows) {
        $color = [Drawing.ColorTranslator]::FromHtml($row.Color)
        Rect 274 $row.Y 476 54 27 $color
        Circle $row.Cx ($row.Y + 27) 66 ([Drawing.ColorTranslator]::FromHtml("#F5F7FA"))
        Circle $row.Cx ($row.Y + 27) 30 $color
    }

    $graphics.Dispose()
    return $bitmap
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    ,$stream.ToArray()
}

$png = New-IconBitmap 1024
$png.Save((Join-Path $assetDir "app-icon.png"), [Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

$iconPath = Join-Path $assetDir "app-icon.ico"
$file = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "Icon assets created in $assetDir"
