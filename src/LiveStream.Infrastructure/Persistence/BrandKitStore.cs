using System.Text.Json;
using LiveStream.Application.Graphics;

namespace LiveStream.Infrastructure.Persistence;

/// <summary>
/// Kit de marca de este usuario en %APPDATA%\LiveStream\brand.json. Exportar produce un archivo .lsbrand portable con el
/// logo incrustado; importar lo extrae a %APPDATA%\LiveStream\brand\ y valida todo antes de aceptarlo.
/// </summary>
public sealed class BrandKitStore(string? directory = null)
{
    public const string FileFilter = "Kit de marca LiveStream (*.lsbrand)";
    private const string Format = "livestream-brand-v1";
    private const long MaxLogoBytes = 5 * 1024 * 1024;
    private static readonly string[] LogoExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record ExportFile(string Format, BrandKit Brand, string? LogoName, string? LogoData);

    public string Directory { get; } = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveStream");

    private string KitPath => Path.Combine(Directory, "brand.json");

    /// <summary>Un archivo dañado no impide trabajar: se usa la marca por defecto.</summary>
    public BrandKit Load()
    {
        try
        {
            var kit = File.Exists(KitPath) ? JsonSerializer.Deserialize<BrandKit>(File.ReadAllText(KitPath), Options) : null;
            return kit is { Style: not null } && kit.Validate() is null ? kit : BrandKit.Default;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return BrandKit.Default;
        }
    }

    public void Save(BrandKit kit)
    {
        if (kit.Validate() is { } error)
            throw new ArgumentException(error);
        System.IO.Directory.CreateDirectory(Directory);
        var temp = KitPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(kit, Options));
        File.Move(temp, KitPath, overwrite: true);
    }

    public void Export(BrandKit kit, string path)
    {
        string? logoName = null, logoData = null;
        if (kit.Logo.Length > 0)
        {
            var logo = new FileInfo(kit.Logo);
            if (!logo.Exists)
                throw new FileNotFoundException($"No se encuentra el logo: {kit.Logo}");
            if (logo.Length > MaxLogoBytes)
                throw new InvalidDataException("El logo pesa más de 5 MB.");
            logoName = logo.Name;
            logoData = Convert.ToBase64String(File.ReadAllBytes(logo.FullName));
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new ExportFile(Format, kit with { Logo = "" }, logoName, logoData), Options));
    }

    /// <summary>Lee un .lsbrand, extrae su logo y devuelve el kit (sin guardarlo como actual). Lanza InvalidDataException si no es válido.</summary>
    public BrandKit Import(string path)
    {
        ExportFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ExportFile>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            file = null;
        }
        if (file is not { Format: Format, Brand.Style: not null })
            throw new InvalidDataException("El archivo no es un kit de marca de LiveStream.");
        var kit = file.Brand with { Logo = "" };
        if (kit.Validate() is { } error)
            throw new InvalidDataException(error);
        if (file.LogoData is null)
            return kit;

        // Solo el nombre (sin rutas) y solo extensiones de imagen: el archivo importado no decide dónde se escribe ni qué.
        var name = Path.GetFileName(file.LogoName ?? "");
        if (!LogoExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            throw new InvalidDataException("El logo del kit no es una imagen admitida (png, jpg, webp o gif).");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(file.LogoData);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("El logo del kit está dañado.");
        }
        if (bytes.Length > MaxLogoBytes)
            throw new InvalidDataException("El logo del kit pesa más de 5 MB.");
        var logoDir = System.IO.Directory.CreateDirectory(Path.Combine(Directory, "brand")).FullName;
        var logoPath = Path.Combine(logoDir, name);
        File.WriteAllBytes(logoPath, bytes);
        return kit with { Logo = logoPath };
    }
}
