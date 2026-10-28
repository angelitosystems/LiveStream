using System.Text.Json;
using LiveStream.Application.Commands;
using LiveStream.Domain.Entities;

namespace LiveStream.Application.Filters;

/// <summary>Filtros que ofrece el estudio. Solo se muestran los que el runtime del motor tiene cargados.</summary>
public enum FilterCategory
{
    Audio,
    ColorCorrection,
    KeyingAndBackground,
    MasksAndCrop,
    CreativeEffects,
    Utility
}

/// <summary>Filtros que ofrece el estudio. Solo se muestran los que el runtime del motor tiene cargados.</summary>
public static class FilterCatalog
{
    public sealed record Entry(
        string TypeId,
        string Name,
        bool Audio,
        string DefaultSettingsJson = "{}",
        FilterCategory Category = FilterCategory.Utility,
        string Description = "",
        string IconKey = "Icon.Sliders");

    public static string CategoryName(FilterCategory category) => category switch
    {
        FilterCategory.Audio => "Filtros de audio",
        FilterCategory.ColorCorrection => "Corrección de imagen y color",
        FilterCategory.KeyingAndBackground => "Clave cromática y fondos",
        FilterCategory.MasksAndCrop => "Máscaras y recorte",
        FilterCategory.CreativeEffects => "Efectos creativos",
        FilterCategory.Utility => "Utilidades",
        _ => "Otros filtros"
    };

