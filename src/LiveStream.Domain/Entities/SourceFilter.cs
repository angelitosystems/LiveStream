namespace LiveStream.Domain.Entities;

/// <summary>
/// Filtro de audio o video aplicado a una fuente (supresión de ruido, compresor, clave cromática...).
/// <see cref="TypeId"/> es el id del filtro en el motor y <see cref="SettingsJson"/> sus ajustes nativos.
/// </summary>
public sealed class SourceFilter
{
    public SourceFilter(Guid id, string name, string typeId, string settingsJson = "{}", bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        Id = id;
        Name = name;
        TypeId = typeId;
        SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
        Enabled = enabled;
    }

    public Guid Id { get; }
    public string TypeId { get; }
    public string Name { get; private set; }
    public string SettingsJson { get; set; }
    public bool Enabled { get; set; }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }
}
