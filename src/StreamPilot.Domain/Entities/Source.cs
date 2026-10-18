namespace StreamPilot.Domain.Entities;

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

    /// <summary>
    /// Canal global (1..5) para fuentes que suenan en todas las escenas, como el audio de escritorio y el
    /// micrófono. 0 = solo suena cuando está en la escena de programa.
    /// </summary>
    public int GlobalAudioChannel { get; private set; }

    public bool IsAudioOnly => Kind is SourceKind.AudioInput or SourceKind.AudioOutput;

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public void SetVolume(double multiplier) =>
        Volume = multiplier is >= 0 and <= 20 ? multiplier : throw new ArgumentOutOfRangeException(nameof(multiplier));

    public void SetGlobalAudioChannel(int channel) =>
        GlobalAudioChannel = channel is >= 0 and <= MaxGlobalAudioChannel
            ? channel
            : throw new ArgumentOutOfRangeException(nameof(channel));
}
