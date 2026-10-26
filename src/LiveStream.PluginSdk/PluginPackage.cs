using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveStream.PluginSdk;

/// <summary>
/// Paquete .lsplugin: un ZIP con plugin.json en la raíz y los archivos del plugin. Se comprueba entero antes de
/// extraer nada: rutas dentro de la carpeta, número de archivos y tamaño descomprimido acotados, entry presente.
/// </summary>
public static class PluginPackage
{
    public const string Extension = ".lsplugin";
    public const long MaxPackageBytes = 100L * 1024 * 1024;
    public const long MaxExtractedBytes = 300L * 1024 * 1024;
    public const int MaxEntries = 5000;

    /// <summary>Lee y valida el paquete sin extraerlo. Lanza <see cref="PluginValidationException"/> si algo no cuadra.</summary>
    public static PluginManifest Inspect(string packagePath)
    {
        using var zip = Open(packagePath);
        return Validate(zip);
    }

    /// <summary>Valida y extrae en <paramref name="directory"/> (que no debe existir). Devuelve el manifiesto.</summary>
    public static PluginManifest Extract(string packagePath, string directory)
    {
        using var zip = Open(packagePath);
        var manifest = Validate(zip);
        if (Directory.Exists(directory))
            throw new IOException($"La carpeta de destino ya existe: {directory}");
        var root = Path.GetFullPath(Directory.CreateDirectory(directory).FullName) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries.Where(e => !IsDirectory(e)))
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new PluginValidationException($"Ruta fuera del paquete: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
        return manifest;
    }

    /// <summary>Empaqueta una carpeta con plugin.json. Orden y fechas fijos: el mismo contenido da el mismo SHA-256.</summary>
    public static PluginManifest Pack(string sourceDirectory, string packagePath)
    {
        var manifestPath = Path.Combine(sourceDirectory, PluginManifest.FileName);
        if (!File.Exists(manifestPath))
            throw new PluginValidationException($"Falta {PluginManifest.FileName} en {sourceDirectory}.");
        var manifest = PluginManifest.Parse(File.ReadAllText(manifestPath));
        if (!File.Exists(Path.Combine(sourceDirectory, manifest.Entry)))
            throw new PluginValidationException($"No existe el ejecutable de entry: {manifest.Entry}");

        var stamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                         .Select(f => Path.GetRelativePath(sourceDirectory, f).Replace('\\', '/'))
                         .Where(f => !f.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                         .Order(StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(file, CompressionLevel.Optimal);
                entry.LastWriteTime = stamp;
                using var input = File.OpenRead(Path.Combine(sourceDirectory, file));
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        Inspect(packagePath);
        return manifest;
    }

    public static string Sha256(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    private static ZipArchive Open(string packagePath)
    {
        var info = new FileInfo(packagePath);
        if (!info.Exists)
            throw new PluginValidationException($"No existe el paquete: {packagePath}");
        if (info.Length > MaxPackageBytes)
            throw new PluginValidationException($"El paquete pasa de {MaxPackageBytes / 1024 / 1024} MB.");
        try
        {
            return ZipFile.OpenRead(packagePath);
        }
        catch (InvalidDataException)
        {
            throw new PluginValidationException("El paquete no es un archivo .lsplugin válido (ZIP dañado).");
        }
    }

    private static bool IsDirectory(ZipArchiveEntry e) => e.FullName.EndsWith('/') && e.Length == 0;

    private static PluginManifest Validate(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries)
            throw new PluginValidationException($"El paquete tiene más de {MaxEntries} archivos.");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!PluginManifest.IsSafeRelativePath(entry.FullName.TrimEnd('/')))
                throw new PluginValidationException($"Ruta no permitida en el paquete: {entry.FullName}");
            total += entry.Length;
            if (total > MaxExtractedBytes)
                throw new PluginValidationException($"El contenido descomprimido pasa de {MaxExtractedBytes / 1024 / 1024} MB.");
        }
        var manifestEntry = zip.GetEntry(PluginManifest.FileName)
            ?? throw new PluginValidationException($"El paquete no tiene {PluginManifest.FileName} en la raíz.");
        if (manifestEntry.Length > 256 * 1024)
            throw new PluginValidationException("plugin.json es demasiado grande.");
        string json;
        using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
            json = reader.ReadToEnd();
        var manifest = PluginManifest.Parse(json);
        if (zip.GetEntry(manifest.Entry.Replace('\\', '/')) is null)
            throw new PluginValidationException($"El paquete no contiene el ejecutable de entry: {manifest.Entry}");
        return manifest;
    }
}

