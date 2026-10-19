<#
.SYNOPSIS
  Genera la distribución de LiveStream en artifacts/release:
    LiveStream-Setup-<versión>.exe     instalador por usuario (paquete incrustado)
    LiveStream-<versión>-portable.zip  la aplicación sin instalar
    LiveStream-<versión>-symbols.zip   símbolos de depuración (.pdb), no se distribuyen
    SHA256SUMS.txt
  Requiere el runtime de scripts/build-native.ps1. Con -SigningKey firma además el manifiesto de actualización
  (scripts/sign-update.ps1). Sin certificado de firma de código, los .exe no llevan firma Authenticode.
#>
param(
    [string] $Configuration = 'Release',
    [string] $SigningKey,
    [string] $DownloadBaseUrl
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No se encontró <Version> en Directory.Build.props.' }
if (-not (Test-Path (Join-Path $root 'artifacts/obs-runtime/livestream_bridge.dll'))) { throw 'Falta el runtime: ejecuta scripts/build-native.ps1.' }

$out = Join-Path $root 'artifacts/release'
$publish = Join-Path $root 'artifacts/publish/LiveStream'
$setupOut = Join-Path $root 'artifacts/publish/setup'
foreach ($d in $out, $publish, $setupOut) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "LiveStream $version"
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
Copy-Item (Join-Path $root 'docs/THIRD-PARTY-NOTICES.md') $out -ErrorAction SilentlyContinue

Write-Host '3/5 Paquete portable…'
$portable = Join-Path $out "LiveStream-$version-portable.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $portable -CompressionLevel Optimal

Write-Host '4/5 Instalador…'
dotnet publish (Join-Path $root 'tools/LiveStream.Setup/LiveStream.Setup.csproj') -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    "-p:PayloadZip=$portable" -o $setupOut -v q -nologo
if ($LASTEXITCODE) { throw 'La publicación del instalador falló.' }
$setup = Join-Path $out "LiveStream-Setup-$version.exe"
Move-Item (Join-Path $setupOut 'LiveStream-Setup.exe') $setup

Write-Host '5/5 Sumas de comprobación…'
$sums = Get-ChildItem $out -File | Where-Object Extension -in '.exe', '.zip' |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
Set-Content (Join-Path $out 'SHA256SUMS.txt') $sums -Encoding ascii

if ($SigningKey) {
    if (-not $DownloadBaseUrl) { throw 'Con -SigningKey hay que indicar -DownloadBaseUrl (dónde se publicará el instalador).' }
    & (Join-Path $PSScriptRoot 'sign-update.ps1') -Installer $setup -Version $version -Url "$($DownloadBaseUrl.TrimEnd('/'))/$(Split-Path $setup -Leaf)" `
        -PrivateKey $SigningKey -Output (Join-Path $out 'update.json')
}

Get-ChildItem $out | ForEach-Object { '{0,-45} {1,8:0.0} MB' -f $_.Name, ($_.Length / 1MB) }