    public static IReadOnlyList<Entry> All { get; } =
    [
        // Audio
        new("noise_suppress_filter_v2", "Supresión de ruido", true, """{"method":"rnnoise"}""", FilterCategory.Audio, "Elimina ruidos de fondo continuos como ventiladores o tráfico (algoritmo neuronal RNNoise).", "Icon.Volume2"),
        new("noise_gate_filter", "Puerta de ruido", true, "{}", FilterCategory.Audio, "Silencia el micrófono cuando no estás hablando según umbrales de apertura y cierre.", "Icon.Mic"),
        new("compressor_filter", "Compresor", true, "{}", FilterCategory.Audio, "Atenúa picos altos y uniforma el volumen general de la voz para un sonido profesional.", "Icon.Sliders"),
        new("limiter_filter", "Limitador", true, "{}", FilterCategory.Audio, "Impide que el audio distorsione o supere los 0 dB cortando picos extremos.", "Icon.Sliders"),
        new("expander_filter", "Expansor hacia abajo", true, "{}", FilterCategory.Audio, "Reduce progresivamente sonidos débiles y respiraciones sin corte abrupto.", "Icon.Volume"),
        new("upward_compressor_filter", "Compresor ascendente", true, "{}", FilterCategory.Audio, "Eleva susurros y partes suaves sin amplificar los momentos de mayor volumen.", "Icon.Volume"),
        new("gain_filter", "Ganancia", true, "{}", FilterCategory.Audio, "Añade o resta decibelios limpios a la señal antes de enviarla a la mezcla.", "Icon.Plus"),
        new("eq_filter", "Ecualizador (3 bandas)", true, "{}", FilterCategory.Audio, "Realza o atenúa frecuencias graves (100 Hz), medias (1 kHz) y agudas (10 kHz).", "Icon.Layers"),
        new("invert_polarity_filter", "Invertir polaridad", true, "{}", FilterCategory.Audio, "Invierte la fase (180°) para solucionar cancelaciones de fase acústica entre micrófonos.", "Icon.Layers"),
        new("async_delay_filter", "Retardo de sincronización", true, "{}", FilterCategory.Audio, "Compensa desfases entre audio y vídeo añadiendo retardo en milisegundos.", "Icon.Clock"),
        new("nvidia_audiofx_filter", "Supresión de ruido con IA (NVIDIA)", true, "{}", FilterCategory.Audio, "Eliminación de ruido y reverberación mediante IA de NVIDIA Broadcast.", "Icon.Sliders"),

        // Corrección de imagen y color
        new("color_filter_v2", "Corrección de color", false, "{}", FilterCategory.ColorCorrection, "Ajusta brillo, contraste, gamma, saturación, matiz y opacidad de la fuente.", "Icon.Palette"),
        new("color_grade_filter", "Gradación de color (LUT)", false, "{}", FilterCategory.ColorCorrection, "Aplica tablas de búsqueda (LUTs .png) para dar un look cinematográfico.", "Icon.Film"),
        new("hdr_tonemap_filter", "Mapeo tonal HDR", false, "{}", FilterCategory.ColorCorrection, "Convierte una señal de vídeo HDR en SDR mediante curvas Reinhard o ACES.", "Icon.Sun"),
        new("sdr_on_hdr_filter", "Ajuste SDR en HDR", false, "{}", FilterCategory.ColorCorrection, "Calibra el nivel de blanco de fuentes SDR sobre un lienzo configurado en HDR.", "Icon.Layers"),

        // Claves de color y fondos
        new("chroma_key_filter_v2", "Clave cromática (Chroma Key)", false, "{}", FilterCategory.KeyingAndBackground, "Elimina fondos verdes o azules con suavizado de bordes y reducción de vertido.", "Icon.Camera"),
        new("color_key_filter_v2", "Clave de color estándar", false, "{}", FilterCategory.KeyingAndBackground, "Elimina un color RGB específico con margen de similitud y suavizado.", "Icon.Palette"),
        new("luma_key_filter_v2", "Clave de luminancia (Luma Key)", false, "{}", FilterCategory.KeyingAndBackground, "Elimina partes de la imagen según su nivel de brillo (blancos o sombras).", "Icon.Sun"),
        new("nvidia_greenscreen_filter", "Fondo virtual con IA (NVIDIA RTX)", false, "{}", FilterCategory.KeyingAndBackground, "Segmentación de persona y eliminación o desenfoque de fondo mediante redes neuronales.", "Icon.Camera"),

        // Máscaras y recortes
        new("crop_filter", "Recortar / Rellenar", false, "{}", FilterCategory.MasksAndCrop, "Recorta píxeles en cualquier borde o añade márgenes de relleno alrededor de la fuente.", "Icon.Square"),
        new("mask_filter_v2", "Máscara de imagen / Mezcla", false, "{}", FilterCategory.MasksAndCrop, "Aplica una imagen como máscara de recorte (canal alfa, color o multiplicar).", "Icon.Image"),
        new("scale_filter", "Escalado y relación de aspecto", false, "{}", FilterCategory.MasksAndCrop, "Reescala con filtrado bicúbico, Lanczos o fuerza una relación 16:9 / 9:16.", "Icon.Maximize"),

        // Efectos creativos
        new("sharpness_filter_v2", "Nitidez", false, "{}", FilterCategory.CreativeEffects, "Aumenta la definición y claridad de los detalles finos de la imagen.", "Icon.Sparkles"),
        new("scroll_filter", "Desplazamiento continuo", false, "{}", FilterCategory.CreativeEffects, "Desplaza la imagen en bucle horizontal o verticalmente (efecto marquesina).", "Icon.ArrowRight"),
        new("gpu_delay", "Retraso de render GPU", false, "{}", FilterCategory.CreativeEffects, "Retarda los fotogramas de vídeo para sincronizar con fuentes lentas de audio.", "Icon.Clock"),
    ];

    public static Entry? Find(string typeId) => All.FirstOrDefault(e => e.TypeId == typeId);

    public static string? ValidateJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? null : "Los ajustes deben ser un objeto JSON.";
        }
        catch (JsonException)
        {
            return "Los ajustes no son JSON válido.";
        }
    }
}

/// <summary>Añade un filtro al final de la cadena de una fuente.</summary>
public sealed class AddFilterCommand(Guid sourceId, string typeId, string name, string? settingsJson = null) : IReversibleStudioCommand
{
    private SourceFilter? _filter;

    public Guid? FilterId => _filter?.Id;

    public string Description => $"Añadir filtro '{name}'";

