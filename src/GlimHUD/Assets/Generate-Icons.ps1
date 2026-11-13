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

function Write-SignalIco {
    param([string]$Path, [switch]$Monochrome)
    $sizes = @(16,20,24,32,48,64,128,256)
    $images = @($sizes | ForEach-Object { ,(New-SignalPng -Size $_ -Monochrome:$Monochrome) })
    $stream = [System.IO.File]::Create($Path)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$index].Length
        }
        foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

Write-SignalIco -Path (Join-Path $PSScriptRoot 'GlimHUD.ico')
Write-SignalIco -Path (Join-Path $PSScriptRoot 'GlimHUD-tray.ico') -Monochrome
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'GlimHUD.png'), (New-SignalPng -Size 256))
Write-Output 'Generated GlimHUD Signal SVG-compatible PNG and multi-resolution ICO assets.'
