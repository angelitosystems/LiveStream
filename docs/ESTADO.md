# Estado real del proyecto

Actualizado al cierre de la **Fase 2 — Estudio profesional**. Solo se marca como hecho lo verificado contra libobs real
en esta máquina (i7-13650HX, Intel UHD + RTX 4050 Laptop, Windows 11, pantalla al 125 %).

## Fase 1 — Motor (completada)

libobs 32.2.2 integrado mediante `streampilot_bridge` (ABI C): inicio/apagado estables, dispositivos, escenas,
grabación, vista previa nativa en Avalonia. Detalles y fallos corregidos en el historial de git (commit b2b8131).

## Fase 2 — Estudio profesional (completada, con las limitaciones de abajo)

| Área | Implementado | Verificación |
|---|---|---|
| Puente ABI v3 | Escenas (crear, eliminar, renombrar, programa con transición), fuentes compartidas, elementos (transformación con giro y recorte, visibilidad, bloqueo, orden), propiedades de fuente en JSON, audio (volumen, silencio, monitorización, desfase, medidores), grabación, RTMP/RTMPS con reconexión, displays con zoom y superposición de edición | Prueba de integración (11 tipos de fuente, transición de fundido, medidores, grabación, fallo de transmisión reportado sin filtrar la clave) |
| Comandos | Todas las mutaciones pasan por `CommandExecutor` (serializado, validado, deshacer/rehacer hasta 200 pasos) | 50 pruebas unitarias |
| Persistencia | Proyectos `.spproj` (JSON versionado), escritura atómica con `.bak`, recuperación de archivo dañado, migración v1→v2, autoguardado a los 2 s | Pruebas unitarias + autoguardado observado en la app |
| Secretos | Clave de transmisión cifrada con DPAPI; nunca en el proyecto ni en registros | Pruebas unitarias |
| Interfaz | Organización tipo OBS: menús, barra lateral, lienzo, paneles redimensionables (escenas, fuentes, mezclador, transiciones, controles), barra de estado con FPS/CPU/memoria/perdidos/bitrate | Capturas de la app en ejecución |
| Editor de lienzo | Selección (Ctrl para varios), mover, escalar con tiradores (proporción fija; Shift libre), recorte con Alt, guías magnéticas a bordes/centro del lienzo y de otros elementos, zoom con rueda centrado en el cursor, desplazamiento con botón central, flechas para mover, giro y volteo desde el menú, diálogo numérico de transformación | `scripts/ui-drag-test.ps1`: arrastre real → guardado → Ctrl+Z restaura exactamente |
| Modo estudio | Vista previa editable + programa, botón Transición (la escena saliente vuelve a la vista previa) | UI Automation + capturas |
| Fuentes | Pantalla, ventana, juego, cámara/capturadora, micrófono, audio de salida, imagen, presentación, video/audio, texto, color sólido; reutilizar una fuente en varias escenas sin duplicar la captura | Diálogo de añadir + propiedades probado; captura de pantalla renderizando |
| Propiedades | Editor generado a partir de las propiedades reales de libobs (casillas, números/deslizadores, texto, rutas, listas, colores, tipografía, listas de archivos, grupos), vista previa en vivo, cancelar revierte | Captura del diálogo con las propiedades de captura de pantalla |
| Mezclador | Canales globales (escritorio y micrófono) + fuentes con audio de las escenas, medidores estéreo de pico/magnitud a ~30 Hz, volumen en dB, silencio, monitorización | Medidores con audio real en la app |
| Grabación | MKV/MP4/MOV/FLV, carpeta configurable, aviso de poco espacio, codificador y bitrate propios | Grabación desde la UI (8 MB en 5 s) |
| Transmisión | RTMP/RTMPS a cualquier servicio con URL + clave, confirmación al iniciar y detener, estado en vivo, motivo legible si la conexión cae | Fallo de conexión verificado; **no** se ha emitido a una plataforma real (no hay credenciales) |
| Diagnóstico | Registro en `%LOCALAPPDATA%\StreamPilot\logs` (rotación diaria, 14 días, URLs de ingesta redactadas), errores de UI registrados y mostrados | Usado para encontrar los fallos de abajo |

### Fallos encontrados y corregidos en la Fase 2

1. **Bloqueo al cerrar**: el guardado síncrono al salir esperaba una continuación que volvía al hilo de UI bloqueado
   (`await using` sin `ConfigureAwait(false)`). Ahora el cierre tarda ~0,6 s y termina con código 0.
2. **Escenas en vista previa sin activar**: libobs no inicializa capturas de escenas que no están en programa salvo que
   se marquen como visibles (`obs_source_inc_showing`), como hace OBS Studio con su vista previa.
3. **Ids de tipo desconocidos**: libobs crea una fuente vacía en vez de fallar; el puente los rechaza.
4. **RTWQ**: el puente inicia las colas de trabajo en tiempo real de Windows para la ruta de baja latencia de WASAPI.
5. **Transición nula** al inicializar el selector de transiciones.

## Limitaciones conocidas (reales, no simuladas)

- **Paneles acoplables**: son redimensionables y se pueden restaurar, pero no se pueden arrastrar a otra posición ni
  desacoplar en ventanas flotantes.
- **Giro con el ratón**: no hay tirador de giro; el giro se hace desde el menú (90°/180°) o el diálogo numérico.
- **Agrupar escenas y elementos, importar/exportar escenas sueltas y atajos personalizables**: pendientes.
- **Botones de propiedades de libobs** (p. ej. «Configurar video» del driver de la cámara) y el selector de FPS del
  dispositivo no se muestran: el puente aún no expone esos callbacks.
- **Fuentes de navegador**: requieren distribuir obs-browser (CEF); no incluidas.
- **Filtros de audio** (supresión de ruido, compresor, limitador, puerta): pendientes (Fase 2b/3).
- **Lienzo y proyecto**: cambiarlos reinicia la aplicación, porque libobs no se puede reiniciar dentro del proceso.
- **GPU de render**: libobs usa el adaptador 0 (Intel UHD en este portátil); NVENC en modo textura hace fallback. La
  selección automática de adaptador/encoder es de la Fase 5.
- **Transmisión** no probada contra YouTube/Twitch/etc. por falta de credenciales; la ruta RTMP es la estándar de libobs.
- **Licencia**: libobs es GPLv2+. Hay que decidir el modelo de distribución con asesoría legal antes de vender.

## Pendiente

- **Fase 3** — Producción televisiva: transiciones avanzadas (stinger, máscara), lower thirds, titulares, tickers,
  plantillas con datos dinámicos y animación por keyframes sincronizada con el reloj del motor.
- **Fases 4–7** — Asistente IA, optimización, multistream, calidad y distribución.

## Cómo ejecutar y verificar

```powershell
./scripts/build-native.ps1          # runtime libobs + puente en artifacts/obs-runtime
dotnet build StreamPilot.sln
dotnet test StreamPilot.sln         # unitarias + integración contra libobs real (requiere GPU y escritorio)
dotnet run --project src/StreamPilot.Desktop -- --project ruta\al\proyecto.spproj
./scripts/ui-drag-test.ps1 -ProcessId <pid> -ProjectPath <proyecto> -ItemName <fuente>   # prueba del editor
```
