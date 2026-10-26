using LiveStream.Domain.Graphics;

namespace LiveStream.Infrastructure.Persistence;

public enum ResourceKind { Image, Video }

public sealed record ResourceItem(string Category, string Name, string Path, ResourceKind Kind, long Size);

/// <summary>Recurso que un gráfico usa y ya no está en su ruta.</summary>
public sealed record MissingResource(Guid GraphicId, string GraphicName, string? ElementKey, string ElementName, string Path);

/// <summary>
/// Biblioteca de recursos compartida entre proyectos: %APPDATA%\LiveStream\library\&lt;categoría&gt;\. Importar copia el
/// archivo (el original se puede borrar o mover sin romper los gráficos) tras comprobar extensión, tamaño y, en las
/// imágenes, que el contenido sea realmente de ese formato.
/// </summary>
public sealed class ResourceLibrary(string? root = null)
{
    public const string ImageFilter = "Imágenes (*.png *.jpg *.jpeg *.gif *.webp *.bmp)";
    public const string VideoFilter = "Videos (*.mp4 *.mov *.webm *.mkv *.m4v)";
    public const string DefaultCategory = "General";
    private const long MaxImageBytes = 50L * 1024 * 1024;
    private const long MaxVideoBytes = 2L * 1024 * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".webm", ".mkv", ".m4v"];

    public string Root { get; } = root ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveStream", "library");

    public static ResourceKind? KindOf(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(ext) ? ResourceKind.Image : VideoExtensions.Contains(ext) ? ResourceKind.Video : null;
    }

    public IReadOnlyList<string> Categories() =>
        Directory.Exists(Root)
            ? Directory.GetDirectories(Root).Select(System.IO.Path.GetFileName).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];

    public IReadOnlyList<ResourceItem> List(string? category = null)
    {
        if (!Directory.Exists(Root))
            return [];
        var dirs = category is null ? Directory.GetDirectories(Root) : [System.IO.Path.Combine(Root, ValidCategory(category))];
        return dirs.Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d).Select(f => (Dir: d, File: new FileInfo(f))))
            .Where(x => KindOf(x.File.Name) is not null)
            .Select(x => new ResourceItem(System.IO.Path.GetFileName(x.Dir), x.File.Name, x.File.FullName, KindOf(x.File.Name)!.Value, x.File.Length))
            .OrderBy(r => r.Category, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Copia un archivo a la biblioteca. Lanza <see cref="InvalidDataException"/> con el motivo si no es válido.</summary>
    public ResourceItem Import(string source, string category = DefaultCategory)
    {
        var info = new FileInfo(source);
        if (!info.Exists)
            throw new InvalidDataException($"No existe el archivo: {source}");
        if (info.Extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("libobs no muestra SVG: exporta el logotipo a PNG (con transparencia) e impórtalo.");
        var kind = KindOf(info.Name) ?? throw new InvalidDataException(
            $"Formato no admitido ({info.Extension}). Imágenes: png, jpg, gif, webp, bmp. Videos: mp4, mov, webm, mkv, m4v.");
        if (info.Length == 0)
            throw new InvalidDataException("El archivo está vacío.");
        if (info.Length > (kind == ResourceKind.Image ? MaxImageBytes : MaxVideoBytes))
            throw new InvalidDataException(kind == ResourceKind.Image ? "La imagen pasa de 50 MB." : "El video pasa de 2 GB.");
        if (kind == ResourceKind.Image && !LooksLikeImage(source))
            throw new InvalidDataException("El contenido del archivo no es una imagen válida (la extensión no coincide con su formato).");

        var dir = Directory.CreateDirectory(System.IO.Path.Combine(Root, ValidCategory(category))).FullName;
        var name = SafeFileName(System.IO.Path.GetFileNameWithoutExtension(info.Name)) + info.Extension.ToLowerInvariant();
        var target = System.IO.Path.Combine(dir, name);
        for (var i = 2; File.Exists(target); i++)
            target = System.IO.Path.Combine(dir, $"{System.IO.Path.GetFileNameWithoutExtension(name)} ({i}){info.Extension.ToLowerInvariant()}");
        File.Copy(info.FullName, target);
        return new ResourceItem(System.IO.Path.GetFileName(dir), System.IO.Path.GetFileName(target), target, kind, info.Length);
    }

    public void Delete(ResourceItem item)
    {
        var full = System.IO.Path.GetFullPath(item.Path);
        if (!full.StartsWith(System.IO.Path.GetFullPath(Root) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Solo se pueden borrar archivos de la biblioteca.");
        File.Delete(full);
    }

    /// <summary>Recursos que usan los gráficos y no existen (imágenes y videos del diseño libre, logos de rótulos).</summary>
    public static IReadOnlyList<MissingResource> Missing(IEnumerable<Graphic> graphics)
    {
        var missing = new List<MissingResource>();
        foreach (var g in graphics)
        {
            foreach (var e in g.Design ?? [])
                if (e is { Kind: DesignElementKind.Image or DesignElementKind.Video, Path: { Length: > 0 } path } && !File.Exists(path))
                    missing.Add(new MissingResource(g.Id, g.Name, e.Key, e.Name, path));
            if (g.Field("logo") is { Length: > 0 } logo && !File.Exists(logo))
                missing.Add(new MissingResource(g.Id, g.Name, null, "Logo", logo));
        }
        return missing;
    }

    private static string ValidCategory(string category)
    {
        var c = category.Trim();
        if (c.Length is 0 or > 40 || c.Any(ch => !(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_')))
            throw new InvalidDataException("La categoría solo admite letras, números, espacios, «-» y «_» (máximo 40).");
        return c;
    }

    private static string SafeFileName(string name)
    {
        var clean = new string(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' ? c : '_').ToArray()).Trim();
        return clean.Length == 0 ? "recurso" : clean[..Math.Min(clean.Length, 80)];
    }

    /// <summary>Firma de los primeros bytes: PNG, JPEG, GIF, BMP o WebP.</summary>
    internal static bool LooksLikeImage(string path)
    {
        Span<byte> head = stackalloc byte[12];
        using var file = File.OpenRead(path);
        var read = file.Read(head);
        if (read < 4)
            return false;
        return head[..4].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 })
            || head[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF })
            || head[..3].SequenceEqual("GIF"u8)
            || head[..2].SequenceEqual("BM"u8)
            || (read >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8));
    }
}
