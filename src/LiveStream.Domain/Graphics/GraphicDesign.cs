namespace LiveStream.Domain.Graphics;

public enum DesignElementKind
{
    /// <summary>Forma rectangular de color (color_source_v3).</summary>
    Rect,
    /// <summary>Texto (text_gdiplus_v3): degradado y contorno nativos.</summary>
    Text,
    /// <summary>Imagen o logotipo (image_source).</summary>
    Image,
    /// <summary>Video en bucle (ffmpeg_source), silenciado por defecto.</summary>
    Video,
}

/// <summary>Texto vivo: el contenido lo calcula el estudio en cada momento.</summary>
public enum DesignBinding
{
    None,
    /// <summary>Hora local; Text es el formato .NET (p. ej. «HH:mm:ss»).</summary>
    Clock,
    /// <summary>Tiempo restante de la cuenta atrás del gráfico (campos duration, endsAt y finished).</summary>
    Countdown,
}

/// <summary>
/// Elemento de un diseño libre. Coordenadas en píxeles del lienzo, relativas al origen del gráfico y a escala 1.
/// El giro es en grados alrededor de la esquina superior izquierda (como en libobs). Los colores son #AARRGGBB.
/// </summary>
public sealed record DesignElement(
    string Key,
    string Name,
    DesignElementKind Kind,
    double X,
    double Y,
    double Width,
    double Height,
    double Rotation = 0,
    string Color = "#FFFFFFFF",
    double Opacity = 1,
    string Text = "",
    string? FontFace = null,
    double FontSize = 48,
    bool Bold = false,
    bool Italic = false,
    string Align = "left",
    string? GradientColor = null,
    string? OutlineColor = null,
    int OutlineSize = 0,
    string? ShadowColor = null,
    double ShadowX = 4,
    double ShadowY = 4,
    string? Path = null,
    int CropLeft = 0,
    int CropTop = 0,
    int CropRight = 0,
    int CropBottom = 0,
    bool Muted = true,
    bool Visible = true,
    bool Locked = false,
    DesignBinding Binding = DesignBinding.None)
{
    public const int MaxElements = 200;

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length > 64 || !Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return $"Clave de elemento no válida: «{Key}».";
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            return "Cada elemento necesita un nombre (máximo 80 caracteres).";
        if (!double.IsFinite(X) || !double.IsFinite(Y) || Math.Abs(X) > 20_000 || Math.Abs(Y) > 20_000)
            return $"«{Name}»: posición fuera de rango.";
        if (!double.IsFinite(Width) || !double.IsFinite(Height) || Width is < 1 or > 20_000 || Height is < 1 or > 20_000)
            return $"«{Name}»: el tamaño debe estar entre 1 y 20000 px.";
        if (!double.IsFinite(Rotation) || Math.Abs(Rotation) > 3600)
            return $"«{Name}»: giro no válido.";
        if (Opacity is < 0 or > 1 || !double.IsFinite(Opacity))
            return $"«{Name}»: la opacidad debe estar entre 0 y 1.";
        foreach (var color in new[] { Color, GradientColor, OutlineColor, ShadowColor })
            if (color is not null && !GraphicColor.TryParse(color, out _))
                return $"«{Name}»: color no válido ({color}).";
        if (Kind == DesignElementKind.Text)
        {
            if (FontSize is < 4 or > 1000)
                return $"«{Name}»: el tamaño de letra debe estar entre 4 y 1000.";
            if (Align is not ("left" or "center" or "right"))
                return $"«{Name}»: alineación no válida.";
            if (OutlineSize is < 0 or > 50)
                return $"«{Name}»: el contorno debe estar entre 0 y 50.";
            if (Text.Length > 4000)
                return $"«{Name}»: el texto es demasiado largo.";
        }
        if (Kind is DesignElementKind.Image or DesignElementKind.Video)
        {
            if (string.IsNullOrWhiteSpace(Path) || !System.IO.Path.IsPathFullyQualified(Path))
                return $"«{Name}»: elige un archivo (ruta absoluta).";
            if (Math.Min(Math.Min(CropLeft, CropTop), Math.Min(CropRight, CropBottom)) < 0)
                return $"«{Name}»: el recorte no puede ser negativo.";
        }
        if (Binding != DesignBinding.None && Kind != DesignElementKind.Text)
            return $"«{Name}»: solo un texto puede mostrar el reloj o la cuenta atrás.";
        return null;
    }
}

public static class GraphicDesignValidation
{
    public static string? Validate(IReadOnlyList<DesignElement>? design)
    {
        if (design is null)
            return "El diseño libre no tiene elementos.";
        if (design.Count > DesignElement.MaxElements)
            return $"Un diseño admite como máximo {DesignElement.MaxElements} elementos.";
        if (design.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != design.Count)
            return "Hay elementos con la misma clave.";
        return design.Select(e => e.Validate()).FirstOrDefault(e => e is not null);
    }
}
