using System.Text.Json;
using StreamPilot.Application.Commands;
using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Filters;

/// <summary>Filtros que ofrece el estudio. Solo se muestran los que el runtime del motor tiene cargados.</summary>
public static class FilterCatalog
{
    public sealed record Entry(string TypeId, string Name, bool Audio, string DefaultSettingsJson = "{}");

    public static IReadOnlyList<Entry> All { get; } =
    [
        // Audio
        new("noise_suppress_filter_v2", "Supresión de ruido", true, """{"method":"rnnoise"}"""),
        new("noise_gate_filter", "Puerta de ruido", true),
        new("compressor_filter", "Compresor", true),
        new("limiter_filter", "Limitador", true),
        new("expander_filter", "Expansor", true),
        new("gain_filter", "Ganancia", true),
        // Video
        new("color_filter_v2", "Corrección de color", false),
        new("chroma_key_filter_v2", "Clave cromática", false),
        new("color_key_filter_v2", "Clave de color", false),
        new("luma_key_filter_v2", "Clave de luminancia", false),
        new("crop_filter", "Recortar / rellenar", false),
        new("sharpness_filter_v2", "Nitidez", false),
        new("scroll_filter", "Desplazamiento", false),
        new("mask_filter_v2", "Máscara de imagen / mezcla", false),
        new("scale_filter", "Escalado / relación de aspecto", false),
        new("gpu_delay", "Retraso de render", false),
    ];

    public static Entry? Find(string typeId) => All.FirstOrDefault(e => e.TypeId == typeId);

    internal static string? ValidateJson(string json)
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
