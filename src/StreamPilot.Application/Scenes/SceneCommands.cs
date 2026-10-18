using StreamPilot.Application.Commands;
using StreamPilot.Application.Studio;
using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Scenes;

public sealed class RemoveSceneCommand(Guid sceneId) : IReversibleStudioCommand
{
    private Scene? _removed;
    private int _index;

    public string Description => "Eliminar escena";

    public string? Validate(StudioContext context)
    {
        if (context.Project.Scenes.All(s => s.Id != sceneId))
            return "La escena no existe.";
        if (context.Project.ActiveSceneId == sceneId)
            return "No se puede eliminar la escena que está en programa. Cambia a otra escena primero.";
        return null;
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        _index = context.Project.IndexOfScene(sceneId);
        _removed = context.Project.RemoveScene(sceneId);
        if (context.Engine.IsInitialized)
            await context.Engine.RemoveSceneAsync(sceneId, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_removed is null)
            return;
        context.Project.InsertScene(_removed, _index);
        if (context.Engine.IsInitialized)
            await EngineSync.CreateSceneAsync(context.Engine, _removed, ct).ConfigureAwait(false);
    }
}

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
        if (context.Project.HasSceneNamed(newName, except: sceneId))
            return $"Ya existe una escena llamada '{newName}'.";
        return null;
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var scene = context.Project.GetScene(sceneId);
        _previousName = scene.Name;
        scene.Rename(newName);
        if (context.Engine.IsInitialized)
            await context.Engine.RenameSceneAsync(sceneId, newName, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_previousName is null)
            return;
        context.Project.GetScene(sceneId).Rename(_previousName);
        if (context.Engine.IsInitialized)
            await context.Engine.RenameSceneAsync(sceneId, _previousName, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Duplica una escena. Los elementos nuevos referencian las MISMAS fuentes (no se duplican capturas),
/// igual que en OBS Studio.
/// </summary>
public sealed class DuplicateSceneCommand(Guid sceneId) : IReversibleStudioCommand
{
    private Scene? _copy;

    public string Description => "Duplicar escena";

    public Guid? CreatedSceneId => _copy?.Id;

    public string? Validate(StudioContext context) =>
        context.Project.Scenes.Any(s => s.Id == sceneId) ? null : "La escena no existe.";

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var original = context.Project.GetScene(sceneId);
        if (_copy is null)
        {
            _copy = new Scene(Guid.NewGuid(), context.Project.NextSceneName(original.Name))
            {
                TransitionOverride = original.TransitionOverride,
            };
            foreach (var item in original.Items)
            {
                var clone = _copy.AddItem(item.SourceId, item.Transform);
                clone.Visible = item.Visible;
                clone.Locked = item.Locked;
            }
        }
        context.Project.InsertScene(_copy, context.Project.IndexOfScene(sceneId) + 1);
        if (context.Engine.IsInitialized)
            await EngineSync.CreateSceneAsync(context.Engine, _copy, ct).ConfigureAwait(false);
    }

    public async Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_copy is null)
            return;
        context.Project.RemoveScene(_copy.Id);
        if (context.Engine.IsInitialized)
            await context.Engine.RemoveSceneAsync(_copy.Id, ct).ConfigureAwait(false);
    }
}

public sealed class MoveSceneCommand(Guid sceneId, int newIndex) : IReversibleStudioCommand
{
    private int _oldIndex = -1;

    public string Description => "Reordenar escena";

    public string? Validate(StudioContext context) =>
        context.Project.Scenes.Any(s => s.Id == sceneId) ? null : "La escena no existe.";

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        _oldIndex = context.Project.IndexOfScene(sceneId);
        context.Project.MoveScene(sceneId, newIndex);
        return Task.CompletedTask;
    }

    public Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        if (_oldIndex >= 0)
            context.Project.MoveScene(sceneId, _oldIndex);
        return Task.CompletedTask;
    }
}

