# Generate SnipTrans icon: blue rounded base + white snipping corners + text lines
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
    $p.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

foreach ($size in @(16, 32, 48, 256)) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $bmp.SetResolution(96, 96)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $size / 256.0

    # rounded gradient background
    $pad = 6 * $k
    $side = $size - $pad * 2
    $radius = 54 * $k
    $path = New-RoundedPath $pad $pad $side $side $radius
    $p1 = [System.Drawing.PointF]::new(0, 0)
    $p2 = [System.Drawing.PointF]::new($size, $size)
    $c1 = [System.Drawing.Color]::FromArgb(255, 59, 130, 246)
    $c2 = [System.Drawing.Color]::FromArgb(255, 29, 78, 216)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($p1, $p2, $c1, $c2)
    $g.FillPath($brush, $path)

    # snipping corner brackets
    $penW = [Math]::Max(2, 15 * $k)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $penW)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $m = 58 * $k
    $len = 46 * $k
    $e = $size - $m
    $g.DrawLine($pen, $m, ($m + $len), $m, $m)
    $g.DrawLine($pen, $m, $m, ($m + $len), $m)
    $g.DrawLine($pen, ($e - $len), $m, $e, $m)
    $g.DrawLine($pen, $e, $m, $e, ($m + $len))
    $g.DrawLine($pen, $m, ($e - $len), $m, $e)
    $g.DrawLine($pen, $m, $e, ($m + $len), $e)
    $g.DrawLine($pen, ($e - $len), $e, $e, $e)
    $g.DrawLine($pen, $e, ($e - $len), $e, $e)

    # text lines
    if ($size -ge 32) {
        $lw = [Math]::Max(2, 14 * $k)
        $lp = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $lw)
        $lp.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $lp.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $y1 = 116 * $k
        $y2 = 146 * $k
        $g.DrawLine($lp, (86 * $k), $y1, (170 * $k), $y1)
        $lp.Color = [System.Drawing.Color]::FromArgb(190, 255, 255, 255)
        $g.DrawLine($lp, (86 * $k), $y2, (140 * $k), $y2)
        $lp.Dispose()
    }

    $out = Join-Path $PSScriptRoot "icon_$size.png"
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)

    $pen.Dispose()
    $brush.Dispose()
    $path.Dispose()
    $g.Dispose()
    $bmp.Dispose()
    Write-Output "generated: $out"
}
