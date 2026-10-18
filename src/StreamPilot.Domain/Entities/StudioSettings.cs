namespace StreamPilot.Domain.Entities;

/// <summary>Lienzo base. Horizontal (1920x1080) o vertical (1080x1920).</summary>
public sealed record CanvasSettings(int Width = 1920, int Height = 1080, int Fps = 30)
{
    public bool IsVertical => Height > Width;

    public void Validate()
    {
        if (Width is < 2 or > 7680 || Height is < 2 or > 7680)
            throw new ArgumentException("Resolución del lienzo fuera de rango (2..7680).");
        if (Fps is < 1 or > 240)
            throw new ArgumentException("FPS fuera de rango (1..240).");
    }
}

/// <summary>Transición usada al cambiar la escena de programa.</summary>
public sealed record TransitionSettings(string TypeId = "fade_transition", int DurationMs = 300);

public sealed record EncoderSettings(string VideoEncoderId = "auto", int VideoBitrateKbps = 6000, int AudioBitrateKbps = 160, int KeyframeIntervalSec = 2);

/// <summary>
/// Destino RTMP/RTMPS. La clave de transmisión NO se guarda en el proyecto: <see cref="CredentialName"/>
/// apunta al almacén de secretos cifrado.
/// </summary>
public sealed record StreamSettings(
    string Server = "",
    string CredentialName = "stream-key-default",
    EncoderSettings? Encoder = null)
{
    public EncoderSettings EffectiveEncoder => Encoder ?? new EncoderSettings();
}

public sealed record RecordingPreferences(
    string Folder = "",
    string Container = "mkv",
    EncoderSettings? Encoder = null)
{
    public static readonly IReadOnlyList<string> SupportedContainers = ["mkv", "mp4", "mov", "flv"];

    public EncoderSettings EffectiveEncoder => Encoder ?? new EncoderSettings(VideoBitrateKbps: 12000);
}
