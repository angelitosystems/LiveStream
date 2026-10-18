using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamPilot.Application.Audio;
using StreamPilot.Application.Commands;
using StreamPilot.Application.Filters;
using StreamPilot.Application.Graphics;
using StreamPilot.Application.Scenes;
using StreamPilot.Application.Sources;
using StreamPilot.Application.Studio;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.Graphics;
using StreamPilot.Domain.ValueObjects;
using static StreamPilot.Application.Assistant.ToolSchema;

namespace StreamPilot.Application.Assistant;

/// <summary>Qué puede hacer una herramienta y si necesita el visto bueno del usuario.</summary>
public enum ToolPermission
{
    /// <summary>Solo lee el estado del estudio.</summary>
    Read,
    /// <summary>Cambia el estudio con un comando (se puede deshacer).</summary>
    Modify,
    /// <summary>Emite en público, corta una emisión o elimina datos: pide confirmación siempre.</summary>
    Confirm,
}

public sealed record AssistantTool(string Name, string Description, JsonObject InputSchema, ToolPermission Permission);

/// <summary>Resultado de una herramienta. Content vuelve al modelo; Summary es la línea que ve el usuario.</summary>
public sealed record AssistantToolResult(string Content, bool IsError, string Summary);

/// <summary>Petición de confirmación al usuario para una acción sensible.</summary>
public sealed record AssistantConfirmation(string Title, string Message, string ConfirmText);

/// <summary>Salidas en directo. Las implementa la interfaz para que el asistente use exactamente el mismo camino.</summary>
public interface IOutputController
{
    bool IsStreaming { get; }
    bool IsRecording { get; }
    Task<string> StartRecordingAsync(CancellationToken ct);
    Task StopRecordingAsync(CancellationToken ct);
    Task StartStreamAsync(CancellationToken ct);
    Task StopStreamAsync(CancellationToken ct);
}

/// <summary>
/// Catálogo de herramientas tipadas del asistente. Todo pasa por el <see cref="CommandExecutor"/> (validado,
/// serializado, con deshacer) o por los servicios de la sesión: el modelo nunca toca el motor, archivos,
/// secretos ni rutas. Cada argumento se valida contra su esquema antes de ejecutar.
/// </summary>
public sealed class AssistantToolbox
{
    private readonly StudioSession _session;
    private readonly IOutputController _outputs;
    private readonly Func<AssistantConfirmation, Task<bool>> _confirm;
    private readonly Dictionary<string, (AssistantTool Tool, Func<JsonElement, CancellationToken, Task<AssistantToolResult>> Run)> _tools = [];

    private static readonly Dictionary<string, SourceKind> Kinds = new()
    {
        ["display_capture"] = SourceKind.DisplayCapture,
        ["window_capture"] = SourceKind.WindowCapture,
        ["game_capture"] = SourceKind.GameCapture,
        ["camera"] = SourceKind.VideoCaptureDevice,
        ["microphone"] = SourceKind.AudioInput,
        ["desktop_audio"] = SourceKind.AudioOutput,
        ["text"] = SourceKind.Text,
        ["color"] = SourceKind.ColorSource,
    };

    private static readonly Dictionary<string, string> AudioFilters = new()
    {
        ["noise_suppression"] = "noise_suppress_filter_v2",
        ["noise_gate"] = "noise_gate_filter",
        ["compressor"] = "compressor_filter",
        ["limiter"] = "limiter_filter",
        ["gain"] = "gain_filter",
    };

    private static readonly Dictionary<string, GraphicKind> GraphicKinds = new()
    {
        ["lower_third"] = GraphicKind.LowerThird,
        ["headline"] = GraphicKind.Headline,
        ["ticker"] = GraphicKind.Ticker,
        ["countdown"] = GraphicKind.Countdown,
        ["clock"] = GraphicKind.Clock,
        ["scoreboard"] = GraphicKind.Scoreboard,
        ["live_badge"] = GraphicKind.LiveBadge,
        ["alert"] = GraphicKind.Alert,
    };

    private readonly Func<string>? _performanceReport;

    /// <param name="performanceReport">Informe del optimizador (Fase 5); null si no hay optimizador.</param>
    public AssistantToolbox(StudioSession session, IOutputController outputs, Func<AssistantConfirmation, Task<bool>> confirm,
        Func<string>? performanceReport = null)
    {
        _performanceReport = performanceReport;
        _session = session;
        _outputs = outputs;
        _confirm = confirm;
        Register();
    }

    private StudioProject Project => _session.Project;
    private CommandExecutor Executor => _session.Executor;

