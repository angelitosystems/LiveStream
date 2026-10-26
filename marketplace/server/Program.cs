using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using LiveStream.Marketplace.Server;
using LiveStream.PluginSdk;
using Microsoft.AspNetCore.HttpOverrides;

// Comprobación de salud para el HEALTHCHECK del contenedor (la imagen de ASP.NET no trae curl).
if (args.Contains("--healthcheck"))
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        return (await probe.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = false);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

// ---- Configuración (variables de entorno) ----
var config = builder.Configuration;
var adminToken = config["ADMIN_TOKEN"] ?? "";
var publicBaseUrl = (config["PUBLIC_BASE_URL"] ?? "").TrimEnd('/');
var dataDir = config["DATA_DIR"] ?? "/data";
var maxPackageBytes = long.Parse(config["MAX_PACKAGE_MB"] ?? "100") * 1024 * 1024;
var publisherKey = config["PUBLISHER_PUBLIC_KEY"] ?? (config["PUBLISHER_PUBLIC_KEY_FILE"] is { Length: > 0 } keyFile ? File.ReadAllText(keyFile) : "");
if (publisherKey.Length > 0 && !publisherKey.Contains("-----BEGIN"))
    publisherKey = $"-----BEGIN PUBLIC KEY-----\n{publisherKey.Trim()}\n-----END PUBLIC KEY-----";

var problems = new List<string>();
if (adminToken.Length < 32)
    problems.Add("ADMIN_TOKEN debe tener al menos 32 caracteres (p. ej. openssl rand -hex 32).");
if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != "https" && !baseUri.IsLoopback))
    problems.Add("PUBLIC_BASE_URL debe ser la URL https pública del servicio (p. ej. https://marketplace.tu-dominio).");
try
{
    using var key = ECDsa.Create();
    key.ImportFromPem(publisherKey);
}
catch (Exception ex) when (ex is ArgumentException or CryptographicException)
{
    problems.Add("PUBLISHER_PUBLIC_KEY no es una clave pública ECDSA válida (salida de «lsplugin keygen»).");
}
if (problems.Count > 0)
{
    foreach (var problem in problems)
        Console.Error.WriteLine($"Configuración no válida: {problem}");
    return 2;
}

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxPackageBytes + 1024 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Detrás del proxy de Coolify (Traefik/Caddy): la IP real del cliente para limitar peticiones.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("public", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("admin", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddSingleton(new PackageRepository(dataDir, publisherKey));

var app = builder.Build();
app.UseForwardedHeaders();
app.UseRateLimiter();
var repo = app.Services.GetRequiredService<PackageRepository>();
var log = app.Logger;

CatalogEntry Public(CatalogEntry e) => e with { DownloadUrl = $"{publicBaseUrl}/{e.DownloadUrl}" };

app.MapGet("/health", () => repo.IsHealthy()
    ? Results.Ok(new { status = "ok", version = typeof(PackageRepository).Assembly.GetName().Version?.ToString(3) })
    : Results.Problem("El volumen de datos no admite escritura.", statusCode: 503));

var v1 = app.MapGroup("/v1").RequireRateLimiting("public");
v1.MapGet("/catalog", async (HttpContext http, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "public, max-age=60";
    var entries = (await repo.ListAsync(ct)).Where(e => !e.Yanked).Select(Public).ToList();
    return Results.Ok(new Catalog(Catalog.CurrentFormat, DateTimeOffset.UtcNow, entries));
});
v1.MapGet("/packages/{id}/{version}", async (string id, string version, CancellationToken ct) =>
{
    var entry = (await repo.ListAsync(ct)).FirstOrDefault(e => e.Id == id && e.Version == version);
    if (entry is null)
        return Results.NotFound();
    if (entry.Yanked)
        return Results.StatusCode(StatusCodes.Status410Gone);
    return Results.File(repo.PackagePath(id, version), "application/octet-stream", $"{id}-{version}{PluginPackage.Extension}",
        entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{entry.Sha256}\""));
});

// ---- Administración (solo el publicador, con ADMIN_TOKEN) ----
var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(adminToken));
var admin = app.MapGroup("/v1/admin").RequireRateLimiting("admin").AddEndpointFilter(async (ctx, next) =>
{
    var header = ctx.HttpContext.Request.Headers.Authorization.ToString();
    var given = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), tokenHash))
    {
        log.LogWarning("Acceso de administración rechazado desde {Ip}", ctx.HttpContext.Connection.RemoteIpAddress);
        return Results.Unauthorized();
    }
    return await next(ctx);
});

admin.MapPost("/packages", async (HttpRequest request, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Envía multipart/form-data con «package» y «signature»." });
    var form = await request.ReadFormAsync(ct);
    if (form.Files.GetFile("package") is not { } file || form["signature"].ToString() is not { Length: > 0 } signature)
        return Results.BadRequest(new { error = "Faltan «package» o «signature»." });
    if (file.Length > maxPackageBytes)
        return Results.BadRequest(new { error = $"El paquete pasa de {maxPackageBytes / 1024 / 1024} MB." });
    var temp = repo.NewTempFile();
    try
    {
        await using (var output = File.Create(temp))
            await file.CopyToAsync(output, ct);
        var entry = await repo.PublishAsync(temp, signature, ct);
        log.LogInformation("Publicado {Id} {Version} ({Size} bytes)", entry.Id, entry.Version, entry.Size);
        return Results.Created($"/v1/packages/{entry.Id}/{entry.Version}", Public(entry));
    }
    catch (PluginValidationException ex)
    {
        log.LogWarning("Paquete rechazado: {Reason}", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    finally
    {
        File.Delete(temp);
    }
}).DisableAntiforgery();

admin.MapGet("/packages", async (CancellationToken ct) =>
    Results.Ok(new { entries = (await repo.ListAsync(ct)).Select(Public) }));

foreach (var (action, yanked) in new[] { ("yank", true), ("unyank", false) })
{
    admin.MapPost($"/packages/{{id}}/{{version}}/{action}", async (string id, string version, CancellationToken ct) =>
    {
        var entry = await repo.SetYankedAsync(id, version, yanked, ct);
        if (entry is null)
            return Results.NotFound();
        log.LogInformation("{Action} {Id} {Version}", action, id, version);
        return Results.Ok(Public(entry));
    });
}

admin.MapGet("/backup", async (HttpContext http, CancellationToken ct) =>
{
    // Se genera en un temporal: ZipArchive escribe su índice de forma síncrona y Kestrel no admite E/S síncrona.
    var temp = Path.ChangeExtension(repo.NewTempFile(), ".zip");
    await using (var file = File.Create(temp))
        await repo.WriteBackupAsync(file, ct);
    http.Response.OnCompleted(() =>
    {
        File.Delete(temp);
        return Task.CompletedTask;
    });
    return Results.File(temp, "application/zip", $"marketplace-{DateTime.UtcNow:yyyyMMddHHmmss}.zip");
});

log.LogInformation("Marketplace en marcha. Datos: {Dir}. URL pública: {Url}", dataDir, publicBaseUrl);
await app.RunAsync();
return 0;