    public string? Validate(StudioContext context)
    {
        if (context.Project.FindSource(sourceId) is null)
            return "La fuente no existe.";
        if (string.IsNullOrWhiteSpace(name))
            return "El filtro necesita un nombre.";
        if (FilterCatalog.Find(typeId) is not { } entry)
            return $"Filtro no soportado: {typeId}.";
        var source = context.Project.GetSource(sourceId);
        if (entry.Audio && source.Kind is not (SourceKind.AudioInput or SourceKind.AudioOutput or SourceKind.Media or SourceKind.VideoCaptureDevice or SourceKind.Browser))
            return "Esta fuente no tiene audio.";
        if (!entry.Audio && source.IsAudioOnly)
            return "Esta fuente no tiene video.";
        return FilterCatalog.ValidateJson(settingsJson ?? entry.DefaultSettingsJson);
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var source = context.Project.GetSource(sourceId);
        _filter ??= new SourceFilter(Guid.NewGuid(), name, typeId, settingsJson ?? FilterCatalog.Find(typeId)!.DefaultSettingsJson);
        source.InsertFilter(_filter, source.Filters.Count);
        if (!context.Engine.IsInitialized)
            return;
        try
        {
            await FilterSync.CreateAsync(context.Engine, sourceId, _filter, ct).ConfigureAwait(false);
        }
        catch
        {
            source.RemoveFilter(_filter.Id);
            throw;
        }
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_filter is null)
            return;
        context.Project.GetSource(sourceId).RemoveFilter(_filter.Id);
        if (context.Engine.IsInitialized)
            await context.Engine.RemoveFilterAsync(_filter.Id, ct).ConfigureAwait(false);
    }
}

/// <summary>Duplica un filtro existente con todos sus parámetros actuales al final de la cadena.</summary>
public sealed class DuplicateFilterCommand(Guid sourceId, Guid filterId, string newName) : IReversibleStudioCommand
{
    private AddFilterCommand? _addCommand;

    public Guid? NewFilterId => _addCommand?.FilterId;

    public string Description => $"Duplicar filtro '{newName}'";

    public string? Validate(StudioContext context)
    {
        var source = context.Project.FindSource(sourceId);
        if (source is null)
            return "La fuente no existe.";
        var filter = source.Filters.FirstOrDefault(f => f.Id == filterId);
        if (filter is null)
            return "El filtro a duplicar no existe.";
        _addCommand = new AddFilterCommand(sourceId, filter.TypeId, newName, filter.SettingsJson);
        return _addCommand.Validate(context);
    }

    public Task ExecuteAsync(StudioContext context, CancellationToken ct) => _addCommand!.ExecuteAsync(context, ct);
    public Task UndoAsync(StudioContext context, CancellationToken ct) => _addCommand!.UndoAsync(context, ct);
}

public sealed class RemoveFilterCommand(Guid sourceId, Guid filterId) : IReversibleStudioCommand
{
    private SourceFilter? _filter;
    private int _index;

    public string Description => "Eliminar filtro";

    public string? Validate(StudioContext context) =>
        context.Project.FindSource(sourceId)?.Filters.Any(f => f.Id == filterId) == true ? null : "El filtro no existe.";

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var source = context.Project.GetSource(sourceId);
        _index = source.IndexOfFilter(filterId);
        _filter = source.RemoveFilter(filterId);
        if (context.Engine.IsInitialized)
            await context.Engine.RemoveFilterAsync(filterId, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_filter is null)
            return;
        var source = context.Project.GetSource(sourceId);
        source.InsertFilter(_filter, _index);
        // El motor añade al final: se recrean los filtros posteriores para conservar el orden de la cadena.
        if (context.Engine.IsInitialized)
            await FilterSync.RebuildFromAsync(context.Engine, source, _index, ct).ConfigureAwait(false);
    }
}

