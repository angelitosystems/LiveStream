# Arquitectura de extensiones

LiveStream separa un **núcleo** pequeño de las **funcionalidades**, que se cargan como extensiones. Hay dos niveles,
con el mismo modelo de activación pero distinto aislamiento:

| Nivel | Dónde corre | Para qué | Instalar / actualizar | Activar / desactivar |
|---|---|---|---|---|
| **Extensiones integradas** (`src/LiveStream.Extensions`) | En el proceso de la app, aisladas por el `ExtensionHost` | Funcionalidades propias que comparten el motor y el proyecto | Llegan con la app | Herramientas → Extensiones y plugins (filtro «Integradas») |
| **Plugins de terceros** (`.lsplugin`, [SDK](plugins/SDK.md)) | Cada uno en **su propio proceso** (JSON-RPC) | Código externo, firmado | Herramientas → Extensiones y plugins | La misma ventana |

## Qué queda en el núcleo

`LiveStream.Application` + `LiveStream.Native` + `native/livestream-bridge`:

- motor de transmisión y libobs (`IStreamingEngine`, puente nativo);
- composición: escenas, fuentes, filtros, transiciones y la capa de gráficos (`GraphicsService`);
- salidas: grabación y el supervisor de destinos RTMP (`StreamSupervisor`), que el asistente y el optimizador usan;
- servicios compartidos: ejecutor de comandos con deshacer, persistencia, credenciales, registro, `StudioSession`.

El núcleo **no referencia** ninguna funcionalidad: `LiveStream.Extensions` depende de `LiveStream.Application`, nunca al
revés. La composición (qué extensiones hay) se decide en `App.axaml.cs`.

## Extensiones integradas

| Id | Nombre | Depende de | Permisos | Publica | Escucha en el bus |
|---|---|---|---|---|---|
| `sound-bank` | Banco de sonidos | — | audio, red | `SoundBankService` | — |
| `alerts` | Alertas | `sound-bank` | proyecto, audio | `AlertQueue` | `PlatformAlertReceived`, `AlertRequested` |
| `chat` | Chat multicanal | — | proyecto, credenciales, red | `IntegrationManager`, `UnifiedChatService`, `ChatOverlayService` | — (publica los tres eventos) |
| `tts` | Lectura de chat (TTS) | — | audio | `ITtsProvider`, `TtsQueueService`, `ChatTtsCoordinator` | `ChatMessageReceived` |
| `donations` | Donaciones y recompensas | `alerts`, `tts` | — | `MonetaryAlertService`, `ChannelPointsService` | `PlatformAlertReceived` |
| `dual-stream` | Emisión dual | — | proyecto | `DualStreamCoordinator` | — |

Los títulos y rótulos dinámicos son plugins oficiales que se distribuyen por el marketplace y corren fuera de proceso
(rotador de titulares, rótulo de escena, marcador): usan la capa de gráficos del núcleo mediante las herramientas del
asistente. Su código no está en este repositorio público, sino en el privado del marketplace.

## Contrato (`LiveStream.Application.Extensions`)

- **`ExtensionManifest`**: id, nombre, versión, descripción, dependencias, permisos y si se puede desactivar.
- **`IStudioExtension`**: ciclo de vida `Register` → `StartAsync` ⇄ `StopAsync` → `DisposeAsync`.
  - `Register` crea y **publica** sus servicios, sin efectos externos (ni conexiones ni temporizadores).
  - `StartAsync` se suscribe a eventos y arranca lo que consume recursos; `StopAsync` lo deshace todo.
    Una extensión desactivada **no consume CPU** (sin manejadores ni temporizadores), pero sus servicios siguen
    registrados para que la interfaz muestre y edite su configuración.
- **`IExtensionContext`**: sesión del estudio (permiso `studio`), credenciales (permiso `secrets`), bus, ajustes propios y
  registro con categoría `ext:<id>`. Usar un servicio sensible sin declarar el permiso lanza `ExtensionPermissionException`.
- **`IExtensionServices`**: `Publish<T>` y `Require<T>`. Solo se puede consumir lo que publica una **dependencia
  declarada**, así el manifiesto describe de verdad el acoplamiento.
- **`StudioExtensionBase`**: base opcional; lo registrado con `Track(...)` / `On<T>(...)` se deshace al detener.
- **`StudioEventBus`** y los eventos de `StudioEvents.cs` (`ChatMessageReceived`, `PlatformAlertReceived`,
  `AlertRequested`). Son contratos estables: se añaden campos opcionales, no se cambian los existentes.

## Aislamiento de errores

