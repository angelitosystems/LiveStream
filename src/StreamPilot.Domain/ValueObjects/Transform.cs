namespace StreamPilot.Domain.ValueObjects;

public readonly record struct Transform(double X, double Y, double ScaleX, double ScaleY, double RotationDegrees)
{
    public static Transform Identity { get; } = new(0, 0, 1, 1, 0);
}
