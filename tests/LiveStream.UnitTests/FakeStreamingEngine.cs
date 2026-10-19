using LiveStream.Application.Abstractions;
using LiveStream.Domain.Animation;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.UnitTests;

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
    public Dictionary<Guid, (Guid SourceId, string TypeId, string SettingsJson, bool Enabled)> Filters { get; } = [];
    public Dictionary<Guid, (AnimationClip Clip, AnimationFlags Flags)> Animations { get; } = [];
    public Dictionary<Guid, int> Channels { get; } = [];

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
        Channels.Remove(sceneId);
        foreach (var item in Items.Where(i => i.Value.SceneId == sceneId || i.Value.SourceId == sceneId).Select(i => i.Key).ToList()) Items.Remove(item);
        foreach (var filter in Filters.Where(f => f.Value.SourceId == sceneId).Select(f => f.Key).ToList()) Filters.Remove(filter);
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
        Channels.Remove(sourceId);
        foreach (var item in Items.Where(i => i.Value.SourceId == sourceId).Select(i => i.Key).ToList()) Items.Remove(item);
        foreach (var filter in Filters.Where(f => f.Value.SourceId == sourceId).Select(f => f.Key).ToList()) Filters.Remove(filter);
    });

    public Task RenameSourceAsync(Guid sourceId, string name, CancellationToken ct) => Do("source~", () => Sources[sourceId] = Sources[sourceId] with { Name = name });
    public Task UpdateSourceSettingsAsync(Guid sourceId, string settingsJson, CancellationToken ct) => Do("settings", () => Sources[sourceId] = Sources[sourceId] with { SettingsJson = settingsJson });
    public Task<string> GetSourceSettingsAsync(Guid sourceId, CancellationToken ct) =>
        Task.FromResult(Sources.TryGetValue(sourceId, out var s) ? s.SettingsJson : Filters[sourceId].SettingsJson);
    public Task<IReadOnlyList<SourceProperty>> GetSourcePropertiesAsync(Guid sourceId, CancellationToken ct) => Task.FromResult<IReadOnlyList<SourceProperty>>([]);
    public Task<(int Width, int Height)> GetSourceSizeAsync(Guid sourceId, CancellationToken ct) => Task.FromResult((1280, 720));
    public Task SetGlobalAudioChannelAsync(Guid sourceId, int channel, CancellationToken ct) => Do($"channel{channel}", () =>
    {
        if (channel == 0) Channels.Remove(sourceId); else Channels[sourceId] = channel;
    });

    public Task<IReadOnlyList<EngineTypeInfo>> ListFilterTypesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EngineTypeInfo>>([new("gain_filter", "Ganancia", false, true), new("color_filter_v2", "Corrección de color", true, false)]);
    public Task<IReadOnlyList<EngineTypeInfo>> ListTransitionTypesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EngineTypeInfo>>([new("fade_transition", "Fundido", true, false)]);

    public Task AddFilterAsync(Guid sourceId, Guid filterId, string typeId, string name, string settingsJson, CancellationToken ct) => Do($"filter+{typeId}", () =>
    {
        if (!Sources.ContainsKey(sourceId) && !Scenes.ContainsKey(sourceId)) throw new KeyNotFoundException("Fuente no encontrada.");
        if (!Filters.TryAdd(filterId, (sourceId, typeId, settingsJson, true))) throw new InvalidOperationException("El filtro ya existe.");
    });
    public Task RemoveFilterAsync(Guid filterId, CancellationToken ct) => Do("filter-", () => { if (!Filters.Remove(filterId)) throw new KeyNotFoundException(); });
    public Task UpdateFilterAsync(Guid filterId, string settingsJson, CancellationToken ct) => Do("filter~", () => Filters[filterId] = Filters[filterId] with { SettingsJson = settingsJson });
    public Task SetFilterEnabledAsync(Guid filterId, bool enabled, CancellationToken ct) => Do("filter-enabled", () => Filters[filterId] = Filters[filterId] with { Enabled = enabled });

    public Task PlayAnimationAsync(Guid itemId, AnimationClip clip, AnimationFlags flags, CancellationToken ct) => Do("anim", () =>
    {
        if (!Items.ContainsKey(itemId)) throw new KeyNotFoundException("Elemento no encontrado.");
        if (clip.Validate() is { } error) throw new ArgumentException(error);
        Animations[itemId] = (clip, flags);
        if (flags.HasFlag(AnimationFlags.ShowAtStart)) Visible[itemId] = true;
        // El motor falso termina la animación al instante.
        if (flags.HasFlag(AnimationFlags.HideAtEnd)) Visible[itemId] = false;
    });
    public Task StopAnimationAsync(Guid itemId, CancellationToken ct) => Do("anim-stop", () => { });
    public Task<bool> IsAnimationPlayingAsync(Guid itemId, CancellationToken ct) => Task.FromResult(false);

    public Task SetVolumeAsync(Guid sourceId, double multiplier, CancellationToken ct) => Do("volume", () => Volumes[sourceId] = multiplier);
    public Task SetMutedAsync(Guid sourceId, bool muted, CancellationToken ct) => Do("mute", () => { });
    public Task SetMonitoringAsync(Guid sourceId, AudioMonitoring monitoring, CancellationToken ct) => Do("monitor", () => { });
    public Task SetSyncOffsetAsync(Guid sourceId, int offsetMs, CancellationToken ct) => Do("sync", () => { });
    public Task<AudioLevels?> GetAudioLevelsAsync(Guid sourceId, CancellationToken ct) => Task.FromResult<AudioLevels?>(null);

    public Task AddItemAsync(Guid sceneId, Guid itemId, Guid sourceId, CancellationToken ct) => Do("item+", () =>
    {
        if (!Scenes.ContainsKey(sceneId) || !(Sources.ContainsKey(sourceId) || Scenes.ContainsKey(sourceId))) throw new KeyNotFoundException("Escena o fuente no encontrada.");
        if (sceneId == sourceId) throw new InvalidOperationException("Anidamiento circular.");
        if (!Items.TryAdd(itemId, (sceneId, sourceId))) throw new InvalidOperationException("El elemento ya existe.");
    });

    public Task RemoveItemAsync(Guid itemId, CancellationToken ct) => Do("item-", () => { if (!Items.Remove(itemId)) throw new KeyNotFoundException(); });
    public Task SetItemTransformAsync(Guid itemId, Transform transform, CancellationToken ct) => Do("transform", () => Transforms[itemId] = transform);
    public Task<Transform> GetItemTransformAsync(Guid itemId, CancellationToken ct) =>
        Task.FromResult(Transforms.TryGetValue(itemId, out var t) ? t : Transform.Identity);
    public Task SetItemVisibleAsync(Guid itemId, bool visible, CancellationToken ct) => Do("visible", () => Visible[itemId] = visible);
    public Task SetItemLockedAsync(Guid itemId, bool locked, CancellationToken ct) => Do("locked", () => { });
    public Task SetItemOrderAsync(Guid itemId, int index, CancellationToken ct) => Do("order", () => { });

    public Task StartRecordingAsync(RecordingSettings settings, CancellationToken ct) => Do("rec+", () => { });
    public Task StopRecordingAsync(CancellationToken ct) => Do("rec-", () => { });
    public Task StartStreamAsync(StreamTarget target, CancellationToken ct) => Do("stream+", () => LastStream = target);
    public Task StopStreamAsync(CancellationToken ct) => Do("stream-", () => { });
    /// <summary>Destinos emitiendo en el motor falso; las pruebas pueden cambiar su estado para simular la red.</summary>
    public Dictionary<Guid, DestinationStats> Destinations { get; } = [];
    public Dictionary<Guid, StreamTarget> DestinationTargets { get; } = [];
    /// <summary>Servidores que rechazan la conexión al iniciar.</summary>
    public HashSet<string> FailingServers { get; } = [];

    public Task StartDestinationAsync(Guid destinationId, StreamTarget target, CancellationToken ct) => Do("dest+", () =>
    {
        if (FailingServers.Contains(target.Server)) throw new InvalidOperationException("obs_output_start falló: no se pudo conectar");
        if (Destinations.TryGetValue(destinationId, out var s) && s.Active) throw new InvalidOperationException("Ese destino ya está emitiendo.");
        DestinationTargets[destinationId] = target;
        var shared = DestinationTargets.Any(t => t.Key != destinationId && Destinations.TryGetValue(t.Key, out var o) && o.Active && t.Value.Encoder == target.Encoder);
        Destinations[destinationId] = new DestinationStats(true, false, 0, 0, 0, 0, target.Encoder.VideoBitrateKbps, DynamicBitrate, shared,
            new OutputStopInfo(0, ""), TimeSpan.FromMilliseconds(120));
    });
    public Task StopDestinationAsync(Guid destinationId, CancellationToken ct) => Do("dest-", () =>
    {
        if (!Destinations.TryGetValue(destinationId, out var s) || !s.Active) throw new InvalidOperationException("Ese destino no está emitiendo.");
        Destinations[destinationId] = s with { Active = false, Reconnecting = false };
    });
    public Task<DestinationStats?> GetDestinationStatsAsync(Guid destinationId, CancellationToken ct) =>
        Task.FromResult(Destinations.TryGetValue(destinationId, out var s) ? s : null);
    public Task SetDestinationBitrateAsync(Guid destinationId, int kbps, CancellationToken ct) => Do("dest-bitrate", () =>
    {
        if (!DynamicBitrate) throw new InvalidOperationException("Sin bitrate dinámico.");
        // Como en libobs: el codificador compartido cambia para todos los destinos que lo usan.
        var encoder = DestinationTargets[destinationId].Encoder;
        foreach (var id in DestinationTargets.Where(t => t.Value.Encoder == encoder).Select(t => t.Key).ToList())
            if (Destinations.TryGetValue(id, out var s) && s.Active)
                Destinations[id] = s with { VideoBitrateKbps = kbps };
        BitrateChanges.Add(kbps);
    });

    public List<int> BitrateChanges { get; } = [];
    public bool DynamicBitrate { get; set; } = true;
    public Task SetStreamVideoBitrateAsync(int kbps, CancellationToken ct) => Do("bitrate", () =>
    {
        if (!DynamicBitrate) throw new InvalidOperationException("Sin bitrate dinámico.");
        BitrateChanges.Add(kbps);
    });
    public Task<bool> SupportsDynamicBitrateAsync(string encoderId, CancellationToken ct) => Task.FromResult(DynamicBitrate);
    public Task<OutputStopInfo> GetLastStopAsync(OutputKind kind, CancellationToken ct) => Task.FromResult(new OutputStopInfo(0, ""));

    private Task Do(string call, Action action)
    {
        Calls.Add(call);
        action();
        return Task.CompletedTask;
    }
}
