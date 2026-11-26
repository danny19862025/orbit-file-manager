# Draws the Orbit app icon and writes Assets\orbit.ico (16–256 px) and Assets\orbit-256.png.
# Run from the repo:  pwsh -STA -File tools\make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot "..\Assets"))
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

function C([string]$hex, [byte]$a = 255) { $c = [System.Windows.Media.ColorConverter]::ConvertFromString($hex); [System.Windows.Media.Color]::FromArgb($a, $c.R, $c.G, $c.B) }

function Draw([int]$size) {
    $s = $size / 256.0
    # small sizes get bolder lines so the ring doesn't vanish
    $ringW = [Math]::Max(11 * $s, 1.6)
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $s, $s))

    # tile: rounded square, mauve → blue
    $grad = New-Object System.Windows.Media.LinearGradientBrush (C "#CBA6F7"), (C "#89B4FA"), (New-Object System.Windows.Point 0, 0), (New-Object System.Windows.Point 1, 1)
    $pad = if ($size -le 24) { 4 } else { 14 }
    $dc.DrawRoundedRectangle($grad, $null, (New-Object System.Windows.Rect $pad, $pad, (256 - 2 * $pad), (256 - 2 * $pad)), 58, 58)
    # soft highlight on the top half
    $hl = New-Object System.Windows.Media.LinearGradientBrush (C "#FFFFFF" 60), (C "#FFFFFF" 0), 90
    $dc.DrawRoundedRectangle($hl, $null, (New-Object System.Windows.Rect $pad, $pad, (256 - 2 * $pad), 120), 58, 58)

    $center = New-Object System.Windows.Point 128, 128
    $rot = New-Object System.Windows.Media.RotateTransform -24, 128, 128
    $ringPen = New-Object System.Windows.Media.Pen (New-Object System.Windows.Media.SolidColorBrush (C "#FFFFFF" 235)), ($ringW / $s)
    $ringPen.StartLineCap = 'Round'; $ringPen.EndLineCap = 'Round'
    $rx = 100; $ry = 38

    # back half of the ring (behind the planet)
    $dc.PushTransform($rot)
    $back = [System.Windows.Media.Geometry]::Parse("M $(128 - $rx),128 A $rx,$ry 0 0 1 $(128 + $rx),128")
    $dc.DrawGeometry($null, $ringPen, $back)
    $dc.Pop()

    # planet: dark core with a gentle rim
    $core = New-Object System.Windows.Media.RadialGradientBrush (C "#313244"), (C "#11111B")
    $core.GradientOrigin = New-Object System.Windows.Point 0.35, 0.3
    $dc.DrawEllipse($core, $null, $center, 46, 46)

    # front half of the ring (in front of the planet)
    $dc.PushTransform($rot)
    $front = [System.Windows.Media.Geometry]::Parse("M $(128 + $rx),128 A $rx,$ry 0 0 1 $(128 - $rx),128")
    $dc.DrawGeometry($null, $ringPen, $front)
    # moon on the ring, front-left
    $t = [Math]::PI * 0.78
    $mx = 128 + $rx * [Math]::Cos($t); $my = 128 + $ry * [Math]::Sin($t)
    $moonR = [Math]::Max(17, 2.6 / $s)
    $dc.DrawEllipse((New-Object System.Windows.Media.SolidColorBrush (C "#FAB387")), (New-Object System.Windows.Media.Pen (New-Object System.Windows.Media.SolidColorBrush (C "#1E1E2E")), ([Math]::Max(6, 1 / $s))), (New-Object System.Windows.Point $mx, $my), $moonR, $moonR)
    $dc.Pop()

    $dc.Pop()
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

New-Item -ItemType Directory $OutDir -Force | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$pngs = @{}
foreach ($z in $sizes) { $pngs[$z] = Draw $z }
[IO.File]::WriteAllBytes((Join-Path $OutDir "orbit-256.png"), $pngs[256])

# ICO container with PNG-compressed images (supported since Windows Vista)
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($z in $sizes) {
    $b = if ($z -ge 256) { 0 } else { $z }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$z].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$z].Length
}
foreach ($z in $sizes) { $w.Write($pngs[$z]) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir "orbit.ico"), $ms.ToArray())
"wrote orbit.ico ($([Math]::Round($ms.Length / 1KB)) KB, $($sizes.Count) sizes) and orbit-256.png"
