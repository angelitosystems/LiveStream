using StreamPilot.Application.Commands;
using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.Application.Sources;

/// <summary>
/// Cambia la transformación de un elemento. Para arrastres en el editor, el motor se actualiza en vivo
/// durante el gesto y este comando se emite al soltar con <paramref name="previous"/> = estado inicial,
/// de modo que deshacer vuelve al punto de partida del gesto completo.
/// </summary>
public sealed class SetItemTransformCommand(Guid itemId, Transform transform, Transform? previous = null) : IReversibleStudioCommand
{
    private Transform? _previous = previous;

    public string Description => "Transformar elemento";

    public string? Validate(StudioContext context)
    {
        if (!Exists(context))
            return "El elemento no existe.";
        if (!double.IsFinite(transform.X) || !double.IsFinite(transform.Y) || !double.IsFinite(transform.ScaleX) ||
            !double.IsFinite(transform.ScaleY) || !double.IsFinite(transform.RotationDegrees))
            return "Transformación inválida.";
        if (Math.Abs(transform.ScaleX) < 1e-4 || Math.Abs(transform.ScaleY) < 1e-4)
            return "La escala no puede ser cero.";
        if (transform.CropLeft < 0 || transform.CropTop < 0 || transform.CropRight < 0 || transform.CropBottom < 0)
            return "El recorte no puede ser negativo.";
        return null;
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var (_, item) = context.Project.FindItem(itemId);
        _previous ??= item.Transform;
        item.Transform = transform;
        if (context.Engine.IsInitialized)
            await context.Engine.SetItemTransformAsync(itemId, transform, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_previous is not { } previous)
            return;
        context.Project.FindItem(itemId).Item.Transform = previous;
        if (context.Engine.IsInitialized)
            await context.Engine.SetItemTransformAsync(itemId, previous, ct).ConfigureAwait(false);
    }

    private bool Exists(StudioContext context)
    {
        try
        {
            context.Project.FindItem(itemId);
            return true;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }
}

public sealed class SetItemVisibleCommand(Guid itemId, bool visible) : IReversibleStudioCommand
{
    public string Description => visible ? "Mostrar elemento" : "Ocultar elemento";

    public string? Validate(StudioContext context) => ItemGuard.Validate(context, itemId);

    public Task ExecuteAsync(StudioContext context, CancellationToken ct) => Apply(context, visible, ct);

    public Task UndoAsync(StudioContext context, CancellationToken ct) => Apply(context, !visible, ct);

    private async Task Apply(StudioContext context, bool value, CancellationToken ct)
    {
        context.Project.FindItem(itemId).Item.Visible = value;
        if (context.Engine.IsInitialized)
            await context.Engine.SetItemVisibleAsync(itemId, value, ct).ConfigureAwait(false);
    }
}

public sealed class SetItemLockedCommand(Guid itemId, bool locked) : IReversibleStudioCommand
{
    public string Description => locked ? "Bloquear elemento" : "Desbloquear elemento";

    public string? Validate(StudioContext context) => ItemGuard.Validate(context, itemId);

    public Task ExecuteAsync(StudioContext context, CancellationToken ct) => Apply(context, locked, ct);

    public Task UndoAsync(StudioContext context, CancellationToken ct) => Apply(context, !locked, ct);

    private async Task Apply(StudioContext context, bool value, CancellationToken ct)
    {
        context.Project.FindItem(itemId).Item.Locked = value;
        if (context.Engine.IsInitialized)
            await context.Engine.SetItemLockedAsync(itemId, value, ct).ConfigureAwait(false);
    }
}

/// <summary>Cambia el orden de capa (0 = abajo).</summary>
public sealed class MoveItemCommand(Guid itemId, int newIndex) : IReversibleStudioCommand
{
    private int _oldIndex = -1;

    public string Description => "Reordenar capa";

    public string? Validate(StudioContext context) => ItemGuard.Validate(context, itemId);

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var (scene, _) = context.Project.FindItem(itemId);
        _oldIndex = scene.IndexOf(itemId);
        await Apply(context, newIndex, ct).ConfigureAwait(false);
    }

    public Task UndoAsync(StudioContext context, CancellationToken ct) =>
        _oldIndex >= 0 ? Apply(context, _oldIndex, ct) : Task.CompletedTask;

    private async Task Apply(StudioContext context, int index, CancellationToken ct)
    {
        var (scene, _) = context.Project.FindItem(itemId);
        scene.MoveItem(itemId, index);
        if (context.Engine.IsInitialized)
            await context.Engine.SetItemOrderAsync(itemId, scene.IndexOf(itemId), ct).ConfigureAwait(false);
    }
}

internal static class ItemGuard
{
    public static string? Validate(StudioContext context, Guid itemId)
    {
        try
        {
            context.Project.FindItem(itemId);
            return null;
        }
        catch (KeyNotFoundException)
        {
            return "El elemento no existe.";
        }
    }
}
