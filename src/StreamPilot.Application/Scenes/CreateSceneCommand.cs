using StreamPilot.Application.Commands;

namespace StreamPilot.Application.Scenes;

public sealed class CreateSceneCommand(string name, int? index = null) : IReversibleStudioCommand
{
    public string Description => $"Crear escena '{name}'";

    public Guid? CreatedSceneId { get; private set; }

    public string? Validate(StudioContext context)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "El nombre de la escena no puede estar vacío.";
        if (context.Project.HasSceneNamed(name))
            return $"Ya existe una escena llamada '{name}'.";
        return null;
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        // Rehacer reutiliza el mismo ID para que los comandos posteriores del historial sigan siendo válidos.
        var scene = context.Project.AddScene(name, CreatedSceneId, index);
        if (context.Engine.IsInitialized)
        {
            await context.Engine.CreateSceneAsync(scene.Id, scene.Name, ct).ConfigureAwait(false);
            if (context.Project.ActiveSceneId == scene.Id)
                await context.Engine.SetProgramSceneAsync(scene.Id, ct).ConfigureAwait(false);
        }
        CreatedSceneId = scene.Id;
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (CreatedSceneId is not { } id || context.Project.ActiveSceneId == id)
            throw new InvalidOperationException("No se puede deshacer: la escena está en programa.");
        context.Project.RemoveScene(id);
        if (context.Engine.IsInitialized)
            await context.Engine.RemoveSceneAsync(id, ct).ConfigureAwait(false);
    }
}
