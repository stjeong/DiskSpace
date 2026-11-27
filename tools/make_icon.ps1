param([string]$OutIco, [string]$PreviewPng)
Add-Type -AssemblyName System.Drawing

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) { $p.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h)); return $p }
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Fill-Block($g, [single]$x, [single]$y, [single]$w, [single]$h, [System.Drawing.Color]$top, [System.Drawing.Color]$bottom, [single]$r) {
    if ($w -le 0 -or $h -le 0) { return }
    $rect = New-Object System.Drawing.RectangleF $x, $y, $w, $h
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $bottom, 90
    $path = New-RoundRect $x $y $w $h $r
    $g.FillPath($br, $path)
    $br.Dispose(); $path.Dispose()
}

function C($r, $g, $b) { [System.Drawing.Color]::FromArgb(255, $r, $g, $b) }

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $small = $s -le 24
    # Outer tile (dark slate) with rounded corners.
    $m = [Math]::Max(0, [Math]::Round($s * 0.03))
    $outerR = if ($small) { [Math]::Max(1, $s * 0.12) } else { $s * 0.16 }
    Fill-Block $g $m $m ($s - 2 * $m) ($s - 2 * $m) (C 58 70 92) (C 30 38 54) $outerR

    # Inner area.
    $pad = if ($small) { [Math]::Max(2, [Math]::Round($s * 0.12)) } else { [Math]::Round($s * 0.12) }
    $gap = if ($s -le 16) { 1 } else { [Math]::Max(1, [Math]::Round($s * 0.035)) }
    $x0 = $pad; $y0 = $pad; $w = $s - 2 * $pad; $h = $s - 2 * $pad
    $br = if ($small) { 0 } else { [Math]::Max(1, $s * 0.025) }

    # Orange header bar (the scanned root).
    $hb = [Math]::Max(2, [Math]::Round($h * 0.16))
    Fill-Block $g $x0 $y0 $w $hb (C 255 176 50) (C 245 135 0) $br
    $y1 = $y0 + $hb + $gap; $h1 = $y0 + $h - $y1

    # Big blue block on the left (the large file).
    $lw = [Math]::Round($w * 0.55)
    Fill-Block $g $x0 $y1 $lw $h1 (C 150 198 245) (C 82 145 220) $br

    # Right column: folders (tan) and a smaller file.
    $rx = $x0 + $lw + $gap; $rw = $x0 + $w - $rx
    $th = [Math]::Round($h1 * 0.55)
    Fill-Block $g $rx $y1 $rw $th (C 236 200 152) (C 206 160 108) $br
    $by = $y1 + $th + $gap; $bh = $y1 + $h1 - $by
    if ($s -le 16) {
        Fill-Block $g $rx $by $rw $bh (C 214 170 120) (C 186 138 88) 0
    } else {
        $bw = [Math]::Round($rw * 0.55)
        Fill-Block $g $rx $by $bw $bh (C 214 170 120) (C 186 138 88) $br
        $sx = $rx + $bw + $gap; $sw = $rx + $rw - $sx
        $sh = [Math]::Round($bh * 0.5)
        Fill-Block $g $sx $by $sw $sh (C 150 198 245) (C 82 145 220) $br
        Fill-Block $g $sx ($by + $sh + $gap) $sw ($bh - $sh - $gap) (C 236 200 152) (C 206 160 108) $br
    }

    # Subtle highlight on the outer tile for depth (large sizes only).
    if (-not $small) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([Math]::Max(1, $s / 64))
        $p = New-RoundRect ($m + 0.5) ($m + 0.5) ($s - 2 * $m - 1) ($s - 2 * $m - 1) $outerR
        $g.DrawPath($pen, $p); $pen.Dispose(); $p.Dispose()
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = Render $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    if ($s -eq 256 -and $PreviewPng) { $bmp.Save($PreviewPng) }
    $bmp.Dispose()
}

# Write ICO container (PNG-compressed entries).
$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"wrote $OutIco"
