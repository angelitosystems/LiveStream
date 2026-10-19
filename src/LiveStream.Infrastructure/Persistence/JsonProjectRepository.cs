using LiveStream.Application.Abstractions;
using LiveStream.Domain.Entities;

namespace LiveStream.Infrastructure.Persistence;

/// <summary>
/// Proyectos como archivos JSON versionados. Escritura atómica: se escribe un temporal en la misma carpeta
/// y se reemplaza el original con <see cref="File.Replace(string, string, string?)"/>, que conserva el estado
/// anterior en un .bak. Un cierre inesperado nunca deja un archivo a medio escribir.
/// </summary>
public sealed class JsonProjectRepository : IProjectRepository
{
    public const string Extension = ".spproj";

    private readonly string _root;
    private readonly string _lastOpenedFile;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonProjectRepository(string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveStream");
        ProjectsDirectory = Directory.CreateDirectory(Path.Combine(_root, "projects")).FullName;
        _lastOpenedFile = Path.Combine(_root, "last-project.txt");
    }

    public string ProjectsDirectory { get; }

    public string? LastOpenedPath
    {
        get
        {
            try
            {
                var path = File.Exists(_lastOpenedFile) ? File.ReadAllText(_lastOpenedFile).Trim() : null;
                return path is not null && File.Exists(path) ? path : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
        set
        {
            if (value is null)
                File.Delete(_lastOpenedFile);
            else
                File.WriteAllText(_lastOpenedFile, value);
        }
    }

    public string DefaultPathFor(StudioProject project)
    {
        var safe = string.Concat(project.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(ProjectsDirectory, (safe.Length == 0 ? project.Id.ToString("N") : safe) + Extension);
    }

    public async Task<StudioProject?> LoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return ProjectSerializer.Deserialize(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            // Recuperación: si el archivo principal está dañado se intenta con la copia anterior.
            var backup = path + ".bak";
            if (!File.Exists(backup))
                throw;
            return ProjectSerializer.Deserialize(await File.ReadAllTextAsync(backup, ct).ConfigureAwait(false));
        }
    }

    public async Task SaveAsync(StudioProject project, string path, CancellationToken ct)
    {
        var json = ProjectSerializer.Serialize(project);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // ConfigureAwait también en la liberación: si no, la continuación vuelve al hilo de UI y un guardado
            // esperado de forma síncrona al cerrar la app se queda bloqueado.
            var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream);
                await using (writer.ConfigureAwait(false))
                    await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
            }

            if (File.Exists(full))
                File.Replace(temp, full, full + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(temp, full);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
