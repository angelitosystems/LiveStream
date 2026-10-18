namespace StreamPilot.Domain.ValueObjects;

/// <summary>
/// Transformación de un elemento en el lienzo. (X, Y) es la esquina superior izquierda en píxeles del lienzo;
/// el recorte se aplica en píxeles de la fuente antes de escalar.
/// </summary>
public readonly record struct Transform(
    double X,
    double Y,
    double ScaleX,
    double ScaleY,
    double RotationDegrees,
    int CropLeft = 0,
    int CropTop = 0,
    int CropRight = 0,
    int CropBottom = 0)
{
    public static Transform Identity { get; } = new(0, 0, 1, 1, 0);

    public Transform WithPosition(double x, double y) => this with { X = x, Y = y };

    public Transform WithScale(double scaleX, double scaleY) => this with { ScaleX = scaleX, ScaleY = scaleY };

    public Transform WithCrop(int left, int top, int right, int bottom) =>
        this with { CropLeft = Math.Max(0, left), CropTop = Math.Max(0, top), CropRight = Math.Max(0, right), CropBottom = Math.Max(0, bottom) };
}
