using LiveStream.Application.Commands;
using LiveStream.Domain.Entities;

namespace LiveStream.Application.Scenes;

/// <summary>Lleva una escena a programa con su transición propia o, si no tiene, con la general.</summary>
public static class ProgramSwitch
{
    public static TransitionSettings TransitionFor(StudioProject project, Guid sceneId) =>
        project.Scenes.FirstOrDefault(s => s.Id == sceneId)?.TransitionOverride ?? project.Transition;

    public static async Task SwitchAsync(StudioContext context, Guid sceneId, CancellationToken ct)
    {
        if (!context.Engine.IsInitialized)
            return;
        var transition = TransitionFor(context.Project, sceneId);
        if (!transition.Equals(context.AppliedTransition))
        {
            await context.Engine.SetTransitionAsync(transition, ct).ConfigureAwait(false);
            context.AppliedTransition = transition;
        }
        await context.Engine.SetProgramSceneAsync(sceneId, ct).ConfigureAwait(false);
    }
}
