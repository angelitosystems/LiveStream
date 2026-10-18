using StreamPilot.Application.Commands;
using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Studio;

/// <summary>Cambia los ajustes de transmisión y grabación del proyecto. No afecta a salidas ya activas.</summary>
public sealed class SetOutputSettingsCommand(StreamSettings stream, RecordingPreferences recording) : IStudioCommand
{
    public string Description => "Ajustes de salida";

    public string? Validate(StudioContext context)
    {
        var server = stream.Server.Trim();
        if (server.Length > 0 && !server.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) &&
            !server.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
            return "El servidor debe empezar por rtmp:// o rtmps://.";
        if (!RecordingPreferences.SupportedContainers.Contains(recording.Container))
            return $"Formato de grabación no soportado: {recording.Container}.";
        foreach (var encoder in new[] { stream.EffectiveEncoder, recording.EffectiveEncoder })
        {
            if (encoder.VideoBitrateKbps is < 200 or > 150_000)
                return "El bitrate de video debe estar entre 200 y 150000 kb/s.";
            if (encoder.AudioBitrateKbps is < 32 or > 512)
                return "El bitrate de audio debe estar entre 32 y 512 kb/s.";
            if (encoder.KeyframeIntervalSec is < 0 or > 20)
                return "El intervalo de fotogramas clave debe estar entre 0 y 20 s.";
        }
        if (recording.Folder.Length > 0 && !Path.IsPathFullyQualified(recording.Folder))
            return "La carpeta de grabación debe ser una ruta completa.";
        return null;
    }

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.Stream = stream with { Server = stream.Server.Trim() };
        context.Project.Recording = recording;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Cambia el lienzo (resolución/FPS). libobs solo aplica un lienzo nuevo al iniciar, así que el cambio se guarda en
/// el proyecto y la UI ofrece reiniciar.
/// </summary>
public sealed class SetCanvasCommand(CanvasSettings canvas) : IStudioCommand
{
    public string Description => $"Lienzo {canvas.Width}x{canvas.Height} a {canvas.Fps} FPS";

    public string? Validate(StudioContext context)
    {
        try
        {
            canvas.Validate();
            return canvas.Width % 2 != 0 || canvas.Height % 2 != 0 ? "La resolución debe ser par (requisito de los codificadores)." : null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    public Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.Canvas = canvas;
        return Task.CompletedTask;
    }
}
