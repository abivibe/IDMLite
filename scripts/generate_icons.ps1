Add-Type -AssemblyName System.Drawing

function Create-IDMIcon {
    param(
        [int]$size,
        [string]$path
    )

    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Background rounded circle
    $rect = New-Object System.Drawing.Rectangle 1, 1, ($size - 2), ($size - 2)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(15, 110, 210)), ([System.Drawing.Color]::FromArgb(0, 160, 240)), 45.0
    $g.FillEllipse($brush, $rect)

    # White border
    $penWidth = [Math]::Max(1, [int]($size / 16))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 255)), $penWidth
    $g.DrawEllipse($pen, $rect)

    # Green download arrow
    $arrowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 210, 90))
    $cx = [float]($size / 2.0)
    $cy = [float]($size / 2.0)
    $s = [float]($size / 3.0)

    $p1 = New-Object System.Drawing.PointF ($cx - $s*0.35), ($cy - $s*0.8)
    $p2 = New-Object System.Drawing.PointF ($cx + $s*0.35), ($cy - $s*0.8)
    $p3 = New-Object System.Drawing.PointF ($cx + $s*0.35), $cy
    $p4 = New-Object System.Drawing.PointF ($cx + $s*0.75), $cy
    $p5 = New-Object System.Drawing.PointF $cx, ($cy + $s*0.8)
    $p6 = New-Object System.Drawing.PointF ($cx - $s*0.75), $cy
    $p7 = New-Object System.Drawing.PointF ($cx - $s*0.35), $cy

    [System.Drawing.PointF[]]$pts = @($p1, $p2, $p3, $p4, $p5, $p6, $p7)
    $g.FillPolygon($arrowBrush, $pts)

    # Base tray line
    $trayPenWidth = [Math]::Max(2, [int]($size / 14))
    $trayPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $trayPenWidth
    $trayPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $trayPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($trayPen, [float]($cx - $s*0.85), [float]($cy + $s*0.95), [float]($cx + $s*0.85), [float]($cy + $s*0.95))

    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Create-IDMIcon 16 'extension/icons/icon16.png'
Create-IDMIcon 32 'extension/icons/icon32.png'
Create-IDMIcon 48 'extension/icons/icon48.png'
Create-IDMIcon 128 'extension/icons/icon128.png'
Create-IDMIcon 256 'installer/assets/app_icon.png'

# Create .ico file
$bmp32 = [System.Drawing.Bitmap]::FromFile('extension/icons/icon32.png')
$hIcon = $bmp32.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hIcon)
$fs = [System.IO.File]::Create('ui/app_icon.ico')
$icon.Save($fs)
$fs.Close()
Copy-Item 'ui/app_icon.ico' 'installer/app_icon.ico'
$bmp32.Dispose()

Write-Host "Icons generated successfully!"
