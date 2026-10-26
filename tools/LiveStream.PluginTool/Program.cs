using System.Net.Http.Headers;
using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStream.PluginSdk;

// lsplugin: herramienta del publicador. El token de administración se lee de LS_MARKETPLACE_TOKEN (nunca de la línea
// de órdenes, para que no quede en el historial) y la clave privada nunca sale de este equipo.
const string TokenVariable = "LS_MARKETPLACE_TOKEN";
// Los plugins se ejecutan en Windows x64: el punto de entrada siempre es un .exe.
const string ExecutableExtension = "exe";

try
{
    return await RunAsync(args);
}
catch (Exception ex) when (ex is PluginValidationException or IOException or HttpRequestException or ArgumentException or UnauthorizedAccessException or CryptographicException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Console.WriteLine("""
            lsplugin — herramienta de plugins de LiveStream

              new <carpeta> --id <com.empresa.plugin> --name <nombre>   Crea un proyecto de plugin completo
              validate <carpeta|paquete.lsplugin>                      Comprueba manifiesto y paquete
              pack <carpeta> [-o salida.lsplugin]                      Empaqueta (reproducible)
              keygen [--private ruta.pem] [--public ruta.pem] [--force]
                                                                       Crea el par de claves del publicador; sin argumentos usa %USERPROFILE%\.livestream-plugins\private.pem y plugin-public-key.pem en la carpeta actual
              sign <paquete> --key <privada.pem>                       Escribe <paquete>.sig.json
              verify <paquete> --public <publica.pem>                  Comprueba paquete y firma
              publish <paquete> --server <url>                         Sube paquete + firma (token en LS_MARKETPLACE_TOKEN)
              yank|unyank <id> <versión> --server <url>                Retira o restaura una versión
              list --server <url>                                      Lista todas las versiones (incluidas retiradas)
              backup --server <url> -o copia.zip                       Descarga una copia de seguridad de los datos
            """);
        return 0;
    }

    var rest = args[1..];
    switch (args[0])
    {
        case "new":
        {
            var dir = Path.GetFullPath(Positional(rest, 0));
            var manifest = new PluginManifest(Option(rest, "--id", usage: "Falta --id. Uso: lsplugin new <carpeta> --id com.empresa.plugin --name \"Nombre\" [--project Nombre] [--author Autor]"),
                Option(rest, "--name", usage: "Falta --name (el nombre visible del plugin, entre comillas si tiene espacios)."), "0.1.0", 1,
                $"{ProjectName(rest)}.{ExecutableExtension}", Author: Option(rest, "--author", ""),
                Description: "Describe aquí qué hace el plugin.", Category: "General",
                MinAppVersion: "0.9.0", Permissions: [PluginPermissions.StudioRead]);
            if (manifest.Validate() is { } error)
                throw new PluginValidationException(error);
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                throw new IOException($"La carpeta {dir} ya tiene contenido.");
            WriteScaffold(dir, manifest, ProjectName(rest));
            Console.WriteLine($"""
                Creado el proyecto en {dir}:
                  {manifest.Id} "{manifest.Name}" (entry {manifest.Entry})

                Siguiente paso:
                  cd {dir}
                  dotnet publish -c Release
                  lsplugin validate bin\Release\net10.0\win-x64\
                  lsplugin pack bin\Release\net10.0\win-x64\

                Para probarlo sin firma: Herramientas → Marketplace de plugins → Modo desarrollador.
                Documentación: https://github.com/angelitosystems/LiveStream/blob/main/docs/plugins/SDK.md
                """);
            return 0;
        }

        case "validate":
        {
            var target = Positional(rest, 0);
            var manifest = Directory.Exists(target)
                ? PluginManifest.Parse(File.ReadAllText(Path.Combine(target, PluginManifest.FileName)))
                : PluginPackage.Inspect(target);
            Console.WriteLine($"Válido: {manifest.Id} {manifest.Version} (API {manifest.ApiVersion}, permisos: {string.Join(", ", manifest.PermissionList)})");
            return 0;
        }

        case "pack":
        {
            var dir = Positional(rest, 0);
            var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(dir, PluginManifest.FileName)));
            var output = Option(rest, "-o", $"{manifest.Id}-{manifest.Version}{PluginPackage.Extension}");
            if (File.Exists(output))
                File.Delete(output);
            PluginPackage.Pack(dir, output);
            Console.WriteLine($"Paquete: {output} ({new FileInfo(output).Length / 1024} KB, sha256 {PluginPackage.Sha256(output)})");
            return 0;
        }

        case "keygen":
        {
            // Sin argumentos usa las rutas recomendadas: privada en el perfil del usuario y pública junto a la consola.
            var privatePath = Path.GetFullPath(Option(rest, "--private",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".livestream-plugins", "private.pem")));
            var publicPath = Path.GetFullPath(Option(rest, "--public", "plugin-public-key.pem"));
            if ((File.Exists(privatePath) || File.Exists(publicPath)) && !rest.Contains("--force"))
                throw new IOException("Ya existe una clave. Usa --force solo si de verdad quieres sustituirla: los plugins firmados con la anterior dejarán de verificarse.");
            if (IsInsideGitRepository(privatePath))
                throw new ArgumentException("La clave privada no puede guardarse dentro de un repositorio git.");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
            File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
            RestrictToCurrentUser(privatePath);
            Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
            File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
            Console.WriteLine($"Clave privada: {privatePath} (haz copia de seguridad; no la subas a ningún sitio)");
            Console.WriteLine($"Clave pública: {publicPath}");
            return 0;
        }

        case "sign":
        {
            var package = Positional(rest, 0);
            var signature = PackageSignature.Create(package, File.ReadAllText(Option(rest, "--key",
                usage: "Falta --key. Uso: lsplugin sign <paquete>.lsplugin --key <private.pem>")));
            File.WriteAllText(package + PackageSignature.FileSuffix, signature.ToJson());
            Console.WriteLine($"Firmado: {package}{PackageSignature.FileSuffix} ({signature.Id} {signature.Version})");
            return 0;
        }

        case "verify":
        {
            var package = Positional(rest, 0);
            var signature = PackageSignature.FromJson(File.ReadAllText(package + PackageSignature.FileSuffix));
            if (signature.VerifyPackage(package, File.ReadAllText(Option(rest, "--public",
                usage: "Falta --public. Uso: lsplugin verify <paquete>.lsplugin --public plugin-public-key.pem"))) is { } error)
                throw new PluginValidationException(error);
            Console.WriteLine($"Firma válida: {signature.Id} {signature.Version}");
            return 0;
        }

        case "publish":
        {
            var package = Positional(rest, 0);
            var signaturePath = package + PackageSignature.FileSuffix;
            PluginPackage.Inspect(package);
            using var form = new MultipartFormDataContent
            {
                { new StreamContent(File.OpenRead(package)), "package", Path.GetFileName(package) },
                { new StringContent(File.ReadAllText(signaturePath)), "signature" },
            };
            var body = await SendAsync(rest, HttpMethod.Post, "v1/admin/packages", form);
            Console.WriteLine($"Publicado: {body}");
            return 0;
        }

        case "yank" or "unyank":
        {
            var body = await SendAsync(rest, HttpMethod.Post, $"v1/admin/packages/{Uri.EscapeDataString(Positional(rest, 0))}/{Uri.EscapeDataString(Positional(rest, 1))}/{args[0]}", null);
            Console.WriteLine(body);
            return 0;
        }

        case "list":
        {
            var body = await SendAsync(rest, HttpMethod.Get, "v1/admin/packages", null);
            foreach (var entry in JsonNode.Parse(body)!["entries"]!.AsArray())
                Console.WriteLine($"{entry!["id"],-40} {entry["version"],-10} {(entry["yanked"]!.GetValue<bool>() ? "RETIRADA" : "publicada")}  {entry["publishedAt"]}");
            return 0;
        }

        case "backup":
        {
            using var http = Client(rest);
            using var response = await http.GetAsync("v1/admin/backup");
            await EnsureAsync(response);
            var output = Option(rest, "-o", $"marketplace-backup-{DateTime.UtcNow:yyyyMMddHHmmss}.zip");
            await using (var file = File.Create(output))
                await response.Content.CopyToAsync(file);
            Console.WriteLine($"Copia guardada: {output}");
            return 0;
        }

        default:
            throw new ArgumentException($"Orden desconocida: {args[0]}. Usa «lsplugin help».");
    }
}

