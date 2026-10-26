<#
.SYNOPSIS
  Prueba de extremo a extremo del marketplace en este equipo: arranca el backend en 127.0.0.1, crea claves
  temporales y publica, lista, retira, restaura y respalda el plugin de ejemplo con lsplugin. No toca producción.
#>
param([int] $Port = 5099)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path ([IO.Path]::GetTempPath()) "ls-marketplace-smoke-$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory $work | Out-Null
$failures = 0
function Succeeds([scriptblock] $b) { & $b *> $null; $LASTEXITCODE -eq 0 }
function Check([string] $name, [bool] $ok) {
    if ($ok) { Write-Host "  OK   $name" } else { Write-Host "  FALLO $name" -ForegroundColor Red; $script:failures++ }
}

dotnet build "$root/tools/LiveStream.PluginTool" -v q --nologo | Out-Null
dotnet build "$root/marketplace/server" -v q --nologo | Out-Null
dotnet build "$root/sdk/examples/HelloPlugin" -v q --nologo | Out-Null
$tool = Get-ChildItem "$root/tools/LiveStream.PluginTool/bin" -Recurse -Filter lsplugin.exe | Sort-Object LastWriteTime | Select-Object -Last 1
$server = Get-ChildItem "$root/marketplace/server/bin" -Recurse -Filter LiveStream.Marketplace.Server.dll | Sort-Object LastWriteTime | Select-Object -Last 1
$example = (Get-ChildItem "$root/sdk/examples/HelloPlugin/bin" -Recurse -Filter HelloPlugin.exe | Sort-Object LastWriteTime | Select-Object -Last 1).Directory.FullName

& $tool.FullName keygen --private "$work/keys/private.pem" --public "$work/keys/public.pem" | Out-Null
& $tool.FullName keygen --private "$work/other/private.pem" --public "$work/other/public.pem" | Out-Null
$token = -join ((1..48) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) })
$base = "http://127.0.0.1:$Port"

$env:ADMIN_TOKEN = $token
$env:PUBLISHER_PUBLIC_KEY = Get-Content "$work/keys/public.pem" -Raw
$env:PUBLIC_BASE_URL = $base
$env:DATA_DIR = "$work/data"
$env:ASPNETCORE_URLS = $base
$env:PORT = "$Port"
$proc = Start-Process dotnet -ArgumentList "`"$($server.FullName)`"" -PassThru -NoNewWindow -RedirectStandardOutput "$work/server.log" -RedirectStandardError "$work/server.err"
try {
    for ($i = 0; $i -lt 60; $i++) {
        try { if ((Invoke-WebRequest "$base/health" -UseBasicParsing).StatusCode -eq 200) { break } } catch { Start-Sleep -Milliseconds 500 }
    }
    Check 'health' ((Invoke-WebRequest "$base/health" -UseBasicParsing).StatusCode -eq 200)
    Check 'healthcheck del contenedor' (Succeeds { & dotnet $server.FullName --healthcheck })

    $env:LS_MARKETPLACE_TOKEN = $token
    $pkg = "$work/hello.lsplugin"
    & $tool.FullName pack $example -o $pkg | Out-Null
    & $tool.FullName sign $pkg --key "$work/keys/private.pem" | Out-Null
    Check 'verify' (Succeeds { & $tool.FullName verify $pkg --public "$work/keys/public.pem" })
    Check 'publish' (Succeeds { & $tool.FullName publish $pkg --server $base })
    Check 'publicar la misma versión se rechaza' (-not (Succeeds { & $tool.FullName publish $pkg --server $base }))

    $forged = "$work/forged.lsplugin"
    Copy-Item $pkg $forged
    & $tool.FullName sign $forged --key "$work/other/private.pem" | Out-Null
    Check 'firma de otra clave se rechaza' (-not (Succeeds { & $tool.FullName publish $forged --server $base }))

    $env:LS_MARKETPLACE_TOKEN = 'token-incorrecto-token-incorrecto-123'
    Check 'token incorrecto se rechaza' (-not (Succeeds { & $tool.FullName list --server $base }))
    $env:LS_MARKETPLACE_TOKEN = $token

    $catalog = Invoke-RestMethod "$base/v1/catalog"
    $entry = $catalog.entries | Where-Object id -eq 'com.angelitosystems.hello'
    Check 'catálogo con la entrada firmada' ($null -ne $entry -and $entry.signature.Length -gt 0)
    Invoke-WebRequest $entry.downloadUrl -OutFile "$work/downloaded.lsplugin" -UseBasicParsing
    Check 'descarga idéntica (SHA-256)' ((Get-FileHash "$work/downloaded.lsplugin").Hash.ToLower() -eq $entry.sha256)

    & $tool.FullName yank com.angelitosystems.hello 1.0.0 --server $base | Out-Null
    Check 'retirada: fuera del catálogo' (@((Invoke-RestMethod "$base/v1/catalog").entries).Count -eq 0)
    $gone = try { (Invoke-WebRequest $entry.downloadUrl -UseBasicParsing).StatusCode } catch { [int]$_.Exception.Response.StatusCode }
    Check 'retirada: descarga 410' ($gone -eq 410)
    & $tool.FullName unyank com.angelitosystems.hello 1.0.0 --server $base | Out-Null
    Check 'restaurada' (@((Invoke-RestMethod "$base/v1/catalog").entries).Count -eq 1)

    & $tool.FullName backup --server $base -o "$work/backup.zip" | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead("$work/backup.zip"); $names = $zip.Entries.FullName; $zip.Dispose()
    Check 'copia de seguridad con índice y paquete' (($names -contains 'index.json') -and ($names -contains 'packages/com.angelitosystems.hello/1.0.0.lsplugin'))
}
finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Remove-Item Env:ADMIN_TOKEN, Env:PUBLISHER_PUBLIC_KEY, Env:PUBLIC_BASE_URL, Env:DATA_DIR, Env:ASPNETCORE_URLS, Env:PORT, Env:LS_MARKETPLACE_TOKEN -ErrorAction SilentlyContinue
}
if ($failures -gt 0) { Write-Host "Registro del servidor: $work/server.log"; exit 1 }
Remove-Item $work -Recurse -Force
Write-Host 'Marketplace: todas las comprobaciones correctas.'
