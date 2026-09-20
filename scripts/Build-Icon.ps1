#Requires -Version 7.0
<#
.SYNOPSIS
Render the CycleArc SVG into the committed multi-resolution Windows icon.
.DESCRIPTION
Uses Windows WPF, with no image tools or third-party dependencies. The small SVG
reader supports only the shapes and linear gradients used by this icon; unsupported
elements fail explicitly. Edit the SVG, then run this script to regenerate the ICO.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$assetDirectory = Join-Path $PSScriptRoot '../src/CycleArc/Assets'
[xml]$source = Get-Content -LiteralPath (Join-Path $assetDirectory 'cyclearc.svg') -Raw
$culture = [Globalization.CultureInfo]::InvariantCulture
$brushes = @{}

function Read-Number($Element, [string]$Name, [double]$Fallback = 0) {
    $value = $Element.GetAttribute($Name)
    if (!$value) { return $Fallback }
    [double]::Parse($value, $culture)
}

function Read-Brush([string]$Value) {
    if (!$Value -or $Value -eq 'none') { return $null }
    if ($Value -match '^url\(#(.+)\)$') {
        if (!$brushes.ContainsKey($Matches[1])) { throw "Unknown gradient: $Value" }
        return $brushes[$Matches[1]]
    }
    [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($Value)
}

foreach ($gradient in $source.svg.defs.ChildNodes) {
    if ($gradient.LocalName -ne 'linearGradient') { throw "Unsupported gradient: $($gradient.LocalName)" }
    $brush = [Windows.Media.LinearGradientBrush]::new()
    $brush.StartPoint = [Windows.Point]::new((Read-Number $gradient 'x1'), (Read-Number $gradient 'y1'))
    $brush.EndPoint = [Windows.Point]::new((Read-Number $gradient 'x2'), (Read-Number $gradient 'y2'))
    foreach ($stop in $gradient.ChildNodes) {
        $color = [Windows.Media.ColorConverter]::ConvertFromString($stop.GetAttribute('stop-color'))
        $brush.GradientStops.Add([Windows.Media.GradientStop]::new($color, (Read-Number $stop 'offset')))
    }
    $brushes[$gradient.GetAttribute('id')] = $brush
}

$frames = foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    # Supersample each size independently so the small frames retain clean edges.
    $renderSize = $size * 4
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($renderSize / 256.0, $renderSize / 256.0))
    foreach ($element in $source.svg.ChildNodes) {
        if ($element.LocalName -in @('title', 'defs')) { continue }
        $fill = Read-Brush $element.GetAttribute('fill')
        $stroke = Read-Brush $element.GetAttribute('stroke')
        $pen = $null
        if ($null -ne $stroke) {
            $stroke = $stroke.Clone()
            $stroke.Opacity = Read-Number $element 'stroke-opacity' 1
            $pen = [Windows.Media.Pen]::new($stroke, (Read-Number $element 'stroke-width' 1))
            if ($element.GetAttribute('stroke-linecap') -eq 'round') {
                $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
            }
        }
        switch ($element.LocalName) {
            'rect' {
                $rect = [Windows.Rect]::new((Read-Number $element 'x'), (Read-Number $element 'y'),
                    (Read-Number $element 'width'), (Read-Number $element 'height'))
                $radius = Read-Number $element 'rx'
                $drawing.DrawRoundedRectangle($fill, $pen, $rect, $radius, $radius)
            }
            'path' { $drawing.DrawGeometry($fill, $pen, [Windows.Media.Geometry]::Parse($element.GetAttribute('d'))) }
            'circle' {
                $center = [Windows.Point]::new((Read-Number $element 'cx'), (Read-Number $element 'cy'))
                $radius = Read-Number $element 'r'
                $drawing.DrawEllipse($fill, $pen, $center, $radius, $radius)
            }
            default { throw "Unsupported SVG element: $($element.LocalName)" }
        }
    }
    $drawing.Pop()
    $drawing.Close()
    $render = [Windows.Media.Imaging.RenderTargetBitmap]::new($renderSize, $renderSize, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $render.Render($visual)
    $scaled = [Windows.Media.Imaging.TransformedBitmap]::new($render, [Windows.Media.ScaleTransform]::new(0.25, 0.25))
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($scaled))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    }
    finally { $stream.Dispose() }
}

$iconPath = Join-Path $assetDirectory 'cyclearc.ico'
$output = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally { $writer.Dispose(); $output.Dispose() }
Write-Host "Rendered $iconPath ($($frames.Size -join ', ') px)"