static async Task<string> SendAsync(string[] rest, HttpMethod method, string path, HttpContent? content)
{
    using var http = Client(rest);
    using var request = new HttpRequestMessage(method, path) { Content = content };
    using var response = await http.SendAsync(request);
    await EnsureAsync(response);
    return await response.Content.ReadAsStringAsync();
}

static HttpClient Client(string[] rest)
{
    var server = new Uri(Option(rest, "--server",
        usage: "Falta --server. Uso: lsplugin <orden> --server https://<marketplace> (el token va en la variable LS_MARKETPLACE_TOKEN).").TrimEnd('/') + "/");
    if (server.Scheme != Uri.UriSchemeHttps && !server.IsLoopback)
        throw new ArgumentException("El servidor debe usar HTTPS.");
    var token = Environment.GetEnvironmentVariable(TokenVariable);
    if (string.IsNullOrWhiteSpace(token))
        throw new ArgumentException($"Define la variable de entorno {TokenVariable} con el token de administración.");
    var http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromMinutes(5) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return http;
}

static async Task EnsureAsync(HttpResponseMessage response)
{
    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"El servidor respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
}

static string Positional(string[] rest, int index)
{
    var values = new List<string>();
    for (var i = 0; i < rest.Length; i++)
    {
        if (rest[i].StartsWith('-'))
        {
            if (rest[i] != "--force")
                i++;
            continue;
        }
        values.Add(rest[i]);
    }
    return index < values.Count ? values[index] : throw new ArgumentException("Faltan argumentos. Usa «lsplugin help».");
}

