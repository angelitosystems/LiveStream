# Estado real del proyecto

> El producto se llamaba **StreamPilot** y pasó a llamarse **LiveStream**. Al primer arranque, los datos de
> `%APPDATA%\StreamPilot` se trasladan a `%APPDATA%\LiveStream`. El prefijo interno `sp_` de la API C y la extensión
> `.spproj` de los proyectos se mantienen por compatibilidad.

Actualizado a la versión 0.9.0 (cierre de la **Fase 8 — Marca, plugins y marketplace** más la **Fase 9 — Editor de diseño libre de gráficos**). Solo se marca como hecho lo verificado contra libobs real
en esta máquina (i7-13650HX, Intel UHD + RTX 4050 Laptop, Windows 11, pantalla al 125 %).

## Fase 1 — Motor (completada)

libobs 32.2.2 integrado mediante `livestream_bridge` (ABI C): inicio/apagado estables, dispositivos, escenas,
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
| Diagnóstico | Registro en `%LOCALAPPDATA%\LiveStream\logs` (rotación diaria, 14 días, URLs de ingesta redactadas), errores de UI registrados y mostrados | Usado para encontrar los fallos de abajo |

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
| Plantillas reutilizables | «Guardar como plantilla…» guarda estilo, campos y animaciones en `%APPDATA%\LiveStream\templates\*.spgfx`; «Añadir desde plantilla» las reutiliza en cualquier proyecto | Pruebas unitarias |
| Datos en vivo | Un gráfico puede enlazarse a un archivo JSON vigilado; cuando otro programa lo guarda, los campos se actualizan en directo sin entrar en el historial de deshacer | Prueba unitaria con escritura real del archivo |
| Persistencia | Esquema v3 (filtros, transición por escena, gráficos); los proyectos v2 se abren sin migración manual | Pruebas unitarias + proyecto de la Fase 2 reabierto |
| Accesibilidad | Filas de escenas/fuentes/gráficos y botones de control con nombres legibles por lectores de pantalla (antes anunciaban el nombre de la clase) | `scripts/ui-invoke.ps1 -List` |

### Fallos encontrados y corregidos en la Fase 3

1. **Gráficos fuera del lienzo vertical**: el marcador se salía por la derecha en 1080x1920. Las plantillas escalan ahora
   por el lado corto del lienzo; una prueba recorre todos los tipos en horizontal y vertical.
2. **Gráficos superpuestos por defecto**: la mosca y el marcador nacían en la misma esquina, y la cuenta atrás tapaba el
   marcador en vertical. Nuevas posiciones por defecto y prueba de no solapamiento en 3 lienzos.
3. **Selección tras crear un gráfico**: la fila nueva aún no existía al seleccionarla y «Editar» no hacía nada.
4. **Nombres accesibles**: las listas anunciaban `LiveStream.Desktop.ViewModels.…` y los botones de control
   `Avalonia.Controls.Panel`.

## Fase 4 — Asistente IA (completada, con las limitaciones de abajo)

