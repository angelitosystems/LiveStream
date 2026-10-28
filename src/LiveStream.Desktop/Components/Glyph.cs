using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace LiveStream.Desktop.Components;

/// <summary>
/// Componente de icono único de la aplicación: dibuja una geometría con trazo (los iconos de la
/// biblioteca Lucide son de trazo sobre una rejilla 24x24) con un grosor proporcional al tamaño,
/// extremos y esquinas redondeados y centrado exacto dentro del espacio dado. Sustituye a PathIcon,
/// que solo rellena la geometría: con trazos los tamaños quedaban inconsistentes entre ventanas.
/// Tamaños estándar: 16 (botones de acción), 18 (barra lateral), 14 (cabeceras de panel) y 24
/// (diálogos/branding). El color lo hereda del texto del contenedor (Foreground).
/// </summary>
public sealed class Glyph : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Glyph, Geometry?>(nameof(Data));

    /// <summary>Grosor del trazo por unidad (2 = estilo Lucide en 24 px; 1.75 más fino en listas densas).</summary>
    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<Glyph, double>(nameof(StrokeWidth), 2.0);

    static Glyph()
    {
        AffectsRender<Glyph>(DataProperty, StrokeWidthProperty, ForegroundProperty, WidthProperty, HeightProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsFinite(Width) && Width > 0 ? Width : double.IsFinite(Height) && Height > 0 ? Height : 16;
        var h = double.IsFinite(Height) && Height > 0 ? Height : w;
        return new Size(w, h);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var data = Data;
        if (data is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var brush = Foreground ?? new SolidColorBrush(Color.Parse("#FFE6E8EB"));
        // Uniforme en la rejilla 24x24: el icono siempre llena el mismo espacio y se centra.
        var scale = Math.Min(Bounds.Width / 24, Bounds.Height / 24);
        if (!double.IsFinite(scale) || scale <= 0)
            return;
        var halfW = (Bounds.Width - 24 * scale) / 2;
        var halfH = (Bounds.Height - 24 * scale) / 2;
        using var state = context.PushTransform(Matrix.CreateTranslation(halfW, halfH) * Matrix.CreateScale(scale, scale));
        context.DrawGeometry(null, new Pen(brush, StrokeWidth)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }, data);
    }
}
