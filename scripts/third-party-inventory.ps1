<#
.SYNOPSIS
  Genera docs/THIRD-PARTY-NOTICES.md: componentes de terceros que se distribuyen con LiveStream y su licencia.
  Paquetes NuGet: licencia leída de su .nuspec (dato real del paquete restaurado).
  Componentes nativos del runtime de libobs: tabla revisada a mano; lo que no se ha confirmado se marca.
  Es un inventario técnico, no asesoría legal.
#>
param([string] $Output)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Output) { $Output = Join-Path $root 'docs/THIRD-PARTY-NOTICES.md' }
$nuget = Join-Path $env:USERPROFILE '.nuget/packages'

# Paquetes que acaban en la aplicación publicada (dependencias transitivas incluidas).
$json = dotnet list (Join-Path $root 'src/LiveStream.Desktop/LiveStream.Desktop.csproj') package --include-transitive --format json | ConvertFrom-Json
$packages = $json.projects.frameworks.topLevelPackages + $json.projects.frameworks.transitivePackages |
    Where-Object { $_ } | Sort-Object id -Unique
$rows = foreach ($p in $packages) {
    $version = if ($p.resolvedVersion) { $p.resolvedVersion } else { $p.requestedVersion }
    $nuspec = Join-Path $nuget "$($p.id.ToLower())/$version/$($p.id.ToLower()).nuspec"
    $license = 'desconocida'
    $url = ''
    if (Test-Path $nuspec) {
        [xml]$x = Get-Content $nuspec -Raw
        $m = $x.package.metadata
        if ($m.license -and $m.license.type -eq 'expression') { $license = $m.license.'#text' }
        elseif ($m.license) { $license = "archivo en el paquete ($($m.license.'#text'))" }
        elseif ($m.licenseUrl) { $license = 'ver URL'; $url = $m.licenseUrl }
        if (-not $url -and $m.projectUrl) { $url = $m.projectUrl }
    }
    "| $($p.id) | $version | $license | $url |"
}

$native = @'
| Componente | Archivos | Licencia | Estado |
|---|---|---|---|
| libobs / plugins de OBS Studio 32.2.2 | obs.dll, libobs-*.dll, obs-plugins/64bit/*, obs-ffmpeg-mux.exe, obs-*-test.exe, data/ | GPL-2.0-or-later | Confirmado (repositorio obsproject/obs-studio) |
| livestream_bridge (propio) | livestream_bridge.dll | Enlaza libobs: debe distribuirse bajo términos compatibles con la GPL | Ver nota |
| x264 | libx264-164.dll | GPL-2.0-or-later | Confirmado |
| FFmpeg (compilación de OBS) | avcodec, avformat, avutil, avfilter, avdevice, swresample, swscale | LGPL-2.1+ o GPL según la configuración de la compilación de OBS | A confirmar con la configuración de obs-deps |
| libcurl | libcurl.dll | curl (tipo MIT) | Confirmado |
| zlib | zlib.dll | Zlib | Confirmado |
| SRT | srt.dll | MPL-2.0 | Confirmado |
| librist | librist.dll | BSD-2-Clause | Confirmado |
| libdatachannel | datachannel.dll | MPL-2.0 | Confirmado |
| pthreads-win32 | w32-pthreads.dll | LGPL-2.1 (o Apache-2.0 en versiones recientes) | A confirmar la versión incluida |
| Qt, Lua, API del frontend de OBS | — | — | **No se distribuyen** (excluidos del runtime en la Fase 7) |
'@

$content = @"
# Avisos de terceros

Generado por ``scripts/third-party-inventory.ps1`` el $(Get-Date -Format 'yyyy-MM-dd'). Inventario técnico de lo que se
distribuye con LiveStream; **no es asesoría legal**.

## Nota sobre la GPL

LiveStream enlaza libobs (GPL-2.0-or-later). Distribuir el programa a terceros obliga a ofrecer el código fuente
correspondiente bajo una licencia compatible con la GPL (incluido el puente nativo y, según la interpretación habitual, la
aplicación que lo carga). Antes de vender o distribuir LiveStream hay que decidir el modelo de licencia con asesoría
legal. Mientras tanto, el instalador y el paquete portable solo deben usarse internamente.

## Componentes nativos (runtime de libobs)

$native

## Paquetes .NET (licencia según el .nuspec de cada paquete)

| Paquete | Versión | Licencia | Proyecto |
|---|---|---|---|
$($rows -join "`n")

## Runtime de .NET

La aplicación se publica autocontenida con el runtime de .NET (MIT, Microsoft).
"@
Set-Content -Path $Output -Value $content -Encoding utf8NoBOM
"Inventario: $Output ($($packages.Count) paquetes .NET)"
