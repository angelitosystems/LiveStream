# Distribución de StreamPilot

> **Antes de distribuir a terceros:** StreamPilot enlaza libobs (GPL-2.0-or-later). Hay que decidir el modelo de
> licencia con asesoría legal (ver `docs/THIRD-PARTY-NOTICES.md`). Hasta entonces, usa el instalador solo internamente.

## Generar una versión

```powershell
./scripts/build-native.ps1              # runtime de libobs + puente (sin Qt ni componentes de la interfaz de OBS)
dotnet test StreamPilot.sln             # unitarias + integración contra libobs real
./scripts/package.ps1                   # artifacts/release
```

`artifacts/release` contiene:

| Archivo | Uso |
|---|---|
| `StreamPilot-Setup-<versión>.exe` | Instalador por usuario (no pide administrador) |
| `StreamPilot-<versión>-portable.zip` | La aplicación sin instalar |
| `StreamPilot-<versión>-symbols.zip` | Símbolos de depuración; no se distribuye, se guarda para analizar fallos |
| `SHA256SUMS.txt` | Sumas de comprobación |
| `THIRD-PARTY-NOTICES.md` | Componentes de terceros y licencias |

La versión sale de `<Version>` en `Directory.Build.props`.

## Instalador

```
StreamPilot-Setup-<versión>.exe [/quiet] [/dir <carpeta>] [/noshortcuts] [/waitpid <pid>] [/launch]
```

- Instala en `%LOCALAPPDATA%\Programs\StreamPilot`, crea el acceso del menú Inicio y la entrada de
  «Aplicaciones instaladas» (HKCU). Nunca pide elevación.
- Extrae la versión nueva al lado y la intercambia: si algo falla, la anterior queda intacta. Se niega a instalar si
  StreamPilot está abierto desde esa carpeta.
- Códigos de salida: `0` instalado, `1` error, `2` cancelado, `3` StreamPilot abierto. Registro en
  `%TEMP%\StreamPilot-Setup.log`.
- Desinstalar: desde «Aplicaciones instaladas» o `StreamPilot.Desktop.exe --uninstall [/quiet]`. Solo borra una carpeta
  marcada por el instalador **y** registrada como instalada (nunca una copia de desarrollo). Los proyectos, ajustes y
  claves del usuario (`%APPDATA%\StreamPilot`) se conservan.

## Actualizaciones firmadas

1. Una sola vez: `./scripts/new-release-key.ps1`. Guarda la clave privada fuera del repositorio
   (`%USERPROFILE%\.streampilot-release\release-private-key.pem`, solo legible por tu usuario) y escribe la pública en
   `src/StreamPilot.Infrastructure/Updates/release-public-key.pem`, que se compila en la aplicación. **Haz copia de
   seguridad de la clave privada**: las versiones instaladas solo aceptan manifiestos firmados con ella.
2. En cada versión: `./scripts/package.ps1 -SigningKey <clave privada> -DownloadBaseUrl https://…/descargas` genera
   además `update.json` (versión, URL, tamaño y SHA-256 del instalador, firmados con ECDSA P-256).
3. Publica el instalador en esa URL y `update.json` en la URL del canal.
4. En cada equipo, el canal se configura en `%APPDATA%\StreamPilot\machine.json` (`"updateFeedUrl": "https://…/update.json"`).
   «Ayuda → Buscar actualizaciones…» comprueba la firma, que la versión sea mayor, descarga, verifica tamaño y SHA-256
   y ejecuta el instalador al cerrar la aplicación. Cualquier manipulación se rechaza antes de ejecutar nada.

El canal debe ser HTTPS (HTTP solo hacia el propio equipo, para pruebas).

## Firma de código (Authenticode)

No hay certificado de firma de código, así que los `.exe` no están firmados y SmartScreen avisará al instalar. Cuando
haya certificado, se firma `StreamPilot-Setup-<versión>.exe` y `StreamPilot.Desktop.exe` con `signtool` antes de
generar `update.json` (el SHA-256 firmado debe ser el del instalador final).

## Pruebas de resistencia

```powershell
dotnet run --project tools/StreamPilot.Soak -c Release -- --minutes 60 --report soak.json
```

Estudio real con captura de pantalla, texto, gráficos animados, grabación y emisión a un servidor RTMP local (si hay
ffmpeg), cambiando de escena, editando y deshaciendo sin parar. Falla si la memoria o los handles crecen de forma
sostenida, si el render o la codificación se retrasan más del 1 %, si se pierden fotogramas en la emisión o si hay
errores. Requiere GPU y una sesión de escritorio.
