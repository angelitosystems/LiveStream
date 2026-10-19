using StreamPilot.Application.Abstractions;
using StreamPilot.Application.Studio;
using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Streaming;

public enum DestinationState { Idle, Connecting, Live, Reconnecting, Failed }

/// <summary>Estado de un destino para la interfaz, el asistente y el optimizador.</summary>
public sealed record DestinationStatus(
    Guid Id,
    string Name,
    StreamPlatform Platform,
    bool Enabled,
    DestinationState State,
    int MeasuredKbps,
    int EncoderKbps,
    double DropPercent,
    double Congestion,
    TimeSpan Uptime,
    int Reconnections,
    bool SharedEncoder,
    string? Error,
    DestinationStats? Stats);

public sealed record SupervisorEvent(DateTime Time, string Destination, string Message, bool IsError);

/// <summary>
/// Supervisa cada destino de emisión: deduce su estado a partir de las estadísticas del motor, mide el bitrate real
/// y registra las transiciones (conectado, conexión perdida, reconectado, caído con motivo). La reconexión la hace
/// libobs (20 reintentos cada 5 s); aquí se observa y se informa.
/// </summary>
public sealed class StreamSupervisor(StudioSession session)
{
    public const int MaxEvents = 200;

    private sealed class Track
    {
        public DestinationState State = DestinationState.Idle;
        public bool Starting;
        public DateTime? LiveSince;
        public DateTime? ReconnectingSince;
        public int Reconnections;
        public ulong LastBytes;
        public DateTime LastSample;
        public int MeasuredKbps;
        public string? Error;
    }

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Track> _tracks = [];
    private readonly List<SupervisorEvent> _events = [];
    private IReadOnlyList<DestinationStatus> _statuses = [];

    public event EventHandler? Changed;

    public IReadOnlyList<DestinationStatus> Statuses
    {
        get
        {
            lock (_lock)
                return _statuses;
        }
    }

    public IReadOnlyList<SupervisorEvent> Events
    {
        get
        {
            lock (_lock)
                return _events.ToList();
        }
    }

    public bool AnyLive => Statuses.Any(s => s.State is DestinationState.Live or DestinationState.Reconnecting or DestinationState.Connecting);

    public bool IsLive(Guid id) => Statuses.FirstOrDefault(s => s.Id == id)?.State is DestinationState.Live or DestinationState.Reconnecting or DestinationState.Connecting;

    /// <summary>La sesión avisa de que inició un destino (hasta que conecte se muestra «Conectando»).</summary>
    public void MarkStarting(Guid id)
    {
        lock (_lock)
        {
            var t = TrackFor(id);
            t.Starting = true;
            t.Error = null;
            t.State = DestinationState.Connecting;
            t.LastBytes = 0;
            t.MeasuredKbps = 0;
        }
    }

    public void MarkFailedToStart(StreamDestination d, string error)
    {
        lock (_lock)
        {
            var t = TrackFor(d.Id);
            t.Starting = false;
            t.State = DestinationState.Failed;
            t.Error = error;
        }
        Log(d.Name, $"No se pudo iniciar: {error}", true);
    }

    public void MarkStopped(StreamDestination d)
    {
        lock (_lock)
        {
            var t = TrackFor(d.Id);
            t.Starting = false;
            t.State = DestinationState.Idle;
            t.LiveSince = t.ReconnectingSince = null;
        }
        Log(d.Name, "Emisión detenida.", false);
    }

    /// <summary>Una muestra por segundo.</summary>
    public async Task TickAsync(DateTime now, CancellationToken ct = default)
    {
        var statuses = new List<DestinationStatus>();
        var events = new List<(string, string, bool)>();
        var common = session.Project.Stream;
        foreach (var d in session.Project.Destinations.ToList())
        {
            var stats = session.Engine.IsInitialized ? await session.Engine.GetDestinationStatsAsync(d.Id, ct).ConfigureAwait(false) : null;
            lock (_lock)
            {
                var t = TrackFor(d.Id);
                var previous = t.State;
                var state = stats switch
                {
                    { Active: true, Reconnecting: true } => DestinationState.Reconnecting,
                    { Active: true } => DestinationState.Live,
                    { LastStop.IsError: true } => DestinationState.Failed,
                    _ when t.Starting && stats is not null && !stats.LastStop.IsError => DestinationState.Connecting,
                    _ when previous == DestinationState.Failed => DestinationState.Failed,
                    _ => DestinationState.Idle,
                };
                if (state is DestinationState.Live or DestinationState.Reconnecting or DestinationState.Failed)
                    t.Starting = false;

                if (state != previous)
                {
                    switch (state)
                    {
                        case DestinationState.Live when previous == DestinationState.Reconnecting:
                            var lost = t.ReconnectingSince is { } since ? now - since : TimeSpan.Zero;
                            events.Add((d.Name, $"Reconectado tras {lost.TotalSeconds:0} s.", false));
                            t.ReconnectingSince = null;
                            break;
                        case DestinationState.Live:
                            t.LiveSince = now;
                            t.Reconnections = 0;
                            events.Add((d.Name, "En directo.", false));
                            break;
                        case DestinationState.Reconnecting:
                            t.ReconnectingSince = now;
                            t.Reconnections++;
                            events.Add((d.Name, "Conexión perdida: reintentando cada 5 s (la emisión al resto de destinos sigue).", true));
                            break;
                        case DestinationState.Failed when stats is not null:
                            t.Error = OutputErrors.Describe(stats.LastStop);
                            t.LiveSince = t.ReconnectingSince = null;
                            events.Add((d.Name, $"Se detuvo: {t.Error}", true));
                            break;
                    }
                    t.State = state;
                }

                if (stats is { Active: true })
                {
                    var seconds = (now - t.LastSample).TotalSeconds;
                    if (t.LastBytes > 0 && seconds > 0 && stats.TotalBytes >= t.LastBytes)
                        t.MeasuredKbps = (int)((stats.TotalBytes - t.LastBytes) * 8 / 1000 / seconds);
                    t.LastBytes = stats.TotalBytes;
                    t.LastSample = now;
                }
                else
                {
                    t.MeasuredKbps = 0;
                    t.LastBytes = 0;
                }

                var frames = stats is null ? 0 : stats.TotalFrames + stats.DroppedFrames;
                statuses.Add(new DestinationStatus(d.Id, d.Name, d.Platform, d.Enabled, t.State, t.MeasuredKbps,
                    stats?.VideoBitrateKbps ?? d.EffectiveEncoder(common).VideoBitrateKbps,
                    frames == 0 ? 0 : 100.0 * stats!.DroppedFrames / frames, stats?.Congestion ?? 0,
                    t.LiveSince is { } live && t.State is DestinationState.Live or DestinationState.Reconnecting ? now - live : TimeSpan.Zero,
                    t.Reconnections, stats?.SharedEncoder ?? false, t.State == DestinationState.Failed ? t.Error : null, stats));
            }
        }
        lock (_lock)
            _statuses = statuses;
        foreach (var (name, message, error) in events)
            Log(name, message, error);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Track TrackFor(Guid id)
    {
        if (!_tracks.TryGetValue(id, out var t))
            _tracks[id] = t = new Track();
        return t;
    }

    private void Log(string destination, string message, bool error)
    {
        lock (_lock)
        {
            _events.Add(new SupervisorEvent(DateTime.Now, destination, message, error));
            if (_events.Count > MaxEvents)
                _events.RemoveAt(0);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
