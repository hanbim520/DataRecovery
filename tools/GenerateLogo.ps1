param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\DataRecovery.App\Assets')
)

Add-Type -AssemblyName System.Drawing

$size = 256
$bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

function New-RoundedRectanglePath([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$backgroundPath = New-RoundedRectanglePath 8 8 240 240 58
$backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.PointF]::new(28, 20),
    [System.Drawing.PointF]::new(226, 238),
    [System.Drawing.ColorTranslator]::FromHtml('#2DD4BF'),
    [System.Drawing.ColorTranslator]::FromHtml('#0F766E'))
$graphics.FillPath($backgroundBrush, $backgroundPath)

$diskBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(248, 255, 255, 255))
$graphics.FillEllipse($diskBrush, 67, 74, 122, 122)
$hubBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#0F766E'))
$graphics.FillEllipse($hubBrush, 110, 117, 36, 36)
$hubHighlight = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#99F6E4'))
$graphics.FillEllipse($hubHighlight, 121, 128, 14, 14)

$arrowPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 15)
$arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawArc($arrowPen, 40, 35, 176, 145, 198, 228)
$arrowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
$arrowHead = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(216, 65),
    [System.Drawing.PointF]::new(218, 101),
    [System.Drawing.PointF]::new(184, 94))
$graphics.FillPolygon($arrowBrush, $arrowHead)

$lowerPen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#CCFBF1'), 9)
$lowerPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$lowerPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$graphics.DrawArc($lowerPen, 62, 128, 132, 92, 24, 132)

$pngPath = Join-Path $OutputDirectory 'datarecovery-logo.png'
$bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$icoPath = Join-Path $OutputDirectory 'datarecovery-logo.ico'
$stream = [System.IO.File]::Create($icoPath)
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$pngBytes.Length)
$writer.Write([uint32]22)
$writer.Write($pngBytes)
$writer.Dispose()

$lowerPen.Dispose()
$arrowBrush.Dispose()
$arrowPen.Dispose()
$hubHighlight.Dispose()
$hubBrush.Dispose()
$diskBrush.Dispose()
$backgroundBrush.Dispose()
$backgroundPath.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

Write-Host "Generated $pngPath"
Write-Host "Generated $icoPath"
