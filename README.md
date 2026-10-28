<p align="center">
  <img src="Assets/generated/logo-512.png" alt="LiveStream" width="140" />
</p>

<h1 align="center">LiveStream</h1>

<p align="center">
  Estudio de producción y transmisión en directo para Windows.<br />
  Motor <b>libobs</b> (el de OBS Studio) · interfaz en <b>Avalonia/.NET 10</b> · grafismo de TV · multistream ·
  <b>asistente de IA</b> · extensiones y plugins
</p>

<p align="center">
  <a href="https://github.com/angelitosystems/LiveStream/releases/latest"><img alt="Última versión" src="https://img.shields.io/github/v/release/angelitosystems/LiveStream?label=versi%C3%B3n%20publicada&color=3d8bff" /></a>
  <img alt="En desarrollo" src="https://img.shields.io/badge/en%20desarrollo-0.9.x-f5a524" />
  <a href="https://github.com/angelitosystems/LiveStream/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/angelitosystems/LiveStream/actions/workflows/ci.yml/badge.svg" /></a>
  <a href="LICENSE"><img alt="Licencia" src="https://img.shields.io/badge/licencia-GPL--2.0--or--later-b13cff" /></a>
  <img alt="Plataforma" src="https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-22d3ff" />
  <a href="https://github.com/sponsors/angelitosystems"><img alt="Auspicia" src="https://img.shields.io/badge/♥-auspicia-ff2d8a" /></a>
</p>

<p align="center">
  <a href="#funcionalidades">Funcionalidades</a> ·
  <a href="#instalar">Instalar</a> ·
  <a href="#arquitectura">Arquitectura</a> ·
  <a href="#compilar-desde-el-código">Compilar</a> ·
  <a href="#línea-de-tiempo">Línea de tiempo</a> ·
  <a href="#pendiente">Pendiente</a>
</p>

---

## Estado del proyecto

