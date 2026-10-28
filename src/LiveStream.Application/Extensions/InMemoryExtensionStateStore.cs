namespace LiveStream.Application.Extensions;

/// <summary>Estado sin persistencia: todas activas salvo que se desactiven en esta ejecución (pruebas, herramientas).</summary>
public sealed class InMemoryExtensionStateStore : IExtensionStateStore
{
    private readonly HashSet<string> _disabled = [];
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _settings = [];

    public bool IsEnabled(string id)
    {
        lock (_disabled)
            return !_disabled.Contains(id);
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_disabled)
        {
            if (enabled)
                _disabled.Remove(id);
            else
                _disabled.Add(id);
        }
    }

    public IReadOnlyDictionary<string, string> LoadSettings(string id)
    {
        lock (_settings)
            return _settings.GetValueOrDefault(id) ?? new Dictionary<string, string>();
    }

    public void SaveSettings(string id, IReadOnlyDictionary<string, string> values)
    {
        lock (_settings)
            _settings[id] = new Dictionary<string, string>(values);
    }
}