/// <summary>
/// Firma de un paquete: identidad, versión, tamaño y SHA-256 firmados con ECDSA P-256 por el publicador. Va en
/// el archivo .sig.json junto al paquete y en cada entrada del catálogo. El servidor no puede alterarla.
/// </summary>
public sealed record PackageSignature(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("signature")] string Signature)
{
    public const string FileSuffix = ".sig.json";

    public static string SignedText(string id, string version, long size, string sha256) =>
        $"livestream-plugin-v1\n{id}\n{version}\n{size}\n{sha256.ToLowerInvariant()}";

    public static PackageSignature Create(string packagePath, string privateKeyPem)
    {
        var manifest = PluginPackage.Inspect(packagePath);
        var size = new FileInfo(packagePath).Length;
        var sha = PluginPackage.Sha256(packagePath);
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(Encoding.UTF8.GetBytes(SignedText(manifest.Id, manifest.Version, size, sha)), HashAlgorithmName.SHA256);
        return new PackageSignature(manifest.Id, manifest.Version, size, sha, Convert.ToBase64String(signature));
    }

    public bool Verify(string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(Encoding.UTF8.GetBytes(SignedText(Id, Version, Size, Sha256)), Convert.FromBase64String(Signature),
                HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>La firma es válida y corresponde exactamente a este archivo.</summary>
    public string? VerifyPackage(string packagePath, string publicKeyPem)
    {
        if (!Verify(publicKeyPem))
            return "La firma no es válida o no es del publicador de confianza.";
        var info = new FileInfo(packagePath);
        if (info.Length != Size || !string.Equals(PluginPackage.Sha256(packagePath), Sha256, StringComparison.OrdinalIgnoreCase))
            return "El paquete no coincide con el firmado (tamaño o SHA-256).";
        var manifest = PluginPackage.Inspect(packagePath);
        return manifest.Id != Id || manifest.Version != Version ? "El manifiesto del paquete no coincide con la firma." : null;
    }

    public string ToJson() => JsonSerializer.Serialize(this, PluginManifest.Json);

    public static PackageSignature FromJson(string json) =>
        JsonSerializer.Deserialize<PackageSignature>(json, PluginManifest.Json) ?? throw new PluginValidationException("Firma vacía.");
}

/// <summary>Entrada del catálogo del marketplace: la firma del paquete más los datos para mostrarlo.</summary>
public sealed record CatalogEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("permissions")] IReadOnlyList<string> Permissions,
    [property: JsonPropertyName("apiVersion")] int ApiVersion,
    [property: JsonPropertyName("minAppVersion")] string MinAppVersion,
    [property: JsonPropertyName("dependencies")] IReadOnlyDictionary<string, string> Dependencies,
    [property: JsonPropertyName("changelog")] string? Changelog,
    [property: JsonPropertyName("homepage")] string? Homepage,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
    [property: JsonPropertyName("yanked")] bool Yanked = false)
{
    public PackageSignature PackageSignature => new(Id, Version, Size, Sha256, Signature);

    public static CatalogEntry From(PluginManifest m, PackageSignature s, string downloadUrl, DateTimeOffset publishedAt) =>
        new(m.Id, m.Version, s.Size, s.Sha256, s.Signature, m.Name, m.Author, m.Description, m.Category, m.Tags ?? [],
            m.PermissionList, m.ApiVersion, m.MinAppVersion, m.Dependencies ?? new Dictionary<string, string>(), m.Changelog, m.Homepage,
            publishedAt, downloadUrl);
}

public sealed record Catalog(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("entries")] IReadOnlyList<CatalogEntry> Entries)
{
    public const string CurrentFormat = "livestream-catalog-v1";
}
