namespace StreamPilot.Domain.Entities;

/// <summary>Raíz del proyecto de estudio. Es la unidad que se persiste y versiona.</summary>
public sealed class StudioProject
{
    public const int CurrentSchemaVersion = 2;

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

    /// <summary>Escena que se emite.</summary>
    public Guid? ActiveSceneId { get; private set; }

    /// <summary>Escena en edición en modo estudio (null fuera de modo estudio).</summary>
    public Guid? PreviewSceneId { get; private set; }

    public bool StudioMode => PreviewSceneId is not null;

    public CanvasSettings Canvas { get; set; } = new();
    public TransitionSettings Transition { get; set; } = new();
    public StreamSettings Stream { get; set; } = new();
    public RecordingPreferences Recording { get; set; } = new();

    public IReadOnlyList<Scene> Scenes => _scenes;
    public IReadOnlyList<Source> Sources => _sources;

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public bool HasSceneNamed(string name, Guid? except = null) =>
        _scenes.Any(s => s.Id != except && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public Scene AddScene(string name, Guid? id = null, int? index = null)
    {
        if (HasSceneNamed(name))
            throw new InvalidOperationException($"Ya existe una escena llamada '{name}'.");

        var scene = new Scene(id ?? Guid.NewGuid(), name);
        _scenes.Insert(Math.Clamp(index ?? _scenes.Count, 0, _scenes.Count), scene);
        ActiveSceneId ??= scene.Id;
        return scene;
    }

    /// <summary>Reinserta una escena existente (usado por deshacer).</summary>
    public void InsertScene(Scene scene, int index)
    {
        if (_scenes.Any(s => s.Id == scene.Id))
            throw new InvalidOperationException("La escena ya existe.");
        _scenes.Insert(Math.Clamp(index, 0, _scenes.Count), scene);
        ActiveSceneId ??= scene.Id;
    }

    public Scene RemoveScene(Guid sceneId)
    {
        var scene = GetScene(sceneId);
        if (ActiveSceneId == sceneId)
            throw new InvalidOperationException("No se puede eliminar la escena que está en programa.");
        _scenes.Remove(scene);
        if (PreviewSceneId == sceneId)
            PreviewSceneId = ActiveSceneId;
        return scene;
    }

    public void MoveScene(Guid sceneId, int newIndex)
    {
        var scene = GetScene(sceneId);
        _scenes.Remove(scene);
        _scenes.Insert(Math.Clamp(newIndex, 0, _scenes.Count), scene);
    }

    public int IndexOfScene(Guid sceneId) => _scenes.FindIndex(s => s.Id == sceneId);

    public void AddSource(Source source)
    {
        if (_sources.Any(s => s.Id == source.Id))
            throw new InvalidOperationException($"La fuente {source.Id} ya existe.");
        _sources.Add(source);
    }

    /// <summary>Elimina la fuente y todos sus elementos en cualquier escena.</summary>
    public Source RemoveSource(Guid sourceId)
    {
        var source = GetSource(sourceId);
        foreach (var scene in _scenes)
            foreach (var item in scene.Items.Where(i => i.SourceId == sourceId).ToList())
                scene.RemoveItem(item.Id);
        _sources.Remove(source);
        return source;
    }

    public Source GetSource(Guid sourceId) =>
        _sources.FirstOrDefault(s => s.Id == sourceId) ?? throw new KeyNotFoundException($"Fuente {sourceId} no encontrada.");

    public Source? FindSource(Guid sourceId) => _sources.FirstOrDefault(s => s.Id == sourceId);

    public void ActivateScene(Guid sceneId)
    {
        if (_scenes.All(s => s.Id != sceneId))
            throw new KeyNotFoundException($"Escena {sceneId} no encontrada.");
        ActiveSceneId = sceneId;
    }

    public void SetStudioMode(bool enabled) => PreviewSceneId = enabled ? PreviewSceneId ?? ActiveSceneId : null;

    public void SetPreviewScene(Guid sceneId)
    {
        if (_scenes.All(s => s.Id != sceneId))
            throw new KeyNotFoundException($"Escena {sceneId} no encontrada.");
        PreviewSceneId = sceneId;
    }

    public Scene GetScene(Guid sceneId) =>
        _scenes.FirstOrDefault(s => s.Id == sceneId) ?? throw new KeyNotFoundException($"Escena {sceneId} no encontrada.");

    /// <summary>Busca la escena que contiene un elemento.</summary>
    public (Scene Scene, SceneItem Item) FindItem(Guid itemId)
    {
        foreach (var scene in _scenes)
            if (scene.Items.FirstOrDefault(i => i.Id == itemId) is { } item)
                return (scene, item);
        throw new KeyNotFoundException($"Elemento {itemId} no encontrado.");
    }

    /// <summary>Genera un nombre libre a partir de una base ("Escena", "Escena 2", ...).</summary>
    public string NextSceneName(string baseName)
    {
        if (!HasSceneNamed(baseName))
            return baseName;
        for (var i = 2; ; i++)
            if (!HasSceneNamed($"{baseName} {i}"))
                return $"{baseName} {i}";
    }
}
