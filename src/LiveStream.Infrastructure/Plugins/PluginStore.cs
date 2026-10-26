using System.Text.Json;
using LiveStream.PluginSdk;

namespace LiveStream.Infrastructure.Plugins;

/// <param name="Signed">Instalado con firma válida del publicador; false solo es posible en modo desarrollador.</param>
public sealed record PluginRecord(string Version, bool Enabled, bool Signed, string Source, DateTimeOffset InstalledAt);

public sealed record InstalledPlugin(PluginManifest Manifest, PluginRecord Record, string Directory)
{
    public string Id => Manifest.Id;
    public string EntryPath => Path.Combine(Directory, Manifest.Entry);
}

/// <summary>
/// Plugins instalados. Código en %LOCALAPPDATA%\LiveStream\plugins\&lt;id&gt; (fuera de la carpeta de la aplicación: un
/// plugin nunca sobrescribe archivos del núcleo), datos y ajustes en %APPDATA%\LiveStream\plugin-data\&lt;id&gt;,
/// estado en %APPDATA%\LiveStream\plugins.json. Todo paquete se verifica entero antes de extraerse.
/// </summary>
public sealed class PluginStore
{
    private const int Schema = 1;
    private const int MaxSettingsChars = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _lock = new();

    private sealed record StateFile(int Schema, Dictionary<string, PluginRecord> Plugins);

    /// <param name="trustedPublicKey">Clave pública del publicador (null: sin clave compilada, solo modo desarrollador).</param>
    public PluginStore(Version appVersion, string? trustedPublicKey, string? installRoot = null, string? dataRoot = null)
    {
        AppVersion = appVersion;
        TrustedPublicKey = trustedPublicKey;
        InstallRoot = installRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveStream", "plugins");
        DataRoot = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveStream", "plugin-data");
        StatePath = Path.Combine(DataRoot, "plugins.json");
    }

    public Version AppVersion { get; }
    public string? TrustedPublicKey { get; }
    public string InstallRoot { get; }
    public string DataRoot { get; }
    public string StatePath { get; }

    /// <summary>Permite instalar paquetes sin firma (para quien desarrolla plugins). Desactivado por defecto.</summary>
    public bool DeveloperMode { get; set; }

    public string DataDirectory(string id) => Path.Combine(DataRoot, id);

