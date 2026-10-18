using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.Application.Abstractions;

/// <summary>
/// Puerto hacia el motor de captura/codificación. La implementación real vive en StreamPilot.Native (libobs).
/// Las implementaciones deben serializar internamente las llamadas nativas.
/// </summary>
public interface IStreamingEngine : IAsyncDisposable
{
    bool IsInitialized { get; }

    Task InitializeAsync(EngineSettings settings, CancellationToken ct);
    Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync(CancellationToken ct);
    /// <summary>Crea la escena nativa usando el mismo ID que la entidad de dominio, para no mantener dos identidades.</summary>
    Task CreateSceneAsync(Guid sceneId, string name, CancellationToken ct);
    Task AddSourceAsync(Guid sceneId, SourceDefinition source, CancellationToken ct);
    Task SetProgramSceneAsync(Guid sceneId, CancellationToken ct);
    Task StartRecordingAsync(RecordingSettings settings, CancellationToken ct);
    Task StopRecordingAsync(CancellationToken ct);
    Task<EngineMetrics> GetMetricsAsync(CancellationToken ct);
    Task ShutdownAsync(CancellationToken ct);
}

public sealed record EngineSettings(
    int BaseWidth = 1920,
    int BaseHeight = 1080,
    int OutputWidth = 1920,
    int OutputHeight = 1080,
    int FpsNumerator = 60,
    int FpsDenominator = 1,
    int AudioSampleRate = 48000,
    string? ModulesDirectory = null,
    string? DataDirectory = null);

public sealed record SourceDefinition(Guid Id, string Name, SourceKind Kind, IReadOnlyDictionary<string, string> Settings)
{
    public static SourceDefinition From(Source source) => new(source.Id, source.Name, source.Kind, source.Settings);
}

public sealed record RecordingSettings(string OutputPath, int VideoBitrateKbps = 6000, int AudioBitrateKbps = 160, string Encoder = "auto");

public sealed record EngineMetrics(
    double ActiveFps,
    double AverageFrameRenderMs,
    uint TotalFrames,
    uint LaggedFrames,
    uint SkippedEncoderFrames,
    bool IsRecording);