| Área | Implementado | Verificación |
|---|---|---|
| Herramientas tipadas | 29 herramientas en `AssistantToolbox`: estado del estudio y dispositivos; escenas (crear, renombrar, duplicar, borrar, poner en programa); fuentes (añadir cámara/micro/audio/pantalla/ventana/juego/texto/color, quitar, mostrar/ocultar, mover/escalar/girar, ajustar al lienzo, capas, texto); audio (volumen, silencio, filtros); transición; gráficos (crear con campos, actualizar en vivo, lanzar, retirar, borrar, cuenta atrás); grabación, emisión y deshacer | 7 pruebas unitarias sobre el estudio real con motor falso |
| Seguridad | Esquemas estrictos (`strict`, `additionalProperties: false`) y validación propia de cada argumento antes de ejecutar; todo pasa por `CommandExecutor` (deshacer). Emitir, detener la emisión y borrar piden confirmación con un diálogo de la app; un rechazo se comunica al modelo para que no insista. El modelo no puede elegir archivos ni rutas, ni ve el servidor/clave de emisión, ni accede al motor | Pruebas: argumentos inválidos y herramientas inexistentes no cambian nada; el estado enviado no contiene destino ni clave; registro de las peticiones reales sin secretos |
| Modelo | Claude Opus 5.5 (`claude-opus-5-5`) con el SDK oficial de C# (`Anthropic` 12.58.0), esfuerzo medio, bucle manual de herramientas, historial solo por el final (los bloques de razonamiento se devuelven intactos). **Fallback del servidor activado** (`fallbacks: "default"`): si un clasificador rechaza la petición, la API la reintenta con otro modelo adecuado. Errores de la API traducidos a mensajes claros; un turno fallido o cancelado se retira del historial | 4 pruebas de protocolo con respuestas grabadas (cuerpo y cabeceras de cada petición) |
| Clave de API | Cifrada con DPAPI para el usuario de Windows, o tomada de `ANTHROPIC_API_KEY`; nunca se muestra ni se registra | Prueba manual en la app |
| Interfaz | Panel lateral (botón «Asistente» o Ctrl+Espacio): chat con la lista de acciones ejecutadas (✓/✗), sugerencias, cancelar, nueva conversación, configuración de la clave | App real contra un servidor local que imita la API: el asistente creó escena, fondo y rótulo, puso la escena en programa, lanzó el gráfico y respetó la negativa a borrar una escena |

### Fallos encontrados y corregidos en la Fase 4

1. **Nombres con acentos**: «micro» no encontraba «Micrófono». La búsqueda ignora ahora mayúsculas y acentos.
2. **JSON con acentos escapados** (`\u00F3`) en el estado enviado al modelo: ahora sale legible.
3. **Grabar/emitir** tenían su lógica dentro de los botones; ahora botones y asistente comparten el mismo camino
   (`IOutputController`), con las confirmaciones en cada llamador.

## Fase 5 — Optimización (completada, con las limitaciones de abajo)

| Área | Implementado | Verificación |
|---|---|---|
| Puente ABI v5 | Métricas de congestión de la emisión, bitrate actual, si el encoder admite bitrate dinámico y adaptador de render; cambio de bitrate en directo (`obs_encoder_update`, solo con `OBS_ENCODER_CAP_DYN_BITRATE`); enumeración de adaptadores DXGI en el orden de libobs | Integración: emisión real a un servidor RTMP local (ffmpeg), bitrate 1500 → 900 kb/s sin cortar la emisión y 0 fotogramas perdidos |
| Hardware | CPU (registro de Windows), hilos, memoria, GPUs con fabricante y memoria dedicada, codificadores del runtime clasificados (CPU / NVIDIA / Intel / AMD) con soporte de bitrate dinámico | En este equipo: i7-13650HX, 15.7 GB, Intel UHD (renderizaba), RTX 4050 Laptop |
| Benchmark | Graba el programa real unos segundos con cada codificador H.264 y mide fotogramas descartados, retraso de render y CPU; archivos temporales borrados | App real: NVENC, QuickSync y x264 medidos |
| Perfiles | Recomendador determinista: codificador por hardware sano con menos CPU, bitrate por resolución/FPS, grabación a 2,5×, GPU de render del mismo fabricante que el codificador, lienzo más ligero si nada aguanta. Nada se aplica sin autorización; los perfiles de salida se pueden revertir desde el registro | Pruebas unitarias; en la app, cambio a la RTX 4050 con reinicio: el motor arrancó en ella, la captura WGC se ve y el render bajó a 1,2 ms |
| Bitrate adaptativo | Controlador con medias móviles, umbrales distintos para bajar (pérdidas > 2 % o congestión > 50 %) y subir (< 0,2 % y < 15 %), muestras seguidas y esperas mínimas (5 s para bajar, 30 s para subir), suelo del 30 % del objetivo, nunca por encima del objetivo, congelado durante reconexiones. Se puede desactivar o restaurar el bitrate configurado | 11 pruebas con series de red simuladas (picos, ruido intermedio, saturación sostenida, recuperación) |
| Sobrecargas | Retraso de render o descartes del codificador sostenidos generan un aviso con recomendaciones (como mucho uno cada 5 min); no se corrigen solos porque las soluciones cortan la salida | Pruebas unitarias |
| Auditoría | Panel «Rendimiento» (barra lateral o Herramientas): equipo, benchmark, recomendaciones, estado en directo y registro de cada decisión con su motivo y botón «Revertir» | App real |
| Asistente | Herramienta `get_performance_report` (solo lectura): explica el rendimiento; las decisiones las toma el controlador o el usuario | Prueba del catálogo |
| Adaptador por equipo | `%APPDATA%\LiveStream\machine.json`; si libobs no arranca con el adaptador elegido, se vuelve solo al 0 para el siguiente inicio | Prueba unitaria (archivo dañado) + reinicio real |

