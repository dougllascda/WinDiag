<#
.SYNOPSIS
  Generates the repository images: docs\assets\logo.png (256x256, transparent) and
  docs\assets\social-preview.png (1280x640).

.DESCRIPTION
  The logo is rendered from the same vector drawing used by the app and by tools\make-icon.ps1
  (app\WinDiag.App\Assets\Logo.xaml, resource "WinDiagLogo"). The social preview has a dark
  background (docs\assets\social-bg.jpg darkened, or a flat color if that file is missing), the
  logo on the left, the product name and an English subtitle, and, if docs\assets\overview-dark.png
  exists, a rounded thumbnail of it on the right.
  Requires Windows PowerShell 5.1 (or newer) with WPF. Safe to re-run (overwrites the PNGs).

.EXAMPLE
  powershell.exe -STA -ExecutionPolicy Bypass -File tools\make-social-preview.ps1
#>
[CmdletBinding()]
param(
    [string]$Logo,
    [string]$AssetsDir,
    [string]$Subtitle = 'Hardware inventory, explained diagnostics, one-click repair tools and reversible optimizations for gaming and office PCs.'
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Logo)      { $Logo      = Join-Path $here '..\app\WinDiag.App\Assets\Logo.xaml' }
if (-not $AssetsDir) { $AssetsDir = Join-Path $here '..\docs\assets' }

# WPF needs an STA thread; relaunch if necessary.
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path -Logo $Logo -AssetsDir $AssetsDir -Subtitle $Subtitle
    exit $LASTEXITCODE
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Xaml

$Logo = (Resolve-Path $Logo).Path
if (-not (Test-Path $AssetsDir)) { New-Item -ItemType Directory -Path $AssetsDir | Out-Null }
$AssetsDir = (Resolve-Path $AssetsDir).Path

$stream = [System.IO.File]::OpenRead($Logo)
try { $dict = [System.Windows.Markup.XamlReader]::Load($stream) } finally { $stream.Dispose() }
$image = $dict['WinDiagLogo']
if ($null -eq $image) { throw "Resource 'WinDiagLogo' not found in $Logo" }

function Save-Png([System.Windows.Media.Visual]$visual, [int]$w, [int]$h, [string]$path) {
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($w, $h, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $fs = [System.IO.File]::Create($path)
    try { $enc.Save($fs) } finally { $fs.Dispose() }
    Write-Host ("Generated: {0} ({1}x{2})" -f $path, $w, $h)
}

function New-Brush([string]$hex) {
    $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
    $b.Freeze(); return $b
}

# --- logo.png (256x256, transparent) ---------------------------------------------------------
$v = New-Object System.Windows.Media.DrawingVisual
$dc = $v.RenderOpen()
$dc.DrawImage($image, (New-Object System.Windows.Rect(0, 0, 256, 256)))
$dc.Close()
Save-Png $v 256 256 (Join-Path $AssetsDir 'logo.png')

# --- social-preview.png (1280x640) -----------------------------------------------------------
$W = 1280; $H = 640
$typeface = New-Object System.Windows.Media.Typeface('Segoe UI Semibold')
$typefaceRegular = New-Object System.Windows.Media.Typeface('Segoe UI')
$ppd = 1.0

function New-Text([string]$text, $face, [double]$size, [string]$color, [double]$maxWidth) {
    $ft = New-Object System.Windows.Media.FormattedText($text, [System.Globalization.CultureInfo]::GetCultureInfo('en-US'),
        [System.Windows.FlowDirection]::LeftToRight, $face, $size, (New-Brush $color), $ppd)
    if ($maxWidth -gt 0) { $ft.MaxTextWidth = $maxWidth }
    return $ft
}

$overview = Join-Path $AssetsDir 'overview-dark.png'
$hasShot = Test-Path $overview

$v = New-Object System.Windows.Media.DrawingVisual
$dc = $v.RenderOpen()
$dc.DrawRectangle((New-Brush '#0E1422'), $null, (New-Object System.Windows.Rect(0, 0, $W, $H)))

# Optional background art (docs\assets\social-bg.jpg): drawn to cover the canvas, then darkened so the
# title, the subtitle and the screenshot keep their contrast. Without the file the background stays flat.
$bgPath = Join-Path $AssetsDir 'social-bg.jpg'
if (Test-Path $bgPath) {
    $bgImg = New-Object System.Windows.Media.Imaging.BitmapImage
    $bgImg.BeginInit(); $bgImg.UriSource = New-Object System.Uri($bgPath); $bgImg.CacheOption = 'OnLoad'; $bgImg.EndInit(); $bgImg.Freeze()
    $scale = [Math]::Max($W / $bgImg.PixelWidth, $H / $bgImg.PixelHeight)
    $bw = $bgImg.PixelWidth * $scale; $bh = $bgImg.PixelHeight * $scale
    $dc.PushClip((New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect(0, 0, $W, $H)))))
    $dc.DrawImage($bgImg, (New-Object System.Windows.Rect((($W - $bw) / 2), (($H - $bh) / 2), $bw, $bh)))
    $dc.Pop()
    $dc.DrawRectangle((New-Brush '#590B1220'), $null, (New-Object System.Windows.Rect(0, 0, $W, $H)))
}

if ($hasShot) {
    $leftW = 560
    $logoSize = 128
    $logoX = 72; $logoY = 150
    $textX = 72
    $titleY = 290
    # Thumbnail on the right, rounded corners, thin border.
    $bmp = New-Object System.Windows.Media.Imaging.BitmapImage
    $bmp.BeginInit(); $bmp.UriSource = New-Object System.Uri($overview); $bmp.CacheOption = 'OnLoad'; $bmp.EndInit(); $bmp.Freeze()
    $boxW = 600.0; $boxH = [Math]::Min(480.0, $boxW * $bmp.PixelHeight / $bmp.PixelWidth)
    $boxX = $W - $boxW - 56; $boxY = ($H - $boxH) / 2
    $rect = New-Object System.Windows.Rect($boxX, $boxY, $boxW, $boxH)
    $dc.PushClip((New-Object System.Windows.Media.RectangleGeometry($rect, 14, 14)))
    $dc.DrawImage($bmp, $rect)
    $dc.Pop()
    $pen = New-Object System.Windows.Media.Pen((New-Brush '#2C323B'), 2)
    $dc.DrawRoundedRectangle($null, $pen, $rect, 14, 14)
} else {
    $leftW = 1000
    $logoSize = 160
    $logoX = ($W - 1000) / 2 + 40; $logoY = 150
    $textX = $logoX
    $titleY = 330
}

$dc.DrawImage($image, (New-Object System.Windows.Rect($logoX, $logoY, $logoSize, $logoSize)))
$titleSize = if ($hasShot) { 64 } else { 80 }
$dc.DrawText((New-Text 'WinDiag' $typeface $titleSize '#FFFFFF' 0), (New-Object System.Windows.Point($textX, $titleY)))
$sub = New-Text $Subtitle $typefaceRegular 24 '#A8B3C2' ($leftW - 40)
$subY = $titleY + $titleSize * 1.45
$dc.DrawText($sub, (New-Object System.Windows.Point($textX, $subY)))
$dc.Close()
Save-Png $v $W $H (Join-Path $AssetsDir 'social-preview.png')

if (-not $hasShot) { Write-Host 'Note: docs\assets\overview-dark.png not found; generated without the screenshot thumbnail.' }
