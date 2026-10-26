using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveStream.Domain.Graphics;

namespace LiveStream.Infrastructure.Persistence;

/// <summary>
/// Archivo .lsgfx para llevar un gráfico a otro equipo: un ZIP con graphic.json (formato versionado) y los recursos
/// que usa (imágenes, videos, logo). Al importar se validan las rutas internas, los tamaños, cada recurso (con la
/// biblioteca) y el gráfico; los recursos quedan en la categoría «Importados» de la biblioteca.
/// </summary>
public static class GraphicPackage
{
    public const string Extension = ".lsgfx";
    public const string FileFilter = "Gráficos de LiveStream (*.lsgfx)";
    private const string Format = "livestream-graphic-v1";
    private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024 + 100L * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Manifest(string Format, Graphic Graphic);

    public static void Export(Graphic graphic, string path)
    {
        if (graphic.Validate() is { } error)
            throw new InvalidDataException(error);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // ruta local → entrada del ZIP
        string Pack(string local)
        {
            if (!File.Exists(local))
                throw new FileNotFoundException($"Falta un recurso del gráfico: {local}. Vuelve a enlazarlo antes de exportar.");
            if (!files.TryGetValue(local, out var entry))
                files[local] = entry = $"resources/{files.Count + 1}-{Path.GetFileName(local)}";
            return entry;
        }

        var design = graphic.Design?.Select(e => e is { Kind: DesignElementKind.Image or DesignElementKind.Video, Path: { } p } ? e with { Path = Pack(p) } : e).ToList();
        var fields = new Dictionary<string, string>(graphic.Fields);
        if (fields.TryGetValue("logo", out var logo) && logo.Length > 0)
            fields["logo"] = Pack(logo);
        var portable = graphic with { Design = design, Fields = fields, DataFile = null };

        var temp = path + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("graphic.json").Open()))
                writer.Write(JsonSerializer.Serialize(new Manifest(Format, portable), Options));
            foreach (var (local, entry) in files)
                zip.CreateEntryFromFile(local, entry, CompressionLevel.Fastest);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Lee un .lsgfx y devuelve el gráfico (con un id nuevo) listo para añadir. Lanza InvalidDataException si no es válido.</summary>
    public static Graphic Import(string path, ResourceLibrary library)
    {
        using var zip = OpenZip(path);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName != "graphic.json" && !(entry.FullName.StartsWith("resources/", StringComparison.Ordinal)
                    && entry.FullName.Count(c => c == '/') == 1 && !entry.FullName.Contains("..")))
                throw new InvalidDataException($"El archivo contiene una ruta no permitida: {entry.FullName}");
            total += entry.Length;
            if (total > MaxTotalBytes)
                throw new InvalidDataException("El contenido del archivo es demasiado grande.");
        }
        var json = zip.GetEntry("graphic.json") ?? throw new InvalidDataException("No es un gráfico de LiveStream (falta graphic.json).");
        Manifest? manifest;
        try
        {
            using var reader = new StreamReader(json.Open());
            manifest = JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd(), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"graphic.json no es válido: {ex.Message}");
        }
        if (manifest is not { Format: Format, Graphic: not null })
            throw new InvalidDataException("Formato de gráfico no soportado (¿de una versión más nueva de LiveStream?).");

        var imported = new Dictionary<string, string>();
        var staging = Directory.CreateTempSubdirectory("lsgfx-").FullName;
        try
        {
            string Unpack(string entryName)
            {
                if (imported.TryGetValue(entryName, out var done))
                    return done;
                var entry = zip.GetEntry(entryName) ?? throw new InvalidDataException($"Falta el recurso {entryName} dentro del archivo.");
                var name = entryName[(entryName.IndexOf('-') + 1)..];
                var temp = Path.Combine(staging, Path.GetFileName(name));
                entry.ExtractToFile(temp, overwrite: true);
                return imported[entryName] = library.Import(temp, "Importados").Path;
            }

            var g = manifest.Graphic;
            var design = g.Design?.Select(e => e is { Kind: DesignElementKind.Image or DesignElementKind.Video, Path: { } p } ? e with { Path = Unpack(p) } : e).ToList();
            var fields = new Dictionary<string, string>(g.Fields ?? new Dictionary<string, string>());
            if (fields.TryGetValue("logo", out var logo) && logo.Length > 0)
                fields["logo"] = Unpack(logo);
            var graphic = g with { Id = Guid.NewGuid(), Design = design, Fields = fields, DataFile = null, Style = g.Style ?? new GraphicStyle() };
            if (graphic.Validate() is { } error)
                throw new InvalidDataException(error);
            return graphic;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static ZipArchive OpenZip(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("El archivo no es un gráfico de LiveStream válido.");
        }
    }
}