## Fase 6 — Multistream (completada, con las limitaciones de abajo)

| Área | Implementado | Verificación |
|---|---|---|
| Puente ABI v6 | Una salida RTMP/RTMPS por destino; los destinos con los mismos ajustes comparten codificador (referencias débiles: desaparece cuando no lo usa nadie); estadísticas por destino (activo, reconectando, perdidos, bytes, congestión, bitrate, último código de error); cambio de bitrate por destino; métricas agregadas; la API de una sola emisión sigue funcionando | Integración con dos servidores RTMP locales (ffmpeg): dos destinos a la vez con codificador compartido; al caer un servidor, ese destino reconecta y el otro sigue; al volver, reconexión automática y envío de datos |
| Destinos | YouTube, Twitch, Facebook, Kick, TikTok, relay propio y RTMP personalizado, con ayuda por plataforma, servidores por defecto donde la plataforma los publica, RTMPS obligatorio en Facebook y límite de 6000 kb/s en Twitch. TikTok se marca como sujeto a elegibilidad | Pruebas unitarias |
| Credenciales | Una clave por destino, cifrada con DPAPI y separada de las demás (también la del relay); nunca en el proyecto ni en los registros | Prueba de la app: claves fuera del `.spproj` y del registro |
| Inicio y parada | «Iniciar transmisión» arranca todos los destinos marcados; si uno falla, los demás siguen y se informa por destino. Inicio, parada y edición por destino; no se puede borrar un destino en directo | Pruebas unitarias + app real |
| Supervisión | Estado por destino (conectando, en directo, reconectando, caído con motivo legible), bitrate real medido, pérdidas, tiempo en directo, número de reconexiones y registro de eventos | Pruebas unitarias + app real: caída y reconexión vistas en el panel |
| Bitrate adaptativo | Un controlador por grupo de destinos que comparten codificador (cifras sumadas, peor congestión) | Pruebas unitarias |
| Prueba de conexión | Conexión TCP al servidor (sin emitir ni enviar la clave) con diagnóstico de DNS, rechazo o tiempo agotado | Manual |
| Migración | Proyectos v3 con un servidor único pasan a un destino «Principal» con la misma credencial (sin volver a pedir la clave) | Prueba unitaria |
| Asistente | Ve nombre, plataforma y estado de cada destino (nunca servidor ni clave) y puede iniciar o detener uno, siempre con confirmación | Pruebas del catálogo |

### Fallos encontrados y corregidos en la Fase 6

1. **Doble inicio durante la conexión**: libobs no marca la salida como activa mientras conecta; un segundo inicio
   liberaba una salida en plena conexión. Ahora el puente lleva su propio estado «iniciada».
2. **Registro de destinos invisible**: los eventos normales tenían el color de texto anulado. Corregido; la corrección
   no se ha vuelto a comprobar visualmente.
3. **Pruebas con ffmpeg de Chocolatey**: `ffmpeg.exe` es un lanzador; matar solo ese proceso dejaba vivo el servidor.
   Las pruebas cierran ahora el árbol completo de procesos.

## Fase 7 — Calidad y distribución (completada, con las limitaciones de abajo)

