using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.UnitTests;

internal sealed class FakeStreamingEngine : IStreamingEngine
{
    public List<(Guid Id, string Name)> CreatedScenes { get; } = [];
    public bool IsInitialized { get; set; } = true;

    public Task InitializeAsync(EngineSettings settings, CancellationToken ct) { IsInitialized = true; return Task.CompletedTask; }
    public Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CaptureDevice>>([]);
    public Task CreateSceneAsync(Guid sceneId, string name, CancellationToken ct) { CreatedScenes.Add((sceneId, name)); return Task.CompletedTask; }
    public Task AddSourceAsync(Guid sceneId, SourceDefinition source, CancellationToken ct) => Task.CompletedTask;
    public Task SetProgramSceneAsync(Guid sceneId, CancellationToken ct) => Task.CompletedTask;
    public Task StartRecordingAsync(RecordingSettings settings, CancellationToken ct) => Task.CompletedTask;
    public Task StopRecordingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<EngineMetrics> GetMetricsAsync(CancellationToken ct) => Task.FromResult(new EngineMetrics(60, 1, 0, 0, 0, false));
    public Task ShutdownAsync(CancellationToken ct) { IsInitialized = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
