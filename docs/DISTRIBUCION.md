# Distribución de LiveStream

> **Licencia:** LiveStream se distribuye bajo GPL-2.0-or-later (`LICENSE`), como libobs. Cada versión publicada debe
> tener su código fuente disponible (tag en el repositorio). Componentes de terceros: `docs/THIRD-PARTY-NOTICES.md`.

## Publicar una versión

1. Sube `<Version>` en `Directory.Build.props` y añade la entrada en `CHANGELOG.md`.
2. Genera y prueba la distribución (abajo).
3. Crea el tag `v<versión>` y una release en GitHub con el instalador, el portable, `SHA256SUMS.txt`, `LICENSE.txt` y
   `THIRD-PARTY-NOTICES.md` (los símbolos no se publican).

## Generar una versión

```powershell
./scripts/build-native.ps1              # runtime de libobs + puente (sin Qt ni componentes de la interfaz de OBS)
dotnet test LiveStream.sln             # unitarias + integración contra libobs real
./scripts/package.ps1                   # artifacts/release
```

`artifacts/release` contiene:

| Archivo | Uso |
|---|---|
| `LiveStream-Setup-<versión>.exe` | Instalador por usuario (no pide administrador) |
| `LiveStream-<versión>-portable.zip` | La aplicación sin instalar |
| `LiveStream-<versión>-symbols.zip` | Símbolos de depuración; no se distribuye, se guarda para analizar fallos |
| `SHA256SUMS.txt` | Sumas de comprobación |
| `THIRD-PARTY-NOTICES.md` | Componentes de terceros y licencias (también se copia a `docs/`) |
| `LICENSE.txt` | Licencia GPL-2.0-or-later (también va dentro de la aplicación) |

La versión sale de `<Version>` en `Directory.Build.props`.

## Instalador

```
LiveStream-Setup-<versión>.exe [/quiet] [/dir <carpeta>] [/noshortcuts] [/waitpid <pid>] [/launch]
```

- Sin `/quiet` muestra un asistente (bienvenida, opciones, instalación con progreso, listo) con la invitación a
  auspiciar; con `/quiet` instala sin interfaz. `/waitpid` lo usa la aplicación al actualizarse.
- Instala en `%LOCALAPPDATA%\Programs\LiveStream` (o en la carpeta ya registrada), crea el acceso del menú Inicio
  (y, si se elige en el asistente, el del escritorio) y la entrada de «Aplicaciones instaladas» (HKCU). Nunca pide
  elevación.
- Extrae la versión nueva al lado y la intercambia: si algo falla, la anterior queda intacta. Se niega a instalar si
  LiveStream está abierto desde esa carpeta.
- Códigos de salida: `0` instalado, `1` error, `2` cancelado, `3` LiveStream abierto. Registro en
  `%TEMP%\LiveStream-Setup.log`.
- Desinstalar: desde «Aplicaciones instaladas» o `LiveStream.Desktop.exe --uninstall [/quiet]`. Solo borra una carpeta
  marcada por el instalador **y** registrada como instalada (nunca una copia de desarrollo). Los proyectos, ajustes y
  claves del usuario (`%APPDATA%\LiveStream`) se conservan.

## Actualizaciones firmadas

1. Una sola vez: `./scripts/new-release-key.ps1`. Guarda la clave privada fuera del repositorio
   (`%USERPROFILE%\.livestream-release\release-private-key.pem`, solo legible por tu usuario) y escribe la pública en
   `src/LiveStream.Infrastructure/Updates/release-public-key.pem`, que se compila en la aplicación. **Haz copia de
   seguridad de la clave privada**: las versiones instaladas solo aceptan manifiestos firmados con ella.
2. En cada versión: `./scripts/package.ps1 -SigningKey <clave privada> -DownloadBaseUrl https://…/descargas` genera
   además `update.json` (versión, URL, tamaño y SHA-256 del instalador, firmados con ECDSA P-256).
3. Publica el instalador en esa URL y `update.json` en la URL del canal.
4. En cada equipo, el canal se configura en `%APPDATA%\LiveStream\machine.json` (`"updateFeedUrl": "https://…/update.json"`).
   «Ayuda → Buscar actualizaciones…» comprueba la firma, que la versión sea mayor, descarga, verifica tamaño y SHA-256
   y ejecuta el instalador al cerrar la aplicación. Cualquier manipulación se rechaza antes de ejecutar nada.

El canal debe ser HTTPS (HTTP solo hacia el propio equipo, para pruebas).

## Firma de código (Authenticode)

La firma la hace SignPath Foundation en GitHub Actions al subir un tag `v*`; la puesta en marcha y el proceso están en
[FIRMA.md](FIRMA.md). `scripts/package.ps1` trabaja por etapas (`-Stage App`, `Installer`, `Checksums`) para firmar
la aplicación antes de incrustarla en el instalador y el instalador antes de calcular las sumas y `update.json`. Con
un certificado propio se puede firmar en local con `-CertificateThumbprint`.

## Pruebas de resistencia

```powershell
dotnet run --project tools/LiveStream.Soak -c Release -- --minutes 60 --report soak.json
```

Estudio real con captura de pantalla, texto, gráficos animados, grabación y emisión a un servidor RTMP local (si hay
ffmpeg), cambiando de escena, editando y deshaciendo sin parar. Falla si la memoria o los handles crecen de forma
sostenida, si el render o la codificación se retrasan más del 1 %, si se pierden fotogramas en la emisión o si hay
errores. Requiere GPU y una sesión de escritorio.
