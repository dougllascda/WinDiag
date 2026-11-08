<#
.SYNOPSIS
  Gera app\WinDiag.App\Assets\WinDiag.ico a partir do MESMO desenho vetorial usado no aplicativo
  (app\WinDiag.App\Assets\Logo.xaml, recurso "WinDiagLogo").

.DESCRIPTION
  Requer Windows PowerShell 5.1 (ou superior) com WPF. Para cada tamanho (16, 24, 32, 48, 64, 128, 256)
  o desenho é renderizado com RenderTargetBitmap, codificado em PNG e gravado como entrada PNG de um
  contêiner ICO. Não usa nenhuma biblioteca externa e é reexecutável (sobrescreve o .ico).

.EXAMPLE
  powershell.exe -STA -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$Logo,
    [string]$Out
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Logo) { $Logo = Join-Path $here '..\app\WinDiag.App\Assets\Logo.xaml' }
if (-not $Out)  { $Out  = Join-Path $here '..\app\WinDiag.App\Assets\WinDiag.ico' }

# WPF exige thread STA; relança a si mesmo se necessário.
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    & powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path -Logo $Logo -Out $Out
    exit $LASTEXITCODE
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Xaml

$Logo = (Resolve-Path $Logo).Path
$Out  = [System.IO.Path]::GetFullPath($Out)

# Carrega o dicionário de recursos e pega a imagem vetorial.
$stream = [System.IO.File]::OpenRead($Logo)
try { $dict = [System.Windows.Markup.XamlReader]::Load($stream) } finally { $stream.Dispose() }
$image = $dict['WinDiagLogo']
if ($null -eq $image) { throw "Recurso 'WinDiagLogo' não encontrado em $Logo" }

function Render-Png([System.Windows.Media.ImageSource]$img, [int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawImage($img, (New-Object System.Windows.Rect(0, 0, $size, $size)))
    $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { ,(Render-Png $image $s) }

# Contêiner ICO: ICONDIR (6 bytes) + ICONDIRENTRY (16 bytes cada) + dados PNG.
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0)               # reservado
$bw.Write([uint16]1)               # tipo: ícone
$bw.Write([uint16]$sizes.Count)    # quantidade de imagens
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }   # 0 significa 256
    $bw.Write([byte]$dim)          # largura
    $bw.Write([byte]$dim)          # altura
    $bw.Write([byte]0)             # cores na paleta
    $bw.Write([byte]0)             # reservado
    $bw.Write([uint16]1)           # planos
    $bw.Write([uint16]32)          # bits por pixel
    $bw.Write([uint32]$pngs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()

$dir = Split-Path $Out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[System.IO.File]::WriteAllBytes($Out, $ms.ToArray())
Write-Host ("Ícone gerado: {0} ({1} bytes, tamanhos: {2})" -f $Out, $ms.Length, ($sizes -join ', '))
