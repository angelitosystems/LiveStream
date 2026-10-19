<#
.SYNOPSIS
  Prepara el runtime de libobs y compila livestream_bridge en artifacts/obs-runtime.

.DESCRIPTION
  -Mode Prebuilt (por defecto, desarrollo): usa los binarios de una instalación de OBS Studio de la
   MISMA versión que $ObsTag, los headers de libobs de ese tag y una import lib generada con dumpbin/lib.
   El runtime se copia a artifacts/, así que la app no depende de OBS instalado al ejecutarse.
  -Mode Source: compila libobs y plugins desde el tag fijado (para CI y distribución).

.NOTES
  Requiere Git y Visual Studio 2022/2026 con C++ (x64). CMake se toma del de Visual Studio si no está en PATH.
#>
param(
    [ValidateSet('Prebuilt', 'Source')] [string] $Mode = 'Prebuilt',
    [string] $ObsTag = '32.2.2',
    [string] $ObsInstallDir = "$env:ProgramFiles\obs-studio",
    [string] $Config = 'RelWithDebInfo'
)
$ErrorActionPreference = 'Stop'

# Plugins del motor que usa LiveStream. Se excluyen a propósito los que dependen de la interfaz Qt de OBS
# (frontend-tools, obs-websocket, obs-vst, decklink-*-ui, aja-output-ui): sin esa interfaz cuelgan el proceso.
# obs-browser (CEF) queda fuera hasta implementar las fuentes de navegador con su subproceso.
$EnginePlugins = @(
    'image-source', 'obs-ffmpeg', 'obs-filters', 'obs-nvenc', 'obs-outputs', 'obs-qsv11', 'obs-text',
    'obs-transitions', 'obs-x264', 'obs-webrtc', 'rtmp-services', 'text-freetype2', 'win-capture',
    'win-dshow', 'win-wasapi', 'nv-filters', 'vlc-video'
)
$root = Split-Path $PSScriptRoot -Parent
$third = Join-Path $root 'third_party'
$obsSrc = Join-Path $third 'obs-studio'
$runtime = Join-Path $root 'artifacts/obs-runtime'
$bridgeSrc = Join-Path $root 'native/livestream-bridge'
$bridgeBuild = Join-Path $bridgeSrc 'build'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No se encontró Visual Studio con herramientas C++ x64.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) { $cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' }

function Invoke-VsCmd([string] $command) {
    cmd /c "`"$vcvars`" >nul 2>&1 && $command"
    if ($LASTEXITCODE) { throw "Falló: $command" }
}

if (-not (Test-Path (Join-Path $obsSrc 'libobs'))) {
    $sparse = if ($Mode -eq 'Prebuilt') { @('--filter=blob:none', '--sparse') } else { @('--recursive') }
    git clone --quiet --depth 1 --branch $ObsTag @sparse https://github.com/obsproject/obs-studio.git $obsSrc
    if ($Mode -eq 'Prebuilt') { git -C $obsSrc sparse-checkout set libobs }
}

if ($Mode -eq 'Prebuilt') {
    $obsDll = Join-Path $ObsInstallDir 'bin\64bit\obs.dll'
    if (-not (Test-Path $obsDll)) { throw "No se encontró $obsDll" }
    $installed = (Get-Item $obsDll).VersionInfo.ProductVersion
    if ($installed -ne $ObsTag) { throw "OBS instalado es $installed pero los headers son de $ObsTag. Deben coincidir." }

    # Import lib a partir de las exportaciones reales de obs.dll.
    $libDir = Join-Path $third 'obs-lib'
    New-Item -ItemType Directory -Force $libDir | Out-Null
    $exports = Join-Path $libDir 'exports.txt'
    Invoke-VsCmd "dumpbin /nologo /exports `"$obsDll`" > `"$exports`""
    $names = Get-Content $exports | ForEach-Object {
        if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]{8}\s+(\S+)') { $Matches[1] }
    }
    $def = Join-Path $libDir 'obs.def'
    @('LIBRARY obs', 'EXPORTS') + $names | Set-Content -Encoding ascii $def
    Invoke-VsCmd "lib /nologo /machine:x64 /def:`"$def`" /out:`"$libDir\obs.lib`""
    Write-Host "Import lib generada con $($names.Count) símbolos."

    & $cmake -S $bridgeSrc -B $bridgeBuild -A x64 "-DOBS_INCLUDE_DIR=$obsSrc/libobs" "-DOBS_IMPORT_LIB=$libDir/obs.lib"
    if ($LASTEXITCODE) { throw 'Configuración de CMake fallida.' }
    & $cmake --build $bridgeBuild --config $Config
    if ($LASTEXITCODE) { throw 'Compilación del puente fallida.' }

    # Runtime autocontenido: bin/64bit + obs-plugins/64bit + data, misma estructura que OBS.
    if (Test-Path $runtime) { Remove-Item -Recurse -Force $runtime }
    New-Item -ItemType Directory -Force $runtime | Out-Null
    # Fuera lo que solo usa la interfaz de OBS (Qt, API del frontend, scripting): libobs y los plugins del motor
    # no lo necesitan, y no distribuirlo reduce tamaño y licencias de terceros (Qt es LGPL).
    $notForEngine = 'obs64.exe', 'obs-browser-page.exe', 'Qt6*.dll', 'iconengines', 'imageformats', 'platforms', 'styles',
                    'obs-frontend-api.*', 'obs-scripting.*', 'lua51.dll'
    Copy-Item -Recurse (Join-Path $ObsInstallDir 'bin\64bit\*') $runtime -Exclude $notForEngine
    $pluginBin = Join-Path $runtime 'obs-plugins\64bit'
    $pluginData = Join-Path $runtime 'data\obs-plugins'
    New-Item -ItemType Directory -Force $pluginBin, $pluginData | Out-Null
    Copy-Item -Recurse (Join-Path $ObsInstallDir 'data\libobs') (Join-Path $runtime 'data\libobs')
    foreach ($plugin in $EnginePlugins) {
        Copy-Item (Join-Path $ObsInstallDir "obs-plugins\64bit\$plugin.dll") $pluginBin
        Copy-Item (Join-Path $ObsInstallDir "obs-plugins\64bit\$plugin.pdb") $pluginBin -ErrorAction SilentlyContinue
        $data = Join-Path $ObsInstallDir "data\obs-plugins\$plugin"
        if (Test-Path $data) { Copy-Item -Recurse $data (Join-Path $pluginData $plugin) }
    }
}
else {
    Push-Location $obsSrc
    try {
        & $cmake --preset windows-x64 -DENABLE_UI=OFF -DENABLE_SCRIPTING=OFF -DENABLE_BROWSER=OFF
        & $cmake --build --preset windows-x64 --config $Config
        & $cmake --install build_x64 --config $Config --prefix (Join-Path $third 'obs-install')
    }
    finally { Pop-Location }
    & $cmake -S $bridgeSrc -B $bridgeBuild -A x64 "-DCMAKE_PREFIX_PATH=$(Join-Path $third 'obs-install')"
    & $cmake --build $bridgeBuild --config $Config
    throw 'Modo Source: falta definir la copia del runtime una vez validado el build de OBS desde fuente.'
}

Copy-Item -Force (Join-Path $bridgeBuild "$Config\livestream_bridge.dll") $runtime
Copy-Item -Force (Join-Path $bridgeBuild "$Config\livestream_bridge.pdb") $runtime -ErrorAction SilentlyContinue
Write-Host "Runtime listo en $runtime"
