namespace StreamPilot.Domain.Entities;

/// <summary>Raíz del proyecto de estudio. Es la unidad que se persiste y versiona.</summary>
public sealed class StudioProject
{
    public const int CurrentSchemaVersion = 1;

    private readonly List<Scene> _scenes = [];
    private readonly List<Source> _sources = [];

    public StudioProject(Guid id, string name, int schemaVersion = CurrentSchemaVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name;
        SchemaVersion = schemaVersion;
    }

    public Guid Id { get; }
    public string Name { get; private set; }
    public int SchemaVersion { get; }
    public Guid? ActiveSceneId { get; private set; }
    public IReadOnlyList<Scene> Scenes => _scenes;
    public IReadOnlyList<Source> Sources => _sources;

    public Scene AddScene(string name)
    {
        if (_scenes.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Ya existe una escena llamada '{name}'.");

        var scene = new Scene(Guid.NewGuid(), name);
        _scenes.Add(scene);
        ActiveSceneId ??= scene.Id;
        return scene;
    }

    public void AddSource(Source source)
    {
        if (_sources.Any(s => s.Id == source.Id))
            throw new InvalidOperationException($"La fuente {source.Id} ya existe.");
        _sources.Add(source);
    }

    public void ActivateScene(Guid sceneId)
    {
        if (_scenes.All(s => s.Id != sceneId))
            throw new KeyNotFoundException($"Escena {sceneId} no encontrada.");
        ActiveSceneId = sceneId;
    }

    public Scene GetScene(Guid sceneId) =>
        _scenes.FirstOrDefault(s => s.Id == sceneId) ?? throw new KeyNotFoundException($"Escena {sceneId} no encontrada.");
}
