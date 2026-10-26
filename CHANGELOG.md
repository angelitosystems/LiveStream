# Cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versiones con
[Versionado Semántico](https://semver.org/lang/es/).

## [0.9.0] - 2026-10-25

### Añadido
- **Editor de diseño libre de gráficos**: ventana de diseño con lienzo interactivo (selección por clic/recuadro,
  arrastre con guías magnéticas y cuadrícula, tiradores de escala, giro, zoom y recuadro), capas con ojo y candado,
  textos con efecto (degradado, contorno, sombra), imágenes y videos con recorte, alineación y distribución, duplicar
  y biblioteca de recursos. Todo lo que se mueve se aplica en directo al aire y entra en el historial de deshacer
  (cada gesto es un paso; el movimiento se previsualiza en vivo sin pasos).
- **Conversión plantilla → diseño libre** con confirmación y deshacer de una vez, «Guardar plantilla…» desde el
  diseñador y biblioteca de recursos compartida (`Herramientas → Biblioteca de recursos de gráficos…`), importar
  y exportar gráficos `.lsgfx` con sus recursos incrustados.
- **Kit de marca** (Herramientas → Kit de marca…): colores, tipografía y logo predeterminados para los gráficos nuevos,
  «Aplicar a todos los gráficos» (se deshace con Ctrl+Z) e importación/exportación en un archivo `.lsbrand` con el
  logo incrustado.
- **Logo en los lower thirds**, con selector de archivo; la imagen se encaja sin deformarse y el rótulo mantiene su ancho.
- **Cuenta atrás con «Hora de fin»** (hh:mm): cuenta contra el reloj del sistema y sobrevive a reinicios.
- **Plugins** en procesos separados con API versionada (JSON-RPC sobre stdio), permisos aceptados por el usuario,
  ajustes por plugin, dependencias y compatibilidad de versiones. Los plugins forman parte de un Job Object de Windows
  (sin procesos huérfanos, 1 GB de memoria como máximo) y se reinician solos, hasta 3 veces en 10 minutos.
- **Marketplace** (Herramientas → Marketplace de plugins…): catálogo remoto y repositorio local, búsqueda, filtros,
  instalación, actualización, activación, desactivación y desinstalación. Solo admite paquetes firmados, salvo en
  modo desarrollador.
- **SDK** (`src/LiveStream.PluginSdk`, `sdk/examples/HelloPlugin`, `docs/plugins/SDK.md`) y herramienta `lsplugin`
  para crear, validar, empaquetar, firmar, publicar, retirar y respaldar. El SDK vive en su propio repositorio
  ([LiveStream-SDK](https://github.com/angelitosystems/LiveStream-SDK)), con instalador por usuario para `lsplugin`
  (lo añade al PATH), paquete NuGet y guía completa; `lsplugin new` genera el proyecto completo del plugin.
- **Backend del marketplace** para Coolify (`marketplace/`): API de catálogo y descargas, publicación solo para el
  administrador, firma verificada también en el servidor, health check, logs en JSON, copias de seguridad y rollback
  por versiones inmutables.

### Corregido
- Los cierres inesperados de la aplicación no quedaban en el registro: ahora se escriben con su pila antes de que
  termine el proceso.
- La clave pública de actualizaciones nunca se incrustaba: la ruta del `.csproj` contenía un retorno de carro.

## [0.8.1] - 2026-10-19

Primera versión compilada y publicada automáticamente por GitHub Actions desde el código del repositorio.

### Añadido
- Workflow de publicación (`.github/workflows/release.yml`): al subir un tag compila, prueba, empaqueta y publica la
  release con estas notas.
- Firma Authenticode de la aplicación y del instalador con SignPath Foundation, que se activa en cuanto se aprueba y
  configura el proyecto (`docs/FIRMA.md`). Esta versión todavía se publica **sin firmar**.
- `scripts/package.ps1` por etapas (`-Stage App|Installer|Checksums`) y firma local opcional con
  `-CertificateThumbprint`.

### Cambiado
- `SHA256SUMS.txt` solo incluye los archivos publicados (no los símbolos).

## [0.8.0] - 2026-10-19

### Añadido
- Instalador con asistente de cuatro pasos (bienvenida, opciones, instalación con progreso y listo), pantalla de error
  con reintento y acceso al registro. `/quiet` sigue instalando sin interfaz.
- Elegir la carpeta de instalación y crear un acceso directo en el escritorio.
- Invitación a auspiciar LiveStream en el instalador (GitHub Sponsors y contacto para marcas).
- Logo e icono propios en el instalador, la ventana de la aplicación y los ejecutables.
- Licencia **GPL-2.0-or-later** (`LICENSE`), incluida en la aplicación y en la distribución.
- Este archivo de cambios.

### Cambiado
- Al actualizar, el instalador usa la carpeta donde ya está instalado LiveStream.
- La desinstalación también quita el acceso directo del escritorio.
- `scripts/package.ps1` regenera `docs/THIRD-PARTY-NOTICES.md` y añade `LICENSE.txt` a la distribución.

## [0.7.0] - 2026-10-18

Primera versión con distribución. Reúne las fases 1 a 7 del plan:

- Motor libobs estable con vista previa nativa en Avalonia.
- Estudio tipo OBS: escenas, fuentes, editor del lienzo, mezclador, modo estudio, grabación y emisión.
- Producción televisiva: transiciones, filtros, capa de gráficos con plantillas y animaciones.
- Asistente de IA (Claude) con herramientas tipadas y confirmación para acciones sensibles.
- Optimización: benchmark de codificadores, perfiles recomendados y bitrate adaptativo con auditoría.
- Multistream con supervisión y reconexión por destino.
- Instalador por usuario, paquete portable, actualizaciones firmadas e inventario de licencias.
- El proyecto pasa a llamarse LiveStream (antes StreamPilot).

[0.9.0]: https://github.com/angelitosystems/LiveStream/compare/v0.8.1...HEAD
[0.8.1]: https://github.com/angelitosystems/LiveStream/releases/tag/v0.8.1
[0.8.0]: https://github.com/angelitosystems/LiveStream/releases/tag/v0.8.0
[0.7.0]: https://github.com/angelitosystems/LiveStream/releases/tag/v0.7.0
