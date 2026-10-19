using LiveStream.Domain.Animation;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;

namespace LiveStream.Application.Graphics;

/// <summary>
/// Clips de entrada y salida de un gráfico. Animan el elemento del gráfico en la capa, cuya posición base es
/// (0,0) con escala 1: la geometría de las piezas no se toca.
/// </summary>
public static class GraphicAnimations
{
    /// <summary>Duración mínima de un corte (el motor necesita al menos un fotograma para aplicar el estado).</summary>
    private const double CutSeconds = 0.001;

    public static AnimationClip In(Graphic graphic, CanvasSettings canvas)
    {
        var layout = GraphicTemplates.Layout(graphic, canvas);
        var (left, top, right, bottom) = layout.Bounds;
        var d = graphic.AnimationMs / 1000.0;
        var tracks = new Dictionary<AnimatedProperty, AnimationTrack>();

        void Track(AnimatedProperty p, double from, double to, Easing e) =>
            tracks[p] = new AnimationTrack(p, [new Keyframe(0, from), new Keyframe(d, to, e)]);

        switch (d <= 0 ? GraphicAnimation.Cut : graphic.AnimationIn)
        {
            case GraphicAnimation.Fade:
                Track(AnimatedProperty.Opacity, 0, 1, Easing.OutCubic);
                break;
            case GraphicAnimation.SlideFromLeft:
                Track(AnimatedProperty.X, -right, 0, Easing.OutCubic);
                break;
            case GraphicAnimation.SlideFromRight:
                Track(AnimatedProperty.X, canvas.Width - left, 0, Easing.OutCubic);
                break;
            case GraphicAnimation.SlideFromBottom:
                Track(AnimatedProperty.Y, canvas.Height - top, 0, Easing.OutCubic);
                break;
            case GraphicAnimation.SlideFromTop:
                Track(AnimatedProperty.Y, -bottom, 0, Easing.OutCubic);
                break;
            case GraphicAnimation.Wipe:
                Track(AnimatedProperty.CropRight, canvas.Width - left, 0, Easing.InOutCubic);
                break;
            case GraphicAnimation.Pop:
                PopTracks(tracks, left, top, right, bottom, d, from: 0.6, to: 1, Easing.OutBack);
                Track(AnimatedProperty.Opacity, 0, 1, Easing.OutCubic);
                break;
        }
        return new AnimationClip(WithBase(tracks), Duration: d <= 0 ? CutSeconds : 0);
    }

    public static AnimationClip Out(Graphic graphic, CanvasSettings canvas)
    {
        var layout = GraphicTemplates.Layout(graphic, canvas);
        var (left, top, right, bottom) = layout.Bounds;
        var d = graphic.AnimationMs / 1000.0;
        var tracks = new Dictionary<AnimatedProperty, AnimationTrack>();

        void Track(AnimatedProperty p, double from, double to, Easing e) =>
            tracks[p] = new AnimationTrack(p, [new Keyframe(0, from), new Keyframe(d, to, e)]);

        switch (d <= 0 ? GraphicAnimation.Cut : graphic.AnimationOut)
        {
            case GraphicAnimation.Fade:
                Track(AnimatedProperty.Opacity, 1, 0, Easing.InCubic);
                break;
            case GraphicAnimation.SlideFromLeft:
                Track(AnimatedProperty.X, 0, -right, Easing.InCubic);
                break;
            case GraphicAnimation.SlideFromRight:
                Track(AnimatedProperty.X, 0, canvas.Width - left, Easing.InCubic);
                break;
            case GraphicAnimation.SlideFromBottom:
                Track(AnimatedProperty.Y, 0, canvas.Height - top, Easing.InCubic);
                break;
            case GraphicAnimation.SlideFromTop:
                Track(AnimatedProperty.Y, 0, -bottom, Easing.InCubic);
                break;
            case GraphicAnimation.Wipe:
                Track(AnimatedProperty.CropRight, 0, canvas.Width - left, Easing.InOutCubic);
                break;
            case GraphicAnimation.Pop:
                PopTracks(tracks, left, top, right, bottom, d, from: 1, to: 0.6, Easing.InCubic);
                Track(AnimatedProperty.Opacity, 1, 0, Easing.InCubic);
                break;
        }
        return new AnimationClip(WithBase(tracks), Duration: d <= 0 ? CutSeconds : 0);
    }

    /// <summary>Escala alrededor del centro: con el ancla arriba-izquierda, pos = centro · (1 − escala).</summary>
    private static void PopTracks(Dictionary<AnimatedProperty, AnimationTrack> tracks, double left, double top, double right,
        double bottom, double d, double from, double to, Easing easing)
    {
        var cx = (left + right) / 2;
        var cy = (top + bottom) / 2;
        tracks[AnimatedProperty.ScaleX] = new(AnimatedProperty.ScaleX, [new Keyframe(0, from), new Keyframe(d, to, easing)]);
        tracks[AnimatedProperty.ScaleY] = new(AnimatedProperty.ScaleY, [new Keyframe(0, from), new Keyframe(d, to, easing)]);
        tracks[AnimatedProperty.X] = new(AnimatedProperty.X, [new Keyframe(0, cx * (1 - from)), new Keyframe(d, cx * (1 - to), easing)]);
        tracks[AnimatedProperty.Y] = new(AnimatedProperty.Y, [new Keyframe(0, cy * (1 - from)), new Keyframe(d, cy * (1 - to), easing)]);
    }

    /// <summary>
    /// Las propiedades que el clip no anima vuelven a su valor base: una salida deslizada deja el elemento
    /// desplazado y la siguiente entrada (p. ej. un fundido) debe empezar en su sitio.
    /// </summary>
    private static List<AnimationTrack> WithBase(Dictionary<AnimatedProperty, AnimationTrack> tracks)
    {
        var bases = new (AnimatedProperty Property, double Value)[]
        {
            (AnimatedProperty.X, 0), (AnimatedProperty.Y, 0), (AnimatedProperty.ScaleX, 1), (AnimatedProperty.ScaleY, 1),
            (AnimatedProperty.CropRight, 0),
        };
        foreach (var (property, value) in bases)
            tracks.TryAdd(property, new AnimationTrack(property, [new Keyframe(0, value)]));
        return tracks.Values.ToList();
    }
}