| Fallo | Qué pasa |
|---|---|
| Excepción en `Register` | La extensión queda en **Error**; sus dependientes, **En espera**; el resto arranca. Su panel queda vacío. |
| Excepción en `StartAsync` | **Error** y se deshace lo que llegó a arrancar; los dependientes esperan. |
| Un manejador del bus lanza | No llega a quien publica ni a otros suscriptores; se registra. A los **3 errores en 10 min** el host la detiene. |
| Dependencia ausente o circular | **En espera** / **Error**, sin bloquear el arranque. |
| Plugin de terceros que se cuelga o cierra | Su proceso muere solo; `PluginHost` lo reinicia (3 cierres en 10 min → se detiene). |

Lo que llega de conectores y temporizadores (hilos del pool) se lleva al hilo de UI con `StudioSession.OnOwnerThread`
antes de tocar el proyecto.

**Límite conocido:** las extensiones integradas comparten proceso. Una excepción .NET queda aislada, pero un fallo
nativo dentro de una de ellas (por ejemplo, SAPI en `tts`) tumbaría la app. Lo que es código de terceros va fuera de
proceso. Mover TTS a un proceso propio es el siguiente candidato si aparecen cierres nativos en SAPI.

## Interfaz

Una sola ventana, **Herramientas → Extensiones y plugins**, lista las extensiones integradas y los plugins con el mismo
diseño: estado, versión, permisos y dependencias. Desactivar una extensión que tiene dependientes pide confirmación.
Mientras está desactivada, con error o esperando una dependencia, **su interfaz desaparece**: el dock no dibuja su
panel (chat, alertas) y se ocultan sus entradas de menú. Al reactivarla, el panel vuelve al sitio que tenía, porque la
distribución guardada lo conserva (`StudioDockHost.SetPanelHidden`).

## Persistencia y compatibilidad

- Activación y ajustes por equipo en `%APPDATA%\LiveStream\extensions.json` (`JsonExtensionStateStore`). Todas empiezan
  activas: una instalación anterior se comporta igual que antes.
- Los datos de cada funcionalidad siguen donde estaban (reglas de alertas, estilo del chat y composición dual dentro
  del `.spproj`), así que los proyectos existentes se abren sin migración.

## Puente nativo: tareas de UI de libobs

`bridge.cpp` registra `obs_set_ui_task_handler` justo después de `obs_startup` y antes de cargar los módulos. Las tareas
`OBS_TASK_UI` se ejecutan en orden en un hilo propio (COM STA) y el manejador sigue vivo durante `obs_shutdown`: el hilo
se detiene después, tras ejecutar lo pendiente. Una tarea con `wait` encolada desde ese mismo hilo, o durante el
apagado, se ejecuta en línea para no bloquearse. No se reenvían a .NET porque algunas llegan mientras el runtime se
cierra. Antes, libobs las descartaba con el error `UI task could not be queued`.

También se conserva la corrección de `draw_solid`: tras `gs_render_stop` se descarga el vertex buffer
(`gs_load_vertexbuffer(nullptr)`). Sin esto, d3d11 se queda con un puntero colgante y la conversión de vídeo de la cámara
cierra la app.

## Escribir una extensión integrada

```csharp
public sealed class MiExtension : StudioExtensionBase
{
    public override ExtensionManifest Manifest { get; } = Describe("mi-extension", "Mi extensión",
        "Qué hace.", [AlertsExtension.Id], ExtensionPermissions.Studio);

    private MiServicio? _servicio;

    protected override void OnRegister(IExtensionServices services)
    {
        _servicio = new MiServicio(services.Require<AlertQueue>());
        services.Publish(_servicio);
    }

    protected override Task OnStartAsync(CancellationToken ct)
    {
        On<ChatMessageReceived>(e => _servicio!.Procesar(e.Message));
        return Task.CompletedTask;
    }
}
```

Añádela en `BuiltInExtensions.Create()` y la interfaz la resuelve con `session.Extensions.Get<MiServicio>()`.

## Pruebas

- `tests/LiveStream.UnitTests/ExtensionTests.cs`:
  - el host: orden por dependencias, fallos al registrar o arrancar, ciclos, desactivación en cascada persistida,
    parada automática tras errores repetidos, permisos y dependencias no declaradas;
  - flujos reales: un evento de plataforma llega a alertas y donaciones, y el chat llega a TTS solo con `tts` activa.
- `tests/LiveStream.IntegrationTests`: ciclo completo contra libobs real (multistream a dos servidores RTMP locales con
  ffmpeg) y comprobación de que libobs no descarta tareas de UI.
