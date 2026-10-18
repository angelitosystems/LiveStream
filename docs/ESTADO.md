# Estado real del proyecto

Actualizado al cierre de la **Fase 3 — Producción televisiva**. Solo se marca como hecho lo verificado contra libobs real
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

## Fase 3 — Producción televisiva (completada, con las limitaciones de abajo)

| Área | Implementado | Verificación |
|---|---|---|
| Puente ABI v4 | Motor de animación por fotogramas clave en el *tick* del hilo gráfico de libobs (sincronizado con el reloj de fotogramas, no con la UI): posición, escala, giro, opacidad y recorte, 7 curvas, retardo, mostrar al empezar / ocultar al terminar. API de filtros, transiciones con ajustes, escenas anidadas (con rechazo de ciclos), enumeración de tipos de filtro y transición disponibles. Los canales de salida con video se componen sobre el programa y en los displays de edición | Prueba de integración: la animación llega exactamente a sus valores finales (x=200, escala 0,5) |
| Transiciones | Corte, fundido, barrido y deslizamiento (dirección), fundido a color (color, punto de cambio), cortinilla luma (34 patrones, suavidad, invertir), **stinger** (video, punto de corte). Transición propia por escena (clic derecho en la escena) | Integración: las 6 con ajustes + stinger con un video real grabado en la prueba; UI: cortinilla luma elegida en el diálogo, persistida y ejecutada |
| Filtros | Audio: supresión de ruido (RNNoise), puerta, compresor, limitador, expansor, ganancia. Video: corrección de color, clave cromática, clave de color, clave de luminancia, recorte, nitidez, desplazamiento, máscara, escalado, retraso. Editor generado de las propiedades reales, vista previa en vivo, activar/desactivar, reordenar; todo con deshacer. Solo se ofrecen los tipos que el runtime tiene cargados y compatibles con la fuente | Integración (cadena de audio, filtro incompatible rechazado, limpieza al eliminar la fuente) + UI (corrección de color sobre un color sólido) |
| Gráficos | Capa de gráficos tipo *downstream keyer*: cada gráfico es una escena anidada en una capa sobre el programa, que no se ve afectada por los cambios de escena. 8 plantillas: rótulo, titular/última hora, cintillo con desplazamiento, cuenta atrás (iniciar/pausar/reiniciar), reloj, marcador deportivo (+1 rápido), mosca EN VIVO, alerta con salida automática. Animaciones de entrada/salida (corte, fundido, 4 deslizamientos, cortinilla, aparición con rebote). Edición en vivo (textos, colores, tipografía, posición, escala) sin recrear nada en el motor | Integración (las 8 plantillas creadas, animadas, editadas y eliminadas); UI: 5 gráficos creados desde el menú, al aire a la vez, edición de la mosca aplicada en directo, marcador persistido |
| Plantillas reutilizables | «Guardar como plantilla…» guarda estilo, campos y animaciones en `%APPDATA%\StreamPilot\templates\*.spgfx`; «Añadir desde plantilla» las reutiliza en cualquier proyecto | Pruebas unitarias |
| Datos en vivo | Un gráfico puede enlazarse a un archivo JSON vigilado; cuando otro programa lo guarda, los campos se actualizan en directo sin entrar en el historial de deshacer | Prueba unitaria con escritura real del archivo |
| Persistencia | Esquema v3 (filtros, transición por escena, gráficos); los proyectos v2 se abren sin migración manual | Pruebas unitarias + proyecto de la Fase 2 reabierto |
| Accesibilidad | Filas de escenas/fuentes/gráficos y botones de control con nombres legibles por lectores de pantalla (antes anunciaban el nombre de la clase) | `scripts/ui-invoke.ps1 -List` |

### Fallos encontrados y corregidos en la Fase 3

1. **Gráficos fuera del lienzo vertical**: el marcador se salía por la derecha en 1080x1920. Las plantillas escalan ahora
   por el lado corto del lienzo; una prueba recorre todos los tipos en horizontal y vertical.
