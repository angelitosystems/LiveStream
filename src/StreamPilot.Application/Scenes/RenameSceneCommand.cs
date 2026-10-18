using StreamPilot.Application.Commands;

namespace StreamPilot.Application.Scenes;

public sealed class RenameSceneCommand(Guid sceneId, string newName) : IReversibleStudioCommand
{
    private string? _previousName;

    public string Description => $"Renombrar escena a '{newName}'";

    public string? Validate(StudioContext context)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return "El nombre de la escena no puede estar vacío.";
        if (context.Project.Scenes.All(s => s.Id != sceneId))
            return "La escena no existe.";
        if (context.Project.Scenes.Any(s => s.Id != sceneId && string.Equals(s.Name, newName, StringComparison.OrdinalIgnoreCase)))
            return $"Ya existe una escena llamada '{newName}'.";
        return null;
    }

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var scene = context.Project.GetScene(sceneId);
        _previousName = scene.Name;
        scene.Rename(newName);
        return Task.CompletedTask;
    }

    public Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_previousName is not null)
            context.Project.GetScene(sceneId).Rename(_previousName);
        return Task.CompletedTask;
    }
}
