using System.Text.Json;

namespace LiveStream.Infrastructure.Persistence;

/// <summary>Ajustes de este equipo (no del proyecto): dependen del hardware, no del estudio.</summary>
/// <param name="UpdateFeedUrl">URL HTTPS del manifiesto firmado de actualizaciones (update.json); null = sin canal.</param>
/// <param name="PluginCatalogUrl">URL HTTPS del backend del marketplace; null = sin catálogo remoto.</param>
/// <param name="PluginRepository">Carpeta con paquetes .lsplugin firmados (repositorio local); null = ninguno.</param>
/// <param name="PluginDeveloperMode">Permite instalar plugins sin firma (solo para quien los desarrolla).</param>
public sealed record MachineSettings(int RenderAdapterIndex = 0, bool AdaptiveBitrate = true, string? UpdateFeedUrl = null,
    string? PluginCatalogUrl = null, string? PluginRepository = null, bool PluginDeveloperMode = false);

/// <summary>%APPDATA%\LiveStream\machine.json. Un archivo dañado no impide arrancar: se usan los valores por defecto.</summary>
public sealed class MachineSettingsStore(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveStream", "machine.json");

    public MachineSettings Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<MachineSettings>(File.ReadAllText(Path), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(MachineSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, Path, overwrite: true);
    }
}
