using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LiveStream.PluginSdk;

/// <summary>Permisos que un plugin declara y el usuario acepta al instalarlo. Solo controlan la API de LiveStream.</summary>
public static class PluginPermissions
{
    /// <summary>Leer el estado del estudio (escenas, fuentes, audio, gráficos, salidas).</summary>
    public const string StudioRead = "studio.read";
    /// <summary>Modificar el estudio con comandos validados que se pueden deshacer.</summary>
    public const string StudioModify = "studio.modify";
    /// <summary>Emitir, grabar o borrar: cada uso pide además confirmación al usuario.</summary>
    public const string StudioLive = "studio.live";

    public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>
    {
        [StudioRead] = "Leer el estado del estudio",
        [StudioModify] = "Modificar escenas, fuentes, audio y gráficos (se puede deshacer)",
        [StudioLive] = "Emitir, grabar y eliminar elementos (con tu confirmación cada vez)",
    };
}

/// <summary>Ajuste configurable desde LiveStream; el valor llega al plugin en initialize y en settings/changed.</summary>
public sealed record PluginSetting(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("type")] string Type = "string",
    [property: JsonPropertyName("default")] string Default = "");

/// <summary>plugin.json: identidad, compatibilidad, permisos y punto de entrada de un plugin.</summary>
public sealed partial record PluginManifest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("apiVersion")] int ApiVersion,
    [property: JsonPropertyName("entry")] string Entry,
    [property: JsonPropertyName("author")] string Author = "",
    [property: JsonPropertyName("description")] string Description = "",
    [property: JsonPropertyName("category")] string Category = "",
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("homepage")] string? Homepage = null,
    [property: JsonPropertyName("minAppVersion")] string MinAppVersion = "0.0.0",
    [property: JsonPropertyName("permissions")] IReadOnlyList<string>? Permissions = null,
    // Id de plugin → versión mínima («1.2.0»). Deben estar instalados y activos.
    [property: JsonPropertyName("dependencies")] IReadOnlyDictionary<string, string>? Dependencies = null,
    [property: JsonPropertyName("settings")] IReadOnlyList<PluginSetting>? Settings = null,
    [property: JsonPropertyName("changelog")] string? Changelog = null)
{
    public const string FileName = "plugin.json";

    /// <summary>Versiones de la API de plugins que entiende esta versión del SDK.</summary>
    public static IReadOnlyList<int> SupportedApiVersions { get; } = [1];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyList<string> PermissionList => Permissions ?? [];

    public static PluginManifest Parse(string json)
    {
        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new PluginValidationException($"plugin.json no es JSON válido: {ex.Message}");
        }
        if (manifest is null)
            throw new PluginValidationException("plugin.json está vacío.");
        if (manifest.Validate() is { } error)
            throw new PluginValidationException(error);
        return manifest;
    }

    /// <summary>Null si el manifiesto es válido; si no, el primer problema en lenguaje claro.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || !IdPattern().IsMatch(Id))
            return "id debe ser en minúsculas con puntos, p. ej. «com.empresa.plugin» (3-100 caracteres: a-z, 0-9, punto, guion).";
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            return "name es obligatorio (máximo 80 caracteres).";
        if (!PluginVersion.TryParse(Version, out _))
            return $"version debe ser MAYOR.MENOR.PARCHE (p. ej. 1.0.0), no «{Version}».";
        if (!PluginVersion.TryParse(MinAppVersion, out _))
            return $"minAppVersion debe ser MAYOR.MENOR.PARCHE, no «{MinAppVersion}».";
        if (!SupportedApiVersions.Contains(ApiVersion))
            return $"apiVersion {ApiVersion} no está soportada (soportadas: {string.Join(", ", SupportedApiVersions)}).";
        if (!IsSafeRelativePath(Entry) || !Entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return "entry debe ser la ruta relativa de un .exe dentro del paquete.";
        if (Description.Length > 2000)
            return "description no puede pasar de 2000 caracteres.";
        foreach (var permission in PermissionList)
            if (!PluginPermissions.Descriptions.ContainsKey(permission))
                return $"Permiso desconocido: «{permission}». Válidos: {string.Join(", ", PluginPermissions.Descriptions.Keys)}.";
        foreach (var (id, min) in Dependencies ?? new Dictionary<string, string>())
        {
            if (!IdPattern().IsMatch(id) || id == Id)
                return $"Dependencia no válida: «{id}».";
            if (!PluginVersion.TryParse(min, out _))
                return $"La versión mínima de la dependencia «{id}» debe ser MAYOR.MENOR.PARCHE.";
        }
        foreach (var setting in Settings ?? [])
            if (!SettingKeyPattern().IsMatch(setting.Key) || setting.Type is not ("string" or "number" or "bool"))
                return $"Ajuste no válido: «{setting.Key}» (clave a-z/0-9/_ y tipo string, number o bool).";
        if (Settings is { } s && s.Select(x => x.Key).Distinct().Count() != s.Count)
            return "Hay ajustes con la misma clave.";
        return null;
    }

    /// <summary>Relativa, sin «..», sin unidad ni raíz: no puede salir de la carpeta del plugin.</summary>
    public static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains(':')
        && path.Replace('\\', '/').Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?){1,9}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex SettingKeyPattern();
}

/// <summary>Versión MAYOR.MENOR.PARCHE (sin sufijos de prelanzamiento).</summary>
public static class PluginVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var parts = (text ?? "").Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length is 0 or > 6 || !p.All(char.IsAsciiDigit)))
            return false;
        version = new Version(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        return true;
    }

    public static Version Parse(string text) =>
        TryParse(text, out var v) ? v : throw new PluginValidationException($"Versión no válida: «{text}».");
}

public sealed class PluginValidationException(string message) : Exception(message);
