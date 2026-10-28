using LiveStream.Domain.Animation;

namespace LiveStream.Desktop.Components;

/// <summary>
/// Estado visual de una capa al reproducir un clip de animación, en coordenadas del lienzo del proyecto:
/// desplazamiento (px), escala alrededor del ancla, opacidad 0..1 y recorte derecho (px).
/// <c>null</c> (o los valores base) significa «estado de reposo», el mismo criterio del motor: las pistas
/// que el clip no anima vuelven a sus valores base.
/// </summary>
public sealed record AnimationPreviewState(double Dx, double Dy, double ScaleX, double ScaleY, double Opacity, double CropRight);

/// <summary>
/// Evaluación en la interfaz de los clips de <see cref="GraphicAnimations"/>: reproduce la MISMA reglas que el
/// motor en emisión (mismas curvas de easing y pistas), solo que sincronizada con el reloj de la interfaz para
/// la vista previa del diseñador.
/// </summary>
public static class ClipEvaluator
{
    public static AnimationPreviewState EvaluateClip(IReadOnlyList<AnimationTrack> tracks, double t) =>
        new(
            Dx: ValueOf(tracks, AnimatedProperty.X, t, 0),
            Dy: ValueOf(tracks, AnimatedProperty.Y, t, 0),
            ScaleX: ValueOf(tracks, AnimatedProperty.ScaleX, t, 1),
            ScaleY: ValueOf(tracks, AnimatedProperty.ScaleY, t, 1),
            Opacity: Math.Clamp(ValueOf(tracks, AnimatedProperty.Opacity, t, 1), 0, 1),
            CropRight: Math.Max(0, ValueOf(tracks, AnimatedProperty.CropRight, t, 0)));

    private static double ValueOf(IReadOnlyList<AnimationTrack> tracks, AnimatedProperty property, double t, double baseValue)
    {
        var track = tracks.FirstOrDefault(p => p.Property == property);
        return track is null ? baseValue : Evaluate(track, t);
    }
    private static double Evaluate(AnimationTrack track, double t)
    {
        var keys = track.Keys;
        if (keys.Count == 0)
            return double.NaN;
        if (t <= keys[0].Time)
            return keys[0].Value;
        var last = keys[^1];
        if (t >= last.Time)
            return last.Value;
        for (var i = 1; i < keys.Count; i++)
        {
            if (t <= keys[i].Time)
            {
                var a = keys[i - 1];
                var b = keys[i];
                var span = b.Time - a.Time;
                if (span <= 0)
                    return b.Value;
                var u = Eased((t - a.Time) / span, b.Easing);
                return a.Value + (b.Value - a.Value) * u;
            }
        }
        return last.Value;
    }

    private static double Eased(double u, Easing e) => e switch
    {
        Easing.InCubic => u * u * u,
        Easing.OutCubic => 1 - Math.Pow(1 - u, 3),
        Easing.InOutCubic => u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2,
        Easing.OutBack => 1 + 2.70158 * Math.Pow(u - 1, 3) + 1.70158 * Math.Pow(u - 1, 2),
        Easing.OutExpo => Math.Abs(u - 1) < 1e-6 ? 1 : 1 - Math.Pow(2, -10 * u),
        Easing.Hold => u < 1 ? 0 : 1,
        _ => u,
    };
}
