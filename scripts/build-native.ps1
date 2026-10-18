<#
.SYNOPSIS
  Compila libobs (tag fijado), sus plugins de Windows y streampilot_bridge, y deja el runtime en artifacts/obs-runtime.
.NOTES
  Requiere: Git, CMake >= 3.28, Visual Studio con C++ (x64). El build de OBS descarga sus dependencias
  precompiladas (obs-deps) durante la configuración.
  BORRADOR: no se ha ejecutado todavía; validar en la Fase 0.
#>
param(
    [Parameter(Mandatory)] [string] $ObsTag,
    [string] $Config = 'RelWithDebInfo'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$obsSrc = Join-Path $root "third_party/obs-studio"
$obsInstall = Join-Path $root "third_party/obs-install"
$runtime = Join-Path $root "artifacts/obs-runtime"

if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { throw "CMake no está en el PATH." }

if (-not (Test-Path $obsSrc)) {
    git clone --recursive --branch $ObsTag --depth 1 https://github.com/obsproject/obs-studio.git $obsSrc
}

Push-Location $obsSrc
try {
    # Preset oficial de Windows x64. Se desactiva la UI de OBS: solo necesitamos libobs y plugins.
    cmake --preset windows-x64 -DENABLE_UI=OFF -DENABLE_SCRIPTING=OFF -DENABLE_BROWSER=OFF
    cmake --build --preset windows-x64 --config $Config
    cmake --install build_x64 --config $Config --prefix $obsInstall
}
finally { Pop-Location }

$bridgeSrc = Join-Path $root "native/streampilot-bridge"
$bridgeBuild = Join-Path $bridgeSrc "build"
cmake -S $bridgeSrc -B $bridgeBuild -A x64 "-DCMAKE_PREFIX_PATH=$obsInstall"
cmake --build $bridgeBuild --config $Config

New-Item -ItemType Directory -Force $runtime | Out-Null
Copy-Item -Recurse -Force (Join-Path $obsInstall "bin/64bit/*") $runtime
Copy-Item -Recurse -Force (Join-Path $obsInstall "obs-plugins") $runtime
Copy-Item -Recurse -Force (Join-Path $obsInstall "data") $runtime
Copy-Item -Force (Join-Path $bridgeBuild "$Config/streampilot_bridge.dll") $runtime
Write-Host "Runtime listo en $runtime"
