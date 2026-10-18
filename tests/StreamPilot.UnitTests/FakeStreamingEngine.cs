using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.UnitTests;

/// <summary>Motor en memoria que refleja el estado que tendría libobs, para verificar la sincronización dominio/motor.</summary>
internal sealed class FakeStreamingEngine : IStreamingEngine
{
    public bool IsInitialized { get; set; } = true;
    public Dictionary<Guid, string> Scenes { get; } = [];
    public Dictionary<Guid, SourceDefinition> Sources { get; } = [];
    public Dictionary<Guid, (Guid SceneId, Guid SourceId)> Items { get; } = [];
    public Dictionary<Guid, Transform> Transforms { get; } = [];
    public Dictionary<Guid, bool> Visible { get; } = [];
    public Dictionary<Guid, double> Volumes { get; } = [];
    public Guid? ProgramScene { get; private set; }
    public TransitionSettings? Transition { get; private set; }
    public List<string> Calls { get; } = [];
    public StreamTarget? LastStream { get; private set; }

    public Task InitializeAsync(EngineSettings settings, CancellationToken ct) { IsInitialized = true; return Task.CompletedTask; }
    public Task ShutdownAsync(CancellationToken ct) { IsInitialized = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<EngineMetrics> GetMetricsAsync(CancellationToken ct) => Task.FromResult(new EngineMetrics(30, 1, 0, 0, 0, false));
    public Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CaptureDevice>>([]);
    public Task<IReadOnlyList<VideoEncoderInfo>> ListVideoEncodersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<VideoEncoderInfo>>([]);

    public Task CreateSceneAsync(Guid sceneId, string name, CancellationToken ct) => Do($"scene+{name}", () =>
    {
        if (!Scenes.TryAdd(sceneId, name)) throw new InvalidOperationException("La escena ya existe.");
    });

    public Task RemoveSceneAsync(Guid sceneId, CancellationToken ct) => Do("scene-", () =>
    {
        if (ProgramScene == sceneId) throw new InvalidOperationException("Escena en programa.");
        Scenes.Remove(sceneId);
        foreach (var item in Items.Where(i => i.Value.SceneId == sceneId).Select(i => i.Key).ToList()) Items.Remove(item);
    });

    public Task RenameSceneAsync(Guid sceneId, string name, CancellationToken ct) => Do("scene~", () => Scenes[sceneId] = name);
    public Task SetProgramSceneAsync(Guid sceneId, CancellationToken ct) => Do("program", () => ProgramScene = Scenes.ContainsKey(sceneId) ? sceneId : throw new KeyNotFoundException());
    public Task SetTransitionAsync(TransitionSettings transition, CancellationToken ct) => Do("transition", () => Transition = transition);

    public Task CreateSourceAsync(SourceDefinition source, CancellationToken ct) => Do($"source+{source.Name}", () =>
    {
        if (source.Name == "falla") throw new InvalidOperationException("El motor rechazó la fuente.");
        if (!Sources.TryAdd(source.Id, source)) throw new InvalidOperationException("La fuente ya existe.");
    });

    public Task RemoveSourceAsync(Guid sourceId, CancellationToken ct) => Do("source-", () =>
    {
        Sources.Remove(sourceId);
        foreach (var item in Items.Where(i => i.Value.SourceId == sourceId).Select(i => i.Key).ToList()) Items.Remove(item);
    });

    public Task RenameSourceAsync(Guid sourceId, string name, CancellationToken ct) => Do("source~", () => Sources[sourceId] = Sources[sourceId] with { Name = name });
    public Task UpdateSourceSettingsAsync(Guid sourceId, string settingsJson, CancellationToken ct) => Do("settings", () => Sources[sourceId] = Sources[sourceId] with { SettingsJson = settingsJson });
    public Task<string> GetSourceSettingsAsync(Guid sourceId, CancellationToken ct) => Task.FromResult(Sources[sourceId].SettingsJson);
    public Task<IReadOnlyList<SourceProperty>> GetSourcePropertiesAsync(Guid sourceId, CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceProperty>>([]);
    public Task<(int Width, int Height)> GetSourceSizeAsync(Guid sourceId, CancellationToken ct) => Task.FromResult((1280, 720));
    public Task SetGlobalAudioChannelAsync(Guid sourceId, int channel, CancellationToken ct) => Do($"channel{channel}", () => { });

    public Task SetVolumeAsync(Guid sourceId, double multiplier, CancellationToken ct) => Do("volume", () => Volumes[sourceId] = multiplier);
    public Task SetMutedAsync(Guid sourceId, bool muted, CancellationToken ct) => Do("mute", () => { });
    public Task SetMonitoringAsync(Guid sourceId, AudioMonitoring monitoring, CancellationToken ct) => Do("monitor", () => { });
    public Task SetSyncOffsetAsync(Guid sourceId, int offsetMs, CancellationToken ct) => Do("sync", () => { });
    public Task<AudioLevels?> GetAudioLevelsAsync(Guid sourceId, CancellationToken ct) => Task.FromResult<AudioLevels?>(null);

    public Task AddItemAsync(Guid sceneId, Guid itemId, Guid sourceId, CancellationToken ct) => Do("item+", () =>
    {
        if (!Scenes.ContainsKey(sceneId) || !Sources.ContainsKey(sourceId)) throw new KeyNotFoundException("Escena o fuente no encontrada.");
        if (!Items.TryAdd(itemId, (sceneId, sourceId))) throw new InvalidOperationException("El elemento ya existe.");
    });

    public Task RemoveItemAsync(Guid itemId, CancellationToken ct) => Do("item-", () => { if (!Items.Remove(itemId)) throw new KeyNotFoundException(); });
    public Task SetItemTransformAsync(Guid itemId, Transform transform, CancellationToken ct) => Do("transform", () => Transforms[itemId] = transform);
    public Task SetItemVisibleAsync(Guid itemId, bool visible, CancellationToken ct) => Do("visible", () => Visible[itemId] = visible);
    public Task SetItemLockedAsync(Guid itemId, bool locked, CancellationToken ct) => Do("locked", () => { });
    public Task SetItemOrderAsync(Guid itemId, int index, CancellationToken ct) => Do("order", () => { });

    public Task StartRecordingAsync(RecordingSettings settings, CancellationToken ct) => Do("rec+", () => { });
    public Task StopRecordingAsync(CancellationToken ct) => Do("rec-", () => { });
    public Task StartStreamAsync(StreamTarget target, CancellationToken ct) => Do("stream+", () => LastStream = target);
    public Task StopStreamAsync(CancellationToken ct) => Do("stream-", () => { });
    public Task<OutputStopInfo> GetLastStopAsync(OutputKind kind, CancellationToken ct) => Task.FromResult(new OutputStopInfo(0, ""));

    private Task Do(string call, Action action)
    {
        Calls.Add(call);
        action();
        return Task.CompletedTask;
    }
}