    public IReadOnlyList<InstalledPlugin> List()
    {
        var result = new List<InstalledPlugin>();
        foreach (var (id, record) in Load().Plugins)
        {
            var directory = Path.Combine(InstallRoot, id);
            try
            {
                var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(directory, PluginManifest.FileName)));
                if (manifest.Id == id)
                    result.Add(new InstalledPlugin(manifest, record, directory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PluginValidationException)
            {
                // Instalación dañada o borrada a mano: no se carga (se puede reinstalar o desinstalar).
            }
        }
        return result.OrderBy(p => p.Manifest.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public InstalledPlugin? Find(string id) => List().FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Comprueba firma, compatibilidad y dependencias sin instalar nada. Devuelve el manifiesto (sus permisos son los
    /// que debe aceptar el usuario). Lanza <see cref="PluginValidationException"/> con el motivo.
    /// </summary>
    public PluginManifest Verify(string packagePath, PackageSignature? signature)
    {
        if (signature is null)
        {
            if (!DeveloperMode)
                throw new PluginValidationException("El paquete no está firmado. Solo se pueden instalar plugins firmados (o activar el modo desarrollador).");
        }
        else if (TrustedPublicKey is null)
        {
            throw new PluginValidationException("Esta versión de LiveStream no tiene clave de publicador: no puede verificar plugins firmados.");
        }
        else if (signature.VerifyPackage(packagePath, TrustedPublicKey) is { } signatureError)
        {
            throw new PluginValidationException(signatureError);
        }

        var manifest = PluginPackage.Inspect(packagePath);
        if (PluginVersion.Parse(manifest.MinAppVersion) > AppVersion)
            throw new PluginValidationException($"Requiere LiveStream {manifest.MinAppVersion} o posterior (tienes {AppVersion.ToString(3)}).");
        var installed = List().ToDictionary(p => p.Id);
        foreach (var (id, min) in manifest.Dependencies ?? new Dictionary<string, string>())
        {
            if (!installed.TryGetValue(id, out var dep))
                throw new PluginValidationException($"Necesita el plugin «{id}» {min} o posterior. Instálalo primero.");
            if (PluginVersion.Parse(dep.Manifest.Version) < PluginVersion.Parse(min))
                throw new PluginValidationException($"Necesita «{dep.Manifest.Name}» {min} o posterior (instalada: {dep.Manifest.Version}).");
        }
        return manifest;
    }

    /// <summary>Instala o actualiza (el plugin debe estar detenido). Una actualización conserva si estaba activo.</summary>
    public InstalledPlugin Install(string packagePath, PackageSignature? signature, string source, bool enable)
    {
        var manifest = Verify(packagePath, signature);
        Directory.CreateDirectory(InstallRoot);
        var staging = Path.Combine(InstallRoot, $".staging-{Guid.NewGuid():N}");
        PluginPackage.Extract(packagePath, staging);

        var target = Path.Combine(InstallRoot, manifest.Id);
        var old = Path.Combine(InstallRoot, $".old-{Guid.NewGuid():N}");
        lock (_lock)
        {
            if (Directory.Exists(target))
                Directory.Move(target, old);
            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                if (Directory.Exists(old))
                    Directory.Move(old, target);
                TryDelete(staging);
                throw;
            }
            TryDelete(old);
            Directory.CreateDirectory(DataDirectory(manifest.Id));

            var state = Load();
            var wasEnabled = state.Plugins.TryGetValue(manifest.Id, out var previous) && previous.Enabled;
            var record = new PluginRecord(manifest.Version, enable || wasEnabled, signature is not null, source, DateTimeOffset.UtcNow);
            state.Plugins[manifest.Id] = record;
            Save(state);
            return new InstalledPlugin(manifest, record, target);
        }
    }

    /// <summary>Quita código, datos y registro. Se niega si otro plugin instalado depende de este.</summary>
    public void Uninstall(string id)
    {
        var dependents = List().Where(p => p.Manifest.Dependencies?.ContainsKey(id) == true).Select(p => p.Manifest.Name).ToList();
        if (dependents.Count > 0)
            throw new PluginValidationException($"No se puede desinstalar: lo necesitan {string.Join(", ", dependents)}.");
        lock (_lock)
        {
            var state = Load();
            state.Plugins.Remove(id);
            Save(state);
        }
        TryDelete(Path.Combine(InstallRoot, id));
        TryDelete(DataDirectory(id));
    }

    public void SetEnabled(string id, bool enabled)
    {
        var plugin = Find(id) ?? throw new PluginValidationException("El plugin no está instalado.");
        if (enabled)
        {
            var active = List().Where(p => p.Record.Enabled).Select(p => p.Id).ToHashSet();
            foreach (var dep in plugin.Manifest.Dependencies?.Keys ?? [])
                if (!active.Contains(dep))
                    throw new PluginValidationException($"Activa antes el plugin «{dep}», del que depende.");
        }
        else
        {
            var dependents = List().Where(p => p.Record.Enabled && p.Manifest.Dependencies?.ContainsKey(id) == true).Select(p => p.Manifest.Name).ToList();
            if (dependents.Count > 0)
                throw new PluginValidationException($"Desactiva antes {string.Join(", ", dependents)}, que dependen de este plugin.");
        }
        lock (_lock)
        {
            var state = Load();
            state.Plugins[id] = state.Plugins[id] with { Enabled = enabled };
            Save(state);
        }
    }

    /// <summary>Ajustes declarados en el manifiesto con los valores guardados (o los predeterminados).</summary>
    public IReadOnlyDictionary<string, string> LoadSettings(InstalledPlugin plugin)
    {
        var values = (plugin.Manifest.Settings ?? []).ToDictionary(s => s.Key, s => s.Default);
        try
        {
            var path = Path.Combine(DataDirectory(plugin.Id), "settings.json");
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } saved)
                foreach (var (key, value) in saved)
                    if (values.ContainsKey(key))
                        values[key] = value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Ajustes dañados: valores por defecto.
        }
        return values;
    }

    public void SaveSettings(InstalledPlugin plugin, IReadOnlyDictionary<string, string> values)
    {
        var declared = (plugin.Manifest.Settings ?? []).ToDictionary(s => s.Key);
        foreach (var (key, value) in values)
        {
            if (!declared.TryGetValue(key, out var setting))
                throw new PluginValidationException($"Ajuste desconocido: {key}");
            if (setting.Type == "number" && !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                throw new PluginValidationException($"«{setting.Label}» debe ser un número.");
            if (setting.Type == "bool" && value is not ("true" or "false"))
                throw new PluginValidationException($"«{setting.Label}» debe ser true o false.");
        }
        var json = JsonSerializer.Serialize(values, Json);
        if (json.Length > MaxSettingsChars)
            throw new PluginValidationException("Los ajustes ocupan demasiado.");
        Directory.CreateDirectory(DataDirectory(plugin.Id));
        File.WriteAllText(Path.Combine(DataDirectory(plugin.Id), "settings.json"), json);
    }

    private StateFile Load()
    {
        try
        {
            if (File.Exists(StatePath) && JsonSerializer.Deserialize<StateFile>(File.ReadAllText(StatePath), Json) is { Plugins: not null } state)
                return state.Schema == Schema ? state : Migrate(state);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Estado dañado: se trata como «sin plugins»; los archivos instalados siguen ahí para reinstalar.
        }
        return new StateFile(Schema, []);
    }

    /// <summary>Punto único de migración del estado cuando cambie su esquema.</summary>
    private static StateFile Migrate(StateFile state) => state with { Schema = Schema };

    private void Save(StateFile state)
    {
        Directory.CreateDirectory(DataRoot);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, StatePath, overwrite: true);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un archivo bloqueado no debe dejar la instalación a medias: se limpia en otro momento.
        }
    }
}
