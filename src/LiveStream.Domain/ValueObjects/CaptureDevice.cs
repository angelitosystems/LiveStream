namespace LiveStream.Domain.ValueObjects;

public enum CaptureDeviceKind { Camera, Microphone, AudioOutput, Display }

/// <param name="Id">Identificador estable que entiende el motor (p. ej. ruta de dispositivo DirectShow).</param>
public sealed record CaptureDevice(string Id, string Name, CaptureDeviceKind Kind);
