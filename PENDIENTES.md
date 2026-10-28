# Pendiente

Lo que falta tras el trabajo del 28/10/2026: estabilidad, arquitectura de extensiones, Marketplace unificado y CI.
El detalle histórico por fases está en [docs/ESTADO.md](docs/ESTADO.md) y la arquitectura en
[docs/EXTENSIONES.md](docs/EXTENSIONES.md).

## Hecho y en GitHub

| Repo | Visibilidad | Último commit | Estado |
|---|---|---|---|
| `LiveStream` (app) | pública | `933c13c` | CI nuevo (pruebas + puente nativo) en verde |
| `LiveStream-SDK` | pública | `67b5d32` (SDK 1.2.0, `PluginClient`) | CI en verde |
| `LiveStream-Marketplace` | **privada** | `ca1a266` (plugins oficiales) | CI en verde |

El código de los plugins oficiales vive solo en el repositorio privado. El historial público nunca lo contuvo, y
los commits locales que lo tenían ya se borraron.

---

## 1. Publicar (hasta que esto ocurra, nada llega a los usuarios)

- [ ] **Versión de la app.** `CHANGELOG.md` acaba en la 0.9.1 (26/10) y no recoge este trabajo. Hay que:
  - añadir la entrada (cierre nativo en d3d11, tareas de UI de libobs, manejadores globales, extensiones, Marketplace
    unificado, vuelta atrás de actualizaciones, separadores y paneles ocultos, CI);
  - subir la versión (`Directory.Build.props`) y crear la etiqueta `v*`. La etiqueta lanza `release`, que compila el
    puente con las correcciones de `bridge.cpp`, pasa las pruebas y publica el instalador.
  - Hasta entonces, la corrección del cierre nativo **solo existe en este equipo**.
- [ ] **Desplegar el backend del marketplace** (Coolify, dominio y VPS): sigue sin desplegar. El runbook está en
  `LiveStream-Marketplace/docs/deploy.md`.
- [ ] **Publicar los plugins oficiales** (rotador de titulares, rótulo de escena, marcador) en el catálogo:
  `lsplugin pack`, `sign` con la clave del publicador (solo la tiene el responsable) y `publish`.
- [ ] **Configurar el canal de actualizaciones de la app** (`updateFeedUrl`) y probar de extremo a extremo
  descargar, cerrar, instalar y reabrir.

## 2. Riesgos de estabilidad que quedan

- [ ] **TTS en su propio proceso.** SAPI es código nativo dentro de la app: un fallo suyo la cerraría (el host de
  extensiones solo aísla excepciones .NET). Está propuesto como tarea aparte, «Aislar TTS (SAPI) en un proceso
  propio».
- [ ] **El asistente y los plugins modifican el proyecto desde hilos de fondo** (`AssistantToolbox` y
  `CommandExecutor`), mientras la UI lo lee. Es la arquitectura actual. Habría que llevar su ejecución al hilo de UI,
  como ya se hizo con alertas y chat (`StudioSession.OnOwnerThread`).

## 3. Sin verificar (requieren credenciales, datos o hardware)

- [ ] **Emitir a plataformas reales** (YouTube, Twitch, TikTok, Facebook…) con claves. El multistream sí está probado
  contra dos servidores RTMP locales con libobs real.
- [ ] **Conectores de chat con cuentas reales.** Hoy se prueban con mensajes simulados.
- [ ] **Marketplace con un catálogo real:** instalar, «Actualizar todo» y la vuelta atrás automática desde la
  interfaz. En esta máquina no hay catálogo configurado y la compilación de depuración no lleva la clave de publicador.
- [ ] **Ocultar el panel de una extensión desactivada cuando está desacoplado** como ventana flotante (debería volver a
  acoplarse y ocultarse).
- [ ] **Medir CPU, GPU y RAM** en una sesión larga con cámara y multistream (`tools/LiveStream.Soak`). No hay cifras de
  mejora porque no se midió.
- [ ] **Asistente con la API de Claude** usando una clave real.

## 4. Mejoras propuestas

- [ ] **Pruebas contra libobs real en CI.** Necesitan GPU y escritorio, así que hoy solo corren en local
  (`tests/LiveStream.IntegrationTests`). Opción: un runner propio con GPU.
- [ ] **Prueba automática del dock** (ocultar y mostrar paneles conservando su sitio). Hoy está verificado a mano.
- [ ] **Salidas RTMP y capa de gráficos como extensiones.** Siguen en el núcleo porque el asistente y el optimizador
  dependen de ellas. Requiere definir antes un contrato para esas herramientas.
- [ ] **Fase 10:** reordenar capas por arrastre, arrastrar desde la biblioteca al lienzo y miniaturas de escena
  (ver `docs/ESTADO.md`).
- [ ] **Firma de código (SignPath) y decisión de licencia** (ver `docs/FIRMA.md`).

## 5. Limpieza menor

- [ ] `LiveStream-SDK/folder.json` está sin rastrear: decidir si se versiona o se ignora.
- [ ] `docs/ESTADO.md` sigue en la 0.9.1: actualizarlo al publicar la próxima versión.
