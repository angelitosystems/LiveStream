using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Studio;

/// <summary>Traslada entidades del dominio al motor. Se usa al abrir un proyecto y al deshacer eliminaciones.</summary>
public static class EngineSync
{
    public static async Task CreateSourceAsync(IStreamingEngine engine, Source source, CancellationToken ct)
    {
        await engine.CreateSourceAsync(SourceDefinition.From(source), ct).ConfigureAwait(false);
        await ApplyAudioAsync(engine, source, ct).ConfigureAwait(false);
    }

    public static async Task ApplyAudioAsync(IStreamingEngine engine, Source source, CancellationToken ct)
    {
        // Solo afecta a fuentes con audio; el motor ignora volumen y mute en fuentes de solo video.
        if (Math.Abs(source.Volume - 1.0) > 1e-9)
            await engine.SetVolumeAsync(source.Id, source.Volume, ct).ConfigureAwait(false);
        if (source.Muted)
            await engine.SetMutedAsync(source.Id, true, ct).ConfigureAwait(false);
        if (source.Monitoring != AudioMonitoring.None)
            await engine.SetMonitoringAsync(source.Id, source.Monitoring, ct).ConfigureAwait(false);
        if (source.SyncOffsetMs != 0)
            await engine.SetSyncOffsetAsync(source.Id, source.SyncOffsetMs, ct).ConfigureAwait(false);
        if (source.GlobalAudioChannel > 0)
            await engine.SetGlobalAudioChannelAsync(source.Id, source.GlobalAudioChannel, ct).ConfigureAwait(false);
    }

    public static async Task CreateItemAsync(IStreamingEngine engine, Guid sceneId, SceneItem item, int index, CancellationToken ct)
    {
        await engine.AddItemAsync(sceneId, item.Id, item.SourceId, ct).ConfigureAwait(false);
        await engine.SetItemTransformAsync(item.Id, item.Transform, ct).ConfigureAwait(false);
        if (!item.Visible)
            await engine.SetItemVisibleAsync(item.Id, false, ct).ConfigureAwait(false);
        if (item.Locked)
            await engine.SetItemLockedAsync(item.Id, true, ct).ConfigureAwait(false);
        await engine.SetItemOrderAsync(item.Id, index, ct).ConfigureAwait(false);
    }

    public static async Task CreateSceneAsync(IStreamingEngine engine, Scene scene, CancellationToken ct)
    {
        await engine.CreateSceneAsync(scene.Id, scene.Name, ct).ConfigureAwait(false);
        for (var i = 0; i < scene.Items.Count; i++)
            await CreateItemAsync(engine, scene.Id, scene.Items[i], i, ct).ConfigureAwait(false);
    }

    /// <summary>Carga un proyecto completo en un motor recién inicializado.</summary>
    public static async Task LoadProjectAsync(IStreamingEngine engine, StudioProject project, CancellationToken ct)
    {
        foreach (var source in project.Sources)
            await CreateSourceAsync(engine, source, ct).ConfigureAwait(false);
        foreach (var scene in project.Scenes)
            await CreateSceneAsync(engine, scene, ct).ConfigureAwait(false);
        await engine.SetTransitionAsync(project.Transition, ct).ConfigureAwait(false);
        if (project.ActiveSceneId is { } active)
            await engine.SetProgramSceneAsync(active, ct).ConfigureAwait(false);
    }
}
