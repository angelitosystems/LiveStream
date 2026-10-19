# Firma de código (Authenticode)

LiveStream firma sus ejecutables con **Authenticode** mediante [SignPath Foundation](https://signpath.org), que da
certificados gratuitos a proyectos de código abierto. La firma se hace en GitHub Actions
(`.github/workflows/release.yml`): así el binario firmado sale siempre del código del repositorio y la clave privada
nunca pasa por un equipo personal.

## Qué se firma

| Etapa | Archivos | Configuración de SignPath |
|---|---|---|
| Aplicación | `LiveStream.Desktop.exe`, `LiveStream.*.dll`, `livestream_bridge.dll` | `.signpath/artifact-configurations/app.xml` |
| Instalador | `LiveStream-Setup-<versión>.exe` (con la aplicación ya firmada dentro) | `.signpath/artifact-configurations/installer.xml` |

Los binarios de terceros (libobs, plugins de OBS, FFmpeg, runtime de .NET…) se distribuyen tal cual. Las sumas
`SHA256SUMS.txt` y el manifiesto de actualizaciones (`update.json`) se calculan **después** de firmar, sobre los
archivos finales.

## Puesta en marcha (una sola vez)

1. **Solicitud**: en https://signpath.org/apply pide el certificado para `angelitosystems/LiveStream`. Requisitos
   principales: licencia OSI (GPL-2.0-or-later ✓), proyecto mantenido y publicado, compilación en GitHub Actions ✓ y la
   política de firma publicada en el README ✓ (sección «Firma de código»). Activa la verificación en dos pasos en
   GitHub y en SignPath.
2. **Proyecto en SignPath** (cuando aprueben la solicitud):
   - Proyecto con slug `LiveStream` vinculado al repositorio y con GitHub como *trusted build system*.
   - Configuraciones de artefacto `app` e `installer`: pega el contenido de los XML de `.signpath/artifact-configurations/`.
   - Política de firma `release-signing` con el certificado de SignPath Foundation y aprobación manual.
3. **GitHub → Settings → Secrets and variables → Actions**:
   - Secreto `SIGNPATH_API_TOKEN`: token de API de un usuario de envío (*submitter*) de SignPath.
   - Variable `SIGNPATH_ORGANIZATION_ID`: id de la organización en SignPath (activa la firma en el workflow).
   - Opcionales: `SIGNPATH_PROJECT_SLUG` (por defecto `LiveStream`) y `SIGNPATH_POLICY_SLUG` (por defecto
     `release-signing`).

Mientras no exista `SIGNPATH_ORGANIZATION_ID`, el workflow compila y publica igual, pero sin firmar.

## Publicar una versión firmada

1. Sube `<Version>` en `Directory.Build.props`, añade la entrada en `CHANGELOG.md` y haz commit.
2. Crea y sube el tag: `git tag -a v<versión> -m "LiveStream <versión>"` y `git push origin v<versión>`.
3. El workflow compila, ejecuta las pruebas unitarias y envía dos solicitudes de firma a SignPath. **Apruébalas** en
   https://app.signpath.io (tienes hasta una hora por solicitud).
4. Al terminar, los archivos firmados quedan en la release del tag (como borrador si no existía). Revisa las notas y
   publícala.

Para comprobar una firma en Windows: clic derecho en el `.exe` → Propiedades → Firmas digitales, o
`Get-AuthenticodeSignature .\LiveStream-Setup-<versión>.exe` en PowerShell.

## Firma local con un certificado propio

Si en algún momento tienes un certificado OV/EV (token USB o `.pfx` importado en el almacén del usuario), el mismo
script firma en local con `signtool` (Windows SDK):

```powershell
./scripts/package.ps1 -CertificateThumbprint <huella SHA-1 del certificado>
```

Firma los binarios propios antes de crear el instalador, después el instalador, y verifica ambas firmas con sello de
tiempo (`-TimestampUrl`, por defecto el de DigiCert).

## SmartScreen

Incluso firmado, SmartScreen puede avisar en las primeras descargas de un certificado nuevo hasta que acumula
reputación. El aviso pasa a mostrar el editor verificado en vez de «Editor desconocido».
