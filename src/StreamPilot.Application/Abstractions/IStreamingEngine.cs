using StreamPilot.Domain.Animation;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.Application.Abstractions;

/// <summary>
/// Puerto hacia el motor de captura/codificación. La implementación real vive en StreamPilot.Native (libobs).
/// Las implementaciones deben serializar internamente las llamadas nativas. Escenas, fuentes y elementos
/// usan los mismos IDs que el dominio.
/// </summary>
public interface IStreamingEngine : IAsyncDisposable
{
    bool IsInitialized { get; }

    Task InitializeAsync(EngineSettings settings, CancellationToken ct);
    Task ShutdownAsync(CancellationToken ct);
    Task<EngineMetrics> GetMetricsAsync(CancellationToken ct);
    Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync(CancellationToken ct);
    Task<IReadOnlyList<VideoEncoderInfo>> ListVideoEncodersAsync(CancellationToken ct);

    // Escenas
    Task CreateSceneAsync(Guid sceneId, string name, CancellationToken ct);
    Task RemoveSceneAsync(Guid sceneId, CancellationToken ct);
    Task RenameSceneAsync(Guid sceneId, string name, CancellationToken ct);
    /// <summary>Cambia la escena de programa usando la transición configurada.</summary>
    Task SetProgramSceneAsync(Guid sceneId, CancellationToken ct);
    Task SetTransitionAsync(TransitionSettings transition, CancellationToken ct);

    // Fuentes
    Task CreateSourceAsync(SourceDefinition source, CancellationToken ct);
    Task RemoveSourceAsync(Guid sourceId, CancellationToken ct);
    Task RenameSourceAsync(Guid sourceId, string name, CancellationToken ct);
    Task UpdateSourceSettingsAsync(Guid sourceId, string settingsJson, CancellationToken ct);
    /// <summary>Ajustes actuales incluidos los valores por defecto del tipo (objeto JSON). Admite ids de filtro.</summary>
    Task<string> GetSourceSettingsAsync(Guid sourceId, CancellationToken ct);
    Task<IReadOnlyList<SourceProperty>> GetSourcePropertiesAsync(Guid sourceId, CancellationToken ct);
    Task<(int Width, int Height)> GetSourceSizeAsync(Guid sourceId, CancellationToken ct);
    /// <summary>
    /// Asigna una fuente (o escena) a un canal de salida global: los canales 1..5 son el audio global y los
    /// superiores se componen en video por encima del programa (capa de gráficos). 0 la retira.
    /// </summary>
    Task SetGlobalAudioChannelAsync(Guid sourceId, int channel, CancellationToken ct);

    // Audio
    Task SetVolumeAsync(Guid sourceId, double multiplier, CancellationToken ct);
    Task SetMutedAsync(Guid sourceId, bool muted, CancellationToken ct);
    Task SetMonitoringAsync(Guid sourceId, AudioMonitoring monitoring, CancellationToken ct);
    Task SetSyncOffsetAsync(Guid sourceId, int offsetMs, CancellationToken ct);
    /// <summary>Niveles más recientes, o null si la fuente no tiene audio.</summary>
    Task<AudioLevels?> GetAudioLevelsAsync(Guid sourceId, CancellationToken ct);

    // Filtros (de fuentes o de escenas)
    Task<IReadOnlyList<EngineTypeInfo>> ListFilterTypesAsync(CancellationToken ct);
    Task<IReadOnlyList<EngineTypeInfo>> ListTransitionTypesAsync(CancellationToken ct);
    Task AddFilterAsync(Guid sourceId, Guid filterId, string typeId, string name, string settingsJson, CancellationToken ct);
    Task RemoveFilterAsync(Guid filterId, CancellationToken ct);
    Task UpdateFilterAsync(Guid filterId, string settingsJson, CancellationToken ct);
    Task SetFilterEnabledAsync(Guid filterId, bool enabled, CancellationToken ct);

    // Animación por fotogramas clave, en el reloj del motor
    Task PlayAnimationAsync(Guid itemId, AnimationClip clip, AnimationFlags flags, CancellationToken ct);
    Task StopAnimationAsync(Guid itemId, CancellationToken ct);
    Task<bool> IsAnimationPlayingAsync(Guid itemId, CancellationToken ct);

    // Elementos de escena. sourceId puede ser otra escena (escena anidada).
    Task AddItemAsync(Guid sceneId, Guid itemId, Guid sourceId, CancellationToken ct);
    Task RemoveItemAsync(Guid itemId, CancellationToken ct);
    Task SetItemTransformAsync(Guid itemId, Transform transform, CancellationToken ct);
    /// <summary>Transformación actual en el motor (incluye el efecto de una animación en curso).</summary>
    Task<Transform> GetItemTransformAsync(Guid itemId, CancellationToken ct);
    Task SetItemVisibleAsync(Guid itemId, bool visible, CancellationToken ct);
    Task SetItemLockedAsync(Guid itemId, bool locked, CancellationToken ct);
    Task SetItemOrderAsync(Guid itemId, int index, CancellationToken ct);

    // Salidas
    Task StartRecordingAsync(RecordingSettings settings, CancellationToken ct);
    Task StopRecordingAsync(CancellationToken ct);
    Task StartStreamAsync(StreamTarget target, CancellationToken ct);
    Task StopStreamAsync(CancellationToken ct);
    /// <summary>Cambia el bitrate de video de la emisión en curso sin cortarla (si el encoder lo admite).</summary>
    Task SetStreamVideoBitrateAsync(int kbps, CancellationToken ct);
    Task<bool> SupportsDynamicBitrateAsync(string encoderId, CancellationToken ct);
    Task<OutputStopInfo> GetLastStopAsync(OutputKind kind, CancellationToken ct);
}

