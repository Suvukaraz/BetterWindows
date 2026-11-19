# Erzeugt Assets\AppIcon.ico (16-256 px) und Assets\AppIcon.png (Vorschau).
# Motiv: App-Fenster mit Haken ("Windows, aufgeraeumt") auf Gruen-Tuerkis-Verlauf.
# Aufruf: powershell -ExecutionPolicy Bypass -File Assets\make-icon.ps1
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Check($g, [float]$s, [float]$cx, [float]$cy, [float]$w, [float]$thick) {
    # Haken aus zwei Strichen mit runden Enden
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $thick)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx - $w * 0.50, $cy + $w * 0.02),
        [System.Drawing.PointF]::new($cx - $w * 0.14, $cy + $w * 0.36),
        [System.Drawing.PointF]::new($cx + $w * 0.50, $cy - $w * 0.30))
    $g.DrawLines($pen, $pts)
    $pen.Dispose()
}

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    # Hintergrund: abgerundetes Quadrat, Verlauf Smaragdgruen -> Tuerkis
    $m = [Math]::Max(0.5, $s * 0.03)
    $bg = New-RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $rect = [System.Drawing.RectangleF]::new(0, 0, $s, $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(255, 16, 185, 129), [System.Drawing.Color]::FromArgb(255, 8, 145, 178), 60.0)
    $g.FillPath($grad, $bg)
    $gloss = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(50, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90.0)
    $g.FillPath($gloss, $bg)

    $white = [System.Drawing.Color]::White
    if ($s -ge 32) {
        # App-Fenster: Rahmen, Titelleiste mit drei Punkten
        $pen = New-Object System.Drawing.Pen($white, [float]($s * 0.05))
        $pen.LineJoin = 'Round'
        $wx = $s * 0.18; $wy = $s * 0.22; $ww = $s * 0.64; $wh = $s * 0.56
        $g.DrawPath($pen, (New-RoundRect $wx $wy $ww $wh ($s * 0.07)))
        $g.DrawLine($pen, [float]$wx, [float]($wy + $s * 0.13), [float]($wx + $ww), [float]($wy + $s * 0.13))
        $dot = New-Object System.Drawing.SolidBrush $white
        foreach ($i in 0..2) {
            $r = $s * 0.022
            $g.FillEllipse($dot, [float]($wx + $s * 0.07 + $i * $s * 0.065 - $r), [float]($wy + $s * 0.065 - $r), [float](2 * $r), [float](2 * $r))
        }
        # Haken im Fensterinhalt
        Draw-Check $g $s ($s * 0.50) ($s * 0.585) ($s * 0.30) ([float]($s * 0.075))
    } else {
        # kleine Groessen: nur ein kraeftiger Haken, damit es scharf bleibt
        Draw-Check $g $s ($s * 0.50) ($s * 0.52) ($s * 0.58) ([float]([Math]::Max(2.0, $s * 0.15)))
    }
    $g.Dispose()
    return $bmp
}
$dir = $PSScriptRoot
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = Render $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $b.Save((Join-Path $dir 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
    , $ms.ToArray()
}

# ICO-Datei mit PNG-komprimierten Bildern schreiben
$fs = [System.IO.File]::Create((Join-Path $dir 'AppIcon.ico'))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $pngs[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $w.Write($data) }
$w.Dispose()
"AppIcon.ico geschrieben ($($sizes.Count) Groessen)"
