using StreamPilot.Application.Commands;
using StreamPilot.Domain.Graphics;

namespace StreamPilot.Application.Graphics;

internal static class GraphicValidation
{
    public static string? Validate(StudioContext context, Graphic graphic, bool isNew)
    {
        if (graphic.Validate() is { } error)
            return error;
        if (GraphicTemplates.ValidateFields(graphic) is { } fieldError)
            return fieldError;
        if (isNew && context.Project.FindGraphic(graphic.Id) is not null)
            return "El gráfico ya existe.";
        if (context.Project.HasGraphicNamed(graphic.Name, isNew ? null : graphic.Id))
            return $"Ya existe un gráfico llamado '{graphic.Name}'.";
        if (graphic.DataFile is { Length: > 0 } file && !Path.IsPathFullyQualified(file))
            return "El archivo de datos debe ser una ruta absoluta.";
        return null;
    }
}

/// <summary>Añade un gráfico a la capa (oculto hasta que se lance al aire).</summary>
public sealed class AddGraphicCommand(Graphic graphic) : IReversibleStudioCommand
{
    public Guid GraphicId => graphic.Id;

    public string Description => $"Añadir gráfico '{graphic.Name}'";

    public string? Validate(StudioContext context) => GraphicValidation.Validate(context, graphic, isNew: true);

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.InsertGraphic(graphic);
        if (!context.Engine.IsInitialized)
            return;
        try
        {
            await GraphicLayer.CreateAsync(context.Engine, context.Project, graphic, context.Project.IndexOfGraphic(graphic.Id),
                context.GraphicText, ct).ConfigureAwait(false);
        }
        catch
        {
            context.Project.RemoveGraphic(graphic.Id);
            throw;
        }
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.RemoveGraphic(graphic.Id);
        if (context.Engine.IsInitialized)
            await GraphicLayer.RemoveAsync(context.Engine, context.Project, graphic, ct).ConfigureAwait(false);
    }
}

public sealed class RemoveGraphicCommand(Guid graphicId) : IReversibleStudioCommand
{
    private Graphic? _graphic;
    private int _index;

    public string Description => "Eliminar gráfico";

    public string? Validate(StudioContext context) => context.Project.FindGraphic(graphicId) is null ? "El gráfico no existe." : null;

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        _index = context.Project.IndexOfGraphic(graphicId);
        _graphic = context.Project.RemoveGraphic(graphicId);
        if (context.Engine.IsInitialized)
            await GraphicLayer.RemoveAsync(context.Engine, context.Project, _graphic, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_graphic is null)
            return;
        context.Project.InsertGraphic(_graphic, _index);
        if (context.Engine.IsInitialized)
            await GraphicLayer.CreateAsync(context.Engine, context.Project, _graphic, _index, context.GraphicText, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Sustituye un gráfico por su versión editada y la aplica en vivo (aunque esté al aire).
/// previous permite deshacer cambios que la UI ya aplicó como vista previa.
/// </summary>
public sealed class UpdateGraphicCommand(Graphic updated, Graphic? previous = null) : IReversibleStudioCommand
{
    private Graphic? _previous = previous;

    public string Description => $"Editar gráfico '{updated.Name}'";

    public string? Validate(StudioContext context)
    {
        if (context.Project.FindGraphic(updated.Id) is not { } current)
            return "El gráfico no existe.";
        return current.Kind != updated.Kind ? "No se puede cambiar el tipo de un gráfico." : GraphicValidation.Validate(context, updated, isNew: false);
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var replaced = context.Project.ReplaceGraphic(updated);
        _previous ??= replaced;
        if (context.Engine.IsInitialized)
            await GraphicLayer.UpdateAsync(context.Engine, context.Project, updated, context.GraphicText, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_previous is null)
            return;
        context.Project.ReplaceGraphic(_previous);
        if (context.Engine.IsInitialized)
            await GraphicLayer.UpdateAsync(context.Engine, context.Project, _previous, context.GraphicText, ct).ConfigureAwait(false);
    }
}

/// <summary>Cambia la posición del gráfico en la pila de la capa (los de arriba tapan a los de abajo).</summary>
public sealed class MoveGraphicCommand(Guid graphicId, int newIndex) : IReversibleStudioCommand
{
    private int _previous;

    public string Description => "Reordenar gráfico";

    public string? Validate(StudioContext context) => context.Project.FindGraphic(graphicId) is null ? "El gráfico no existe." : null;

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        _previous = context.Project.IndexOfGraphic(graphicId);
        return MoveAsync(context, newIndex, ct);
    }

    public Task UndoAsync(StudioContext context, CancellationToken ct) => MoveAsync(context, _previous, ct);

    private static async Task MoveAsync(StudioContext context, Guid id, int index, CancellationToken ct)
    {
        var graphic = context.Project.RemoveGraphic(id);
        index = Math.Clamp(index, 0, context.Project.Graphics.Count);
        context.Project.InsertGraphic(graphic, index);
        if (context.Engine.IsInitialized)
            await context.Engine.SetItemOrderAsync(GraphicLayer.OverlayItemId(id), index, ct).ConfigureAwait(false);
    }

    private Task MoveAsync(StudioContext context, int index, CancellationToken ct) => MoveAsync(context, graphicId, index, ct);
}

/// <summary>
/// Datos en vivo (archivo enlazado): sustituye campos sin entrar en el historial de deshacer, porque los
/// produce una fuente externa y no el operador.
/// </summary>
public sealed class ApplyGraphicDataCommand(Guid graphicId, IReadOnlyDictionary<string, string> values) : IStudioCommand
{
    public string Description => "Datos en vivo del gráfico";

    public string? Validate(StudioContext context)
    {
        if (context.Project.FindGraphic(graphicId) is not { } graphic)
            return "El gráfico no existe.";
        var known = GraphicTemplates.Fields(graphic.Kind).Select(f => f.Key).ToHashSet();
        return values.Keys.Any(known.Contains) ? GraphicTemplates.ValidateFields(Merge(graphic)) : "Los datos no contienen ningún campo del gráfico.";
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var merged = Merge(context.Project.GetGraphic(graphicId));
        context.Project.ReplaceGraphic(merged);
        if (context.Engine.IsInitialized)
            await GraphicLayer.UpdateAsync(context.Engine, context.Project, merged, context.GraphicText, ct).ConfigureAwait(false);
    }

    private Graphic Merge(Graphic graphic)
    {
        var known = GraphicTemplates.Fields(graphic.Kind).Select(f => f.Key).ToHashSet();
        var fields = new Dictionary<string, string>(graphic.Fields);
        foreach (var (key, value) in values)
            if (known.Contains(key))
                fields[key] = value;
        return graphic with { Fields = fields };
    }
}
