using StreamPilot.Application.Commands;
using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Audio;

/// <summary>
/// Ajustes del mezclador. Son operaciones en vivo: no entran en el historial de deshacer (como en OBS),
/// pero sí se validan, se serializan y se persisten.
/// </summary>
public sealed class SetVolumeCommand(Guid sourceId, double multiplier) : IStudioCommand
{
    public string Description => "Volumen";

    public string? Validate(StudioContext context)
    {
        if (context.Project.FindSource(sourceId) is null)
            return "La fuente no existe.";
        return multiplier is >= 0 and <= 20 ? null : "Volumen fuera de rango.";
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.GetSource(sourceId).SetVolume(multiplier);
        if (context.Engine.IsInitialized)
            await context.Engine.SetVolumeAsync(sourceId, multiplier, ct).ConfigureAwait(false);
    }

    /// <summary>Convierte decibelios (-inf..+26) en multiplicador lineal.</summary>
    public static double DbToMultiplier(double db) => db <= -96 ? 0 : Math.Pow(10, db / 20);

    public static double MultiplierToDb(double multiplier) => multiplier <= 0 ? double.NegativeInfinity : 20 * Math.Log10(multiplier);
}

public sealed class SetMutedCommand(Guid sourceId, bool muted) : IStudioCommand
{
    public string Description => muted ? "Silenciar" : "Activar sonido";

    public string? Validate(StudioContext context) => context.Project.FindSource(sourceId) is null ? "La fuente no existe." : null;

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.GetSource(sourceId).Muted = muted;
        if (context.Engine.IsInitialized)
            await context.Engine.SetMutedAsync(sourceId, muted, ct).ConfigureAwait(false);
    }
}

public sealed class SetMonitoringCommand(Guid sourceId, AudioMonitoring monitoring) : IStudioCommand
{
    public string Description => "Monitorización de audio";

    public string? Validate(StudioContext context) => context.Project.FindSource(sourceId) is null ? "La fuente no existe." : null;

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.GetSource(sourceId).Monitoring = monitoring;
        if (context.Engine.IsInitialized)
            await context.Engine.SetMonitoringAsync(sourceId, monitoring, ct).ConfigureAwait(false);
    }
}

public sealed class SetSyncOffsetCommand(Guid sourceId, int offsetMs) : IStudioCommand
{
    public string Description => "Desfase de sincronización";

    public string? Validate(StudioContext context)
    {
        if (context.Project.FindSource(sourceId) is null)
            return "La fuente no existe.";
        return offsetMs is >= -950 and <= 20_000 ? null : "El desfase debe estar entre -950 y 20000 ms.";
    }

    public async Task ExecuteAsync(StudioContext context, CancellationToken ct)
    {
        context.Project.GetSource(sourceId).SyncOffsetMs = offsetMs;
        if (context.Engine.IsInitialized)
            await context.Engine.SetSyncOffsetAsync(sourceId, offsetMs, ct).ConfigureAwait(false);
    }
}
