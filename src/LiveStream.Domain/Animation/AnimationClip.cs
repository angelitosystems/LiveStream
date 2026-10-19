namespace LiveStream.Domain.Animation;

/// <summary>Propiedades de un elemento de escena que el motor sabe animar.</summary>
public enum AnimatedProperty
{
    X,
    Y,
    ScaleX,
    ScaleY,
    Rotation,
    /// <summary>0..1. Se aplica con un filtro sobre la fuente: afecta a todas sus apariciones.</summary>
    Opacity,
    CropLeft,
    CropTop,
    CropRight,
    CropBottom,
}

/// <summary>Curva del tramo que termina en el fotograma clave.</summary>
public enum Easing
{
    Linear,
    InCubic,
    OutCubic,
    InOutCubic,
    OutBack,
    OutExpo,
    /// <summary>Mantiene el valor anterior hasta el fotograma clave (salto).</summary>
    Hold,
}

/// <summary>Valor en un instante, en segundos desde el inicio del clip (después del retardo).</summary>
public sealed record Keyframe(double Time, double Value, Easing Easing = Easing.Linear);

public sealed record AnimationTrack(AnimatedProperty Property, IReadOnlyList<Keyframe> Keys);

[Flags]
public enum AnimationFlags
{
    None = 0,
    /// <summary>Hace visible el elemento al empezar (con los valores del primer fotograma ya aplicados).</summary>
    ShowAtStart = 1,
    /// <summary>Oculta el elemento al terminar.</summary>
    HideAtEnd = 2,
}

/// <summary>
/// Animación por fotogramas clave. La reproduce el motor en su hilo gráfico, sincronizada con su reloj de
/// fotogramas, no con la interfaz.
/// </summary>
public sealed record AnimationClip(IReadOnlyList<AnimationTrack> Tracks, double Delay = 0, double Duration = 0)
{
    public const double MaxSeconds = 600;

    /// <summary>Duración efectiva: la mayor entre <see cref="Duration"/> y el último fotograma clave.</summary>
    public double EffectiveDuration =>
        Math.Max(Duration, Tracks.Count == 0 ? 0 : Tracks.Max(t => t.Keys.Count == 0 ? 0 : t.Keys.Max(k => k.Time)));

    public string? Validate()
    {
        if (Delay is < 0 or > MaxSeconds || Duration is < 0 or > MaxSeconds)
            return "Retardo o duración fuera de rango.";
        if (Tracks.Count == 0 && Duration <= 0)
            return "La animación no tiene pistas ni duración.";
        foreach (var track in Tracks)
        {
            if (track.Keys.Count == 0)
                return $"La pista {track.Property} no tiene fotogramas clave.";
            if (track.Keys.Any(k => k.Time is < 0 or > MaxSeconds || !double.IsFinite(k.Value)))
                return $"La pista {track.Property} tiene fotogramas clave fuera de rango.";
        }
        return null;
    }
}
