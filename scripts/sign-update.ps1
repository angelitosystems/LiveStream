<#
.SYNOPSIS
  Firma el manifiesto de actualización de un instalador: versión, URL de descarga, tamaño y SHA-256, con la clave
  privada de scripts/new-release-key.ps1. El resultado (update.json) se publica en la URL del canal de actualizaciones.
#>
param(
    [Parameter(Mandatory)] [string] $Installer,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Url,
    [Parameter(Mandatory)] [string] $PrivateKey,
    [string] $Notes = '',
    [string] $Output = 'update.json'
)
$ErrorActionPreference = 'Stop'
if (-not $Url.StartsWith('https://')) { throw 'La URL de descarga debe ser HTTPS.' }
$file = Get-Item $Installer
$sha = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
# Mismo texto canónico que UpdateManifest.SignedText.
$signed = "livestream-update-v1`n$Version`n$Url`n$($file.Length)`n$sha"
$key = [System.Security.Cryptography.ECDsa]::Create()
$key.ImportFromPem((Get-Content $PrivateKey -Raw))
$signature = [Convert]::ToBase64String($key.SignData([Text.Encoding]::UTF8.GetBytes($signed), [System.Security.Cryptography.HashAlgorithmName]::SHA256))
[ordered]@{ version = $Version; url = $Url; size = $file.Length; sha256 = $sha; notes = $Notes; signature = $signature } |
    ConvertTo-Json | Set-Content -Path $Output -Encoding utf8NoBOM
"Manifiesto firmado: $Output ($Version, $([math]::Round($file.Length / 1MB, 1)) MB)"
