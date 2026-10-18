namespace StreamPilot.Domain.Graphics;

/// <summary>Plantillas de grafismo televisivo que sabe componer el estudio.</summary>
public enum GraphicKind
{
    /// <summary>Rótulo inferior: nombre y cargo.</summary>
    LowerThird,
    /// <summary>Titular / última hora: etiqueta y titular.</summary>
    Headline,
    /// <summary>Cintillo con texto en desplazamiento continuo.</summary>
    Ticker,
    /// <summary>Cuenta atrás controlable (iniciar, pausar, reiniciar).</summary>
    Countdown,
    /// <summary>Reloj con la hora local.</summary>
    Clock,
    /// <summary>Marcador deportivo: equipos, tanteo, periodo y tiempo.</summary>
    Scoreboard,
    /// <summary>Mosca "EN VIVO".</summary>
    LiveBadge,
    /// <summary>Alerta de directo (título y mensaje) que se oculta sola.</summary>
    Alert,
}

/// <summary>Animación de entrada o salida de un gráfico.</summary>
public enum GraphicAnimation
{
    Cut,
    Fade,
    SlideFromLeft,
    SlideFromRight,
    SlideFromBottom,
    SlideFromTop,
    /// <summary>Revelado horizontal por recorte.</summary>
    Wipe,
    /// <summary>Escala desde el centro con rebote.</summary>
    Pop,
}

/// <summary>Colores (#RRGGBB o #AARRGGBB) y tipografía de un gráfico.</summary>
public sealed record GraphicStyle(
    string PrimaryColor = "#FFC62828",
    string SecondaryColor = "#F0141A26",
    string TextColor = "#FFFFFFFF",
    string AccentTextColor = "#FFFFFFFF",
    string FontFace = "Segoe UI")
{
    public string? Validate()
    {
        foreach (var color in new[] { PrimaryColor, SecondaryColor, TextColor, AccentTextColor })
            if (!GraphicColor.TryParse(color, out _))
                return $"Color no válido: {color}. Usa #RRGGBB o #AARRGGBB.";
        return string.IsNullOrWhiteSpace(FontFace) ? "La tipografía no puede estar vacía." : null;
    }
}

/// <summary>
/// Gráfico de la capa superpuesta del programa (rótulos, titulares, marcadores...). Es inmutable: editar
/// produce una copia, lo que hace triviales el deshacer y las actualizaciones en directo.
/// </summary>
public sealed record Graphic(
    Guid Id,
    string Name,
    GraphicKind Kind,
    IReadOnlyDictionary<string, string> Fields,
    GraphicStyle Style,
    double X,
    double Y,
    double Scale = 1.0,
    GraphicAnimation AnimationIn = GraphicAnimation.SlideFromLeft,
    GraphicAnimation AnimationOut = GraphicAnimation.Fade,
    int AnimationMs = 600,
    /// <summary>Segundos en pantalla antes de salir solo; 0 = hasta que se retire a mano.</summary>
    double AutoHideSeconds = 0,
    /// <summary>Archivo JSON vigilado cuyos valores sustituyen a los campos (datos en vivo); null = sin enlace.</summary>
    string? DataFile = null)
{
    public string Field(string key) => Fields.TryGetValue(key, out var value) ? value : "";

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "El gráfico necesita un nombre.";
        if (Scale is < 0.1 or > 5)
            return "La escala debe estar entre 0,1 y 5.";
        if (!double.IsFinite(X) || !double.IsFinite(Y) || Math.Abs(X) > 20_000 || Math.Abs(Y) > 20_000)
            return "Posición fuera de rango.";
        if (AnimationMs is < 0 or > 10_000)
            return "La duración de la animación debe estar entre 0 y 10000 ms.";
        if (AutoHideSeconds is < 0 or > 3600)
            return "La salida automática debe estar entre 0 y 3600 s.";
        return Style.Validate();
    }
}

/// <summary>Colores en el formato de texto del proyecto y en el entero ABGR que usa el motor.</summary>
public static class GraphicColor
{
    public static bool TryParse(string? text, out uint argb)
    {
        argb = 0;
        if (text is null || !text.StartsWith('#'))
            return false;
        var hex = text[1..];
        if (hex.Length == 6)
            hex = "FF" + hex;
        return hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out argb);
    }

    /// <summary>Entero 0xAABBGGRR de libobs.</summary>
    public static uint ToAbgr(string text)
    {
        if (!TryParse(text, out var argb))
            throw new FormatException($"Color no válido: {text}");
        var a = argb >> 24 & 0xFF;
        var r = argb >> 16 & 0xFF;
        var g = argb >> 8 & 0xFF;
        var b = argb & 0xFF;
        return a << 24 | b << 16 | g << 8 | r;
    }
}
