<#
.SYNOPSIS
  Escribe las notas de la release de una versión: cómo instalar + su sección de CHANGELOG.md.
  Lo usa .github/workflows/release.yml; también sirve en local para revisar las notas antes de publicar.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Output,
    [switch] $Signed,
    [string] $Repository = 'angelitosystems/LiveStream'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$changelog = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding utf8
$pattern = '(?ms)^## \[' + [regex]::Escape($Version) + '\][^\r\n]*\r?\n(.*?)(?=^## \[|^\[[^\]]+\]:|\z)'
$section = [regex]::Match($changelog, $pattern).Groups[1].Value.Trim()
if (-not $section) { throw "CHANGELOG.md no tiene la sección [$Version]." }

$signature = if ($Signed) { 'Los ejecutables están firmados con Authenticode (SignPath Foundation).' }
             else { 'Los ejecutables **no están firmados** con Authenticode: SmartScreen puede avisar («Más información → Ejecutar de todas formas»).' }
$tag = "v$Version"
$notes = @"
Descarga **``LiveStream-Setup-$Version.exe``** y ejecútalo: un asistente te guía en la instalación (solo para tu usuario, sin permisos de administrador). Si ya tienes una versión anterior, se actualiza en la misma carpeta y conserva tus proyectos y ajustes. También hay un ``.zip`` portable.

- Requisitos: Windows 10/11 x64 y GPU con Direct3D 11.
- $signature
- ``SHA256SUMS.txt`` contiene el SHA-256 del instalador y del portable.
- Código fuente de esta versión: tag [``$tag``](https://github.com/$Repository/tree/$tag) · licencia GPL-2.0-or-later.

## Cambios

$section
"@
Set-Content $Output $notes -Encoding utf8
