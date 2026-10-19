using System.Text;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Studio;
using LiveStream.Domain.Entities;

namespace LiveStream.Application.Optimization;

public enum OptimizationEventKind { Info, Adjusted, Warning, Applied, Reverted }

/// <summary>Entrada del registro de auditoría: qué se decidió, cuándo y por qué.</summary>
public sealed record OptimizationEvent(DateTime Time, OptimizationEventKind Kind, string Title, string Detail, Guid? RevertId = null);

/// <summary>Estado en vivo para el panel y el asistente.</summary>
public sealed record OptimizerStatus(
    bool AdaptiveEnabled,
    bool Active,
    int TargetKbps,
    int CurrentKbps,
    double DropRate,
    double Congestion,
    double RenderLagRate,
    double EncoderSkipRate,
    string? Note);

/// <summary>
/// Optimizador en directo. Recibe las métricas del motor cada segundo, aplica las decisiones del controlador de
/// bitrate (lo único que se toca sin preguntar, y solo si el codificador lo admite) y detecta sobrecargas de render o
/// de codificación, que solo se avisan. Todo queda en un registro de auditoría con su motivo; los perfiles aplicados
/// se pueden revertir.
/// </summary>
public sealed class PerformanceOptimizer(StudioSession session, AdaptiveBitrateOptions? options = null)
{
    public const int MaxEvents = 300;
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly List<OptimizationEvent> _events = [];
    private readonly Dictionary<Guid, (StreamSettings Stream, RecordingPreferences Recording)> _reverts = [];
    private readonly Dictionary<string, Group> _groups = [];
    private EngineMetrics? _previous;
    private double _renderLag, _encoderSkip;
    private int _renderBad, _encoderBad;
    private DateTime _lastRenderWarning = DateTime.MinValue, _lastEncoderWarning = DateTime.MinValue;

    /// <summary>
    /// Destinos que comparten codificador (mismos ajustes): un cambio de bitrate les afecta a todos, así que se decide
    /// una vez por grupo con sus cifras sumadas y la peor congestión.
    /// </summary>
    private sealed class Group(string names, AdaptiveBitrateController? controller, string? note)
    {
        public string Names { get; set; } = names;
        public AdaptiveBitrateController? Controller { get; } = controller;
        public string? Note { get; } = note;
        public int CurrentKbps { get; set; }
        public bool Seen { get; set; }
    }

    /// <summary>Bitrate adaptativo activado (ajusta el bitrate de la emisión en directo).</summary>
    public bool AdaptiveEnabled { get; set; } = true;

    public event EventHandler? Changed;

    public IReadOnlyList<OptimizationEvent> Events
    {
        get
        {
            lock (_lock)
                return _events.ToList();
        }
    }

    /// <summary>Estado resumido: el grupo con más congestión (o el primero) y los avisos de sobrecarga.</summary>
    public OptimizerStatus Status
    {
        get
        {
            lock (_lock)
            {
                var g = _groups.Values.OrderByDescending(x => x.Controller?.Congestion ?? 0).FirstOrDefault();
                return new OptimizerStatus(AdaptiveEnabled, g is not null, g?.Controller?.TargetKbps ?? 0, g?.CurrentKbps ?? 0,
                    g?.Controller?.DropRate ?? 0, g?.Controller?.Congestion ?? 0, _renderLag, _encoderSkip, g?.Note);
            }
        }
    }

    private static string EncoderKey(EncoderSettings e) => $"{e.VideoEncoderId}|{e.VideoBitrateKbps}|{e.KeyframeIntervalSec}|{e.AudioBitrateKbps}";