    /// <summary>JSON legible para el modelo (acentos sin escapar).</summary>
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };

    public IReadOnlyList<AssistantTool> Tools => _tools.Values.Select(t => t.Tool).ToList();

    public async Task<AssistantToolResult> ExecuteAsync(string name, JsonElement input, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var entry))
            return Fail($"No existe la herramienta {name}.");
        if (ToolSchema.Validate(entry.Tool.InputSchema, input) is { } invalid)
            return Fail($"Argumentos no válidos: {invalid}");
        try
        {
            return await entry.Run(input, ct).ConfigureAwait(false);
        }
        catch (ToolException ex)
        {
            return Fail(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"Error al ejecutar {name}: {ex.Message}");
        }
    }

    // ---- Registro ---------------------------------------------------------------------------------

    private void Add(string name, string description, JsonObject schema, ToolPermission permission,
        Func<JsonElement, CancellationToken, Task<AssistantToolResult>> run) =>
        _tools[name] = (new AssistantTool(name, description, schema, permission), run);

    private void Register()
    {
        const string sceneRef = "Nombre (o id) de la escena.";
        const string itemRef = "Nombre de la fuente dentro de la escena.";

        // Lectura
        Add("get_studio_state",
            "Estado actual del estudio: escenas y sus fuentes (con posición), audio, gráficos, transición, lienzo y si se está grabando o emitiendo. Consúltalo antes de modificar algo si no conoces los nombres exactos.",
            Object(), ToolPermission.Read, (_, _) => Task.FromResult(Ok(StudioState(), "Consultó el estado del estudio")));
        Add("get_performance_report",
            "Rendimiento en directo: bitrate de emisión y objetivo, pérdidas de red, congestión, retraso de render, fotogramas descartados al codificar y las últimas decisiones del optimizador con su motivo. Úsalo para explicar problemas de calidad; los ajustes los decide el optimizador o el usuario.",
            Object(), ToolPermission.Read,
            (_, _) => Task.FromResult(_performanceReport is null
                ? Fail("El optimizador no está disponible.")
                : Ok(_performanceReport(), "Consultó el rendimiento")));
        Add("list_capture_devices", "Lista cámaras, micrófonos, salidas de audio y monitores disponibles, con su id.",
            Object(), ToolPermission.Read, ListDevicesAsync);

        // Escenas
        Add("create_scene", "Crea una escena vacía.",
            Object(("name", Str("Nombre de la escena nueva.", maxLength: 80), true)), ToolPermission.Modify, CreateSceneAsync);
        Add("rename_scene", "Cambia el nombre de una escena.",
            Object(("scene", Str(sceneRef), true), ("new_name", Str("Nombre nuevo.", maxLength: 80), true)), ToolPermission.Modify,
            async (a, ct) =>
            {
                var scene = FindScene(a.Str("scene"));
                await Run(new RenameSceneCommand(scene.Id, a.Str("new_name")), ct);
                return Ok($"Escena renombrada a «{a.Str("new_name")}».", $"Renombró la escena «{scene.Name}»");
            });
        Add("duplicate_scene", "Duplica una escena con todas sus fuentes (las fuentes se comparten, no se duplican las capturas).",
            Object(("scene", Str(sceneRef), true)), ToolPermission.Modify,
            async (a, ct) =>
            {
                var scene = FindScene(a.Str("scene"));
                var command = new DuplicateSceneCommand(scene.Id);
                await Run(command, ct);
                var copy = Project.GetScene(command.CreatedSceneId!.Value);
                return Ok($"Creada la escena «{copy.Name}».", $"Duplicó la escena «{scene.Name}»");
            });
        Add("delete_scene", "Elimina una escena (no puede ser la que está en programa). Pide confirmación al usuario.",
            Object(("scene", Str(sceneRef), true)), ToolPermission.Confirm,
            async (a, ct) =>
            {
                var scene = FindScene(a.Str("scene"));
                if (!await Ask("Eliminar escena", $"El asistente quiere eliminar la escena «{scene.Name}». Se puede deshacer.", "Eliminar"))
                    return Declined($"eliminar la escena «{scene.Name}»");
                await Run(new RemoveSceneCommand(scene.Id), ct);
                return Ok($"Escena «{scene.Name}» eliminada.", $"Eliminó la escena «{scene.Name}»");
            });
        Add("switch_program_scene",
            "Pone una escena en programa (lo que se graba y emite) con la transición configurada. En modo estudio también la deja en programa directamente.",
            Object(("scene", Str(sceneRef), true)), ToolPermission.Modify,
            async (a, ct) =>
            {
                var scene = FindScene(a.Str("scene"));
                await Run(new ActivateSceneCommand(scene.Id), ct);
                return Ok($"«{scene.Name}» está en programa.", $"Puso «{scene.Name}» en programa");
            });

        // Fuentes
        Add("add_source",
            "Añade una fuente nueva a una escena, encima del resto. Para cámara, micrófono, audio de salida o pantalla puedes indicar device_id (de list_capture_devices); si no, se usa el dispositivo por defecto. No se pueden añadir archivos (imágenes, videos): eso lo hace el usuario.",
            Object(
                ("scene", Str(sceneRef), true),
                ("kind", Str("Tipo de fuente.", Kinds.Keys), true),
                ("name", Str("Nombre de la fuente.", maxLength: 80), true),
                ("device_id", Str("Id del dispositivo (opcional)."), false),
                ("text", Str("Texto inicial (solo kind=text).", maxLength: 2000), false),
                ("color", Str("Color #RRGGBB o #AARRGGBB (solo kind=color).", maxLength: 9), false),
                ("fit_to_canvas", Bool("Ajustarla al lienzo conservando su proporción (por defecto true para capturas y cámara)."), false)),
            ToolPermission.Modify, AddSourceAsync);
        Add("remove_source", "Quita una fuente de una escena. Pide confirmación al usuario.",
            Object(("scene", Str(sceneRef), true), ("source", Str(itemRef), true)), ToolPermission.Confirm,
            async (a, ct) =>
            {
                var (scene, item, source) = FindItem(a.Str("scene"), a.Str("source"));
                if (!await Ask("Quitar fuente", $"El asistente quiere quitar «{source.Name}» de la escena «{scene.Name}». Se puede deshacer.", "Quitar"))
                    return Declined($"quitar «{source.Name}»");
                await Run(new RemoveItemCommand(item.Id), ct);
                return Ok($"«{source.Name}» quitada de «{scene.Name}».", $"Quitó «{source.Name}» de «{scene.Name}»");
            });
        Add("set_source_visibility", "Muestra u oculta una fuente en una escena.",
            Object(("scene", Str(sceneRef), true), ("source", Str(itemRef), true), ("visible", Bool("true para mostrarla."), true)),
            ToolPermission.Modify,
            async (a, ct) =>
            {
                var (scene, item, source) = FindItem(a.Str("scene"), a.Str("source"));
                var visible = a.Bool("visible");
                await Run(new SetItemVisibleCommand(item.Id, visible), ct);
                return Ok($"«{source.Name}» {(visible ? "visible" : "oculta")} en «{scene.Name}».",
                    $"{(visible ? "Mostró" : "Ocultó")} «{source.Name}»");
            });
        Add("transform_source",
            "Mueve, escala o gira una fuente. Coordenadas en píxeles del lienzo desde la esquina superior izquierda; escala 1 = tamaño original. Solo cambia lo que indiques.",
            Object(
                ("scene", Str(sceneRef), true), ("source", Str(itemRef), true),
                ("x", Num("Posición X en píxeles.", -20000, 20000), false),
                ("y", Num("Posición Y en píxeles.", -20000, 20000), false),
                ("scale", Num("Escala uniforme.", 0.01, 20), false),
                ("rotation", Num("Giro en grados.", -360, 360), false)),
            ToolPermission.Modify, TransformSourceAsync);
        Add("fit_source_to_canvas", "Ajusta una fuente para que ocupe el lienzo conservando su proporción, centrada.",
            Object(("scene", Str(sceneRef), true), ("source", Str(itemRef), true)), ToolPermission.Modify,
            async (a, ct) =>
            {
                var (scene, item, source) = FindItem(a.Str("scene"), a.Str("source"));
                var (w, h) = await _session.Engine.GetSourceSizeAsync(source.Id, ct);
                if (w <= 0 || h <= 0)
                    throw new ToolException($"«{source.Name}» aún no tiene tamaño (¿está la captura activa?).");
                await Run(new SetItemTransformCommand(item.Id, StudioSession.FitToCanvas(w, h, Project.Canvas)), ct);
                return Ok($"«{source.Name}» ajustada al lienzo ({w}x{h}).", $"Ajustó «{source.Name}» al lienzo");
            });
        Add("set_source_layer", "Cambia el orden de capas de una fuente en su escena.",
            Object(("scene", Str(sceneRef), true), ("source", Str(itemRef), true),
                ("position", Str("Dónde ponerla.", ["top", "bottom", "up", "down"]), true)),
            ToolPermission.Modify,
            async (a, ct) =>
            {
                var (scene, item, source) = FindItem(a.Str("scene"), a.Str("source"));
                var index = scene.IndexOf(item.Id);
                var target = a.Str("position") switch
                {
                    "top" => scene.Items.Count - 1,
                    "bottom" => 0,
                    "up" => Math.Min(scene.Items.Count - 1, index + 1),
                    _ => Math.Max(0, index - 1),
                };
                await Run(new MoveItemCommand(item.Id, target), ct);
                return Ok($"«{source.Name}» en la capa {target + 1} de {scene.Items.Count}.", $"Reordenó «{source.Name}»");
            });
        Add("set_text", "Cambia el texto de una fuente de texto (afecta a todas las escenas donde aparece).",
            Object(("source", Str("Nombre de la fuente de texto."), true), ("text", Str("Texto nuevo.", maxLength: 2000), true)),
            ToolPermission.Modify,
            async (a, ct) =>
            {
                var source = FindSource(a.Str("source"));
                if (source.Kind != SourceKind.Text)
                    throw new ToolException($"«{source.Name}» no es una fuente de texto.");
                var settings = JsonNode.Parse(source.SettingsJson) as JsonObject ?? [];
                settings["text"] = a.Str("text");
                await Run(new UpdateSourceSettingsCommand(source.Id, settings.ToJsonString()), ct);
                return Ok($"Texto de «{source.Name}» actualizado.", $"Cambió el texto de «{source.Name}»");
            });

        // Audio
        Add("set_volume", "Ajusta el volumen de una fuente de audio en dB (0 = sin cambio, -60 = casi silencio, máximo +20).",
            Object(("source", Str("Nombre de la fuente."), true), ("db", Num("Volumen en dB.", -60, 20), true)), ToolPermission.Modify,
            async (a, ct) =>
            {
                var source = FindSource(a.Str("source"));
                var db = a.Num("db");
                await Run(new SetVolumeCommand(source.Id, db <= -60 ? 0 : SetVolumeCommand.DbToMultiplier(db)), ct);
                return Ok($"Volumen de «{source.Name}»: {db:0.#} dB.", $"Ajustó el volumen de «{source.Name}» a {db:0.#} dB");
            });
        Add("set_mute", "Silencia o activa una fuente de audio.",
            Object(("source", Str("Nombre de la fuente."), true), ("muted", Bool("true para silenciarla."), true)), ToolPermission.Modify,
            async (a, ct) =>
            {
                var source = FindSource(a.Str("source"));
                await Run(new SetMutedCommand(source.Id, a.Bool("muted")), ct);
                return Ok($"«{source.Name}» {(a.Bool("muted") ? "silenciada" : "con sonido")}.",
                    $"{(a.Bool("muted") ? "Silenció" : "Activó el sonido de")} «{source.Name}»");
            });
        Add("add_audio_filter",
            "Añade un filtro de audio con valores por defecto razonables al final de la cadena de una fuente (p. ej. supresión de ruido para un micrófono).",
            Object(("source", Str("Nombre de la fuente de audio."), true), ("filter", Str("Filtro.", AudioFilters.Keys), true)),
            ToolPermission.Modify,
            async (a, ct) =>
            {
                var source = FindSource(a.Str("source"));
                var entry = FilterCatalog.Find(AudioFilters[a.Str("filter")])!;
                await Run(new AddFilterCommand(source.Id, entry.TypeId, entry.Name), ct);
                return Ok($"Filtro «{entry.Name}» añadido a «{source.Name}».", $"Añadió «{entry.Name}» a «{source.Name}»");
            });

        // Transiciones
        Add("set_transition",
            "Cambia la transición general entre escenas. El stinger necesita un video y lo configura el usuario.",
            Object(
                ("type", Str("Tipo de transición.", ["cut", "fade", "swipe", "slide", "fade_to_color", "luma_wipe"]), true),
                ("duration_ms", Int("Duración en milisegundos.", 0, 20000), true),
                ("direction", Str("Dirección (swipe y slide).", TransitionCatalog.Directions), false)),
            ToolPermission.Modify, SetTransitionAsync);

        // Gráficos
        Add("create_graphic",
            "Crea un gráfico de TV en la capa superpuesta (queda oculto hasta show_graphic). Campos por tipo: lower_third(name,title), headline(label,headline), ticker(label,items con una noticia por línea,speed), countdown(label,duration mm:ss,finished), clock(format), scoreboard(home,away,homeScore,awayScore,period,clock), live_badge(text), alert(title,message).",
            Object(
                ("kind", Str("Tipo de gráfico.", GraphicKinds.Keys), true),
                ("name", Str("Nombre del gráfico en el panel.", maxLength: 80), false),
                ("fields", Fields("Campos a rellenar; los omitidos usan el valor por defecto."), false)),
            ToolPermission.Modify, CreateGraphicAsync);
        Add("update_graphic", "Cambia campos de un gráfico; si está al aire se actualiza en directo.",
            Object(("graphic", Str("Nombre del gráfico."), true), ("fields", Fields("Campos a cambiar."), true)), ToolPermission.Modify,
            async (a, ct) =>
            {
                var graphic = FindGraphic(a.Str("graphic"));
                var fields = new Dictionary<string, string>(graphic.Fields);
                foreach (var (key, value) in ReadFields(a, graphic.Kind))
                    fields[key] = value;
                await Run(new UpdateGraphicCommand(graphic with { Fields = fields }), ct);
                return Ok($"Gráfico «{graphic.Name}» actualizado: {Describe(fields)}.", $"Actualizó el gráfico «{graphic.Name}»");
            });
        Add("show_graphic", "Lanza un gráfico al aire con su animación de entrada.",
            Object(("graphic", Str("Nombre del gráfico."), true)), ToolPermission.Modify,
            async (a, ct) =>
            {
                var graphic = FindGraphic(a.Str("graphic"));
                await _session.Graphics.TakeInAsync(graphic.Id, ct);
                return Ok($"«{graphic.Name}» al aire.", $"Lanzó «{graphic.Name}» al aire");
            });
        Add("hide_graphic", "Retira un gráfico del aire con su animación de salida.",
            Object(("graphic", Str("Nombre del gráfico."), true)), ToolPermission.Modify,
            async (a, ct) =>
            {
                var graphic = FindGraphic(a.Str("graphic"));
                await _session.Graphics.TakeOutAsync(graphic.Id, ct);
                return Ok($"«{graphic.Name}» retirado.", $"Retiró «{graphic.Name}»");
            });
        Add("delete_graphic", "Elimina un gráfico. Pide confirmación al usuario.",
            Object(("graphic", Str("Nombre del gráfico."), true)), ToolPermission.Confirm,
            async (a, ct) =>
            {
                var graphic = FindGraphic(a.Str("graphic"));
                if (!await Ask("Eliminar gráfico", $"El asistente quiere eliminar el gráfico «{graphic.Name}». Se puede deshacer.", "Eliminar"))
                    return Declined($"eliminar «{graphic.Name}»");
                await Run(new RemoveGraphicCommand(graphic.Id), ct);
                return Ok($"Gráfico «{graphic.Name}» eliminado.", $"Eliminó el gráfico «{graphic.Name}»");
            });
        Add("control_countdown", "Inicia, pausa o reinicia una cuenta atrás.",
            Object(("graphic", Str("Nombre de la cuenta atrás."), true), ("action", Str("Acción.", ["start", "pause", "reset"]), true)),
            ToolPermission.Modify,
            (a, _) =>
            {
                var graphic = FindGraphic(a.Str("graphic"));
                if (graphic.Kind != GraphicKind.Countdown)
                    throw new ToolException($"«{graphic.Name}» no es una cuenta atrás.");
                switch (a.Str("action"))
                {
                    case "start": _session.Graphics.StartCountdown(graphic.Id); break;
                    case "pause": _session.Graphics.PauseCountdown(graphic.Id); break;
                    default: _session.Graphics.ResetCountdown(graphic.Id); break;
                }
                var status = _session.Graphics.GetCountdown(graphic.Id);
                return Task.FromResult(Ok($"Cuenta atrás «{graphic.Name}»: {GraphicTemplates.FormatRemaining(status.Remaining)}{(status.Running ? " en marcha" : " parada")}.",
                    $"Cuenta atrás «{graphic.Name}»: {a.Str("action")}"));
            });

        // Salidas
        Add("start_recording", "Empieza a grabar el programa en la carpeta de grabaciones del usuario.", Object(), ToolPermission.Modify,
            async (_, ct) =>
            {
                if (_outputs.IsRecording)
                    return Ok("Ya se está grabando.", "Comprobó que ya se graba");
                var path = await _outputs.StartRecordingAsync(ct);
                return Ok($"Grabando en {Path.GetFileName(path)}.", "Empezó a grabar");
            });
        Add("stop_recording", "Detiene la grabación.", Object(), ToolPermission.Modify,
            async (_, ct) =>
            {
                if (!_outputs.IsRecording)
                    return Ok("No había ninguna grabación en curso.", "Comprobó que no se graba");
                await _outputs.StopRecordingAsync(ct);
                return Ok("Grabación detenida y guardada.", "Detuvo la grabación");
            });
        Add("start_streaming", "Empieza la transmisión en directo al destino configurado por el usuario. Pide confirmación al usuario.",
            Object(), ToolPermission.Confirm,
            async (_, ct) =>
            {
                if (_outputs.IsStreaming)
                    return Ok("Ya se está emitiendo.", "Comprobó que ya se emite");
                if (await _session.ValidateStreamAsync(ct) is { } problem)
                    throw new ToolException(problem);
                if (!await Ask("Iniciar transmisión", "El asistente quiere empezar a emitir en directo con la escena de programa actual.", "Transmitir"))
                    return Declined("iniciar la transmisión");
                await _outputs.StartStreamAsync(ct);
                return Ok("Transmisión iniciada.", "Inició la transmisión");
            });
        Add("stop_streaming", "Detiene la transmisión en directo. Pide confirmación al usuario.", Object(), ToolPermission.Confirm,
            async (_, ct) =>
            {
                if (!_outputs.IsStreaming)
                    return Ok("No se está emitiendo.", "Comprobó que no se emite");
                if (!await Ask("Detener transmisión", "El asistente quiere detener la transmisión en directo.", "Detener"))
                    return Declined("detener la transmisión");
                await _outputs.StopStreamAsync(ct);
                return Ok("Transmisión detenida.", "Detuvo la transmisión");
            });

        Add("undo_last_change", "Deshace el último cambio del estudio (hecho por el usuario o por ti).", Object(), ToolPermission.Modify,
            async (_, ct) =>
            {
                var description = Executor.UndoDescription ?? throw new ToolException("No hay nada que deshacer.");
                var result = await Executor.UndoAsync(ct);
                if (!result.Success)
                    throw new ToolException(result.Error ?? "No se pudo deshacer.");
                return Ok($"Deshecho: {description}.", $"Deshizo: {description}");
            });
    }

    // ---- Implementaciones ------------------------------------------------------------------------

    private async Task<AssistantToolResult> ListDevicesAsync(JsonElement _, CancellationToken ct)
    {
        if (!_session.Engine.IsInitialized)
            throw new ToolException("El motor no está iniciado.");
        var devices = await _session.Engine.ListDevicesAsync(ct);
        var json = new JsonArray(devices.Select(d => (JsonNode)new JsonObject
        {
            ["kind"] = d.Kind.ToString(),
            ["name"] = d.Name.TrimStart(':', ' '),
            ["device_id"] = d.Id,
        }).ToArray());
        return Ok(json.ToJsonString(Json), $"Consultó los dispositivos ({devices.Count})");
    }

    private async Task<AssistantToolResult> CreateSceneAsync(JsonElement a, CancellationToken ct)
    {
        var command = new CreateSceneCommand(a.Str("name"));
        await Run(command, ct);
        return Ok($"Escena «{a.Str("name")}» creada (vacía).", $"Creó la escena «{a.Str("name")}»");
    }

    private async Task<AssistantToolResult> AddSourceAsync(JsonElement a, CancellationToken ct)
    {
        var scene = FindScene(a.Str("scene"));
        var kind = Kinds[a.Str("kind")];
        var settings = new JsonObject();
        if (a.OptStr("device_id") is { } device)
        {
            var key = kind switch
            {
                SourceKind.VideoCaptureDevice => "video_device_id",
                SourceKind.AudioInput or SourceKind.AudioOutput => "device_id",
                SourceKind.DisplayCapture => "monitor_id",
                _ => throw new ToolException("device_id solo vale para cámara, micrófono, audio de salida y pantalla."),
            };
            settings[key] = device;
        }
        else if (kind is SourceKind.AudioInput or SourceKind.AudioOutput)
        {
            settings["device_id"] = "default";
        }
        if (kind == SourceKind.Text)
            settings["text"] = a.OptStr("text") ?? a.Str("name");
        if (kind == SourceKind.ColorSource)
        {
            var color = a.OptStr("color") ?? "#FF1E2126";
            settings["color"] = (long)GraphicColorOrThrow(color);
            settings["width"] = Project.Canvas.Width;
            settings["height"] = Project.Canvas.Height;
        }

        var command = new AddSourceCommand(scene.Id, a.Str("name"), kind, settings.ToJsonString());
        await Run(command, ct);
        var fit = a.OptBool("fit_to_canvas") ?? kind is SourceKind.DisplayCapture or SourceKind.WindowCapture or SourceKind.GameCapture or SourceKind.VideoCaptureDevice;
        var note = "";
        if (fit && _session.Engine.IsInitialized && command.SourceId is { } sourceId)
        {
            // Las capturas tardan unos fotogramas en conocer su tamaño.
            for (var i = 0; i < 10; i++)
            {
                var (w, h) = await _session.Engine.GetSourceSizeAsync(sourceId, ct);
                if (w > 0 && h > 0)
                {
                    await Run(new SetItemTransformCommand(command.ItemId!.Value, StudioSession.FitToCanvas(w, h, Project.Canvas)), ct);
                    note = $" Ajustada al lienzo ({w}x{h}).";
                    break;
                }
                await Task.Delay(100, ct);
            }
            if (note.Length == 0)
                note = " Aún no tiene tamaño (la captura no ha enviado imagen); no se ajustó.";
        }
        return Ok($"Fuente «{a.Str("name")}» ({a.Str("kind")}) añadida a «{scene.Name}».{note}", $"Añadió «{a.Str("name")}» a «{scene.Name}»");
    }

    private async Task<AssistantToolResult> TransformSourceAsync(JsonElement a, CancellationToken ct)
    {
        var (_, item, source) = FindItem(a.Str("scene"), a.Str("source"));
        var t = item.Transform;
        var scale = a.OptNum("scale");
        var updated = t with
        {
            X = a.OptNum("x") ?? t.X,
            Y = a.OptNum("y") ?? t.Y,
            ScaleX = scale ?? t.ScaleX,
            ScaleY = scale ?? t.ScaleY,
            RotationDegrees = a.OptNum("rotation") ?? t.RotationDegrees,
        };
        await Run(new SetItemTransformCommand(item.Id, updated), ct);
        return Ok($"«{source.Name}»: x={updated.X:0}, y={updated.Y:0}, escala={updated.ScaleX:0.###}, giro={updated.RotationDegrees:0}°.",
            $"Transformó «{source.Name}»");
    }

    private async Task<AssistantToolResult> SetTransitionAsync(JsonElement a, CancellationToken ct)
    {
        var type = a.Str("type") switch
        {
            "cut" => TransitionCatalog.Cut,
            "fade" => TransitionCatalog.Fade,
            "swipe" => TransitionCatalog.Swipe,
            "slide" => TransitionCatalog.Slide,
            "fade_to_color" => TransitionCatalog.FadeToColor,
            _ => TransitionCatalog.LumaWipe,
        };
        var settings = new JsonObject();
        if (a.OptStr("direction") is { } direction)
        {
            if (type is not (TransitionCatalog.Swipe or TransitionCatalog.Slide))
                throw new ToolException("direction solo vale para swipe y slide.");
            settings["direction"] = direction;
        }
        if (type == TransitionCatalog.LumaWipe)
            settings["luma_image"] = "linear-h.png";
        var transition = new TransitionSettings(type, a.Int("duration_ms"), settings.ToJsonString());
        await Run(new SetTransitionCommand(transition), ct);
        return Ok($"Transición: {TransitionCatalog.Names[type]}, {transition.DurationMs} ms.", $"Cambió la transición a {TransitionCatalog.Names[type]}");
    }

    private async Task<AssistantToolResult> CreateGraphicAsync(JsonElement a, CancellationToken ct)
    {
        var kind = GraphicKinds[a.Str("kind")];
        var name = a.OptStr("name") ?? GraphicTemplates.DisplayName(kind);
        if (Project.HasGraphicNamed(name))
            name = Enumerable.Range(2, 100).Select(i => $"{name} {i}").First(n => !Project.HasGraphicNamed(n));
        var graphic = GraphicTemplates.Create(kind, name, Project.Canvas);
        var fields = new Dictionary<string, string>(graphic.Fields);
        foreach (var (key, value) in ReadFields(a, kind))
            fields[key] = value;
        graphic = graphic with { Fields = fields };
        await Run(new AddGraphicCommand(graphic), ct);
        return Ok($"Gráfico «{name}» creado (oculto): {Describe(fields)}. Usa show_graphic para lanzarlo.", $"Creó el gráfico «{name}»");
    }

    // ---- Estado -----------------------------------------------------------------------------------

    /// <summary>Resumen del estudio para el modelo. Nunca incluye el servidor, la clave de emisión ni rutas de archivos.</summary>
    public string StudioState()
    {
        var p = Project;
        var state = new JsonObject
        {
            ["canvas"] = $"{p.Canvas.Width}x{p.Canvas.Height} @ {p.Canvas.Fps} fps",
            ["streaming"] = _outputs.IsStreaming,
            ["recording"] = _outputs.IsRecording,
            ["studio_mode"] = p.StudioMode,
            ["transition"] = $"{TransitionCatalog.Names.GetValueOrDefault(p.Transition.TypeId, p.Transition.TypeId)} {p.Transition.DurationMs} ms",
            ["can_undo"] = Executor.UndoDescription,
            ["scenes"] = new JsonArray(p.Scenes.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name,
                ["program"] = p.ActiveSceneId == s.Id,
                ["preview"] = p.StudioMode && p.PreviewSceneId == s.Id,
                ["sources_top_to_bottom"] = new JsonArray(s.Items.Reverse().Select(i =>
                {
                    var src = p.GetSource(i.SourceId);
                    return (JsonNode)new JsonObject
                    {
                        ["name"] = src.Name,
                        ["kind"] = KindName(src.Kind),
                        ["visible"] = i.Visible,
                        ["x"] = Math.Round(i.Transform.X),
                        ["y"] = Math.Round(i.Transform.Y),
                        ["scale"] = Math.Round(i.Transform.ScaleX, 3),
                    };
                }).ToArray()),
            }).ToArray()),
            ["audio"] = new JsonArray(p.Sources.Where(s => s.Kind is SourceKind.AudioInput or SourceKind.AudioOutput or SourceKind.Media or SourceKind.VideoCaptureDevice)
                .Select(s => (JsonNode)new JsonObject
                {
                    ["name"] = s.Name,
                    ["volume_db"] = s.Volume <= 0 ? -60 : Math.Round(SetVolumeCommand.MultiplierToDb(s.Volume), 1),
                    ["muted"] = s.Muted,
                    ["filters"] = new JsonArray(s.Filters.Select(f => (JsonNode)$"{f.Name}{(f.Enabled ? "" : " (desactivado)")}").ToArray()),
                }).ToArray()),
            ["graphics"] = new JsonArray(p.Graphics.Select(g => (JsonNode)new JsonObject
            {
                ["name"] = g.Name,
                ["kind"] = GraphicKinds.First(k => k.Value == g.Kind).Key,
                ["on_air"] = _session.Graphics.IsOnAir(g.Id),
                ["fields"] = Describe(g.Fields),
            }).ToArray()),
        };
        return state.ToJsonString(Json);
    }

    private static string KindName(SourceKind kind) =>
        Kinds.FirstOrDefault(k => k.Value == kind).Key ?? kind.ToString().ToLowerInvariant();

    private static string Describe(IReadOnlyDictionary<string, string> fields) =>
        string.Join("; ", fields.Select(f => $"{f.Key}={f.Value.Replace('\n', '|')}"));

    // ---- Resolución de nombres --------------------------------------------------------------------

    private Scene FindScene(string reference) =>
        Resolve(reference, Project.Scenes, s => s.Id, s => s.Name, "escena");

    private Source FindSource(string reference) =>
        Resolve(reference, Project.Sources, s => s.Id, s => s.Name, "fuente");

    private Graphic FindGraphic(string reference) =>
        Resolve(reference, Project.Graphics, g => g.Id, g => g.Name, "gráfico");

    private (Scene Scene, SceneItem Item, Source Source) FindItem(string sceneRef, string sourceRef)
    {
        var scene = FindScene(sceneRef);
        var items = scene.Items.Select(i => (Item: i, Source: Project.GetSource(i.SourceId))).ToList();
        var match = Resolve(sourceRef, items, x => x.Item.Id, x => x.Source.Name, $"fuente de «{scene.Name}»");
        return (scene, match.Item, match.Source);
    }

    /// <summary>Por id, nombre exacto (sin distinguir mayúsculas) o fragmento único del nombre.</summary>
    internal static T Resolve<T>(string reference, IEnumerable<T> items, Func<T, Guid> id, Func<T, string> name, string what)
    {
        var list = items.ToList();
        reference = reference.Trim();
        if (Guid.TryParse(reference, out var guid) && list.FirstOrDefault(i => id(i) == guid) is { } byId)
            return byId;
        // Sin distinguir mayúsculas ni acentos: «micro» encuentra «Micrófono».
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        var exact = list.Where(i => compare.Compare(name(i), reference, loose) == 0).ToList();
        if (exact.Count == 1)
            return exact[0];
        var partial = list.Where(i => compare.IndexOf(name(i), reference, loose) >= 0).ToList();
        if (exact.Count == 0 && partial.Count == 1)
            return partial[0];
        var options = list.Count == 0 ? "no hay ninguna" : string.Join(", ", list.Select(i => $"«{name(i)}»"));
        throw new ToolException(exact.Count + partial.Count > 1
            ? $"Hay varias coincidencias para {what} «{reference}»: {string.Join(", ", (exact.Count > 1 ? exact : partial).Select(i => $"«{name(i)}»"))}."
            : $"No encuentro {what} «{reference}». Opciones: {options}.");
    }

    private static IEnumerable<(string Key, string Value)> ReadFields(JsonElement a, GraphicKind kind)
    {
        if (!a.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            yield break;
        var known = GraphicTemplates.Fields(kind).Select(f => f.Key).ToList();
        foreach (var field in fields.EnumerateArray())
        {
            var key = field.GetProperty("key").GetString()!;
            var match = known.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException($"El campo «{key}» no existe en este tipo de gráfico. Campos: {string.Join(", ", known)}.");
            yield return (match, field.GetProperty("value").GetString()!);
        }
    }

    private static uint GraphicColorOrThrow(string color) =>
        GraphicColor.TryParse(color, out _) ? GraphicColor.ToAbgr(color) : throw new ToolException($"Color no válido: {color}. Usa #RRGGBB.");

    // ---- Ejecución ---------------------------------------------------------------------------------

    private async Task Run(IStudioCommand command, CancellationToken ct)
    {
        var result = await Executor.ExecuteAsync(command, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new ToolException(result.Error ?? "El estudio rechazó el cambio.");
    }

    private Task<bool> Ask(string title, string message, string confirmText) => _confirm(new AssistantConfirmation(title, message, confirmText));

    private static AssistantToolResult Ok(string content, string summary) => new(content, false, summary);

    private static AssistantToolResult Fail(string message) => new(message, true, message);

    private static AssistantToolResult Declined(string what) =>
        new($"El usuario no autorizó {what}. No lo reintentes salvo que te lo pida.", false, $"No autorizado: {what}");

    private sealed class ToolException(string message) : Exception(message);
}

/// <summary>Lectura de argumentos ya validados contra el esquema.</summary>
internal static class ToolArgs
{
    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!.Trim();

    public static string? OptStr(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    public static bool Bool(this JsonElement e, string name) => e.GetProperty(name).GetBoolean();

    public static bool? OptBool(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static double Num(this JsonElement e, string name) => e.GetProperty(name).GetDouble();

    public static double? OptNum(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static int Int(this JsonElement e, string name) => (int)e.GetProperty(name).GetDouble();
}