| | |
|---|---|
| **Versión publicada** | [0.8.1](https://github.com/angelitosystems/LiveStream/releases/latest) (19/10/2026) |
| **En desarrollo** | 0.9.x en `main`: plugins, marketplace, editor de diseño libre, extensiones, alertas, chat multicanal y TTS |
| **Verificación** | 282 pruebas unitarias en CI en cada push; prueba de integración contra libobs real (GPU y escritorio) en local |
| **Qué falta** | [PENDIENTES.md](PENDIENTES.md): publicar la 0.9.x, riesgos abiertos y lo que necesita credenciales para probarse |

El estado detallado de cada fase, con lo verificado y sus limitaciones reales, está en [docs/ESTADO.md](docs/ESTADO.md).
Los cambios de cada versión están en [CHANGELOG.md](CHANGELOG.md).

## Funcionalidades

**Estudio**
- Escenas y fuentes compartidas entre escenas: pantalla, ventana, juego, cámara, micrófono, audio del sistema,
  imagen, presentación, video, texto y color.
- Editor visual del lienzo: mover, escalar, recortar, girar, guías magnéticas y zoom.
- Mezclador con medidores, modo estudio (vista previa + programa) y deshacer/rehacer de todo.

**Producción de TV**
- Transiciones (fundido, barrido, cortinilla luma, stinger…) y transición propia por escena.
- Filtros de audio y video.
- Capa de gráficos con plantillas (rótulos, titulares, cintillos, cuenta atrás, marcador, alertas) y animaciones por
  fotogramas clave.
- Editor de diseño libre y kit de marca.

**Directo con la audiencia** (extensiones integradas)
- Chat multicanal (YouTube, Twitch, TikTok, Facebook, Instagram) con moderación y chat en pantalla.
- Alertas con sonido y gráfico.
- Lectura de chat por voz (TTS).
- Donaciones y puntos del canal.
- Banco de sonidos.

**Salida**
- Grabación MKV/MP4/MOV/FLV.
- Multistream RTMP/RTMPS a varios destinos con codificador compartido, reconexión automática y supervisión por
  destino.
- Emisión dual horizontal 16:9 + vertical 9:16.

**Optimización**
- Detección de hardware y benchmark real de codificadores.
- Perfiles recomendados (se aplican solo con autorización) y bitrate adaptativo con registro de cada decisión.

**Asistente de IA** (Claude, de Anthropic)
- Crea escenas, añade fuentes, ajusta audio y lanza gráficos con herramientas validadas.
- Emitir, detener la emisión o borrar piden confirmación.
- Necesita tu propia clave de API, que se guarda cifrada.

**Extensiones y plugins**
- Una sola ventana para activar, desactivar, instalar, actualizar y configurar. Al desactivar una funcionalidad, su
  panel desaparece hasta que la reactivas.
- Los plugins de terceros corren en su propio proceso, firmados, y una actualización que no arranca vuelve sola a la
  versión anterior.

## Instalar

Descarga `LiveStream-Setup-<versión>.exe` de la [última versión publicada](https://github.com/angelitosystems/LiveStream/releases/latest)
y ejecútalo.
- **Instalación:** un asistente de cuatro pasos instala solo para tu usuario, sin pedir administrador, en
  `%LOCALAPPDATA%\Programs\LiveStream`.
- **Desinstalación:** desde «Aplicaciones instaladas».
- **Portable:** también hay un `.zip`.

Instalación desatendida: `LiveStream-Setup-<versión>.exe /quiet [/dir <carpeta>] [/noshortcuts]`
([docs/DISTRIBUCION.md](docs/DISTRIBUCION.md)).

**Requisitos:** Windows 10/11 x64 y una GPU con Direct3D 11. Tus proyectos, ajustes y claves cifradas se guardan en
`%APPDATA%\LiveStream` y se conservan al desinstalar o actualizar.

### Crear plugins

El SDK se distribuye aparte, en [LiveStream-SDK](https://github.com/angelitosystems/LiveStream-SDK/releases). Su
instalador deja el CLI `lsplugin` en tu PATH:

```powershell
lsplugin new mi-plugin --id com.empresa.miplugin --name "Mi plugin"
cd mi-plugin
dotnet publish -c Release
lsplugin pack bin\Release\net10.0\win-x64\
```

Guía completa en [docs/plugins/SDK.md](docs/plugins/SDK.md). `PluginClient` (SDK 1.2.0) resuelve el protocolo:
un plugin típico cabe en unas 50 líneas.

## Arquitectura

```text
┌────────────────────────────────────── LiveStream.Desktop (Avalonia) ──────────────────────────────────────┐
│ Interfaz · dock de paneles · diálogos · composición de la app                                             │
└─────────────────────────────────────────────────────┬─────────────────────────────────────────────────────┘
                                                      │
┌───────── LiveStream.Application ─────────┐                     ┌───────── LiveStream.Extensions ──────────┐
│ núcleo · sesión · comandos con deshacer  │    bus de eventos   │ alertas · chat · TTS · donaciones        │
│ gráficos · salidas · ExtensionHost       │   ◄─────────────►   │ banco de sonidos · emisión dual          │
└─────────────────────┬────────────────────┘      contratos      └──────────────────────────────────────────┘
                      │
┌─────── LiveStream.Native + bridge ───────┐                     ┌────────── Plugins de terceros ───────────┐
│ ABI C estable sobre libobs 32.2.2        │                     │ .lsplugin firmados · un proceso cada uno │
│ Direct3D 11 · displays · salidas RTMP    │                     │ JSON-RPC con la app (PluginHost)         │
└──────────────────────────────────────────┘                     └──────────────────────────────────────────┘
```

- **Núcleo** (`LiveStream.Application`, `LiveStream.Native`, `native/livestream-bridge`): motor y libobs,
  composición de escenas, fuentes, capa de gráficos, salidas y servicios compartidos. No conoce las funcionalidades.
- **Extensiones** (`LiveStream.Extensions`): cada funcionalidad declara manifiesto, dependencias y permisos. El
  `ExtensionHost` las arranca en orden y aísla sus fallos: una que falla repetidamente se detiene sola. Detalle en
  [docs/EXTENSIONES.md](docs/EXTENSIONES.md).
- **Plugins**: procesos aparte por JSON-RPC; si uno se cuelga, LiveStream sigue funcionando. El código de los plugins
  oficiales y el backend del marketplace viven en un repositorio privado.
- Toda modificación del estudio (desde la interfaz, el asistente, el optimizador o un plugin) pasa por un único
  ejecutor de comandos que valida, serializa y permite deshacer.

| Carpeta | Contenido |
|---|---|
| `native/livestream-bridge` | Puente C/C++ sobre libobs con ABI C estable (motor, escenas, fuentes, filtros, animación, salidas, displays) |
| `src/LiveStream.Domain` | Modelo del estudio: proyecto, escenas, fuentes, gráficos, destinos, animaciones |
| `src/LiveStream.Application` | Núcleo: sesión, comandos, gráficos, salidas, optimización, asistente y host de extensiones |
| `src/LiveStream.Extensions` | Funcionalidades integradas como extensiones |
| `src/LiveStream.Infrastructure` | Persistencia (`.spproj`), DPAPI, registro, cliente de Claude, plugins y actualizaciones firmadas |
| `src/LiveStream.Native` | Interop .NET con el puente nativo |
| `src/LiveStream.Desktop` | Aplicación Avalonia |
| `src/LiveStream.PluginSdk` | Contratos del SDK: manifiesto, paquetes `.lsplugin`, firma, JSON-RPC y `PluginClient` |
| `sdk/` | Plugin de ejemplo y esquema del manifiesto |
| `tests/` | Pruebas unitarias y de integración contra libobs real |
| `tools/` | Instalador (`LiveStream.Setup`), `lsplugin` (`LiveStream.PluginTool`) y prueba de resistencia (`LiveStream.Soak`) |
| `scripts/` | Compilación nativa, empaquetado, firma de actualizaciones, inventario de licencias, automatización de la UI |

## Compilar desde el código

**Requisitos:** Windows x64, .NET SDK 10, Visual Studio 2022 con C++ (CMake), PowerShell 7 y **OBS Studio 32.2.2
instalado**. El modo de desarrollo toma de ahí los binarios de libobs; los headers se descargan del tag 32.2.2.

```powershell
./scripts/build-native.ps1                 # puente livestream_bridge + runtime de libobs en artifacts/obs-runtime
dotnet build LiveStream.sln
dotnet test tests/LiveStream.UnitTests     # lo mismo que el CI
dotnet test tests/LiveStream.IntegrationTests   # contra libobs real: necesita GPU y escritorio
dotnet run --project src/LiveStream.Desktop
./scripts/package.ps1                      # instalador, portable y símbolos en artifacts/release
```

**Integración continua:**
- `ci.yml`, en cada push y PR: compila, pasa las pruebas unitarias y compila el puente nativo contra libobs.
- `release.yml`, con cada etiqueta `v*`: genera la distribución y publica la versión.

Más detalle en [docs/DISTRIBUCION.md](docs/DISTRIBUCION.md).

## Línea de tiempo

| Fecha | Versión | Hito |
|---|---|---|
| **18/10/2026** | 0.7.0 | **Fases 1 a 7 en un día.** Motor libobs real con vista previa en Avalonia, estudio tipo OBS, producción televisiva, asistente de IA con herramientas tipadas, optimización determinista con auditoría, multistream con supervisión por destino, calidad y distribución. |
| **19/10/2026** | 0.8.0 · 0.8.1 | Renombre de StreamPilot a **LiveStream**, instalador con asistente, licencia GPL-2.0-or-later, firma Authenticode con SignPath desde GitHub Actions. **0.8.1 es la última versión publicada.** |
| **25/10/2026** | 0.9.0 | Kit de marca, plugins en procesos aislados, marketplace con paquetes firmados, SDK y editor de diseño libre de gráficos. |
| **26/10/2026** | 0.9.1 | Escenas con plantillas, panel de fuentes visual, arrastrar archivos al lienzo, iconografía y atajos. El backend del marketplace pasa a su propio repositorio. |
| **28/10/2026** | 0.9.x | Estabilidad, arquitectura de extensiones, Marketplace unificado y CI ([detalle abajo](#28102026-qué-se-hizo-hoy)). |

### 28/10/2026: qué se hizo hoy

**Estabilidad**
- **Cierre inesperado más frecuente resuelto en su origen.** Era un fallo nativo en `d3d11.dll` (driver de Intel)
  que .NET no podía capturar. Se diagnosticó con WinDbg sobre los volcados de memoria:
  - el puente destruía un vertex buffer sin descargarlo;
  - la conversión de vídeo de la cámara usaba después ese puntero ya liberado;
  - la corrección es una línea en `bridge.cpp`.
- **Tareas de UI de libobs:** el puente registra `obs_set_ui_task_handler`, con un hilo propio que vive hasta después
  del apagado. Antes libobs las descartaba.
- **Manejadores globales de errores** (dominio de la app, tareas no observadas y dispatcher de Avalonia): registran el
  error con su contexto y la app sigue funcionando.
- **Condiciones de carrera y recursos:** alertas y chat dejaron de modificar el proyecto desde hilos de fondo. La
  vista previa de fuentes suelta su ventana nativa a tiempo. Los `catch` vacíos ahora registran el error.

**Arquitectura de extensiones**
- **Núcleo sin funcionalidades:** alertas, chat multicanal, TTS, donaciones, banco de sonidos y emisión dual salieron
  del núcleo a `LiveStream.Extensions`, con contratos estables, bus de eventos y aislamiento de fallos.
- **Activación por equipo:** cada extensión se activa o desactiva sin reiniciar, y desactivada no consume recursos ni
  muestra su panel.

**Plugins y marketplace**
- **Código privado:** el de los plugins oficiales sale del repositorio público y vive en el privado del marketplace.
  El historial público nunca lo contuvo.
- **Una sola ventana, «Extensiones y plugins»:**
  - tarjetas con estado y filtros con contadores;
  - «Actualizar todo» y «Ver registro»;
  - una actualización que no arranca vuelve sola a la versión anterior;
  - si una actualización firmada no pide permisos nuevos, no se repite el consentimiento.
- **SDK 1.2.0** publicado con `PluginClient`.

**Calidad**
- **CI nuevo** en cada push y PR: pruebas unitarias y compilación del puente nativo.
- **Pruebas:** 282 unitarias, entre ellas las de extensiones, estabilidad y vuelta atrás de actualizaciones. La de
  integración contra libobs real ahora comprueba que no se descartan tareas de UI.
- **Interfaz:** menús con separadores visibles y agrupados.

## Pendiente

Lo que falta está en **[PENDIENTES.md](PENDIENTES.md)**. Lo principal:

1. **Publicar la 0.9.x:** entrada del CHANGELOG, versión y etiqueta `v*`. Hasta entonces, las correcciones de hoy no
   llegan a los usuarios.
2. **Marketplace:** desplegar el backend y publicar los plugins oficiales firmados.
3. **Aislar TTS (SAPI) en un proceso propio:** es el último componente nativo que comparte proceso con la app.
4. **Verificar con credenciales reales:** emitir a plataformas, conectores de chat y el asistente.

## Seguridad

- Las claves de transmisión y la clave de API se guardan cifradas con DPAPI para tu usuario de Windows; nunca se
  escriben en el proyecto ni en los registros.
- El asistente no puede elegir archivos, no ve servidores ni claves y no accede al motor directamente.
- Las actualizaciones solo se aceptan si el manifiesto está firmado (ECDSA P-256) y el instalador coincide con el
  tamaño y SHA-256 firmados.
- Los plugins solo se instalan firmados por el publicador (salvo en modo desarrollador), con los permisos aceptados
  por el usuario, en un proceso aparte que Windows cierra si LiveStream termina
  ([docs/plugins/MARKETPLACE.md](docs/plugins/MARKETPLACE.md)).
- Las extensiones integradas declaran permisos: sin el permiso, no acceden al proyecto ni a las credenciales.

## Firma de código

Los ejecutables de LiveStream se firman con Authenticode en GitHub Actions a partir del código de este repositorio
(proceso en [docs/FIRMA.md](docs/FIRMA.md)).

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

- **Autores y revisores**: [colaboradores del repositorio](https://github.com/angelitosystems/LiveStream/graphs/contributors).
- **Aprobación de firmas**: [Angel Calderon Mantilla](https://github.com/angelito-systems).
- **Privacidad**: LiveStream no envía información a otros sistemas en red salvo cuando quien lo usa lo pide
  expresamente: emitir a los destinos que configura, usar el asistente de IA con su propia clave de API de Anthropic,
  conectar el chat de sus plataformas o buscar actualizaciones en el canal que configura.

## Licencia

LiveStream es software libre: puedes redistribuirlo y modificarlo según los términos de la **GNU General Public
License, versión 2 o (a tu elección) cualquier versión posterior** (GPL-2.0-or-later), la misma licencia que libobs.
El texto completo está en [LICENSE](LICENSE). Se distribuye **sin ninguna garantía**.

Si redistribuyes el instalador o el paquete portable, ofrece también el código fuente de esa versión (este repositorio,
en su tag). Los componentes de terceros y sus licencias están en
[docs/THIRD-PARTY-NOTICES.md](docs/THIRD-PARTY-NOTICES.md).

© 2026 Angel Calderon Mantilla.

## Auspicia LiveStream

LiveStream es un proyecto independiente. Si te resulta útil:

- **Personas**: apóyalo en [GitHub Sponsors](https://github.com/sponsors/angelitosystems).
- **Marcas y empresas**: escríbenos en las [Discusiones del repositorio](https://github.com/angelitosystems/LiveStream/discussions);
  tu marca aparecerá en el instalador y en esta página.

El instalador muestra estos mismos enlaces (se cambian en `tools/LiveStream.Setup/Sponsorship.cs`).
