param([string]$Preview = "")
# Redraws the app icon ("bolt + gauge") and writes src\ClaudeTracker.App\Assets\ClaudeTracker.ico.
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File scripts\generate-appicon.ps1
#
# The design and its numbers are the Mac app's (its scripts/generate-appicon.swift), in the
# same 100-unit space. One thing differs on purpose: a Mac icon leaves a margin around its
# rounded square, and a Windows icon fills its tile, so here the square is grown to the canvas.
# The .ico is committed; this script is how it is made again, not a build step.
# -Preview <file.png> also writes every size side by side on a light and a dark strip.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$out = Join-Path (Split-Path -Parent $PSScriptRoot) "src\ClaudeTracker.App\Assets\ClaudeTracker.ico"

function Rgb([int]$hex, [int]$alpha = 255) {
    [System.Drawing.Color]::FromArgb($alpha, ($hex -shr 16) -band 0xFF, ($hex -shr 8) -band 0xFF, $hex -band 0xFF)
}

# A gradient across the whole 100-unit canvas with its stops placed where the design wants
# them: a brush no wider than its shape repeats past its ends, under the round caps.
function Gradient([single]$angle, [double[]]$positions, [int[]]$colors) {
    $rect = New-Object System.Drawing.RectangleF -ArgumentList (-30), (-30), 160, 160
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList $rect, (Rgb 0), (Rgb 0), $angle
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend
    $blend.Positions = [single[]](@(0) + ($positions | ForEach-Object { ($_ + 30) / 160 }) + @(1))
    $blend.Colors = [System.Drawing.Color[]](@(Rgb $colors[0]) + ($colors | ForEach-Object { Rgb $_ }) + @(Rgb $colors[-1]))
    $brush.InterpolationColors = $blend
    $brush
}

function Render([int]$px) {
    $bitmap = New-Object System.Drawing.Bitmap -ArgumentList $px, $px, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    # The 100-unit design space, with the Mac's 82-unit square grown to fill it.
    $g.ScaleTransform($px / 100, $px / 100)
    $g.TranslateTransform(50, 50); $g.ScaleTransform(100 / 82, 100 / 82); $g.TranslateTransform(-50, -50)

    # The rounded square, lighter at the top.
    $square = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 19 * 2
    $square.AddArc(9, 9, $r, $r, 180, 90); $square.AddArc(91 - $r, 9, $r, $r, 270, 90)
    $square.AddArc(91 - $r, 91 - $r, $r, $r, 0, 90); $square.AddArc(9, 91 - $r, $r, $r, 90, 90)
    $square.CloseFigure()
    $background = Gradient 90 @(9, 91) @(0x23283A, 0x13161F)
    $g.FillPath($background, $square)

    # The gauge: a 270-degree track from the bottom left through the top, 72 % of it filled
    # from green to red. A little thicker at the smallest sizes, where it would thin out.
    $width = if ($px -le 32) { 11.5 } else { 9 }
    $arc = New-Object System.Drawing.RectangleF -ArgumentList (50 - 31.5), (50 - 31.5), 63, 63
    $track = New-Object System.Drawing.Pen -ArgumentList (Rgb 0xFFFFFF 31), $width
    $track.StartCap = $track.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($track, $arc, 135, 270)
    $urgency = Gradient 0 @(18.5, (18.5 + 0.45 * 63), (18.5 + 0.75 * 63), 81.5) @(0x2ECC71, 0xF1C40F, 0xE67E22, 0xE74C3C)
    $fill = New-Object System.Drawing.Pen -ArgumentList $urgency, $width
    $fill.StartCap = $fill.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($fill, $arc, 135, (270 * 0.72))

    # The bolt: the menu bar's, at 85 % around its centre.
    $points = @(@(54, 26), @(36, 54), @(47, 54), @(42, 76), @(62, 46), @(50, 46)) | ForEach-Object {
        New-Object System.Drawing.PointF -ArgumentList (50 + 0.85 * ($_[0] - 49)), (51 + 0.85 * ($_[1] - 51))
    }
    $coral = Gradient 90 @(26, 76) @(0xE8917A, 0xD97757)
    $g.FillPolygon($coral, [System.Drawing.PointF[]]$points)

    $g.Dispose()
    $bitmap
}

# One image of an .ico: a PNG at 256 pixels, and below that the plain bitmap every reader
# of icons understands (32-bit pixels bottom row first, then a 1-bit mask nobody uses any more).
function Entry([System.Drawing.Bitmap]$bitmap) {
    $px = $bitmap.Width
    $stream = New-Object System.IO.MemoryStream
    if ($px -ge 256) {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $stream.ToArray()
    }
    $writer = New-Object System.IO.BinaryWriter -ArgumentList $stream
    $maskRow = [int]([math]::Ceiling($px / 32.0) * 4)
    $writer.Write([int]40); $writer.Write([int]$px); $writer.Write([int]($px * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($px * $px * 4 + $maskRow * $px)); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    $whole = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $px, $px
    $data = $bitmap.LockBits($whole, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($px * 4)
    for ($y = $px - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
        $writer.Write($row)
    }
    $bitmap.UnlockBits($data)
    $writer.Write((New-Object byte[] ($maskRow * $px)))
    $writer.Flush()
    , $stream.ToArray()
}

$images = $sizes | ForEach-Object { Render $_ }
$entries = $images | ForEach-Object { , (Entry $_) }
New-Item -ItemType Directory -Force (Split-Path -Parent $out) | Out-Null
$file = New-Object System.IO.BinaryWriter -ArgumentList ([System.IO.File]::Create($out))
$file.Write([int16]0); $file.Write([int16]1); $file.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }   # 0 stands for 256
    $file.Write([byte]$side); $file.Write([byte]$side); $file.Write([byte]0); $file.Write([byte]0)
    $file.Write([int16]1); $file.Write([int16]32); $file.Write([int]$entries[$i].Length); $file.Write([int]$offset)
    $offset += $entries[$i].Length
}
foreach ($entry in $entries) { $file.Write([byte[]]$entry) }
$file.Close()
"wrote $out ($($sizes -join ', ') px; $((Get-Item $out).Length) bytes)"

if ($Preview) {
    $gap = 16; $rowHeight = 256 + 2 * $gap
    $total = [int](($sizes | Measure-Object -Sum).Sum + $gap * ($sizes.Count + 1))
    $sheet = New-Object System.Drawing.Bitmap -ArgumentList $total, ([int]($rowHeight * 2))
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush -ArgumentList (Rgb 0xF3F3F3)), 0, 0, $total, $rowHeight)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush -ArgumentList (Rgb 0x202020)), 0, $rowHeight, $total, $rowHeight)
    foreach ($row in 0, 1) {
        $x = $gap
        foreach ($image in $images) {
            $g.DrawImageUnscaled($image, $x, ($row * $rowHeight + $rowHeight - $gap - $image.Height))
            $x += $image.Width + $gap
        }
    }
    $g.Dispose(); $sheet.Save($Preview)
    "wrote $Preview"
}
