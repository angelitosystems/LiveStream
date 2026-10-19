using LiveStream.Application.Commands;
using LiveStream.Application.Studio;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.Application.Sources;

/// <summary>Crea una fuente nueva y la coloca en una escena (encima del resto).</summary>
public sealed class AddSourceCommand(Guid sceneId, string name, SourceKind kind, string settingsJson = "{}",
    Transform? transform = null, int globalAudioChannel = 0) : IReversibleStudioCommand
{
    private Source? _source;
    private SceneItem? _item;

    public string Description => $"Añadir fuente '{name}'";

    public Guid? SourceId => _source?.Id;
    public Guid? ItemId => _item?.Id;

    public string? Validate(StudioContext context)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "El nombre de la fuente no puede estar vacío.";
        if (context.Project.Scenes.All(s => s.Id != sceneId))
            return "La escena no existe.";
        if (globalAudioChannel is < 0 or > Source.MaxGlobalAudioChannel)
            return "Canal de audio global fuera de rango.";
        return null;
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var scene = context.Project.GetScene(sceneId);
        _source ??= new Source(Guid.NewGuid(), name, kind, settingsJson);
        _source.SetGlobalAudioChannel(globalAudioChannel);
        context.Project.AddSource(_source);
        if (_item is null)
            _item = scene.AddItem(_source.Id, transform);
        else
            scene.InsertItem(_item, scene.Items.Count);

        if (!context.Engine.IsInitialized)
            return;
        try
        {
            await EngineSync.CreateSourceAsync(context.Engine, _source, ct).ConfigureAwait(false);
            await EngineSync.CreateItemAsync(context.Engine, sceneId, _item, scene.IndexOf(_item.Id), ct).ConfigureAwait(false);
        }
        catch
        {
            // El dominio no puede quedar con una fuente que el motor rechazó.
            scene.RemoveItem(_item.Id);
            context.Project.RemoveSource(_source.Id);
            await TryRemoveFromEngineAsync(context, _source.Id).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_source is null)
            return;
        context.Project.RemoveSource(_source.Id);
        if (context.Engine.IsInitialized)
            await context.Engine.RemoveSourceAsync(_source.Id, ct).ConfigureAwait(false);
    }

    private static async Task TryRemoveFromEngineAsync(StudioContext context, Guid sourceId)
    {
        try
        {
            await context.Engine.RemoveSourceAsync(sourceId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // La fuente no llegó a crearse en el motor.
        }
    }
}

/// <summary>Coloca una fuente existente en otra escena (referencia compartida, sin duplicar la captura).</summary>
public sealed class AddExistingSourceCommand(Guid sceneId, Guid sourceId, Transform? transform = null) : IReversibleStudioCommand
{
    private SceneItem? _item;

    public string Description => "Añadir fuente existente";

    public Guid? ItemId => _item?.Id;