| Área | Implementado | Verificación |
|---|---|---|
| Robustez del estudio | Prueba de propiedades: 40 secuencias aleatorias de 150 comandos; un comando rechazado no cambia nada, deshacer todo devuelve exactamente el estado inicial (proyecto y motor) y rehacer todo el final. 2000 archivos de proyecto corruptos (truncados, caracteres cambiados, tipos erróneos) solo producen errores controlados | 41 pruebas unitarias |
| Resistencia | `tools/LiveStream.Soak`: estudio real con captura, texto, gráficos animados, grabación y emisión local, cambiando de escena, editando y deshaciendo sin parar; mide memoria, handles, render, codificación y pérdidas | Soak de 20 min en este equipo: 0 errores, memoria +0,5 MB/min y handles +0,3/min (sin crecimiento sostenido), 0,01 % de fotogramas tarde, 0 descartes al codificar, 0 perdidos de ~34 000 emitidos, grabación activa todo el tiempo y cierre limpio |
| Runtime | Fuera Qt, la API del frontend de OBS, scripting y Lua (no los usa el motor): 163 → 132 MB con símbolos; los .pdb van en un paquete aparte | Integración completa de todas las fases sin esos componentes |
| Instalador | `scripts/package.ps1` publica la app autocontenida y genera instalador por usuario (sin administrador), paquete portable, símbolos y SHA-256. Instalación atómica con vuelta atrás, acceso en el menú Inicio, entrada en «Aplicaciones instaladas», negativa a instalar con la app abierta, desinstalación que conserva los datos del usuario y solo borra una carpeta marcada y registrada | Ciclo real en este equipo: instalar → la app arranca el motor desde la carpeta instalada → reinstalar con la app abierta (rechazado, código 3) → actualizar → desinstalar (carpeta, registro y acceso eliminados; datos conservados) → `--uninstall` en la copia de desarrollo no borra nada (código 4) |
| Actualizaciones firmadas | Manifiesto con versión, URL, tamaño y SHA-256 firmado con ECDSA P-256; clave privada fuera del repositorio (`scripts/new-release-key.ps1`), firma con `scripts/sign-update.ps1`, clave pública compilada en la app; solo HTTPS; descarga verificada antes de ejecutar; «Ayuda → Buscar actualizaciones…» | 5 pruebas con un servidor HTTP local: versión nueva, manifiesto manipulado, clave ajena, sin firma, instalador alterado o más grande, canal sin HTTPS; un manifiesto firmado por el script de PowerShell se verifica en la app |
| Licencias | `scripts/third-party-inventory.ps1` genera `docs/THIRD-PARTY-NOTICES.md` (licencias de los 30 paquetes .NET leídas de sus metadatos y tabla de componentes nativos) y se incluye en cada paquete | Inventario generado |

### Fallos encontrados y corregidos en la Fase 7

1. **Deshacer «Mostrar/Ocultar» y «Bloquear/Desbloquear»** invertía el valor en vez de restaurar el anterior: «Mostrar» un
   elemento ya visible y deshacer lo ocultaba.
2. **Deshacer «Eliminar elemento»** reinsertaba la fuente huérfana al final de la lista en vez de en su posición.
3. **`scripts/capture-window.ps1`** copiaba la región de la pantalla: si otra ventana quedaba encima, la capturaba. Ahora
   usa `PrintWindow` (solo la ventana pedida); esta versión del script no se ha vuelto a ejecutar.

## Fase 8 — Marca, plugins y marketplace (implementada, sin publicar)

