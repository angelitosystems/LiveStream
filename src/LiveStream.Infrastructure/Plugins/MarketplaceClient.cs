using System.Net.Http.Json;
using System.Text.Json;
using LiveStream.PluginSdk;

namespace LiveStream.Infrastructure.Plugins;

/// <param name="Entries">Solo entradas con firma válida del publicador; la última versión compatible de cada plugin.</param>
/// <param name="Rejected">Entradas descartadas (firma no válida o incompatibles), para avisar.</param>
public sealed record MarketplaceCatalog(IReadOnlyList<CatalogEntry> Entries, int Rejected, string Source);

/// <summary>
/// Catálogo remoto (HTTPS; HTTP solo en este equipo) o repositorio local (carpeta con .lsplugin y su .sig.json).
/// El servidor no es de confianza: cada entrada se verifica con la clave del publicador y cada descarga con su SHA-256.
/// </summary>
public sealed class MarketplaceClient(HttpClient http, string? trustedPublicKey, Version appVersion)
{
    public async Task<MarketplaceCatalog> GetRemoteAsync(Uri baseUrl, CancellationToken ct)
    {
        if (!IsAllowed(baseUrl))
            throw new PluginValidationException("El catálogo debe usar HTTPS.");
        Catalog? catalog;
        try
        {
            catalog = await http.GetFromJsonAsync<Catalog>(new Uri(baseUrl.ToString().TrimEnd('/') + "/v1/catalog"), PluginManifest.Json, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new PluginValidationException("El catálogo recibido no es válido.");
        }
        if (catalog is not { Format: Catalog.CurrentFormat })
            throw new PluginValidationException("Formato de catálogo no soportado.");
        return Select(catalog.Entries, baseUrl.ToString());
    }

    public MarketplaceCatalog GetLocal(string directory)
    {
        var entries = new List<CatalogEntry>();
        var rejected = 0;
        foreach (var package in Directory.EnumerateFiles(directory, "*" + PluginPackage.Extension))
        {
            try
            {
                var signature = PackageSignature.FromJson(File.ReadAllText(package + PackageSignature.FileSuffix));
                var manifest = PluginPackage.Inspect(package);
                entries.Add(CatalogEntry.From(manifest, signature, package, File.GetLastWriteTimeUtc(package)));
            }
            catch (Exception ex) when (ex is IOException or PluginValidationException or JsonException)
            {
                rejected++;
            }
        }
        var result = Select(entries, directory);
        return result with { Rejected = result.Rejected + rejected };
    }

    /// <summary>Última versión no retirada, compatible y con firma válida de cada plugin.</summary>
    private MarketplaceCatalog Select(IEnumerable<CatalogEntry> entries, string source)
    {
        var valid = new List<CatalogEntry>();
        var rejected = 0;
        foreach (var entry in entries)
        {
            var ok = trustedPublicKey is not null && entry.PackageSignature.Verify(trustedPublicKey)
                && PluginVersion.TryParse(entry.Version, out _) && PluginVersion.TryParse(entry.MinAppVersion, out var minApp)
                && minApp <= appVersion && PluginManifest.SupportedApiVersions.Contains(entry.ApiVersion);
            if (!ok)
                rejected++;
            else if (!entry.Yanked)
                valid.Add(entry);
        }
        var latest = valid.GroupBy(e => e.Id).Select(g => g.MaxBy(e => PluginVersion.Parse(e.Version))!)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new MarketplaceCatalog(latest, rejected, source);
    }

    /// <summary>Descarga (o copia, si es local) el paquete a un archivo temporal y comprueba tamaño y SHA-256 firmados.</summary>
    public async Task<string> DownloadAsync(CatalogEntry entry, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"livestream-plugin-{Guid.NewGuid():N}{PluginPackage.Extension}");
        try
        {
            if (Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var url) && !url.IsFile)
            {
                if (!IsAllowed(url))
                    throw new PluginValidationException("La descarga debe usar HTTPS.");
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(path);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > entry.Size)
                        throw new PluginValidationException("La descarga es más grande que el paquete firmado.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            else
            {
                File.Copy(entry.DownloadUrl, path);
            }
            if (trustedPublicKey is null || entry.PackageSignature.VerifyPackage(path, trustedPublicKey) is { } error)
                throw new PluginValidationException("El paquete descargado no coincide con el firmado: se ha descartado.");
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static bool IsAllowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
}

/// <summary>Clave pública del publicador de plugins compilada en la aplicación (tools/LiveStream.PluginTool keygen).</summary>
public static class PluginTrust
{
    public static string? EmbeddedPublicKey()
    {
        using var stream = typeof(PluginTrust).Assembly.GetManifestResourceStream("plugin-public-key.pem");
        return stream is null ? null : new StreamReader(stream).ReadToEnd();
    }
}
