namespace StreamPilot.Application.Commands;

/// <summary>
/// Mutación del estudio. Toda modificación (UI, IA u optimizador) pasa por un comando
/// para validarla, serializarla y poder deshacerla.
/// </summary>
public interface IStudioCommand
{
    string Description { get; }

    /// <summary>Devuelve un mensaje de error si el comando no es válido en el estado actual; null si es válido.</summary>
    string? Validate(StudioContext context);

    Task ExecuteAsync(StudioContext context, CancellationToken ct);
}

/// <summary>Comando que además sabe revertirse (para Undo/Redo).</summary>
public interface IReversibleStudioCommand : IStudioCommand
{
    Task UndoAsync(StudioContext context, CancellationToken ct);
}
