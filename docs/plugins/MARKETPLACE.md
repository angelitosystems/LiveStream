# Marketplace: backend, despliegue en Coolify y operación

## Arquitectura

```
lsplugin (tu equipo)                      Backend (Coolify)                    LiveStream (usuarios)
  pack → sign (clave privada local)  ──►  POST /v1/admin/packages          ◄──  GET /v1/catalog
  publish/yank/unyank/list/backup         verifica firma + manifiesto           verifica firma de cada entrada
                                          /data: index.json, history/,     ◄──  GET /v1/packages/{id}/{versión}
                                                 packages/<id>/<v>.lsplugin      verifica tamaño + SHA-256
```

- **El servidor no es de confianza.** La clave privada nunca sale de tu equipo y el servidor solo guarda paquetes ya
  firmados. La app verifica la firma con la clave pública compilada, así que quien comprometa el VPS no puede
  distribuir código.
- **Solo publica el administrador**: no existe registro de terceros (ver `PORTAL-FUTURO.md`).
- **Versiones inmutables**: no se sobrescriben ni se borran. Para retirar una versión se usa `yank`, y para volver
  atrás, `unyank`.
- **Sin dependencias externas**: ASP.NET Core del framework, sin base de datos. Los datos viven en un volumen:
  índice JSON con escritura atómica, historial de las 50 últimas versiones del índice y los paquetes.

### API

| Método y ruta | Acceso | Uso |
|---|---|---|
| `GET /health` | público | 200 si el volumen admite escritura (lo usa el healthcheck) |
| `GET /v1/catalog` | público, 120/min por IP | Entradas publicadas no retiradas |
| `GET /v1/packages/{id}/{version}` | público | Descarga (410 si está retirada) |
| `POST /v1/admin/packages` | token, 30/min | multipart `package` + `signature` |
| `GET /v1/admin/packages` | token | Todas las versiones, retiradas incluidas |
| `POST /v1/admin/packages/{id}/{version}/yank` y `/unyank` | token | Retirar o restaurar |
| `GET /v1/admin/backup` | token | ZIP con todo `/data` |

## Variables de entorno

| Variable | Obligatoria | Valor |
|---|---|---|
| `ADMIN_TOKEN` | sí | Al menos 32 caracteres aleatorios: `openssl rand -hex 32`. Se compara en tiempo constante. |
| `PUBLISHER_PUBLIC_KEY` | sí | La clave pública de `lsplugin keygen`: el PEM completo o solo la línea base64 interior. |
| `PUBLIC_BASE_URL` | sí | URL https pública del servicio, sin barra final. Con ella se forman las URLs de descarga. |
| `MAX_PACKAGE_MB` | no | Por defecto 100. |
| `DATA_DIR` | no | Por defecto `/data` (el volumen). |

Si falta alguna variable obligatoria o no es válida, el contenedor **no arranca** y escribe el motivo en el log.

## Desplegar en Coolify

Requisitos: un servidor con Coolify, un dominio que apunte al VPS y acceso al repositorio de GitHub. Ningún valor de
este documento es real: sustituye los marcadores.

1. **Claves**, en tu equipo y una sola vez:
   ```powershell
   dotnet run --project tools/LiveStream.PluginTool -- keygen `
     --private $env:USERPROFILE\.livestream-plugins\private.pem `
     --public src\LiveStream.Infrastructure\Plugins\plugin-public-key.pem
   ```
   Haz una copia de seguridad de `private.pem` fuera del VPS y del repositorio. Si se pierde, habrá que publicar una
   versión de LiveStream con una clave nueva.
2. **Coolify → Projects → New → Resource → Public/Private Repository**: elige `angelitosystems/LiveStream`, la rama y
   **Build Pack: Docker Compose**.
   - *Base Directory*: `/`
   - *Docker Compose Location*: `/marketplace/docker-compose.yml`
3. **Environment Variables**: `ADMIN_TOKEN`, `PUBLISHER_PUBLIC_KEY` y `PUBLIC_BASE_URL`, como en `marketplace/.env.example`.
   Marca `ADMIN_TOKEN` como secreto.
4. **Domains**: asigna `https://<tu-subdominio>` al servicio `marketplace`, puerto **8080**. Coolify (Traefik o Caddy)
   emite y renueva el certificado HTTPS. El contenedor solo escucha en HTTP interno.
5. **Deploy**. Comprueba `https://<tu-subdominio>/health` → `{"status":"ok"}`. El estado del contenedor en Coolify
   debe pasar a *healthy*.
6. **Persistencia**: el volumen `marketplace-data` sobrevive a los redespliegues. No lo borres al eliminar el recurso.

### Copias de seguridad

- **Bajo demanda**: `lsplugin backup --server https://<marketplace> -o copia.zip`, con el token en `LS_MARKETPLACE_TOKEN`.
- **Programada**: en Coolify → *Scheduled Tasks*, añade una tarea en el contenedor o en el host que copie el volumen
  `marketplace-data` (por ejemplo, `tar czf` hacia un almacenamiento S3 configurado). También puedes ejecutar
  `lsplugin backup` desde otra máquina con cron.
- **Restaurar**: detén el servicio, extrae el ZIP en el volumen (`index.json`, `history/` y `packages/`) y arráncalo.

### Rollback

- **De un plugin**: `lsplugin yank <id> <versión>` hace que la app vuelva a ofrecer la versión anterior.
  `unyank` lo deshace.
- **Del índice**: `history/index-*.json` guarda las 50 últimas versiones. Copia la que quieras sobre `index.json`.
- **Del servicio**: Coolify → *Deployments* → *Rollback* al despliegue anterior. Los datos del volumen no cambian.

### Logs

El servicio escribe JSON por stdout, visible en Coolify → *Logs*. Registra publicaciones, retiradas, paquetes
rechazados y accesos de administración denegados (con IP). Los logs de cada petición se reducen a nivel *Warning*.

## Conectar LiveStream

- **Clave**: con `plugin-public-key.pem` en `src/LiveStream.Infrastructure/Plugins/`, la compilación la incrusta.
  Sin ella, la app solo instala plugins sin firma en modo desarrollador.
- **Catálogo**: Marketplace → «Origen…» → `https://<marketplace>`. Se guarda en `%APPDATA%\LiveStream\machine.json`
  (`pluginCatalogUrl`).
- **Repositorio local**, opcional: una carpeta con `*.lsplugin` y sus `*.lsplugin.sig.json`.

## Prueba local completa

`marketplace/smoke-test.ps1` arranca el backend en `127.0.0.1` con claves temporales y comprueba publicar, rechazar
duplicados, firmas ajenas y tokens incorrectos, descargar con el SHA-256 correcto, retirar (410), restaurar y hacer
una copia de seguridad. No toca producción.
