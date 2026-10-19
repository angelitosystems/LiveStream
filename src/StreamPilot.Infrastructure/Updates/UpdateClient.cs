using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamPilot.Infrastructure.Updates;

/// <summary>Manifiesto publicado junto al instalador. Signature firma <see cref="SignedText"/> con ECDSA P-256 (SHA-256).</summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("signature")] string Signature)
{
    /// <summary>Texto canónico que se firma: cualquier cambio en versión, URL, tamaño o hash invalida la firma.</summary>
    public string SignedText => $"streampilot-update-v1\n{Version}\n{Url}\n{Size}\n{Sha256.ToLowerInvariant()}";
}

public abstract record UpdateCheckResult
{
    public sealed record UpToDate(string Version) : UpdateCheckResult;
    public sealed record Available(UpdateManifest Manifest) : UpdateCheckResult;
    /// <summary>Manifiesto rechazado (firma, formato o dirección no válidos). Nunca se descarga nada.</summary>
    public sealed record Rejected(string Reason) : UpdateCheckResult;
}

/// <summary>
/// Actualizaciones firmadas. La aplicación solo acepta un manifiesto firmado por la clave de publicación cuya parte
/// pública lleva compilada, y solo ejecuta un instalador cuyo tamaño y SHA-256 coinciden con los firmados.
/// </summary>
public sealed class UpdateClient(HttpClient http, string publicKeyPem)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Clave pública compilada en esta versión (null si se compiló sin clave: actualizaciones desactivadas).</summary>
    public static string? EmbeddedPublicKey()
    {
        using var stream = typeof(UpdateClient).Assembly.GetManifestResourceStream("release-public-key.pem");
        return stream is null ? null : new StreamReader(stream).ReadToEnd();
    }

    public static Version CurrentVersion =>
        Version.Parse((Assembly.GetEntryAssembly() ?? typeof(UpdateClient).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");

    public bool Verify(UpdateManifest manifest)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(Encoding.UTF8.GetBytes(manifest.SignedText), Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(Uri feed, Version current, CancellationToken ct)
    {
        if (!IsAllowed(feed))
            return new UpdateCheckResult.Rejected("El canal de actualizaciones debe usar HTTPS.");
        UpdateManifest? manifest;
        try
        {
            manifest = await http.GetFromJsonAsync<UpdateManifest>(feed, Json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return new UpdateCheckResult.Rejected("El manifiesto de actualización no es válido.");
        }
        if (manifest is null || string.IsNullOrEmpty(manifest.Signature))
            return new UpdateCheckResult.Rejected("El manifiesto no está firmado.");
        if (!Verify(manifest))
            return new UpdateCheckResult.Rejected("La firma del manifiesto no es válida: no se descargará nada.");
        if (!Version.TryParse(manifest.Version, out var offered))
            return new UpdateCheckResult.Rejected("Versión no válida en el manifiesto.");
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var url) || !IsAllowed(url))
            return new UpdateCheckResult.Rejected("La descarga debe usar HTTPS.");
        return offered > current ? new UpdateCheckResult.Available(manifest) : new UpdateCheckResult.UpToDate(current.ToString(3));
    }

    /// <summary>Descarga el instalador y comprueba tamaño y SHA-256 firmados. Devuelve la ruta o lanza si no coincide.</summary>
    public async Task<string> DownloadAsync(UpdateManifest manifest, string directory, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Verify(manifest))
            throw new InvalidDataException("Manifiesto sin firma válida.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"StreamPilot-Setup-{manifest.Version}.exe");
        using (var response = await http.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > manifest.Size)
                    break; // más grande de lo firmado: se rechaza abajo sin seguir descargando
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(manifest.Size == 0 ? 1 : (double)total / manifest.Size);
            }
        }

        var info = new FileInfo(path);
        string hash;
        await using (var file = File.OpenRead(path))
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
        if (info.Length != manifest.Size || !string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("El instalador descargado no coincide con el firmado (tamaño o SHA-256): se ha borrado.");
        }
        return path;
    }

    /// <summary>HTTPS siempre; HTTP solo a este mismo equipo (pruebas y servidores de distribución locales).</summary>
    private static bool IsAllowed(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
}
