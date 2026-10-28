using System.Text.Json;
using LiveStream.Application.Extensions;

namespace LiveStream.Infrastructure.Persistence;

/// <summary>
/// %APPDATA%\LiveStream\extensions.json: qué extensiones están desactivadas en este equipo y sus ajustes.
/// Todas empiezan activas (así un proyecto o una instalación anteriores siguen igual). Un archivo dañado no impide arrancar.
/// </summary>
public sealed class JsonExtensionStateStore : IExtensionStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _lock = new();
    private StateFile _state;

    public JsonExtensionStateStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveStream", "extensions.json");
        _state = Load(Path);
    }

    public string Path { get; }

    public bool IsEnabled(string id)
    {
        lock (_lock)
            return !_state.Disabled.Contains(id);
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            var disabled = new SortedSet<string>(_state.Disabled);
            if (!(enabled ? disabled.Remove(id) : disabled.Add(id)))
                return;
            _state = _state with { Disabled = [.. disabled] };
            Save();
        }
    }

    public IReadOnlyDictionary<string, string> LoadSettings(string id)
    {
        lock (_lock)
            return _state.Settings.GetValueOrDefault(id) ?? new Dictionary<string, string>();
    }

    public void SaveSettings(string id, IReadOnlyDictionary<string, string> values)
    {
        lock (_lock)
        {
            _state = _state with { Settings = new Dictionary<string, Dictionary<string, string>>(_state.Settings) { [id] = new(values) } };
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, Options));
        File.Move(temp, Path, overwrite: true);
    }

    private static StateFile Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path), Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Diagnostics.AppLog.Warn("extensiones", $"No se pudo leer {path}; se usan los valores por defecto: {ex.Message}");
            return new();
        }
    }

    private sealed record StateFile
    {
        public string[] Disabled { get; init; } = [];

        public Dictionary<string, Dictionary<string, string>> Settings { get; init; } = [];
    }
}
