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
}

/// <summary>Fuente reutilizable entre escenas. <see cref="Settings"/> son pares clave/valor propios del tipo.</summary>
public sealed record Source(Guid Id, string Name, SourceKind Kind, IReadOnlyDictionary<string, string> Settings);
