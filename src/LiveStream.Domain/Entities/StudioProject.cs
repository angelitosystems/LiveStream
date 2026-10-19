using LiveStream.Domain.Graphics;

namespace LiveStream.Domain.Entities;

/// <summary>Raíz del proyecto de estudio. Es la unidad que se persiste y versiona.</summary>
public sealed class StudioProject
{
    public const int CurrentSchemaVersion = 4;

    private readonly List<Scene> _scenes = [];
    private readonly List<Source> _sources = [];
    private readonly List<Graphic> _graphics = [];
    private readonly List<StreamDestination> _destinations = [];

    /// <summary>Destinos de emisión (multistream). Se emite a todos los activados a la vez.</summary>
    public IReadOnlyList<StreamDestination> Destinations => _destinations;

    public StreamDestination? FindDestination(Guid id) => _destinations.FirstOrDefault(d => d.Id == id);

    public void InsertDestination(StreamDestination destination, int? index = null)
    {
        if (_destinations.Any(d => d.Id == destination.Id))
            throw new InvalidOperationException("El destino ya existe.");
        _destinations.Insert(Math.Clamp(index ?? _destinations.Count, 0, _destinations.Count), destination);
    }

    public StreamDestination ReplaceDestination(StreamDestination destination)
    {
        var index = _destinations.FindIndex(d => d.Id == destination.Id);
        if (index < 0)
            throw new KeyNotFoundException($"Destino {destination.Id} no encontrado.");
        var previous = _destinations[index];
        _destinations[index] = destination;
        return previous;
    }

    public (StreamDestination Destination, int Index) RemoveDestination(Guid id)
    {
        var index = _destinations.FindIndex(d => d.Id == id);
        if (index < 0)
            throw new KeyNotFoundException($"Destino {id} no encontrado.");
        var destination = _destinations[index];
        _destinations.RemoveAt(index);
        return (destination, index);
    }

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

    /// <summary>Gráficos de la capa superpuesta, de abajo (índice 0) hacia arriba.</summary>
    public IReadOnlyList<Graphic> Graphics => _graphics;

    public Graphic GetGraphic(Guid graphicId) =>
        _graphics.FirstOrDefault(g => g.Id == graphicId) ?? throw new KeyNotFoundException($"Gráfico {graphicId} no encontrado.");

    public Graphic? FindGraphic(Guid graphicId) => _graphics.FirstOrDefault(g => g.Id == graphicId);

    public int IndexOfGraphic(Guid graphicId) => _graphics.FindIndex(g => g.Id == graphicId);

    public void InsertGraphic(Graphic graphic, int? index = null)
    {
        if (_graphics.Any(g => g.Id == graphic.Id))
            throw new InvalidOperationException("El gráfico ya existe.");
        _graphics.Insert(Math.Clamp(index ?? _graphics.Count, 0, _graphics.Count), graphic);
    }

    /// <summary>Sustituye un gráfico por su versión editada (mismo Id y tipo).</summary>
    public Graphic ReplaceGraphic(Graphic graphic)
    {
        var index = IndexOfGraphic(graphic.Id);
        if (index < 0)
            throw new KeyNotFoundException($"Gráfico {graphic.Id} no encontrado.");
        var previous = _graphics[index];
        if (previous.Kind != graphic.Kind)
            throw new InvalidOperationException("No se puede cambiar el tipo de un gráfico.");
        _graphics[index] = graphic;
        return previous;
    }

    public Graphic RemoveGraphic(Guid graphicId)
    {
        var graphic = GetGraphic(graphicId);
        _graphics.Remove(graphic);
        return graphic;
    }

    public bool HasGraphicNamed(string name, Guid? except = null) =>
        _graphics.Any(g => g.Id != except && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

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

    public void AddSource(Source source, int? index = null)
    {
        if (_sources.Any(s => s.Id == source.Id))
            throw new InvalidOperationException($"La fuente {source.Id} ya existe.");
        _sources.Insert(Math.Clamp(index ?? _sources.Count, 0, _sources.Count), source);
    }

    public int IndexOfSource(Guid sourceId) => _sources.FindIndex(s => s.Id == sourceId);

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
