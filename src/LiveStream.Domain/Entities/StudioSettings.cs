namespace LiveStream.Domain.Entities;

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

/// <summary>
/// Transición usada al cambiar la escena de programa. <see cref="SettingsJson"/> son los ajustes nativos del
/// tipo (archivo y punto de corte del stinger, imagen de la cortinilla, dirección del deslizamiento...).
/// </summary>
public sealed record TransitionSettings(string TypeId = "fade_transition", int DurationMs = 300, string SettingsJson = "{}")
{
    public bool Equals(TransitionSettings? other) =>
        other is not null && TypeId == other.TypeId && DurationMs == other.DurationMs &&
        NormalizedJson == other.NormalizedJson;

    public override int GetHashCode() => HashCode.Combine(TypeId, DurationMs, NormalizedJson);

    private string NormalizedJson => string.IsNullOrWhiteSpace(SettingsJson) ? "{}" : SettingsJson.Trim();
}

public sealed record EncoderSettings(string VideoEncoderId = "auto", int VideoBitrateKbps = 6000, int AudioBitrateKbps = 160, int KeyframeIntervalSec = 2);

/// <summary>
/// Ajustes comunes de emisión: el codificador que usan los destinos sin codificador propio. Server y
/// CredentialName son del esquema anterior a v4 (un solo destino); al abrir un proyecto antiguo se convierten en
/// el destino «Principal».
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