    public string? Validate(StudioContext context)
    {
        if (context.Project.Scenes.All(s => s.Id != sceneId))
            return "La escena no existe.";
        if (context.Project.FindSource(sourceId) is null)
            return "La fuente no existe.";
        return null;
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var scene = context.Project.GetScene(sceneId);
        if (_item is null)
            _item = scene.AddItem(sourceId, transform);
        else
            scene.InsertItem(_item, scene.Items.Count);
        if (context.Engine.IsInitialized)
            await EngineSync.CreateItemAsync(context.Engine, sceneId, _item, scene.IndexOf(_item.Id), ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_item is null)
            return;
        context.Project.GetScene(sceneId).RemoveItem(_item.Id);
        if (context.Engine.IsInitialized)
            await context.Engine.RemoveItemAsync(_item.Id, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Quita un elemento de su escena. Si la fuente queda sin usar en ninguna escena y no es un canal de audio
/// global, también se elimina (como en OBS). Deshacer restaura ambos.
/// </summary>
public sealed class RemoveItemCommand(Guid itemId) : IReversibleStudioCommand
{
    private Guid _sceneId;
    private SceneItem? _item;
    private int _index;
    private Source? _orphan;
    private int _orphanIndex;

    public string Description => "Eliminar elemento";

    public string? Validate(StudioContext context)
    {
        try
        {
            context.Project.FindItem(itemId);
            return null;
        }
        catch (KeyNotFoundException)
        {
            return "El elemento no existe.";
        }
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var (scene, item) = context.Project.FindItem(itemId);
        _sceneId = scene.Id;
        _index = scene.IndexOf(itemId);
        _item = scene.RemoveItem(itemId);

        var source = context.Project.GetSource(item.SourceId);
        var stillUsed = context.Project.Scenes.Any(s => s.Items.Any(i => i.SourceId == source.Id));
        _orphanIndex = context.Project.IndexOfSource(source.Id);
        _orphan = !stillUsed && source.GlobalAudioChannel == 0 ? context.Project.RemoveSource(source.Id) : null;

        if (!context.Engine.IsInitialized)
            return;
        await context.Engine.RemoveItemAsync(itemId, ct).ConfigureAwait(false);
        if (_orphan is not null)
            await context.Engine.RemoveSourceAsync(_orphan.Id, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_item is null)
            return;
        if (_orphan is not null)
            context.Project.AddSource(_orphan, _orphanIndex); // en su sitio: el orden de fuentes también es estado
        context.Project.GetScene(_sceneId).InsertItem(_item, _index);

        if (!context.Engine.IsInitialized)
            return;
        if (_orphan is not null)
            await EngineSync.CreateSourceAsync(context.Engine, _orphan, ct).ConfigureAwait(false);
        await EngineSync.CreateItemAsync(context.Engine, _sceneId, _item, _index, ct).ConfigureAwait(false);
    }
}

public sealed class RenameSourceCommand(Guid sourceId, string newName) : IReversibleStudioCommand
{
    private string? _previous;

    public string Description => $"Renombrar fuente a '{newName}'";

    public string? Validate(StudioContext context)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return "El nombre no puede estar vacío.";
        return context.Project.FindSource(sourceId) is null ? "La fuente no existe." : null;
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var source = context.Project.GetSource(sourceId);
        _previous = source.Name;
        source.Rename(newName);
        if (context.Engine.IsInitialized)
            await context.Engine.RenameSourceAsync(sourceId, newName, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_previous is null)
            return;
        context.Project.GetSource(sourceId).Rename(_previous);
        if (context.Engine.IsInitialized)
            await context.Engine.RenameSourceAsync(sourceId, _previous, ct).ConfigureAwait(false);
    }
}

public sealed class UpdateSourceSettingsCommand(Guid sourceId, string settingsJson, string? previousSettingsJson = null) : IReversibleStudioCommand
{
    // libobs fusiona los ajustes en lugar de reemplazarlos: para deshacer de verdad hace falta el estado completo
    // anterior (con valores por defecto), que el diálogo de propiedades lee del motor al abrirse.
    private string? _previous = previousSettingsJson;

    public string Description => "Cambiar propiedades de la fuente";

    public string? Validate(StudioContext context)
    {
        if (context.Project.FindSource(sourceId) is null)
            return "La fuente no existe.";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(settingsJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object ? null : "Los ajustes deben ser un objeto JSON.";
        }
        catch (System.Text.Json.JsonException ex)
        {
            return $"JSON inválido: {ex.Message}";
        }
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var source = context.Project.GetSource(sourceId);
        _previous ??= source.SettingsJson;
        source.SettingsJson = settingsJson;
        if (context.Engine.IsInitialized)
            await context.Engine.UpdateSourceSettingsAsync(sourceId, settingsJson, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_previous is null)
            return;
        context.Project.GetSource(sourceId).SettingsJson = _previous;
        if (context.Engine.IsInitialized)
            await context.Engine.UpdateSourceSettingsAsync(sourceId, _previous, ct).ConfigureAwait(false);
    }
}
