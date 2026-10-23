# Generates Assets/appicon.ico (blue rounded square + white battery, PNG-embedded ICO).
Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

# Rounded-square background with a subtle accent blue
$bg = New-Object System.Drawing.Drawing2D.GraphicsPath
$bg.AddArc(16, 16, 40, 40, 180, 90)
$bg.AddArc($size-56, 16, 40, 40, 270, 90)
$bg.AddArc($size-56, $size-56, 40, 40, 0, 90)
$bg.AddArc(16, $size-56, 40, 40, 90, 90)
$bg.CloseFigure()
$g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 120, 212))), $bg)

# Battery body (white outline)
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 12)
$pen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
$rect = New-Object System.Drawing.Rectangle(66, 96, 104, 72)
$g.DrawRectangle($pen, $rect)

# Battery tip
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), (New-Object System.Drawing.Rectangle(172, 114, 14, 36)))

# Battery fill (green, ~3/4)
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 108, 203, 95))), (New-Object System.Drawing.Rectangle(78, 108, 70, 48)))

$g.Dispose()

$outDir = Join-Path $PSScriptRoot "..\src\BTDeviceBattery\Assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$pngPath = Join-Path $env:TEMP "btbattery_icon.png"
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

# ICO container with a single PNG-embedded entry (valid since Vista)
$png = [System.IO.File]::ReadAllBytes($pngPath)
$icoPath = Join-Path $outDir "appicon.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)      # reserved
$bw.Write([uint16]1)      # type: icon
$bw.Write([uint16]1)      # count
$bw.Write([byte]0)        # width 256
$bw.Write([byte]0)        # height 256
$bw.Write([byte]0)        # colors
$bw.Write([byte]0)        # reserved
$bw.Write([uint16]1)      # planes
$bw.Write([uint16]32)     # bpp
$bw.Write([uint32]$png.Length)
$bw.Write([uint32]22)     # data offset (6 + 16)
$bw.Write($png)
$bw.Close()
Write-Host "Written $icoPath"