    /// <summary>Procesa una muestra por segundo: métricas globales y el estado de cada destino.</summary>
    public async Task TickAsync(EngineMetrics m, DateTime now, IReadOnlyList<Streaming.DestinationStatus> destinations,
        CancellationToken ct = default)
    {
        DetectOverload(m, now);

        var common = session.Project.Stream;
        var live = destinations
            .Where(d => d.Stats is { Active: true })
            .Select(d => (Status: d, Encoder: session.Project.FindDestination(d.Id)?.EffectiveEncoder(common)))
            .Where(x => x.Encoder is not null)
            .GroupBy(x => EncoderKey(x.Encoder!))
            .ToList();

        foreach (var g in _groups.Values)
            g.Seen = false;
        foreach (var members in live)
        {
            var key = members.Key;
            var stats = members.Select(x => x.Status.Stats!).ToList();
            var names = string.Join(", ", members.Select(x => x.Status.Name));
            if (!_groups.TryGetValue(key, out var group))
                _groups[key] = group = StartGroup(members.First().Encoder!, names, stats.All(s => s.DynamicBitrate));
            group.Seen = true;
            group.Names = names;
            group.CurrentKbps = stats[0].VideoBitrateKbps;

            if (group.Controller is null || !AdaptiveEnabled)
                continue;
            var sample = new StreamSample(now, (uint)stats.Sum(s => (long)s.DroppedFrames), (uint)stats.Sum(s => (long)s.TotalFrames),
                stats.Max(s => s.Congestion), stats.Any(s => s.Reconnecting), group.CurrentKbps);
            if (group.Controller.Observe(sample) is not { } decision)
                continue;
            try
            {
                await session.Engine.SetDestinationBitrateAsync(members.First().Status.Id, decision.ToKbps, ct).ConfigureAwait(false);
                group.CurrentKbps = decision.ToKbps;
                Log(OptimizationEventKind.Adjusted, $"Bitrate {decision.FromKbps} → {decision.ToKbps} kb/s ({names})",
                    $"{decision.Reason} Pérdidas {decision.DropRate:P1}, congestión {decision.Congestion:P0}. Objetivo {group.Controller.TargetKbps} kb/s.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log(OptimizationEventKind.Warning, "No se pudo ajustar el bitrate", ex.Message);
            }
        }

        foreach (var (key, g) in _groups.Where(x => !x.Value.Seen).ToList())
        {
            if (g.Controller is not null && g.CurrentKbps != g.Controller.TargetKbps)
                Log(OptimizationEventKind.Info, $"Emisión detenida ({g.Names})", $"Terminó a {g.CurrentKbps} kb/s (objetivo {g.Controller.TargetKbps}).");
            _groups.Remove(key);
        }
    }

    private Group StartGroup(EncoderSettings encoder, string names, bool dynamic)
    {
        if (!dynamic)
        {
            var note = $"El codificador de {names} no admite cambiar el bitrate en directo: el ajuste adaptativo queda en pausa para ese destino.";
            Log(OptimizationEventKind.Info, "Bitrate adaptativo no disponible", note);
            return new Group(names, null, note);
        }
        var controller = new AdaptiveBitrateController(encoder.VideoBitrateKbps, options);
        Log(OptimizationEventKind.Info, $"Emisión iniciada ({names})",
            $"Objetivo {encoder.VideoBitrateKbps} kb/s; el bitrate adaptativo puede bajar hasta {controller.FloorKbps} kb/s si la red se satura." +
            (AdaptiveEnabled ? "" : " (Desactivado por el usuario.)"));
        return new Group(names, controller, null);
    }

    /// <summary>
    /// Sobrecarga de render (fotogramas con retraso) o de codificación (fotogramas descartados): se avisa con una
    /// recomendación, nunca se corrige sola porque las soluciones (FPS, resolución, codificador) cortan la salida.
    /// </summary>
    private void DetectOverload(EngineMetrics m, DateTime now)
    {
        var previous = _previous;
        _previous = m;
        if (previous is null || m.TotalFrames < previous.TotalFrames)
            return;
        var rendered = m.TotalFrames - previous.TotalFrames;
        var lagged = m.LaggedFrames >= previous.LaggedFrames ? m.LaggedFrames - previous.LaggedFrames : 0;
        var encoded = m.EncodedFrames >= previous.EncodedFrames ? m.EncodedFrames - previous.EncodedFrames : 0;
        var skipped = m.SkippedEncoderFrames >= previous.SkippedEncoderFrames ? m.SkippedEncoderFrames - previous.SkippedEncoderFrames : 0;
        _renderLag = 0.3 * (rendered == 0 ? 0 : (double)lagged / rendered) + 0.7 * _renderLag;
        _encoderSkip = 0.3 * (encoded + skipped == 0 ? 0 : (double)skipped / (encoded + skipped)) + 0.7 * _encoderSkip;
        _renderBad = _renderLag > 0.02 ? _renderBad + 1 : 0;
        _encoderBad = _encoderSkip > 0.02 ? _encoderBad + 1 : 0;

        if (_renderBad >= 10 && now - _lastRenderWarning > WarningInterval)
        {
            _lastRenderWarning = now;
            Log(OptimizationEventKind.Warning, "El render va con retraso",
                $"{_renderLag:P1} de los fotogramas no se renderizan a tiempo. Cierra aplicaciones que usen la GPU, reduce fuentes " +
                "pesadas o baja los FPS del lienzo (Perfil → Ajustes). Ejecuta el benchmark para ver opciones.");
        }
        if (_encoderBad >= 10 && now - _lastEncoderWarning > WarningInterval)
        {
            _lastEncoderWarning = now;
            Log(OptimizationEventKind.Warning, "El codificador no da abasto",
                $"Se descartan {_encoderSkip:P1} de los fotogramas al codificar. Usa un codificador por hardware o un preset más rápido; " +
                "el benchmark indica cuál aguanta en este equipo.");
        }
    }

    /// <summary>Vuelve al bitrate objetivo en todos los destinos y desactiva el ajuste adaptativo.</summary>
    public async Task RestoreTargetAsync(IReadOnlyList<Streaming.DestinationStatus> destinations, CancellationToken ct = default)
    {
        AdaptiveEnabled = false;
        var common = session.Project.Stream;
        foreach (var d in destinations.Where(d => d.Stats is { Active: true }))
        {
            var target = session.Project.FindDestination(d.Id)?.EffectiveEncoder(common).VideoBitrateKbps;
            if (target is { } kbps && d.Stats!.VideoBitrateKbps != kbps)
                await session.Engine.SetDestinationBitrateAsync(d.Id, kbps, ct).ConfigureAwait(false);
        }
        foreach (var g in _groups.Values.Where(g => g.Controller is not null))
            g.CurrentKbps = g.Controller!.TargetKbps;
        Log(OptimizationEventKind.Reverted, "Bitrate restaurado", "Ajuste adaptativo desactivado; cada destino vuelve al bitrate configurado.");
    }

    /// <summary>Aplica los ajustes de salida de un perfil (autorizado por el usuario) guardando los anteriores para revertir.</summary>
    public async Task<bool> ApplyOutputsAsync(OptimizationPlan plan, CancellationToken ct = default)
    {
        if (plan.Stream is null && plan.Recording is null)
            return false;
        var project = session.Project;
        var before = (project.Stream, project.Recording);
        var stream = plan.Stream is null ? project.Stream : project.Stream with { Encoder = plan.Stream };
        var recording = plan.Recording is null ? project.Recording : project.Recording with { Encoder = plan.Recording };
        var result = await session.Executor.ExecuteAsync(new SetOutputSettingsCommand(stream, recording), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            Log(OptimizationEventKind.Warning, "No se pudo aplicar el perfil", result.Error ?? "");
            return false;
        }
        var id = Guid.NewGuid();
        lock (_lock)
            _reverts[id] = before;
        Log(OptimizationEventKind.Applied, "Perfil aplicado",
            $"Emisión: {Describe(stream.EffectiveEncoder)}. Grabación: {Describe(recording.EffectiveEncoder)}. Antes: {Describe(before.Stream.EffectiveEncoder)} / {Describe(before.Recording.EffectiveEncoder)}.",
            id);
        return true;
    }

    /// <summary>Deshace un perfil aplicado (vuelve a los ajustes de salida anteriores).</summary>
    public async Task<bool> RevertAsync(Guid revertId, CancellationToken ct = default)
    {
        (StreamSettings Stream, RecordingPreferences Recording) before;
        lock (_lock)
        {
            if (!_reverts.Remove(revertId, out before))
                return false;
        }
        var project = session.Project;
        // Se conserva el destino actual (el usuario pudo cambiarlo después); solo se restauran los codificadores.
        var result = await session.Executor.ExecuteAsync(new SetOutputSettingsCommand(
            project.Stream with { Encoder = before.Stream.Encoder }, project.Recording with { Encoder = before.Recording.Encoder }), ct).ConfigureAwait(false);
        Log(result.Success ? OptimizationEventKind.Reverted : OptimizationEventKind.Warning,
            result.Success ? "Perfil revertido" : "No se pudo revertir",
            result.Success ? $"Emisión: {Describe(before.Stream.EffectiveEncoder)}. Grabación: {Describe(before.Recording.EffectiveEncoder)}." : result.Error ?? "");
        return result.Success;
    }

    public bool CanRevert(Guid revertId)
    {
        lock (_lock)
            return _reverts.ContainsKey(revertId);
    }

    public void Log(OptimizationEventKind kind, string title, string detail, Guid? revertId = null)
    {
        lock (_lock)
        {
            _events.Add(new OptimizationEvent(DateTime.Now, kind, title, detail, revertId));
            if (_events.Count > MaxEvents)
                _events.RemoveAt(0);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Informe en texto para el asistente (explica; no decide).</summary>
    public string Report()
    {
        var s = Status;
        var sb = new StringBuilder();
        List<Group> groups;
        lock (_lock)
            groups = _groups.Values.ToList();
        sb.AppendLine(groups.Count == 0
            ? $"Sin emisión activa; bitrate adaptativo {(AdaptiveEnabled ? "activado" : "desactivado")}."
            : $"Bitrate adaptativo {(AdaptiveEnabled ? "activado" : "desactivado")}.");
        foreach (var g in groups)
            sb.AppendLine(g.Controller is null
                ? $"{g.Names}: {g.CurrentKbps} kb/s. {g.Note}"
                : $"{g.Names}: {g.CurrentKbps} kb/s (objetivo {g.Controller.TargetKbps}); pérdidas {g.Controller.DropRate:P1}, congestión {g.Controller.Congestion:P0}.");
        sb.AppendLine($"Render con retraso {s.RenderLagRate:P1}; fotogramas descartados al codificar {s.EncoderSkipRate:P1}.");
        if (s.Note is not null)
            sb.AppendLine(s.Note);
        var recent = Events.TakeLast(15).ToList();
        sb.AppendLine(recent.Count == 0 ? "Sin decisiones registradas." : "Últimas decisiones:");
        foreach (var e in recent)
            sb.AppendLine($"- {e.Time:HH:mm:ss} [{e.Kind}] {e.Title}: {e.Detail}");
        return sb.ToString();
    }

    private static string Describe(EncoderSettings e) => $"{e.VideoEncoderId} {e.VideoBitrateKbps} kb/s";
}