| Área | Implementado | Verificación |
|---|---|---|
| Kit de marca | Colores, tipografía y logo predeterminados para gráficos nuevos (interfaz y asistente), «Aplicar a todos» en un solo comando que se deshace de una vez, exportar/importar `.lsbrand` con el logo incrustado (solo imágenes, 5 MB como máximo, sin rutas del archivo importado) | 7 casos de prueba unitaria; diálogo abierto y capturado en la app real |
| Lower thirds y cuenta atrás | Logo en el rótulo (`image_source` encajado sin deformar, mismo ancho total); cuenta atrás por «Hora de fin» contra el reloj del sistema | Pruebas unitarias; integración contra libobs real con la pieza de imagen |
| Plugins | Proceso aparte por plugin, JSON-RPC 2.0 por stdio, API v1 = herramientas tipadas del estudio filtradas por permisos (`studio.read`, `studio.modify`, `studio.live`); Job Object de Windows (sin huérfanos, 1 GB por plugin); reinicio con espera y parada tras 3 cierres en 10 min; sin `ANTHROPIC_API_KEY` en su entorno; ajustes tipados; dependencias; instalación atómica fuera de la carpeta de la app | 16 casos de prueba, con el plugin de ejemplo como proceso real: uso de la API, permisos denegados, cierre forzado → reinicio con otro PID, desactivar sin dejar procesos; paquetes con rutas `..`, firmas ajenas o manipuladas, dependencias y versiones |
| Marketplace (app) | Catálogo remoto (HTTPS) y repositorio local, entradas y descargas verificadas con la clave del publicador, búsqueda, filtros (todos / instalados / actualizaciones), ficha con permisos, versión, compatibilidad, dependencias y cambios, aceptación de permisos, activar/desactivar/desinstalar, ajustes, modo desarrollador | Pruebas con catálogo HTTP simulado (entrada manipulada descartada, descarga alterada rechazada, HTTP rechazado); diálogo abierto y capturado en la app real |
| Backend y publicación | `marketplace/server` (ASP.NET Core sin dependencias externas), Docker no root con HEALTHCHECK, compose para Coolify, límite de peticiones, token comparado en tiempo constante, firma verificada también en el servidor, versiones inmutables, retirada/restauración, historial del índice, copia de seguridad; `lsplugin` (new, validate, pack, keygen, sign, verify, publish, yank, unyank, list, backup) | `marketplace/smoke-test.ps1`: 13 comprobaciones de extremo a extremo correctas; imagen Docker construida y en marcha: *healthy*, usuario `app`, sale con error si falta configuración |

### Fallos encontrados y corregidos en la Fase 8

1. **Cierres sin rastro**: el manejador de excepciones no controladas encolaba la línea y el proceso moría antes de
   escribirla. Ahora `AppLog.Fatal` espera a que llegue al disco e incluye la pila.
2. **Clave de actualizaciones nunca incrustada**: la ruta del `.csproj` tenía un retorno de carro en lugar de `\r`.
3. **Paquetes no reproducibles**: no se podía fijar la fecha de una entrada ZIP ya escrita (corregido en `Pack`).
4. **Copia de seguridad del backend**: el ZIP se cerraba con E/S síncrona sobre la respuesta; ahora se genera en un
   temporal.

### Limitaciones de la Fase 8

- **Aislamiento**: el plugin corre con los permisos del usuario de Windows; los permisos limitan la API de LiveStream,
  no el sistema de archivos ni la red. Por eso solo se instalan paquetes firmados. AppContainer: ver
  `docs/plugins/PORTAL-FUTURO.md`.
- **Sin clave de publicador generada**: hasta ejecutar `lsplugin keygen`, la app solo instala plugins sin firma en
  modo desarrollador y descarta todo el catálogo.
- **Backend no desplegado**: verificado en local y en Docker; el despliegue en Coolify requiere dominio y acceso al VPS.
- **Marketplace sin capturas, favoritos ni valoraciones**: la ficha muestra texto (descripción, permisos, cambios).
- **Los plugins no añaden interfaz propia ni tipos de fuente**: actúan a través de las herramientas del estudio.

## Fase 9 — Editor de diseño libre de gráficos (implementada, sin publicar)

