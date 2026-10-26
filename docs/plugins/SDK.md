# SDK de plugins de LiveStream (API v1)

Un plugin es **un programa aparte** (`.exe` de Windows x64, en cualquier lenguaje) que LiveStream arranca en su
propio proceso y con el que habla por **JSON-RPC 2.0 en stdin/stdout**, un mensaje JSON por línea. El plugin no se
carga dentro de LiveStream: si se cuelga o se cierra, el estudio sigue funcionando.

- Biblioteca .NET: `src/LiveStream.PluginSdk` (manifiesto, empaquetado, firma y `JsonRpcPeer`).
- Ejemplo completo: `sdk/examples/HelloPlugin`.
- Herramienta de quien publica: `tools/LiveStream.PluginTool` (`lsplugin`).
- Esquema del manifiesto: `sdk/plugin.schema.json`.

## 1. Manifiesto (`plugin.json`)

```json
{
  "id": "com.angelitosystems.hello",
  "name": "Hola LiveStream (ejemplo)",
  "version": "1.0.0",
  "apiVersion": 1,
  "entry": "HelloPlugin.exe",
  "author": "Angelito Systems S.A.C.",
  "description": "Qué hace el plugin.",
  "category": "Ejemplos",
  "tags": ["ejemplo"],
  "homepage": "https://…",
  "minAppVersion": "0.9.0",
  "permissions": ["studio.read"],
  "dependencies": { "com.empresa.otro": "1.2.0" },
  "settings": [{ "key": "greeting", "label": "Saludo", "type": "string", "default": "Hola" }],
  "changelog": "1.0.0: primera versión."
}
```

| Campo | Regla |
|---|---|
| `id` | Minúsculas con puntos (`com.empresa.plugin`). Único y estable para siempre. |
| `version` | `MAYOR.MENOR.PARCHE`. Cada publicación necesita una versión nueva: las publicadas no se sobrescriben. |
| `apiVersion` | Versión del protocolo. Hoy solo existe `1`. |
| `entry` | Ruta relativa de un `.exe` dentro del paquete. |
| `minAppVersion` | Versión mínima de LiveStream. Las versiones anteriores no ofrecen ni instalan el plugin. |
| `permissions` | Ver §3. Solo se aceptan los que se declaran aquí. |
| `dependencies` | Id → versión mínima. Esos plugins deben estar instalados, y activos para poder activar este. |
| `settings` | Ajustes que LiveStream muestra en el marketplace. Tipos: `string`, `number`, `bool`. |

## 2. Ciclo de vida

1. **Instalar**: se verifica el paquete (firma, rutas, tamaños, compatibilidad y dependencias). El usuario acepta los
   permisos y se extrae en `%LOCALAPPDATA%\LiveStream\plugins\<id>`, fuera de la carpeta de la aplicación: un plugin
   nunca toca archivos del núcleo.
2. **Arrancar**: al iniciar el motor, o al activarlo, LiveStream lanza `entry`. El directorio de trabajo es
   `%APPDATA%\LiveStream\plugin-data\<id>` (variable `LIVESTREAM_PLUGIN_DATA`). El plugin no hereda
   `ANTHROPIC_API_KEY`.
3. **`initialize`**: el plugin tiene 10 s para responder `{}`.
4. **En marcha**: recibe eventos y llama a herramientas.
5. **Detener**: llega `shutdown`; el plugin responde y sale. A los 2 s se cierra su stdin y, si sigue vivo, se termina.
6. **Fallos**: si el proceso se cierra o rompe el protocolo, se reinicia tras 2, 4… segundos. Tras 3 cierres en
   10 minutos queda **detenido con el error visible** en el marketplace, sin bucles infinitos.
7. Todos los plugins forman parte de un *Job Object* de Windows: si LiveStream termina, aunque sea por un cierre
   inesperado, Windows los termina también. No quedan procesos huérfanos. Cada plugin tiene un límite de 1 GB de memoria.

## 3. Permisos

| Permiso | Permite |
|---|---|
| `studio.read` | Leer el estado del estudio (`get_studio_state`, dispositivos, rendimiento). |
| `studio.modify` | Herramientas que modifican el estudio mediante comandos validados que se pueden deshacer (Ctrl+Z). |
| `studio.live` | Emitir, grabar y borrar. **Cada uso** pide confirmación al usuario en un diálogo. |

Los permisos controlan **la API de LiveStream**. El proceso del plugin se ejecuta con los permisos del usuario de
Windows, igual que cualquier programa instalado. Por eso solo se instalan plugins firmados por el publicador de
confianza (§6).

## 4. Protocolo

Mensajes JSON-RPC 2.0, uno por línea, en UTF-8. **No escribas nada más en stdout**: usa `log` o stderr (stderr se
guarda en el registro de LiveStream). Tamaño máximo por mensaje: 1 MB.

### LiveStream → plugin

| Método | Tipo | params | Respuesta |
|---|---|---|---|
| `initialize` | petición | `apiVersion`, `appVersion`, `pluginId`, `permissions[]`, `settings{}` | `{}` |
| `shutdown` | petición | — | `{}` |
| `event` | notificación | `name` (`studio.changed`, agrupado como máximo cada 250 ms) | — |
| `settings/changed` | notificación | `settings{}` | — |