static string Option(string[] rest, string name, string? fallback = null, string? usage = null)
{
    var i = Array.IndexOf(rest, name);
    if (i >= 0 && i + 1 < rest.Length)
        return rest[i + 1];
    return fallback ?? throw new ArgumentException(usage ?? $"Falta {name}. Usa «lsplugin help».");
}

static bool IsInsideGitRepository(string path)
{
    for (var dir = Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
        if (Directory.Exists(Path.Combine(dir, ".git")))
            return true;
    return false;
}

static void RestrictToCurrentUser(string path)
{
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return;
    }
    var security = new FileSecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.FullControl, AccessControlType.Allow));
    new FileInfo(path).SetAccessControl(security);
}

// Scaffold: `new` crea un proyecto completo (plugin.json, csproj, Program.cs, NuGet.config y .gitignore), no solo el
// manifiesto. Las plantillas van incrustadas en el ejecutable para que `new` funcione igual en el repositorio que con
// el SDK instalado.

static void WriteScaffold(string dir, PluginManifest manifest, string projectName)
{
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, PluginManifest.FileName), JsonSerializer.Serialize(manifest, PluginManifest.Json));
    // Referencia al SDK: con el SDK instalado va por paquete (feed local, sin conexión) y también se escribe la
    // NuGet.config; depurando desde el repositorio, por ProjectReference al código fuente del SDK.
    if (LocalFeed() is { } feed)
    {
        WriteTemplate(dir, "Project.csproj", $"{projectName}.csproj",
            [("__SDKREF__", $"<PackageReference Include=\"LiveStream.PluginSdk\" Version=\"{SdkVersion()}\" />")]);
        WriteTemplate(dir, "NuGet.config", "NuGet.config", [("__SDKDIR__", feed)]);
    }
    else if (RepoSdkPath() is { } source)
    {
        WriteTemplate(dir, "Project.csproj", $"{projectName}.csproj",
            [("__SDKREF__", $"<ProjectReference Include=\"{source}\" />")]);
    }
    else
    {
        throw new IOException("Falta el SDK de plugins: instala LiveStream-SDK-Setup (define LIVESTREAM_SDK_DIR) o ejecuta esto desde el repositorio de LiveStream.");
    }
    WriteTemplate(dir, "Program.cs", "Program.cs", [("__NAME__", projectName)]);
    WriteTemplate(dir, "gitignore", ".gitignore", []);
    WriteTemplate(dir, "README.md", "README.md", [("__NAME__", projectName)]);
}

