# Draws the app icon (src/XDesigner.App/Assets/AppIcon.ico): a form window with a selected control
# and a pointer on a purple-blue tile. Each size is drawn separately so small sizes stay crisp.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$OutPath = (Join-Path $PSScriptRoot "..\src\XDesigner.App\Assets\AppIcon.ico"))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single]($r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tile
    $tile = New-RoundedPath 0 0 ($s - 0.5) ($s - 0.5) ([single]($s * 0.22))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 124, 77, 255)), ([System.Drawing.Color]::FromArgb(255, 33, 150, 243))
    $g.FillPath($grad, $tile)

    # Form window: white frame with a title strip
    $white = [System.Drawing.Color]::White
    $fx = [single]($s * 0.17); $fy = [single]($s * 0.22); $fw = [single]($s * 0.62); $fh = [single]($s * 0.52)
    $frame = New-RoundedPath $fx $fy $fw $fh ([single][Math]::Max(1, $s * 0.06))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 255, 255))), $frame)
    $pen = New-Object System.Drawing.Pen $white, ([single][Math]::Max(1, $s * 0.05))
    $g.DrawPath($pen, $frame)
    $strip = New-RoundedPath $fx $fy $fw ([single]($s * 0.13)) ([single][Math]::Max(1, $s * 0.06))
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), $strip)

    if ($s -ge 32) {
        # A selected "button" inside the form, with dashed selection
        $bx = [single]($fx + $s * 0.08); $by = [single]($fy + $s * 0.22); $bw = [single]($s * 0.26); $bh = [single]($s * 0.12)
        $g.FillPath((New-Object System.Drawing.SolidBrush $white), (New-RoundedPath $bx $by $bw $bh ([single]($s * 0.025))))
        $sel = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(230, 255, 255, 255)), ([single][Math]::Max(1, $s * 0.018))
        $sel.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
        $m = [single]($s * 0.035)
        $g.DrawRectangle($sel, $bx - $m, $by - $m, $bw + 2 * $m, $bh + 2 * $m)
    }

    # Pointer arrow at the bottom right
    $a = [single]$s
    $ox = $a * 0.56; $oy = $a * 0.50; $k = $a * 0.40
    $pts = @(
        (New-Object System.Drawing.PointF $ox, $oy),
        (New-Object System.Drawing.PointF $ox, ($oy + $k * 0.92)),
        (New-Object System.Drawing.PointF ($ox + $k * 0.25), ($oy + $k * 0.68)),
        (New-Object System.Drawing.PointF ($ox + $k * 0.42), ($oy + $k * 1.0)),
        (New-Object System.Drawing.PointF ($ox + $k * 0.56), ($oy + $k * 0.93)),
        (New-Object System.Drawing.PointF ($ox + $k * 0.40), ($oy + $k * 0.62)),
        (New-Object System.Drawing.PointF ($ox + $k * 0.72), ($oy + $k * 0.62))
    )
    $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $arrow.AddPolygon([System.Drawing.PointF[]]$pts)
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), $arrow)
    $outline = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 40, 30, 110)), ([single][Math]::Max(1, $s * 0.03))
    $outline.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($outline, $arrow)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG entries (supported by Windows Vista and later at every size).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()

$dir = Split-Path $OutPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())

"wrote $OutPath ($($out.Length) bytes, sizes $($sizes -join ', '))"