### Plugin → LiveStream

| Método | Tipo | params | Respuesta |
|---|---|---|---|
| `tools/list` | petición | — | `{tools:[{name, description, inputSchema, permission}]}`, solo las permitidas |
| `tools/call` | petición | `name`, `arguments{}` | `{content, isError, summary}` |
| `log` | notificación | `level` (`info`, `warn`, `error`), `message` | — |

Las herramientas son las mismas que usa el asistente de IA (escenas, fuentes, audio, transiciones, gráficos con
lower thirds, cuenta atrás…). Cada argumento se valida contra su `inputSchema`. Si una herramienta falla, la respuesta
trae `isError: true` y un mensaje legible. Si falta el permiso, se devuelve el error JSON-RPC `-32001`.

Ejemplo (consultar el estado):

```json
→ {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_studio_state","arguments":{}}}
← {"jsonrpc":"2.0","id":1,"result":{"content":"{\"scenes\":[…]}","isError":false,"summary":"Consultó el estado del estudio"}}
```

En .NET, `JsonRpcPeer` resuelve el entramado. Mira `sdk/examples/HelloPlugin/Program.cs`, unas 60 líneas.

## 5. Crear, probar y empaquetar

Instala el SDK con `LiveStream-SDK-Setup-<versión>.exe`, de la
[última release del repositorio del SDK](https://github.com/angelitosystems/LiveStream-SDK/releases): deja `lsplugin`
en el PATH de tu usuario, el esquema, la documentación y el paquete NuGet local de `LiveStream.PluginSdk`. Después
(en una terminal nueva):

```powershell
lsplugin new mi-plugin --id com.empresa.miplugin --name "Mi plugin"
cd mi-plugin
dotnet publish -c Release
lsplugin validate bin\Release\net10.0\win-x64\
lsplugin pack bin\Release\net10.0\win-x64\ -o com.empresa.miplugin-0.1.0.lsplugin
```

`lsplugin new` genera proyecto completo: `plugin.json`, `<Proyecto>.csproj` (referencia al paquete del SDK local),
`Program.cs` ya conectado al protocolo, `NuGet.config` y `.gitignore`. Programar el plugin es completar la lógica bajo
esas llamadas; el ejemplo de referencia completo está en `sdk/examples/HelloPlugin`.

Sin el SDK instalado también funciona desde el repositorio:

```powershell
dotnet run --project tools/LiveStream.PluginTool -- new mi-plugin --id com.empresa.miplugin --name "Mi plugin"
```

Para probarlo sin firma, activa **Herramientas → Marketplace de plugins → Modo desarrollador** e instálalo con
«Instalar desde archivo…». El registro de LiveStream (Herramientas → Abrir registros de diagnóstico) muestra las líneas
`[plugin:<id>]`.

El paquete `.lsplugin` es un ZIP con `plugin.json` en la raíz. Límites: 100 MB por paquete, 300 MB descomprimido y
5000 archivos. No admite rutas absolutas ni `..`. `pack` es reproducible: el mismo contenido da el mismo SHA-256.

## 6. Firma y publicación

1. **Una sola vez**: `lsplugin keygen --private %USERPROFILE%\.livestream-plugins\private.pem --public src\LiveStream.Infrastructure\Plugins\plugin-public-key.pem`.
   La clave privada no puede quedar dentro de un repositorio git y solo la puede leer tu usuario. La pública se
   compila en la aplicación.
2. `lsplugin sign paquete.lsplugin --key <privada.pem>` crea `paquete.lsplugin.sig.json` (ECDSA P-256 sobre id,
   versión, tamaño y SHA-256).
3. `$env:LS_MARKETPLACE_TOKEN = "<token>"` y luego `lsplugin publish paquete.lsplugin --server https://<marketplace>`.

LiveStream verifica cada entrada del catálogo y cada descarga con la clave compilada. Un servidor comprometido no puede
colar ni alterar plugins.

## 7. Versiones y compatibilidad

- **API**: los cambios que solo añaden (herramientas, eventos o campos nuevos) mantienen `apiVersion: 1`. Un plugin debe
  ignorar los campos y eventos que no conozca. Un cambio incompatible crearía `apiVersion: 2`, y LiveStream seguiría
  aceptando la 1 durante al menos una versión mayor.
- **Herramientas**: sus nombres y argumentos actuales no cambian de significado dentro de la API v1.
- **Actualizaciones**: el marketplace ofrece la última versión no retirada, compatible y firmada. Al actualizar, se
  conserva si el plugin estaba activo y también sus ajustes.
- **Configuración**: `plugins.json` lleva un número de esquema con un punto único de migración (`PluginStore.Migrate`).

## 8. Errores frecuentes

| Mensaje | Causa |
|---|---|
| «no respondió a initialize en 10 s» | El plugin no lee stdin o escribe otra cosa en stdout. |
| «Se cerró 3 veces en 10 minutos» | Excepción no controlada en el plugin. Revisa `[plugin:<id>] stderr` en el registro. |
| «El paquete no está firmado» | Fírmalo, o activa el modo desarrollador para probar. |
| «Requiere LiveStream X» | `minAppVersion` es mayor que la versión instalada. |
