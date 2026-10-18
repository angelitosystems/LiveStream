using StreamPilot.Application.Commands;

namespace StreamPilot.Application.Scenes;

public sealed class CreateSceneCommand(string name) : IStudioCommand
{
    public string Description => $"Crear escena '{name}'";

    public Guid? CreatedSceneId { get; private set; }

    public string? Validate(StudioContext context)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "El nombre de la escena no puede estar vacío.";
        if (context.Project.Scenes.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"Ya existe una escena llamada '{name}'.";
        return null;
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var scene = context.Project.AddScene(name);
        if (context.Engine.IsInitialized)
            await context.Engine.CreateSceneAsync(scene.Id, scene.Name, ct).ConfigureAwait(false);
        CreatedSceneId = scene.Id;
    }
}
