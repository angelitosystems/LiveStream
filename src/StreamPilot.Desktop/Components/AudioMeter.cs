using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StreamPilot.Desktop.Components;

/// <summary>
/// Medidor estéreo horizontal (-60..0 dB). Dibuja directamente en el contexto de render: sin plantillas ni
/// elementos hijos, para poder actualizarse ~30 veces por segundo por canal sin coste de layout.
/// Verde hasta -20 dB, ámbar hasta -9 dB, rojo por encima (zonas de OBS).
/// </summary>
public sealed class AudioMeter : Control
{
    public const double MinDb = -60;

    public static readonly StyledProperty<double> PeakLeftProperty = AvaloniaProperty.Register<AudioMeter, double>(nameof(PeakLeft), -100);
    public static readonly StyledProperty<double> PeakRightProperty = AvaloniaProperty.Register<AudioMeter, double>(nameof(PeakRight), -100);
    public static readonly StyledProperty<double> MagnitudeLeftProperty = AvaloniaProperty.Register<AudioMeter, double>(nameof(MagnitudeLeft), -100);
    public static readonly StyledProperty<double> MagnitudeRightProperty = AvaloniaProperty.Register<AudioMeter, double>(nameof(MagnitudeRight), -100);
    public static readonly StyledProperty<bool> MutedProperty = AvaloniaProperty.Register<AudioMeter, bool>(nameof(Muted));

    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#FF24282E"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#FF2FB36B"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#FFE0A526"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FFE5484D"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#66FFFFFF"));
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#FF5B616B"));

    static AudioMeter()
    {
        AffectsRender<AudioMeter>(PeakLeftProperty, PeakRightProperty, MagnitudeLeftProperty, MagnitudeRightProperty, MutedProperty);
    }

    public double PeakLeft { get => GetValue(PeakLeftProperty); set => SetValue(PeakLeftProperty, value); }
    public double PeakRight { get => GetValue(PeakRightProperty); set => SetValue(PeakRightProperty, value); }
    public double MagnitudeLeft { get => GetValue(MagnitudeLeftProperty); set => SetValue(MagnitudeLeftProperty, value); }
    public double MagnitudeRight { get => GetValue(MagnitudeRightProperty); set => SetValue(MagnitudeRightProperty, value); }
    public bool Muted { get => GetValue(MutedProperty); set => SetValue(MutedProperty, value); }

    public static double Fraction(double db) => Math.Clamp((db - MinDb) / -MinDb, 0, 1);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var barH = Math.Max(2, (Bounds.Height - 2) / 2);
        DrawBar(context, 0, w, barH, PeakLeft, MagnitudeLeft);
        DrawBar(context, barH + 2, w, barH, PeakRight, MagnitudeRight);
    }

    private void DrawBar(DrawingContext context, double y, double width, double height, double peak, double magnitude)
    {
        context.FillRectangle(Track, new Rect(0, y, width, height));
        var level = Fraction(peak) * width;
        double greenEnd = Fraction(-20) * width, amberEnd = Fraction(-9) * width;
        if (Muted)
        {
            context.FillRectangle(Grey, new Rect(0, y, level, height));
            return;
        }
        context.FillRectangle(Green, new Rect(0, y, Math.Min(level, greenEnd), height));
        if (level > greenEnd)
            context.FillRectangle(Amber, new Rect(greenEnd, y, Math.Min(level, amberEnd) - greenEnd, height));
        if (level > amberEnd)
            context.FillRectangle(Red, new Rect(amberEnd, y, level - amberEnd, height));
        // Marca de la magnitud (nivel medio) sobre el pico.
        var mag = Fraction(magnitude) * width;
        if (mag > 1)
            context.FillRectangle(Dim, new Rect(mag - 1, y, 2, height));
    }
}
