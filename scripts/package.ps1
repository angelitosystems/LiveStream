<#
.SYNOPSIS
  Genera la distribución de LiveStream en artifacts/release:
    LiveStream-Setup-<versión>.exe     instalador por usuario (paquete incrustado)
    LiveStream-<versión>-portable.zip  la aplicación sin instalar
    LiveStream-<versión>-symbols.zip   símbolos de depuración (.pdb), no se distribuyen
    SHA256SUMS.txt, LICENSE.txt, THIRD-PARTY-NOTICES.md
  Requiere el runtime de scripts/build-native.ps1. Con -SigningKey firma además el manifiesto de actualización
  (scripts/sign-update.ps1).

.DESCRIPTION
  Firma de código (Authenticode). Los ejecutables propios deben firmarse ANTES de incrustarse en el instalador, y el
  instalador antes de calcular las sumas y el manifiesto de actualización. Por eso el proceso tiene etapas:
    -Stage App        publica la aplicación en artifacts/publish/LiveStream (aquí se firman sus .exe/.dll propios)
    -Stage Installer  portable + instalador a partir de esa carpeta (aquí se firma el instalador)
    -Stage Checksums  SHA256SUMS.txt y, con -SigningKey, update.json
    -Stage All        las tres seguidas (por defecto)
  En GitHub Actions la firma la hace SignPath entre etapas (.github/workflows/release.yml). En local, con
  -CertificateThumbprint se firma con signtool y un certificado del almacén del usuario (token USB o .pfx importado).
#>
param(
    [string] $Configuration = 'Release',
    [ValidateSet('All', 'App', 'Installer', 'Checksums')] [string] $Stage = 'All',
    [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $SigningKey,
    [string] $DownloadBaseUrl
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No se encontró <Version> en Directory.Build.props.' }

$out = Join-Path $root 'artifacts/release'
$publish = Join-Path $root 'artifacts/publish/LiveStream'
$setupOut = Join-Path $root 'artifacts/publish/setup'
$setup = Join-Path $out "LiveStream-Setup-$version.exe"
$portable = Join-Path $out "LiveStream-$version-portable.zip"

# Archivos propios que se firman; los de terceros (libobs, FFmpeg, .NET…) se distribuyen tal cual.
$OwnBinaries = 'LiveStream.Desktop.exe', 'LiveStream.*.dll', 'livestream_bridge.dll'

function Invoke-Sign([string[]] $files) {
    if (-not $CertificateThumbprint) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
        Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'No se encontró signtool.exe (Windows SDK).' }
    & $signtool.FullName sign /sha1 $CertificateThumbprint /fd sha256 /tr $TimestampUrl /td sha256 /d LiveStream $files
    if ($LASTEXITCODE) { throw 'La firma Authenticode falló.' }
    & $signtool.FullName verify /pa /q $files
    if ($LASTEXITCODE) { throw 'La firma Authenticode no se verifica.' }
}

Write-Host "LiveStream $version ($Stage)"

if ($Stage -in 'All', 'App') {
    if (-not (Test-Path (Join-Path $root 'artifacts/obs-runtime/livestream_bridge.dll'))) { throw 'Falta el runtime: ejecuta scripts/build-native.ps1.' }
    foreach ($d in $out, $publish, $setupOut) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
    New-Item -ItemType Directory -Force $out | Out-Null

    Write-Host '1/5 Publicando la aplicación (autocontenida, win-x64)…'
    dotnet publish (Join-Path $root 'src/LiveStream.Desktop/LiveStream.Desktop.csproj') -c $Configuration -r win-x64 --self-contained true -o $publish -v q -nologo
    if ($LASTEXITCODE) { throw 'dotnet publish falló.' }
    foreach ($required in 'LiveStream.Desktop.exe', 'livestream_bridge.dll', 'obs.dll', 'obs-plugins/64bit/win-capture.dll', 'data/libobs') {
        if (-not (Test-Path (Join-Path $publish $required))) { throw "La publicación no contiene $required." }
    }

    Write-Host '2/5 Separando símbolos y añadiendo avisos de licencias…'
    $symbols = Join-Path $out "LiveStream-$version-symbols.zip"
    $pdbs = Get-ChildItem $publish -Recurse -Filter *.pdb
    Compress-Archive -Path $pdbs.FullName -DestinationPath $symbols -CompressionLevel Optimal
    $pdbs | Remove-Item
    & (Join-Path $PSScriptRoot 'third-party-inventory.ps1') -Output (Join-Path $publish 'THIRD-PARTY-NOTICES.md') | Out-Null
    Copy-Item (Join-Path $publish 'THIRD-PARTY-NOTICES.md') $out
    Copy-Item (Join-Path $publish 'THIRD-PARTY-NOTICES.md') (Join-Path $root 'docs/THIRD-PARTY-NOTICES.md')
    # Licencia GPL-2.0-or-later: viaja con la aplicación y junto a la distribución.
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publish 'LICENSE.txt')
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE.txt')
    Invoke-Sign (Get-ChildItem (Join-Path $publish '*') -File -Include $OwnBinaries).FullName
}

if ($Stage -in 'All', 'Installer') {
    if (-not (Test-Path (Join-Path $publish 'LiveStream.Desktop.exe'))) { throw "Falta la aplicación publicada en $publish (etapa App)." }
    New-Item -ItemType Directory -Force $out | Out-Null
    foreach ($f in $portable, $setup, $setupOut) { if (Test-Path $f) { Remove-Item -Recurse -Force $f } }

    Write-Host '3/5 Paquete portable…'
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $portable -CompressionLevel Optimal

    Write-Host '4/5 Instalador…'
    dotnet publish (Join-Path $root 'tools/LiveStream.Setup/LiveStream.Setup.csproj') -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        "-p:PayloadZip=$portable" -o $setupOut -v q -nologo
    if ($LASTEXITCODE) { throw 'La publicación del instalador falló.' }
    Move-Item (Join-Path $setupOut 'LiveStream-Setup.exe') $setup
    Invoke-Sign $setup
}

if ($Stage -in 'All', 'Checksums') {
    if (-not (Test-Path $setup)) { throw "Falta $setup (etapa Installer)." }
    Write-Host '5/5 Sumas de comprobación…'
    $sums = Get-ChildItem $out -File | Where-Object Extension -in '.exe', '.zip' |
        ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
    Set-Content (Join-Path $out 'SHA256SUMS.txt') $sums -Encoding ascii

    if ($SigningKey) {
        if (-not $DownloadBaseUrl) { throw 'Con -SigningKey hay que indicar -DownloadBaseUrl (dónde se publicará el instalador).' }
        & (Join-Path $PSScriptRoot 'sign-update.ps1') -Installer $setup -Version $version -Url "$($DownloadBaseUrl.TrimEnd('/'))/$(Split-Path $setup -Leaf)" `
            -PrivateKey $SigningKey -Output (Join-Path $out 'update.json')
    }
}

Get-ChildItem $out | ForEach-Object { '{0,-45} {1,8:0.0} MB' -f $_.Name, ($_.Length / 1MB) }
