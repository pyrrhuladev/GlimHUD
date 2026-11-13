[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectangle {
    param([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return ,$path
}

function New-SignalPng {
    param([int]$Size, [switch]$Monochrome)
    $supersampling = 4
    $bitmap = [System.Drawing.Bitmap]::new($Size * $supersampling, $Size * $supersampling, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.ScaleTransform($Size * $supersampling / 256.0, $Size * $supersampling / 256.0)

    $white = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#E9FAFF'))
    $tile = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#17212B'))
    $cyan = [System.Drawing.ColorTranslator]::FromHtml('#58D8F5')
    if ($Monochrome) { $cyan = [System.Drawing.Color]::White }
    $outline = [System.Drawing.Pen]::new($cyan, 14)
    $tilePath = New-RoundedRectangle 12 12 232 232 54
    $pillPath = New-RoundedRectangle 51 88 154 80 40
    if (-not $Monochrome) { $graphics.FillPath($tile, $tilePath) }
    $graphics.DrawPath($outline, $pillPath)
    foreach ($bar in @(@(86,115,13,26),@(121.5,102,13,52),@(157,115,13,26))) {
        $barPath = New-RoundedRectangle $bar[0] $bar[1] $bar[2] $bar[3] 6.5
        $graphics.FillPath($white, $barPath)
        $barPath.Dispose()
    }
    $graphics.Dispose()
    $tile.Dispose()
    $white.Dispose()
    $outline.Dispose()
    $tilePath.Dispose()
    $pillPath.Dispose()

    $output = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $outputGraphics = [System.Drawing.Graphics]::FromImage($output)
    $outputGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $outputGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $outputGraphics.DrawImage($bitmap, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
    $stream = [System.IO.MemoryStream]::new()
    $output.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    $outputGraphics.Dispose()
    $output.Dispose()
    $bitmap.Dispose()
    return ,$bytes
}

# The same Signal geometry as src/GlimHUD/Assets/Generate-Icons.ps1.
$outputPath = Join-Path $PSScriptRoot 'Assets'
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
foreach ($asset in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
    [IO.File]::WriteAllBytes((Join-Path $outputPath $asset[0]), (New-SignalPng -Size $asset[1]))
}
Write-Output 'Generated MSIX Signal assets (50x50, 44x44, 150x150).'