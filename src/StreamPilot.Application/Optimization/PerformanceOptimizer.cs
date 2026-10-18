using System.Text;
using StreamPilot.Application.Abstractions;
using StreamPilot.Application.Studio;
using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Optimization;

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
    private AdaptiveBitrateController? _controller;
    private bool _wasStreaming;
    private string? _note;
    private EngineMetrics? _previous;
    private double _renderLag, _encoderSkip;
    private int _renderBad, _encoderBad;
    private DateTime _lastRenderWarning = DateTime.MinValue, _lastEncoderWarning = DateTime.MinValue;
    private int _currentKbps;

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

    public OptimizerStatus Status
    {
        get
        {
            lock (_lock)
                return new OptimizerStatus(AdaptiveEnabled, _controller is not null, _controller?.TargetKbps ?? 0, _currentKbps,
                    _controller?.DropRate ?? 0, _controller?.Congestion ?? 0, _renderLag, _encoderSkip, _note);
        }
    }

    /// <summary>Procesa una muestra de métricas (una por segundo).</summary>
    public async Task TickAsync(EngineMetrics m, DateTime now, CancellationToken ct = default)
    {
        DetectOverload(m, now);

        if (m.IsStreaming && !_wasStreaming)
            OnStreamStarted(m);
        else if (!m.IsStreaming && _wasStreaming)
            OnStreamStopped();
        _wasStreaming = m.IsStreaming;
        if (!m.IsStreaming)
            return;
        _currentKbps = m.StreamVideoBitrateKbps;

        if (_controller is null || !AdaptiveEnabled)
            return;
        var decision = _controller.Observe(new StreamSample(now, m.StreamDroppedFrames, m.StreamTotalFrames, m.StreamCongestion,
            m.StreamReconnecting, m.StreamVideoBitrateKbps));
        if (decision is null)
            return;
        try
        {
            await session.Engine.SetStreamVideoBitrateAsync(decision.ToKbps, ct).ConfigureAwait(false);
            _currentKbps = decision.ToKbps;
            Log(OptimizationEventKind.Adjusted, $"Bitrate {decision.FromKbps} → {decision.ToKbps} kb/s",
                $"{decision.Reason} Pérdidas {decision.DropRate:P1}, congestión {decision.Congestion:P0}. Objetivo {_controller.TargetKbps} kb/s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(OptimizationEventKind.Warning, "No se pudo ajustar el bitrate", ex.Message);
        }
    }

    private void OnStreamStarted(EngineMetrics m)
    {
        _currentKbps = m.StreamVideoBitrateKbps;
        if (m.StreamVideoBitrateKbps <= 0)
            return;
        if (!m.StreamDynamicBitrate)
        {
            _controller = null;
            _note = "El codificador de la emisión no admite cambiar el bitrate en directo: el ajuste adaptativo queda en pausa.";
            Log(OptimizationEventKind.Info, "Bitrate adaptativo no disponible", _note);
            return;
        }
        _note = null;
        _controller = new AdaptiveBitrateController(m.StreamVideoBitrateKbps, options);
        Log(OptimizationEventKind.Info, "Emisión iniciada",
            $"Objetivo {m.StreamVideoBitrateKbps} kb/s; el bitrate adaptativo puede bajar hasta {_controller.FloorKbps} kb/s si la red se satura." +
            (AdaptiveEnabled ? "" : " (Desactivado por el usuario.)"));
    }

    private void OnStreamStopped()
    {
        if (_controller is not null && _currentKbps != _controller.TargetKbps)
            Log(OptimizationEventKind.Info, "Emisión detenida", $"Terminó a {_currentKbps} kb/s (objetivo {_controller.TargetKbps}).");
        _controller = null;
        _currentKbps = 0;
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

    /// <summary>Vuelve al bitrate objetivo y desactiva el ajuste adaptativo hasta que el usuario lo reactive.</summary>
    public async Task RestoreTargetAsync(CancellationToken ct = default)
    {
        AdaptiveEnabled = false;
        if (_controller is { } controller && _currentKbps != controller.TargetKbps)
        {
            await session.Engine.SetStreamVideoBitrateAsync(controller.TargetKbps, ct).ConfigureAwait(false);
            _currentKbps = controller.TargetKbps;
        }
        Log(OptimizationEventKind.Reverted, "Bitrate restaurado", "Ajuste adaptativo desactivado; la emisión vuelve al bitrate configurado.");
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
        sb.AppendLine(s.Active
            ? $"Emitiendo a {s.CurrentKbps} kb/s (objetivo {s.TargetKbps}); pérdidas {s.DropRate:P1}, congestión {s.Congestion:P0}; bitrate adaptativo {(s.AdaptiveEnabled ? "activado" : "desactivado")}."
            : $"Sin emisión activa; bitrate adaptativo {(s.AdaptiveEnabled ? "activado" : "desactivado")}.");
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
