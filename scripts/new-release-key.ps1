<#
.SYNOPSIS
  Crea el par de claves de publicación de actualizaciones (ECDSA P-256).
  - La clave PRIVADA se guarda fuera del repositorio (por defecto en tu perfil) y firma los manifiestos. Guárdala con
    copia de seguridad: si se pierde, las versiones instaladas no aceptarán actualizaciones firmadas con otra clave.
  - La clave PÚBLICA se escribe en src/StreamPilot.Infrastructure/Updates/release-public-key.pem y se compila en la app.
  No sobrescribe una clave existente salvo con -Force.
#>
param(
    [string] $PrivateKeyPath = (Join-Path $env:USERPROFILE '.streampilot-release\release-private-key.pem'),
    [switch] $Force
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publicPath = Join-Path $root 'src/StreamPilot.Infrastructure/Updates/release-public-key.pem'
if (((Test-Path $PrivateKeyPath) -or (Test-Path $publicPath)) -and -not $Force) {
    throw "Ya existe una clave ($PrivateKeyPath o $publicPath). Usa -Force solo si de verdad quieres sustituirla."
}
if ($PrivateKeyPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'La clave privada no puede guardarse dentro del repositorio.'
}

$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
New-Item -ItemType Directory -Force (Split-Path $PrivateKeyPath) | Out-Null
Set-Content -Path $PrivateKeyPath -Value $key.ExportPkcs8PrivateKeyPem() -Encoding ascii -NoNewline
# Solo el usuario actual puede leer la clave privada.
$acl = New-Object System.Security.AccessControl.FileSecurity
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule([System.Security.Principal.WindowsIdentity]::GetCurrent().Name, 'FullControl', 'Allow')))
Set-Acl -Path $PrivateKeyPath -AclObject $acl
Set-Content -Path $publicPath -Value $key.ExportSubjectPublicKeyInfoPem() -Encoding ascii -NoNewline
"Clave privada: $PrivateKeyPath (no la subas a ningún repositorio)"
"Clave pública: $publicPath (se compila en la aplicación)"