/// <summary>Carpeta del feed local que el instalador del SDK dejó junto a lsplugin (variable LIVESTREAM_SDK_DIR).</summary>
static string? LocalFeed()
{
    var installed = Environment.GetEnvironmentVariable("LIVESTREAM_SDK_DIR");
    if (!string.IsNullOrWhiteSpace(installed) && Directory.Exists(Path.Combine(installed, "packages")))
        return Path.Combine(installed, "packages");
    var local = Path.Combine(AppContext.BaseDirectory, "packages");
    return Directory.Exists(local) ? local : null;
}

/// <summary>Proyecto del SDK de plugins dentro del repositorio si esta herramienta se ejecuta desde él.</summary>
static string? RepoSdkPath()
{
    // AppContext.BaseDirectory para la carpeta del ejecutable (también en publicación single-file).
    var homes = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory };
    return homes.SelectMany(home =>
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(home ?? "."));
            return dir is null ? [] : WalkUp(dir);
        })
        .Select(dir => Path.Combine(dir, "src", "LiveStream.PluginSdk", "LiveStream.PluginSdk.csproj"))
        .FirstOrDefault(File.Exists);
}

static IEnumerable<string> WalkUp(string directory)
{
    for (var dir = directory; dir is not null; dir = Path.GetDirectoryName(dir))
        yield return dir;
}

static void WriteTemplate(string dir, string template, string output, (string From, string To)[] replacements)
{
    var text = RawTemplate(template);
    foreach (var (from, to) in replacements)
    {
        if (to is not null)
            text = text.Replace(from, to);
    }
    var path = Path.Combine(dir, output);
    if (File.Exists(path))
        throw new IOException($"Ya existe {path}.");
    File.WriteAllText(path, text);
}

static string RawTemplate(string name)
{
    using var stream = typeof(Program).Assembly.GetManifestResourceStream($"Template.{name}")
        ?? throw new IOException($"Falta la plantilla incrustada «{name}».");
    using var reader = new StreamReader(stream, Encoding.UTF8);
    return reader.ReadToEnd();
}

/// <summary>Versión del paquete LiveStream.PluginSdk que acompañó a esta compilación de lsplugin.</summary>
static string SdkVersion()
{
    var informational = Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    var version = (informational ?? "1.0.0").Split('+')[0];
    var parts = version.Split('.');
    return string.Join('.', parts.Take(3).Concat(parts.Length == 2 ? ["0"] : []));
}

/// <summary>Nombre del proyecto (sin espacios) a partir de --project o de la última parte del id.</summary>
static string ProjectName(string[] rest)
{
    var source = rest.Contains("--project") ? Option(rest, "--project") : Option(rest, "--id").Split('.')[^1];
    var clean = new string(source.Where(char.IsLetterOrDigit).ToArray());
    if (clean.Length == 0)
        throw new ArgumentException("No se puede derivar el nombre del proyecto. Usa --project MiPlugin.");
    if (!char.IsLetter(clean[0]))
        clean = "Plugin" + clean;
    return char.ToUpperInvariant(clean[0]) + clean[1..];
}
