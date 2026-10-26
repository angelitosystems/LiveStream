using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.Application.Editing;

/// <summary>Cómo se ajusta un archivo recién soltado en el lienzo.</summary>
public enum DropFit
{
    /// <summary>Tamaño original si cabe; si desborda, se reduce hasta caber (proporción intacta).</summary>
    Natural,

    /// <summary>Encaja dentro del lienzo, aunque sea más pequeño en origen, sin deformarse.</summary>
    Screen,

    /// <summary>Cubre todo el lienzo (puede desbordar por los bordes) sin deformarse.</summary>
    Fill,
}

/// <summary>
/// Posición de un archivo soltado en el lienzo: el punto de soltar queda en el centro de la imagen (clamped a los
/// bordes del lienzo) y la escala respeta la relación de aspecto. Puro y determinista para probar sin motor.
/// </summary>
public static class DropPlacement
{
    /// <summary>
    /// Transformación para una fuente con tamaño natural (<paramref name="sourceWidth"/>/H; 0 = desconocido,
    /// entonces queda a 1:1 en la esquina superior izquierda del punto).
    /// </summary>
    public static Transform Place(double canvasX, double canvasY, int sourceWidth, int sourceHeight,
        CanvasSettings canvas, DropFit fit = DropFit.Natural)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return Transform.Identity;
        var scale = fit switch
        {
            DropFit.Screen => Math.Min((double)canvas.Width / sourceWidth, (double)canvas.Height / sourceHeight),
            DropFit.Fill => Math.Max((double)canvas.Width / sourceWidth, (double)canvas.Height / sourceHeight),
            _ => Math.Min(1, Math.Min((double)canvas.Width / sourceWidth, (double)canvas.Height / sourceHeight)),
        };
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;
        var w = sourceWidth * scale;
        var h = sourceHeight * scale;
        var x = Math.Clamp(canvasX - w / 2, 0, Math.Max(0, canvas.Width - w));
        var y = Math.Clamp(canvasY - h / 2, 0, Math.Max(0, canvas.Height - h));
        return new Transform(Math.Round(x, 1), Math.Round(y, 1), scale, scale, 0);
    }
}