/// <summary>Vistas nativas del motor (vista previa y programa). Lo implementa el mismo adaptador nativo.</summary>
public interface IEngineDisplays
{
    Task CreateDisplayAsync(Guid displayId, nint nativeWindow, int width, int height, CancellationToken ct = default);
    Task DestroyDisplayAsync(Guid displayId, CancellationToken ct = default);
    Task ResizeDisplayAsync(Guid displayId, int width, int height, CancellationToken ct = default);
    /// <summary>sceneId null = textura de programa (lo que se emite).</summary>
    Task SetDisplaySourceAsync(Guid displayId, Guid? sceneId, CancellationToken ct = default);
    /// <summary>zoom 0 = ajustar a la ventana; offset en píxeles de la ventana.</summary>
    Task SetDisplayViewAsync(Guid displayId, double zoom, double offsetX, double offsetY, CancellationToken ct = default);
    Task SetDisplayOverlayAsync(Guid displayId, IReadOnlyList<Guid> selectedItems, IReadOnlyList<double> guidesX,
        IReadOnlyList<double> guidesY, CancellationToken ct = default);
}

public sealed record EngineSettings(
    int BaseWidth = 1920,
    int BaseHeight = 1080,
    int OutputWidth = 1920,
    int OutputHeight = 1080,
    int FpsNumerator = 30,
    int FpsDenominator = 1,
    int AudioSampleRate = 48000,
    int AdapterIndex = 0,
    string? ModulesDirectory = null,
    string? DataDirectory = null,
    string? ConfigDirectory = null)
{
    public static EngineSettings From(CanvasSettings canvas, int adapterIndex = 0) =>
        new(canvas.Width, canvas.Height, canvas.Width, canvas.Height, canvas.Fps, 1, AdapterIndex: adapterIndex);
}

public sealed record SourceDefinition(Guid Id, string Name, SourceKind Kind, string SettingsJson)
{
    public static SourceDefinition From(Source source) => new(source.Id, source.Name, source.Kind, source.SettingsJson);
}

public sealed record RecordingSettings(string OutputPath, EncoderSettings Encoder)
{
    public RecordingSettings(string outputPath, int videoBitrateKbps = 6000) : this(outputPath, new EncoderSettings(VideoBitrateKbps: videoBitrateKbps)) { }
}

/// <summary>Destino de transmisión con la clave ya descifrada. Nunca se registra ni se persiste.</summary>
public sealed record StreamTarget(string Server, string Key, EncoderSettings Encoder)
{
    public override string ToString() => $"StreamTarget {{ Server = {Server}, Key = ***, Encoder = {Encoder} }}";
}

public sealed record VideoEncoderInfo(string Id, string Name, string Codec);

/// <summary>Tipo de filtro o transición disponible en el runtime del motor.</summary>
public sealed record EngineTypeInfo(string Id, string Name, bool HasVideo, bool HasAudio);

public enum OutputKind { Recording = 0, Stream = 1 }

public sealed record OutputStopInfo(int Code, string Message)
{
    public bool IsError => Code != 0;
}

public sealed record AudioLevels(double PeakLeftDb, double PeakRightDb, double MagnitudeLeftDb, double MagnitudeRightDb, int Channels);

public sealed record EngineMetrics(
    double ActiveFps,
    double AverageFrameRenderMs,
    uint TotalFrames,
    uint LaggedFrames,
    uint SkippedEncoderFrames,
    bool IsRecording,
    double CpuUsagePercent = 0,
    ulong MemoryBytes = 0,
    uint EncodedFrames = 0,
    bool IsStreaming = false,
    bool StreamReconnecting = false,
    uint StreamDroppedFrames = 0,
    uint StreamTotalFrames = 0,
    ulong StreamTotalBytes = 0,
    ulong RecordingTotalBytes = 0,
    /// <summary>0..1: presión de la conexión de emisión.</summary>
    double StreamCongestion = 0,
    int StreamVideoBitrateKbps = 0,
    bool StreamDynamicBitrate = false,
    int RenderAdapterIndex = 0);

/// <summary>Adaptador gráfico (DXGI). Index es el que usa libobs para renderizar.</summary>
public sealed record GraphicsAdapter(int Index, string Name, uint VendorId, ulong DedicatedMemoryBytes, bool IsSoftware)
{
    public string Vendor => VendorId switch { 0x10DE => "NVIDIA", 0x8086 => "Intel", 0x1002 => "AMD", 0x1414 => "Microsoft", _ => $"0x{VendorId:X4}" };
    public bool IsDiscrete => !IsSoftware && DedicatedMemoryBytes >= 1L << 30 && VendorId is 0x10DE or 0x1002;
}

/// <summary>Propiedad editable de una fuente, descrita por el motor.</summary>
public sealed record SourceProperty(
    string Name,
    string Description,
    SourcePropertyType Type,
    bool Enabled,
    string? Tooltip = null,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    bool Slider = false,
    bool Multiline = false,
    bool Password = false,
    bool Info = false,
    string? PathType = null,
    string? Filter = null,
    string? ListFormat = null,
    bool Editable = false,
    IReadOnlyList<SourcePropertyOption>? Options = null,
    IReadOnlyList<SourceProperty>? Children = null);

public sealed record SourcePropertyOption(string Name, object? Value, bool Disabled);

public enum SourcePropertyType
{
    Unknown, Bool, Int, Float, Text, Path, List, Color, ColorAlpha, Font, Button, EditableList, FrameRate, Group,
}
