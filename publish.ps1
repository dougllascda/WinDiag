# publish.ps1 - gera os artefatos de release do WinDiag em dist\
#   dist\WinDiag-Portable-{versao}.exe   self-contained, arquivo unico, comprimido (nao exige nada instalado)
#   dist\WinDiag-{versao}-fx.exe         framework-dependent, arquivo unico, pequeno (exige .NET 8 Desktop Runtime)
#   dist\WinDiag-Setup-{versao}.exe      instalador Inno Setup (so se o ISCC.exe estiver disponivel)
#   dist\SHA256SUMS.txt
#
# Uso: pwsh -File publish.ps1 [-SkipTests] [-Scan]
#   A versao vem de app\Directory.Build.props.
#
# Assinatura (opcional): se a variavel de ambiente WINDIAG_SIGN_CMD existir, ela e executada
# para cada .exe gerado, antes do hash. O token {file} e substituido pelo caminho do arquivo
# (se nao houver {file}, o caminho e acrescentado ao final, entre aspas). Exemplo:
#   $env:WINDIAG_SIGN_CMD = 'signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f cert.pfx /p SENHA "{file}"'
# Veja docs\SIGNING.md.
param([switch]$SkipTests, [switch]$Scan)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$app = Join-Path $root 'app'
$dist = Join-Path $root 'dist'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $cmd = Get-Command dotnet -ErrorAction SilentlyContinue; if ($cmd) { $dotnet = $cmd.Source } else { throw 'dotnet nao encontrado.' } }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

# Versao (fonte unica: Directory.Build.props)
[xml]$props = Get-Content (Join-Path $app 'Directory.Build.props') -Raw
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Versao nao encontrada em app\Directory.Build.props.' }
Write-Host "Versao: $version"

function Invoke-Sign([string]$file) {
    $tpl = $env:WINDIAG_SIGN_CMD
    if (-not $tpl) { return }
    if ($tpl -like '*{file}*') { $cmdLine = $tpl.Replace('{file}', $file) } else { $cmdLine = "$tpl `"$file`"" }
    Write-Host ("[assinando] " + (Split-Path $file -Leaf))
    & cmd.exe /c $cmdLine
    if ($LASTEXITCODE -ne 0) { throw "Assinatura falhou para $file (WINDIAG_SIGN_CMD)." }
}

if (-not $SkipTests) {
    & $dotnet test (Join-Path $app 'WinDiag.sln') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Testes falharam.' }
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null
$proj = Join-Path $app 'WinDiag.App\WinDiag.App.csproj'
$common = @('-c', 'Release', '-r', 'win-x64', '-p:PublishSingleFile=true', '-p:DebugType=none', '--nologo', '-v', 'q')

# framework-dependent
$tmp1 = Join-Path $env:TEMP ('wd_pub_fd_' + [guid]::NewGuid().ToString('N'))
& $dotnet publish $proj @common --self-contained false -o $tmp1
if ($LASTEXITCODE -ne 0) { throw 'publish (framework-dependent) falhou.' }
$fxExe = Join-Path $dist "WinDiag-$version-fx.exe"
Copy-Item (Join-Path $tmp1 'WinDiag.exe') $fxExe

# self-contained (portatil; tambem e a base do instalador)
$tmp2 = Join-Path $env:TEMP ('wd_pub_sc_' + [guid]::NewGuid().ToString('N'))
& $dotnet publish $proj @common --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $tmp2
if ($LASTEXITCODE -ne 0) { throw 'publish (self-contained) falhou.' }
$portableExe = Join-Path $dist "WinDiag-Portable-$version.exe"
Copy-Item (Join-Path $tmp2 'WinDiag.exe') $portableExe
Remove-Item $tmp1, $tmp2 -Recurse -Force -ErrorAction SilentlyContinue

# assina os executaveis antes de embuti-los no instalador
Invoke-Sign $fxExe
Invoke-Sign $portableExe

# instalador (Inno Setup)
$iscc = $null
$candidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    # Instalacao por usuario (winget/instalador sem admin)
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { $iscc = $c; break } }
if (-not $iscc) { $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue; if ($cmd) { $iscc = $cmd.Source } }
if ($iscc) {
    Write-Host "Inno Setup: $iscc"
    & $iscc "/DMyAppVersion=$version" "/DSourceExe=$portableExe" "/O$dist" (Join-Path $root 'installer\WinDiag.iss')
    if ($LASTEXITCODE -ne 0) { throw 'ISCC falhou ao gerar o instalador.' }
    Invoke-Sign (Join-Path $dist "WinDiag-Setup-$version.exe")
} else {
    Write-Warning 'Inno Setup (ISCC.exe) nao encontrado; instalador ignorado. Instale o Inno Setup 6 (https://jrsoftware.org/isinfo.php) para gerar o Setup.'
}

# hashes
$lines = foreach ($f in Get-ChildItem $dist -Filter '*.exe' | Sort-Object Name) {
    $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower()
    Write-Host ('[OK] {0}  {1:N1} MB  sha256 {2}' -f $f.Name, ($f.Length / 1MB), $h.Substring(0, 12))
    "$h  $($f.Name)"
}
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), (($lines -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding($false)))

if ($Scan) {
    $avp = Get-ChildItem "${env:ProgramFiles(x86)}\Kaspersky Lab", "$env:ProgramFiles\Kaspersky Lab" -Recurse -Filter avp.com -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($avp) { & $avp.FullName SCAN $dist /i0 | Select-String 'Total detected|Suspicions|Processed objects' }
    else { Write-Host 'Kaspersky avp.com nao encontrado; varredura ignorada.' }
}
