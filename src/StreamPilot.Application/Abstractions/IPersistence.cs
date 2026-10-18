using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Abstractions;

public interface IProjectRepository
{
    /// <summary>Carpeta donde viven los proyectos.</summary>
    string ProjectsDirectory { get; }

    Task<StudioProject?> LoadAsync(string path, CancellationToken ct);

    /// <summary>Guarda de forma atómica (archivo temporal + reemplazo) conservando una copia del estado anterior.</summary>
    Task SaveAsync(StudioProject project, string path, CancellationToken ct);

    string DefaultPathFor(StudioProject project);

    /// <summary>Último proyecto abierto, si sigue existiendo.</summary>
    string? LastOpenedPath { get; set; }
}

/// <summary>Secretos (claves de transmisión) cifrados para el usuario de Windows actual.</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string name, CancellationToken ct);
    Task SetAsync(string name, string value, CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
}
