namespace LiveStream.Application.Optimization;

/// <summary>Muestra de la emisión tomada cada segundo (contadores acumulados tal como los da el motor).</summary>
public sealed record StreamSample(
    DateTime Time,
    uint DroppedFrames,
    uint TotalFrames,
    double Congestion,
    bool Reconnecting,
    int CurrentBitrateKbps);

/// <summary>Cambio de bitrate decidido por el controlador, con su motivo y los valores que lo justifican.</summary>
public sealed record BitrateDecision(int FromKbps, int ToKbps, string Reason, double DropRate, double Congestion);

/// <summary>Parámetros del controlador. Umbrales distintos para bajar y subir y tiempos mínimos = histéresis.</summary>
public sealed record AdaptiveBitrateOptions
{
    /// <summary>Peso de cada muestra nueva en las medias móviles exponenciales.</summary>
    public double Smoothing { get; init; } = 0.3;
    /// <summary>Por encima: conexión saturada (fracción de fotogramas perdidos / congestión).</summary>
    public double BadDropRate { get; init; } = 0.02;
    public double BadCongestion { get; init; } = 0.5;
    /// <summary>Por debajo de ambos: conexión sana.</summary>
    public double GoodDropRate { get; init; } = 0.002;
    public double GoodCongestion { get; init; } = 0.15;
    /// <summary>Muestras seguidas necesarias para bajar / subir.</summary>
    public int BadSamplesToDecrease { get; init; } = 3;
    public int GoodSamplesToIncrease { get; init; } = 30;
    /// <summary>Tiempo mínimo entre cambios.</summary>
    public TimeSpan DecreaseCooldown { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan IncreaseCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public double DecreaseFactor { get; init; } = 0.8;
    public double IncreaseFactor { get; init; } = 1.1;
    /// <summary>Nunca por debajo de esta fracción del objetivo (ni de 500 kb/s).</summary>
    public double FloorFraction { get; init; } = 0.3;
}

/// <summary>
/// Controlador determinista del bitrate de emisión. Baja rápido cuando la conexión se satura y sube despacio
/// cuando lleva un rato sana, sin pasar nunca del bitrate objetivo que configuró el usuario. Solo decide: quien
/// aplica el cambio (y lo registra) es <see cref="PerformanceOptimizer"/>.
/// </summary>
public sealed class AdaptiveBitrateController(int targetKbps, AdaptiveBitrateOptions? options = null)
{
    private readonly AdaptiveBitrateOptions _o = options ?? new AdaptiveBitrateOptions();
    private StreamSample? _previous;
    private int _bad;
    private int _good;
    private DateTime _lastChange = DateTime.MinValue;

    public int TargetKbps { get; } = targetKbps;
    public int FloorKbps => Math.Max(500, Round(TargetKbps * _o.FloorFraction));
    public double DropRate { get; private set; }
    public double Congestion { get; private set; }

    /// <summary>Procesa una muestra; devuelve un cambio de bitrate o null si no hay que tocar nada.</summary>
    public BitrateDecision? Observe(StreamSample sample)
    {
        var previous = _previous;
        _previous = sample;
        if (previous is null || sample.TotalFrames < previous.TotalFrames)
            return null; // primera muestra o contadores reiniciados (emisión nueva)

        var frames = sample.TotalFrames - previous.TotalFrames;
        var dropped = sample.DroppedFrames >= previous.DroppedFrames ? sample.DroppedFrames - previous.DroppedFrames : 0;
        var instantDrop = frames + dropped == 0 ? 0 : (double)dropped / (frames + dropped);
        DropRate = _o.Smoothing * instantDrop + (1 - _o.Smoothing) * DropRate;
        Congestion = _o.Smoothing * Math.Clamp(sample.Congestion, 0, 1) + (1 - _o.Smoothing) * Congestion;

        // Durante una reconexión las métricas no describen la conexión: no se decide nada.
        if (sample.Reconnecting)
        {
            _bad = _good = 0;
            return null;
        }

        var bad = DropRate > _o.BadDropRate || Congestion > _o.BadCongestion;
        var good = DropRate < _o.GoodDropRate && Congestion < _o.GoodCongestion;
        _bad = bad ? _bad + 1 : 0;
        _good = good ? _good + 1 : 0;

        var current = sample.CurrentBitrateKbps;
        if (_bad >= _o.BadSamplesToDecrease && sample.Time - _lastChange >= _o.DecreaseCooldown && current > FloorKbps)
        {
            var to = Math.Max(FloorKbps, Round(current * _o.DecreaseFactor));
            return Change(sample, current, to, DropRate > _o.BadDropRate
                ? $"Se pierden fotogramas por la red ({DropRate:P1})."
                : $"La conexión está saturada (congestión {Congestion:P0}).");
        }
        if (_good >= _o.GoodSamplesToIncrease && sample.Time - _lastChange >= _o.IncreaseCooldown && current < TargetKbps)
        {
            var to = Math.Min(TargetKbps, Math.Max(current + 50, Round(current * _o.IncreaseFactor)));
            return Change(sample, current, to, $"La conexión lleva {_good} s estable: se recupera calidad.");
        }
        return null;
    }

    private BitrateDecision Change(StreamSample sample, int from, int to, string reason)
    {
        _lastChange = sample.Time;
        _bad = _good = 0;
        return new BitrateDecision(from, to, reason, DropRate, Congestion);
    }

    private static int Round(double kbps) => (int)(Math.Round(kbps / 50) * 50);
}