| Área | Implementado | Verificación |
|---|---|---|
| Editor de diseño libre | Ventana diseñador con lienzo interactivo (`DesignCanvas`): selección por clic, Ctrl y recuadro, arrastre (con guías magnéticas a bordes/centros de otros elementos y a la cuadrícula), redimensionado con tiradores, giro, zoom con rueda y botón central, cuadrícula y guías activables, encuadre automático | Pruebas unitarias de `DesignOps` (hit-test con giro, alineación, distribución, apilado, guías y duplicado); la interacción de ratón se ha comprobado compilando y con capturas pendientes de rehacer con la ventana abierta |
| Elementos | Texto (con vivo: reloj, cuenta atrás y campos `{x}`), forma (rectángulo con color y opacidad), imagen y video (ruta desde la biblioteca o archivo, recorte, silenciado, en bucle) con opacidad, capas visibles/bloqueadas, duplicar y hasta 200 elementos | Pruebas unitarias: el diseño libre se convierte en piezas nativas (texto con degradado/contorno/sombra, giro, recortes y medias) y la edición en vivo solo recrea lo cambiado |
| Plantilla → diseño libre | «Diseñar» sobre una plantilla la convierte (con confirmación y permitido por el motor en el mismo gráfico, deshacible de una vez); las piezas conservan posición y los textos quedan como campos `{x}` de modo que formulario, datos en vivo y asistente siguen funcionando | Prueba unitaria: cambio de tipo con `allowKindChange` y deshacer devuelve la plantilla exacta |
| Historial | Cada gesto del lienzo es un paso de deshacer (al soltar); durante el arrastre se aplica una vista previa en directo con `PreviewGraphicCommand`, que no entra en el historial; textos vivos (reloj/cuenta atrás) se repintan cada 200 ms; un grafo puede editarse estando al aire | Prueba unitaria: la vista previa cambia proyecto y motor sin entrar en el historial y Ctrl+Z revierte el gesto completo de una vez |
| Capas y propiedades | Lista de capas (arriba = delante) con ojo/candado por elemento, alinear (6 modos), distribuir, ordenar, duplicar y eliminar por menú y teclado (Supr, Ctrl+D, Ctrl+Z/Y, flechas para ajuste fino); panel de propiedades del elemento y de la presentación del gráfico (nombre, posición/escala en el lienzo, animaciones, salida automática, datos en vivo) | Mismo cauce de comandos validados del estudio (todo deshacible) |
| Biblioteca de recursos | Imágenes y videos importados a `%APPDATA%\LiveStream\library` (el original puede borrarse), categorías, eliminar, abrir carpeta; disponible en Herramientas y como selector dentro del diseñador; importar y exportar `.lsgfx` (el paquete lleva los recursos incrustados) | Pruebas unitarias (validación de contenido, rutas fuera del archivo, nombres duplicados, recursos ausentes) |
| Accesos en la UI principal | Botón «Editor de diseño libre» en el panel Gráficos, doble clic (diseñador en diseño libre, formulario en plantillas), menú contextual por fila (diseñar, formulario, convertir, exportar) e «Importar gráfico (.lsgfx)…» en el menú «+» | Compilación y pruebas en verde; queda por verificar el flujo completo a mano en la app con emisión activa |

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
- **Cuenta atrás**: con duración, su estado (en marcha, tiempo restante) es de la sesión y al reabrir vuelve a la duración. Con «Hora de fin» (hh:mm) cuenta contra el reloj del sistema y sobrevive a reinicios; en ese modo iniciar/pausar no tienen efecto.
- **Editor de gráficos (incompleto)**: existe ya el diseño libre con lienzo y capas (Fase 9), pero el formulario sigue
  siendo la vía de edición de textos/colores de las piezas seleccionadas; no hay línea de tiempo de keyframes en la UI
  (el motor sí soporta animación por keyframes) ni máscaras/degradados completos por elemento. Sin verificar aún a mano
  con la ventana del diseñador en ejecución.
- **Asistente sin probar contra la API real**: en este equipo no hay clave de Anthropic. El protocolo está verificado
  con respuestas grabadas y un servidor local, pero la calidad de las respuestas del modelo real no se ha medido
  (no hay conjunto de evaluación todavía). Cada mensaje consume tokens de la cuenta del usuario.
