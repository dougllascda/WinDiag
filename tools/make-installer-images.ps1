<#
.SYNOPSIS
  Gera installer\wizard.bmp, a imagem lateral do assistente de instalação (Inno Setup, WizardImageFile).

.DESCRIPTION
  A imagem é desenhada a partir do MESMO desenho vetorial do aplicativo (app\WinDiag.App\Assets\Logo.xaml,
  recurso "WinDiagLogo"): fundo escuro, logo, nome do produto e uma linha de descrição.
  Tamanho 328 x 628 (o dobro do padrão 164 x 314, para telas de alta densidade; o Inno Setup reduz quando preciso).
  BMP de 24 bits sem transparência, que é o formato aceito por todas as versões do Inno Setup 6.
  Requer Windows PowerShell 5.1 (ou superior) com WPF. Reexecutável (sobrescreve o arquivo).

.EXAMPLE
  powershell.exe -STA -ExecutionPolicy Bypass -File tools\make-installer-images.ps1
#>
[CmdletBinding()]
param(
    [string]$Logo,
    [string]$Out
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Logo) { $Logo = Join-Path $here '..\app\WinDiag.App\Assets\Logo.xaml' }
if (-not $Out)  { $Out  = Join-Path $here '..\installer\wizard.bmp' }

# WPF exige thread STA; relança a si mesmo se necessário.
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path -Logo $Logo -Out $Out
    exit $LASTEXITCODE
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Xaml

$Logo = (Resolve-Path $Logo).Path
$Out  = [System.IO.Path]::GetFullPath($Out)

$stream = [System.IO.File]::OpenRead($Logo)
try { $dict = [System.Windows.Markup.XamlReader]::Load($stream) } finally { $stream.Dispose() }
$image = $dict['WinDiagLogo']
if ($null -eq $image) { throw "Recurso 'WinDiagLogo' não encontrado em $Logo" }

function New-Brush([string]$hex) {
    $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
    $b.Freeze(); return $b
}

$W = 328; $H = 628
$v = New-Object System.Windows.Media.DrawingVisual
$dc = $v.RenderOpen()

# Fundo: gradiente vertical no mesmo azul-marinho do logo.
$bg = New-Object System.Windows.Media.LinearGradientBrush
$bg.StartPoint = New-Object System.Windows.Point(0, 0); $bg.EndPoint = New-Object System.Windows.Point(0, 1)
$bg.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.ColorConverter]::ConvertFromString('#14233F')), 0))
$bg.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.ColorConverter]::ConvertFromString('#0B1220')), 1))
$dc.DrawRectangle($bg, $null, (New-Object System.Windows.Rect(0, 0, $W, $H)))

# Logo centralizado no terço superior.
$logoSize = 168
$dc.DrawImage($image, (New-Object System.Windows.Rect((($W - $logoSize) / 2), 150, $logoSize, $logoSize)))

# Nome e descrição, centralizados.
$culture = [System.Globalization.CultureInfo]::GetCultureInfo('en-US')
$title = New-Object System.Windows.Media.FormattedText('WinDiag', $culture, [System.Windows.FlowDirection]::LeftToRight,
    (New-Object System.Windows.Media.Typeface('Segoe UI Semibold')), 44, (New-Brush '#FFFFFF'), 1.0)
$dc.DrawText($title, (New-Object System.Windows.Point((($W - $title.Width) / 2), 344)))

$sub = New-Object System.Windows.Media.FormattedText('Hardware and system diagnostics', $culture, [System.Windows.FlowDirection]::LeftToRight,
    (New-Object System.Windows.Media.Typeface('Segoe UI')), 19, (New-Brush '#A8B8D0'), 1.0)
$dc.DrawText($sub, (New-Object System.Windows.Point((($W - $sub.Width) / 2), 408)))
$dc.Close()

$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($W, $H, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($v)
# Converte para 24 bits (sem canal alfa): o fundo é opaco, então nada se perde.
$bgr = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($rtb, [System.Windows.Media.PixelFormats]::Bgr24, $null, 0)
$enc = New-Object System.Windows.Media.Imaging.BmpBitmapEncoder
$enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bgr))
$fs = [System.IO.File]::Create($Out)
try { $enc.Save($fs) } finally { $fs.Dispose() }
Write-Host ("Imagem do instalador gerada: {0} ({1}x{2})" -f $Out, $W, $H)
