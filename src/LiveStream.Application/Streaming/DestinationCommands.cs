using LiveStream.Application.Commands;
using LiveStream.Domain.Entities;

namespace LiveStream.Application.Streaming;

internal static class DestinationValidation
{
    public static string? Validate(StudioContext context, StreamDestination d, bool isNew)
    {
        if (isNew && context.Project.FindDestination(d.Id) is not null)
            return "El destino ya existe.";
        if (context.Project.Destinations.Any(o => o.Id != d.Id && string.Equals(o.Name, d.Name, StringComparison.OrdinalIgnoreCase)))
            return $"Ya hay un destino llamado «{d.Name}».";
        if (string.IsNullOrWhiteSpace(d.CredentialName))
            return "El destino necesita una credencial.";
        // Un destino sin servidor se puede guardar (se completa después) pero no activar.
        if (d.Server.Trim().Length == 0)
            return d.Enabled ? $"«{d.Name}»: falta el servidor." : null;
        return PlatformCatalog.Validate(d, d.EffectiveEncoder(context.Project.Stream));
    }
}

/// <summary>Añade un destino de emisión. La clave se guarda aparte (cifrada) con la sesión.</summary>
public sealed class AddDestinationCommand(StreamDestination destination) : IReversibleStudioCommand
{
    public string Description => $"Añadir destino «{destination.Name}»";

    public string? Validate(StudioContext context) => DestinationValidation.Validate(context, destination, isNew: true);

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.InsertDestination(destination);
        return Task.CompletedTask;
    }

    public Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.RemoveDestination(destination.Id);
        return Task.CompletedTask;
    }
}

/// <summary>Edita un destino (nombre, servidor, plataforma, activación, codificador propio).</summary>
public sealed class UpdateDestinationCommand(StreamDestination updated) : IReversibleStudioCommand
{
    private StreamDestination? _previous;

    public string Description => $"Editar destino «{updated.Name}»";

    public string? Validate(StudioContext context) =>
        context.Project.FindDestination(updated.Id) is null ? "El destino no existe." : DestinationValidation.Validate(context, updated, isNew: false);

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        _previous = context.Project.ReplaceDestination(updated with { Server = updated.Server.Trim() });
        return Task.CompletedTask;
    }

    public Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_previous is not null)
            context.Project.ReplaceDestination(_previous);
        return Task.CompletedTask;
    }
}

/// <summary>Quita un destino. No se puede mientras emite.</summary>
public sealed class RemoveDestinationCommand(Guid destinationId, Func<Guid, bool> isLive) : IReversibleStudioCommand
{
    private StreamDestination? _removed;
    private int _index;

    public string Description => "Eliminar destino";

    public string? Validate(StudioContext context)
    {
        if (context.Project.FindDestination(destinationId) is null)
            return "El destino no existe.";
        return isLive(destinationId) ? "Detén la emisión a ese destino antes de eliminarlo." : null;
    }

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        (_removed, _index) = context.Project.RemoveDestination(destinationId);
        return Task.CompletedTask;
    }

    public Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_removed is not null)
            context.Project.InsertDestination(_removed, _index);
        return Task.CompletedTask;
    }
}
