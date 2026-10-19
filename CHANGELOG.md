# Cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/); versiones con
[Versionado Semántico](https://semver.org/lang/es/).

## [Sin publicar]

### Añadido
- Firma Authenticode de la aplicación y del instalador con SignPath Foundation desde GitHub Actions
  (`.github/workflows/release.yml`, `docs/FIRMA.md`).
- `scripts/package.ps1` por etapas (`-Stage App|Installer|Checksums`) y firma local opcional con
  `-CertificateThumbprint`.

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

[0.8.0]: https://github.com/angelitosystems/LiveStream/releases/tag/v0.8.0
[0.7.0]: https://github.com/angelitosystems/LiveStream/releases/tag/v0.7.0