2. **Gráficos superpuestos por defecto**: la mosca y el marcador nacían en la misma esquina, y la cuenta atrás tapaba el
   marcador en vertical. Nuevas posiciones por defecto y prueba de no solapamiento en 3 lienzos.
3. **Selección tras crear un gráfico**: la fila nueva aún no existía al seleccionarla y «Editar» no hacía nada.
4. **Nombres accesibles**: las listas anunciaban `StreamPilot.Desktop.ViewModels.…` y los botones de control
   `Avalonia.Controls.Panel`.

## Limitaciones conocidas (reales, no simuladas)

- **Paneles acoplables**: son redimensionables y se pueden restaurar, pero no se pueden arrastrar a otra posición ni
  desacoplar en ventanas flotantes.
- **Giro con el ratón**: no hay tirador de giro; el giro se hace desde el menú (90°/180°) o el diálogo numérico.
- **Agrupar escenas y elementos, importar/exportar escenas sueltas y atajos personalizables**: pendientes.
- **Botones de propiedades de libobs** (p. ej. «Configurar video» del driver de la cámara) y el selector de FPS del
  dispositivo no se muestran: el puente aún no expone esos callbacks.
- **Fuentes de navegador**: requieren distribuir obs-browser (CEF); no incluidas.
- **Opacidad animada**: se aplica con un filtro sobre la fuente, así que afecta a todas las apariciones de esa fuente.
  Los gráficos no se ven afectados porque cada uno tiene su propia escena.
- **Transición de máscara por shader propio**: no incluida; la cortinilla luma usa los 34 patrones de obs-transitions.
  Tampoco hay *track matte* del stinger en el diálogo (libobs lo admite; falta la UI).
- **Alertas de plataformas** (seguidores, suscripciones, donaciones): la plantilla de alerta existe y funciona, pero
  se lanza a mano o desde el archivo de datos; la conexión con YouTube/Twitch llega con la Fase 6.
- **Cuenta atrás**: su estado (en marcha, tiempo restante) es de la sesión; al reabrir el proyecto vuelve a la duración.
- **Editor de gráficos**: los gráficos se editan con formulario (campos, colores, posición numérica), no arrastrándolos
  sobre el lienzo; las plantillas son las 8 del estudio (no hay editor de diseño libre ni línea de tiempo de keyframes
  en la UI, aunque el motor sí la soporta).
- **Animaciones**: las salidas automáticas y la cuenta atrás se comprueban cada 200 ms en .NET; la animación en sí corre
  en el reloj del motor.
- **Lienzo y proyecto**: cambiarlos reinicia la aplicación, porque libobs no se puede reiniciar dentro del proceso.
- **GPU de render**: libobs usa el adaptador 0 (Intel UHD en este portátil); NVENC en modo textura hace fallback. La
  selección automática de adaptador/encoder es de la Fase 5.
- **Transmisión** no probada contra YouTube/Twitch/etc. por falta de credenciales; la ruta RTMP es la estándar de libobs.
- **Licencia**: libobs es GPLv2+. Hay que decidir el modelo de distribución con asesoría legal antes de vender.

## Pendiente

- **Fases 4–7** — Asistente IA, optimización, multistream, calidad y distribución.

## Cómo ejecutar y verificar

```powershell
./scripts/build-native.ps1          # runtime libobs + puente en artifacts/obs-runtime
dotnet build StreamPilot.sln
dotnet test StreamPilot.sln         # unitarias + integración contra libobs real (requiere GPU y escritorio)
dotnet run --project src/StreamPilot.Desktop -- --project ruta\al\proyecto.spproj
./scripts/ui-drag-test.ps1 -ProcessId <pid> -ProjectPath <proyecto> -ItemName <fuente>   # prueba del editor
./scripts/ui-invoke.ps1 -ProcessId <pid> -Name "Añadir gráfico"   # pulsa un control por su nombre accesible (-List los muestra)
```