- **Asistente**: no ve la imagen del programa ni puede usar archivos (imágenes, videos, stingers); las respuestas no se
  muestran en streaming (aparecen al terminar cada paso); el historial no se compacta (conversaciones muy largas
  deben reiniciarse con «Nueva conversación»).
- **Animaciones**: las salidas automáticas y la cuenta atrás se comprueban cada 200 ms en .NET; la animación en sí corre
  en el reloj del motor.
- **Lienzo y proyecto**: cambiarlos reinicia la aplicación, porque libobs no se puede reiniciar dentro del proceso.
- **GPU de render**: por defecto se usa el adaptador 0 (Intel UHD en este portátil). El panel de rendimiento recomienda
  la RTX y la cambia con un reinicio si el usuario lo autoriza; no se cambia sola.
- **Bitrate adaptativo** verificado con series simuladas y con un cambio real de bitrate contra un servidor local;
  no se ha probado con pérdidas de red reales (no hay forma de saturar la red de forma controlada en este equipo).
- **Benchmark**: mide con la escena de programa actual; con una escena ligera todos los codificadores salen sanos.
  No mide la subida de red (no hay prueba de ancho de banda) y solo prueba H.264.
- **Transmisión** no probada contra YouTube/Twitch/etc. por falta de credenciales; multistream, caídas y reconexión
  verificados con servidores RTMP locales (ffmpeg). La ruta RTMP es la estándar de libobs.
- **Relay SRS/MediaMTX**: el relay se configura como un destino con su propia credencial, pero no se ha probado contra
  SRS ni MediaMTX reales (no están instalados). No se generan configuraciones para ellos.
- **Multistream con resoluciones distintas** (p. ej. horizontal a una plataforma y vertical a otra): no soportado; todos
  los destinos emiten el mismo lienzo. Cada destino puede tener su propio codificador y bitrate.
- **Límites de plataformas**: solo se aplican los documentados con certeza (RTMPS en Facebook, 6000 kb/s en Twitch).
- **Licencia**: libobs es GPLv2+. Hay que decidir el modelo de distribución con asesoría legal antes de vender o
  distribuir a terceros (ver `docs/THIRD-PARTY-NOTICES.md`). La licencia exacta de la compilación de FFmpeg y de
  pthreads incluidas está marcada «a confirmar».
- **Sin firma de código (Authenticode)**: no hay certificado; SmartScreen avisará al instalar.
- **Actualizaciones**: el cliente y el instalador están verificados por separado, pero el flujo completo desde la app
  (descargar → cerrar → instalar → reabrir) no se ha ejecutado de extremo a extremo; tampoco hay todavía un canal
  publicado ni clave de publicación generada (decisión del responsable de la publicación).
- **Instalador sin interfaz propia**: usa cuadros de diálogo de Windows (confirmar, resultado); no muestra progreso.

## Pendiente

- Las fases 1-9 están implementadas (la 8 y la 9 sin publicar). Lo pendiente es lo indicado en las limitaciones: pruebas contra
  plataformas reales y la API de Claude con credenciales, relay SRS/MediaMTX, firma de código y la decisión de licencia.

## Cómo ejecutar y verificar

```powershell
./scripts/build-native.ps1          # runtime libobs + puente en artifacts/obs-runtime
dotnet build LiveStream.sln
dotnet test LiveStream.sln         # unitarias + integración contra libobs real (requiere GPU y escritorio)
dotnet run --project src/LiveStream.Desktop -- --project ruta\al\proyecto.spproj
./scripts/ui-drag-test.ps1 -ProcessId <pid> -ProjectPath <proyecto> -ItemName <fuente>   # prueba del editor
./scripts/package.ps1                # instalador, portable, símbolos (ver docs/DISTRIBUCION.md)
dotnet run --project tools/LiveStream.Soak -c Release -- --minutes 60   # prueba de resistencia
./scripts/ui-invoke.ps1 -ProcessId <pid> -Name "Añadir gráfico"   # pulsa un control por su nombre accesible (-List los muestra)
# Asistente: clave en el panel (se guarda cifrada) o variable ANTHROPIC_API_KEY
```
