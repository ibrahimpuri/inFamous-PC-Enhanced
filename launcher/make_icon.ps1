# Draws the launcher icon (a lightning bolt on a dark tile) into assets\icon.ico. Run once; build.cmd uses the result.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'assets\icon.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 256.0
    # rounded tile
    $r = 56 * $k; $m = 8 * $k; $w = $size - 2 * $m
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $r, $r, 180, 90); $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90); $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size),
        ([System.Drawing.Color]::FromArgb(255, 22, 40, 72)), ([System.Drawing.Color]::FromArgb(255, 9, 13, 22))
    $g.FillPath($bg, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 59, 169, 255)), ([Math]::Max(1.0, 6 * $k))
    $g.DrawPath($pen, $path)
    # bolt
    $pts = @(@(150, 30), @(66, 140), @(122, 140), @(100, 228), @(192, 108), @(136, 108), @(166, 30)) |
        ForEach-Object { New-Object System.Drawing.PointF ($_[0] * $k), ($_[1] * $k) }
    $glow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 59, 169, 255)), (18 * $k)
    $glow.LineJoin = 'Round'
    $g.DrawPolygon($glow, $pts)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, (30 * $k)), (New-Object System.Drawing.PointF 0, (228 * $k)),
        ([System.Drawing.Color]::FromArgb(255, 200, 236, 255)), ([System.Drawing.Color]::FromArgb(255, 40, 140, 240))
    $g.FillPolygon($fill, $pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (Draw $s) }
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "Wrote $out"