/// <summary>Pone una escena en programa (con la transición configurada). No entra en el historial de deshacer.</summary>
public sealed class ActivateSceneCommand(Guid sceneId) : IStudioCommand
{
    public string Description => "Cambiar escena de programa";

    public string? Validate(StudioContext context) =>
        context.Project.Scenes.Any(s => s.Id == sceneId) ? null : "La escena no existe.";

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.ActivateScene(sceneId);
        await ProgramSwitch.SwitchAsync(context, sceneId, ct).ConfigureAwait(false);
    }
}

/// <summary>Activa o desactiva el modo estudio (vista previa editable + programa separado).</summary>
public sealed class SetStudioModeCommand(bool enabled) : IStudioCommand
{
    public string Description => enabled ? "Activar modo estudio" : "Desactivar modo estudio";

    public string? Validate(StudioContext context) => context.Project.Scenes.Count == 0 ? "No hay escenas." : null;

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.SetStudioMode(enabled);
        return Task.CompletedTask;
    }
}

/// <summary>En modo estudio, elige la escena que se edita en la vista previa sin tocar el programa.</summary>
public sealed class SetPreviewSceneCommand(Guid sceneId) : IStudioCommand
{
    public string Description => "Cambiar escena de vista previa";

    public string? Validate(StudioContext context)
    {
        if (!context.Project.StudioMode)
            return "El modo estudio no está activo.";
        return context.Project.Scenes.Any(s => s.Id == sceneId) ? null : "La escena no existe.";
    }

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.SetPreviewScene(sceneId);
        return Task.CompletedTask;
    }
}

/// <summary>En modo estudio, lleva la escena de vista previa al programa con la transición configurada.</summary>
public sealed class TransitionToProgramCommand : IStudioCommand
{
    public string Description => "Transición a programa";

    public string? Validate(StudioContext context)
    {
        if (context.Project.PreviewSceneId is not { } preview)
            return "El modo estudio no está activo.";
        return preview == context.Project.ActiveSceneId ? "La vista previa ya está en programa." : null;
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var preview = context.Project.PreviewSceneId!.Value;
        var previousProgram = context.Project.ActiveSceneId;
        context.Project.ActivateScene(preview);
        // Como en OBS: la escena que sale de programa pasa a la vista previa.
        if (previousProgram is { } old)
            context.Project.SetPreviewScene(old);
        await ProgramSwitch.SwitchAsync(context, preview, ct).ConfigureAwait(false);
    }
}

/// <summary>Cambia la transición general del proyecto.</summary>
public sealed class SetTransitionCommand(TransitionSettings transition) : IStudioCommand
{
    public static IReadOnlyDictionary<string, string> Available => TransitionCatalog.Names;

    public string Description => $"Transición: {transition.TypeId} {transition.DurationMs} ms";

    public string? Validate(StudioContext context) => TransitionCatalog.Validate(transition);

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.Transition = transition;
        if (!context.Engine.IsInitialized)
            return;
        await context.Engine.SetTransitionAsync(transition, ct).ConfigureAwait(false);
        context.AppliedTransition = transition;
    }
}

/// <summary>Asigna (o quita, con null) la transición propia de una escena: se usa al entrar en ella.</summary>
public sealed class SetSceneTransitionCommand(Guid sceneId, TransitionSettings? transition) : IReversibleStudioCommand
{
    private TransitionSettings? _previous;

    public string Description => transition is null ? "Quitar transición de la escena" : "Transición de la escena";

    public string? Validate(StudioContext context)
    {
        if (context.Project.Scenes.All(s => s.Id != sceneId))
            return "La escena no existe.";
        return transition is null ? null : TransitionCatalog.Validate(transition);
    }

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        var scene = context.Project.GetScene(sceneId);
        _previous = scene.TransitionOverride;
        scene.TransitionOverride = transition;
        return Task.CompletedTask;
    }

    public Task UndoAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.GetScene(sceneId).TransitionOverride = _previous;
        return Task.CompletedTask;
    }
}
