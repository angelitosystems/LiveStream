namespace LiveStream.Domain.Entities;

public enum SourceKind
{
    DisplayCapture,
    GameCapture,
    WindowCapture,
    VideoCaptureDevice,
    AudioInput,
    AudioOutput,
    Image,
    Text,
    Browser,
    Media,
    ColorSource,
    ImageSlideshow,
}

public enum AudioMonitoring
{
    None = 0,
    MonitorOnly = 1,
    MonitorAndOutput = 2,
}

/// <summary>
/// Fuente reutilizable entre escenas. Una misma fuente puede aparecer en varias escenas sin duplicar la
/// captura ni los recursos gráficos. <see cref="SettingsJson"/> son los ajustes nativos del tipo (objeto JSON).
/// </summary>
public sealed class Source
{
    public const int MaxGlobalAudioChannel = 5;

    public Source(Guid id, string name, SourceKind kind, string settingsJson = "{}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name;
        Kind = kind;
        SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
    }

    public Guid Id { get; }
    public SourceKind Kind { get; }
    public string Name { get; private set; }
    public string SettingsJson { get; set; }

    public double Volume { get; private set; } = 1.0;
    public bool Muted { get; set; }
    public AudioMonitoring Monitoring { get; set; }
    public int SyncOffsetMs { get; set; }

    /// <summary>Indica si se captura y procesa el audio de esta fuente (false = modo solo vídeo o sin captura de audio).</summary>
    public bool AudioCaptureEnabled { get; set; } = true;

    /// <summary>
    /// Canal global (1..5) para fuentes que suenan en todas las escenas, como el audio de escritorio y el
    /// micrófono. 0 = solo suena cuando está en la escena de programa.
    /// </summary>
    public int GlobalAudioChannel { get; private set; }

    public bool IsAudioOnly => Kind is SourceKind.AudioInput or SourceKind.AudioOutput;

    /// <summary>Indica si el tipo de fuente tiene capacidades potenciales de audio.</summary>
    public bool SupportsAudio => Kind is SourceKind.AudioInput or SourceKind.AudioOutput or SourceKind.Media or SourceKind.VideoCaptureDevice or SourceKind.Browser;

    /// <summary>Indica si la fuente entrega audio activo a la mezcla actual.</summary>
    public bool HasActiveAudio => SupportsAudio && (IsAudioOnly || AudioCaptureEnabled);

    private readonly List<SourceFilter> _filters = [];

    /// <summary>Filtros en orden de aplicación.</summary>
    public IReadOnlyList<SourceFilter> Filters => _filters;

    public SourceFilter GetFilter(Guid filterId) =>
        _filters.FirstOrDefault(f => f.Id == filterId) ?? throw new KeyNotFoundException($"Filtro {filterId} no encontrado.");

    public void InsertFilter(SourceFilter filter, int index)
    {
        if (_filters.Any(f => f.Id == filter.Id))
            throw new InvalidOperationException("El filtro ya existe.");
        _filters.Insert(Math.Clamp(index, 0, _filters.Count), filter);
    }

    public SourceFilter RemoveFilter(Guid filterId)
    {
        var filter = GetFilter(filterId);
        _filters.Remove(filter);
        return filter;
    }

    public int IndexOfFilter(Guid filterId) => _filters.FindIndex(f => f.Id == filterId);

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public void SetVolume(double multiplier) =>
        Volume = multiplier is >= 0 and <= 20 ? multiplier : throw new ArgumentOutOfRangeException(nameof(multiplier));

    public void SetAudioCaptureEnabled(bool enabled) => AudioCaptureEnabled = enabled;

    public void SetGlobalAudioChannel(int channel) =>
        GlobalAudioChannel = channel is >= 0 and <= MaxGlobalAudioChannel
            ? channel
            : throw new ArgumentOutOfRangeException(nameof(channel));
}
