using System.Text.Json;

namespace StreamPilot.Infrastructure.Persistence;

/// <summary>Ajustes de este equipo (no del proyecto): dependen del hardware, no del estudio.</summary>
public sealed record MachineSettings(int RenderAdapterIndex = 0, bool AdaptiveBitrate = true);

/// <summary>%APPDATA%\StreamPilot\machine.json. Un archivo dañado no impide arrancar: se usan los valores por defecto.</summary>
public sealed class MachineSettingsStore(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Path { get; } = path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamPilot", "machine.json");

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
