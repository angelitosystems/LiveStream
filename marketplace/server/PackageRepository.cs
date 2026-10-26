using System.IO.Compression;
using System.Text.Json;
using LiveStream.PluginSdk;

namespace LiveStream.Marketplace.Server;

/// <summary>
/// Paquetes publicados en DATA_DIR: packages/&lt;id&gt;/&lt;versión&gt;.lsplugin (+ .sig.json) e index.json. Las versiones
/// son inmutables: no se sobrescriben ni se borran, solo se retiran (yank), lo que permite volver atrás sin perder nada.
/// Cada cambio del índice deja una copia en history/ (las últimas 50).
/// </summary>
public sealed class PackageRepository(string dataDirectory, string publisherPublicKey)
{
    private const int HistoryToKeep = 50;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string IndexPath => Path.Combine(dataDirectory, "index.json");

    public string PackagePath(string id, string version) => Path.Combine(dataDirectory, "packages", id, version + PluginPackage.Extension);

    public async Task<IReadOnlyList<CatalogEntry>> ListAsync(CancellationToken ct)
    {
        if (!File.Exists(IndexPath))
            return [];
        await using var stream = File.OpenRead(IndexPath);
        return await JsonSerializer.DeserializeAsync<List<CatalogEntry>>(stream, PluginManifest.Json, ct) ?? [];
    }

    /// <summary>Valida el paquete y su firma con la clave del publicador y lo añade. Devuelve la entrada o lanza.</summary>
    public async Task<CatalogEntry> PublishAsync(string uploadedPackage, string signatureJson, CancellationToken ct)
    {
        var signature = PackageSignature.FromJson(signatureJson);
        if (signature.VerifyPackage(uploadedPackage, publisherPublicKey) is { } error)
            throw new PluginValidationException(error);
        var manifest = PluginPackage.Inspect(uploadedPackage);

        await _lock.WaitAsync(ct);
        try
        {
            var entries = (await ListAsync(ct)).ToList();
            if (entries.Any(e => e.Id == manifest.Id && e.Version == manifest.Version))
                throw new InvalidOperationException($"{manifest.Id} {manifest.Version} ya está publicado. Las versiones no se sobrescriben: sube una versión nueva.");
            var target = PackagePath(manifest.Id, manifest.Version);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(uploadedPackage, target);
            await File.WriteAllTextAsync(target + PackageSignature.FileSuffix, signature.ToJson(), ct);
            var entry = CatalogEntry.From(manifest, signature, $"v1/packages/{manifest.Id}/{manifest.Version}", DateTimeOffset.UtcNow);
            entries.Add(entry);
            await SaveAsync(entries, ct);
            return entry;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CatalogEntry?> SetYankedAsync(string id, string version, bool yanked, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = (await ListAsync(ct)).ToList();
            var index = entries.FindIndex(e => e.Id == id && e.Version == version);
            if (index < 0)
                return null;
            entries[index] = entries[index] with { Yanked = yanked };
            await SaveAsync(entries, ct);
            return entries[index];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>ZIP con índice, historial y paquetes (copia de seguridad completa).</summary>
    public async Task WriteBackupAsync(Stream output, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dataDirectory, file).Replace('\\', '/');
                if (relative.StartsWith("tmp/", StringComparison.Ordinal))
                    continue;
                await using var entry = await zip.CreateEntry(relative, CompressionLevel.Fastest).OpenAsync(ct);
                await using var input = File.OpenRead(file);
                await input.CopyToAsync(entry, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Comprueba que el volumen de datos existe y admite escritura.</summary>
    public bool IsHealthy()
    {
        try
        {
            var probe = Path.Combine(dataDirectory, "tmp", ".health");
            Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
            File.WriteAllText(probe, DateTime.UtcNow.ToString("O"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string NewTempFile()
    {
        var dir = Directory.CreateDirectory(Path.Combine(dataDirectory, "tmp")).FullName;
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + PluginPackage.Extension);
    }

    private async Task SaveAsync(List<CatalogEntry> entries, CancellationToken ct)
    {
        Directory.CreateDirectory(dataDirectory);
        var json = JsonSerializer.Serialize(entries, PluginManifest.Json);
        var temp = IndexPath + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct);
        File.Move(temp, IndexPath, overwrite: true);

        var history = Directory.CreateDirectory(Path.Combine(dataDirectory, "history")).FullName;
        await File.WriteAllTextAsync(Path.Combine(history, $"index-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json"), json, ct);
        foreach (var old in Directory.GetFiles(history, "index-*.json").Order(StringComparer.Ordinal).SkipLast(HistoryToKeep))
            File.Delete(old);
    }
}
