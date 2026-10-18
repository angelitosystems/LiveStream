using System.Text.Json;
using System.Text.Json.Serialization;
using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.Graphics;

namespace StreamPilot.Infrastructure.Persistence;

/// <summary>
/// Plantillas de gráficos como archivos JSON (.spgfx) en %APPDATA%\StreamPilot\templates. Son portables:
/// se pueden copiar a otro equipo.
/// </summary>
public sealed class GraphicTemplateLibrary : IGraphicTemplateLibrary
{
    public const string Extension = ".spgfx";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public GraphicTemplateLibrary(string? directory = null) =>
        Directory = System.IO.Directory.CreateDirectory(directory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamPilot", "templates")).FullName;

    public string Directory { get; }

    public async Task<IReadOnlyList<GraphicTemplateInfo>> ListAsync(CancellationToken ct)
    {
        var list = new List<GraphicTemplateInfo>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var dto = await ReadAsync(file, ct).ConfigureAwait(false);
                list.Add(new GraphicTemplateInfo(dto.Name, dto.Graphic.Kind));
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                // Una plantilla dañada no impide ver las demás.
            }
        }
        return list;
    }

    public async Task SaveAsync(string name, Graphic graphic, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (graphic.Validate() is { } error)
            throw new ArgumentException(error, nameof(graphic));
        var path = PathFor(name);
        var temp = path + ".tmp";
        var dto = new TemplateDto(FormatVersion, name.Trim(), graphic with { DataFile = null });
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(dto, Options), ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<Graphic> InstantiateAsync(string name, CancellationToken ct)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No existe la plantilla '{name}'.", path);
        var dto = await ReadAsync(path, ct).ConfigureAwait(false);
        return dto.Graphic with { Id = Guid.NewGuid(), DataFile = null };
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        File.Delete(PathFor(name));
        return Task.CompletedTask;
    }

    private static async Task<TemplateDto> ReadAsync(string path, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<TemplateDto>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), Options)
            ?? throw new InvalidDataException("Plantilla vacía.");
        if (dto.Version > FormatVersion)
            throw new InvalidDataException("Plantilla de una versión más nueva de StreamPilot.");
        if (dto.Graphic?.Validate() is { } error)
            throw new InvalidDataException(error);
        return dto with { Graphic = dto.Graphic! with { Fields = dto.Graphic.Fields ?? new Dictionary<string, string>() } };
    }

    private string PathFor(string name)
    {
        var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safe.Length == 0)
            throw new ArgumentException("Nombre de plantilla vacío.", nameof(name));
        return Path.Combine(Directory, safe + Extension);
    }

    private sealed record TemplateDto(int Version, string Name, Graphic Graphic);
}