/// <summary>Cambia los ajustes de un filtro. previousSettingsJson permite deshacer una edición ya aplicada en vivo.</summary>
public sealed class UpdateFilterSettingsCommand(Guid sourceId, Guid filterId, string settingsJson, string? previousSettingsJson = null)
    : IReversibleStudioCommand
{
    private string? _previous = previousSettingsJson;

    public string Description => "Ajustes del filtro";

    public string? Validate(StudioContext context)
    {
        if (context.Project.FindSource(sourceId)?.Filters.Any(f => f.Id == filterId) != true)
            return "El filtro no existe.";
        return FilterCatalog.ValidateJson(settingsJson);
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var filter = context.Project.GetSource(sourceId).GetFilter(filterId);
        _previous ??= filter.SettingsJson;
        filter.SettingsJson = settingsJson;
        if (context.Engine.IsInitialized)
            await context.Engine.UpdateFilterAsync(filterId, settingsJson, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        var filter = context.Project.GetSource(sourceId).GetFilter(filterId);
        filter.SettingsJson = _previous ?? "{}";
        if (context.Engine.IsInitialized)
            await context.Engine.UpdateFilterAsync(filterId, filter.SettingsJson, ct).ConfigureAwait(false);
    }
}

public sealed class SetFilterEnabledCommand(Guid sourceId, Guid filterId, bool enabled) : IReversibleStudioCommand
{
    private bool _previous;

    public string Description => enabled ? "Activar filtro" : "Desactivar filtro";

    public string? Validate(StudioContext context) =>
        context.Project.FindSource(sourceId)?.Filters.Any(f => f.Id == filterId) == true ? null : "El filtro no existe.";

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var filter = context.Project.GetSource(sourceId).GetFilter(filterId);
        _previous = filter.Enabled;
        filter.Enabled = enabled;
        if (context.Engine.IsInitialized)
            await context.Engine.SetFilterEnabledAsync(filterId, enabled, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.GetSource(sourceId).GetFilter(filterId).Enabled = _previous;
        if (context.Engine.IsInitialized)
            await context.Engine.SetFilterEnabledAsync(filterId, _previous, ct).ConfigureAwait(false);
    }
}

/// <summary>Mueve un filtro dentro de la cadena (el orden importa: p. ej. puerta antes que compresor).</summary>
public sealed class MoveFilterCommand(Guid sourceId, Guid filterId, int newIndex) : IReversibleStudioCommand
{
    private int _previousIndex;

    public string Description => "Reordenar filtro";

    public string? Validate(StudioContext context) =>
        context.Project.FindSource(sourceId)?.Filters.Any(f => f.Id == filterId) == true ? null : "El filtro no existe.";

    public Task ExecuteAsync(StudioContext context, CancellationToken ct) => MoveAsync(context, newIndex, record: true, ct);

    public Task UndoAsync(StudioContext context, CancellationToken ct) => MoveAsync(context, _previousIndex, record: false, ct);

    private async Task MoveAsync(StudioContext context, int index, bool record, CancellationToken ct)
    {
        var source = context.Project.GetSource(sourceId);
        var current = source.IndexOfFilter(filterId);
        if (record)
            _previousIndex = current;
        var filter = source.RemoveFilter(filterId);
        index = Math.Clamp(index, 0, source.Filters.Count);
        source.InsertFilter(filter, index);
        if (context.Engine.IsInitialized)
            await FilterSync.RebuildFromAsync(context.Engine, source, Math.Min(current, index), ct).ConfigureAwait(false);
    }
}

/// <summary>Traslada filtros del dominio al motor.</summary>
public static class FilterSync
{
    public static async Task CreateAsync(Abstractions.IStreamingEngine engine, Guid sourceId, SourceFilter filter, CancellationToken ct)
    {
        await engine.AddFilterAsync(sourceId, filter.Id, filter.TypeId, filter.Name, filter.SettingsJson, ct).ConfigureAwait(false);
        if (!filter.Enabled)
            await engine.SetFilterEnabledAsync(filter.Id, false, ct).ConfigureAwait(false);
    }

    /// <summary>Recrea en el motor los filtros desde una posición, para que su orden coincida con el del dominio.</summary>
    public static async Task RebuildFromAsync(Abstractions.IStreamingEngine engine, Source source, int fromIndex, CancellationToken ct)
    {
        var tail = source.Filters.Skip(Math.Max(0, fromIndex)).ToList();
        foreach (var filter in tail)
        {
            try
            {
                await engine.RemoveFilterAsync(filter.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // El filtro restaurado por deshacer aún no existe en el motor.
            }
        }
        foreach (var filter in tail)
            await CreateAsync(engine, source.Id, filter, ct).ConfigureAwait(false);
    }
}
