using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;

namespace LiveStream.Application.Graphics;

/// <summary>Campo editable de una plantilla.</summary>
/// <param name="FileFilter">Si no es null, el campo es la ruta de un archivo y el editor ofrece «Examinar…» con este filtro.</param>
public sealed record GraphicFieldDefinition(string Key, string Label, string Default, bool Multiline = false, string? FileFilter = null);

public enum GraphicElementKind
{
    /// <summary>Rectángulo de color sólido (color_source_v3).</summary>
    Rect,
    /// <summary>Texto en una caja fija (text_gdiplus_v3 con extents).</summary>
    Text,
    /// <summary>Texto de ancho natural que se desplaza dentro de su caja (scroll_filter).</summary>
    Ticker,
    /// <summary>Imagen (image_source) encajada en su caja sin deformarla; Text es la ruta del archivo.</summary>
    Image,
    /// <summary>Video en bucle (ffmpeg_source) estirado a su caja; Text es la ruta del archivo.</summary>
    Video,
}

/// <summary>Pieza de un gráfico en coordenadas absolutas del lienzo.</summary>
public sealed record GraphicElement(
    string Key,
    GraphicElementKind Kind,
    double X,
    double Y,
    double Width,
    double Height,
    string Color,
    string Text = "",
    double FontSize = 0,
    bool Bold = false,
    string Align = "left",
    double Rotation = 0,
    /// <summary>Elemento de diseño libre del que sale la pieza (degradado, contorno, opacidad, recorte...).</summary>
    DesignElement? Design = null);

public sealed record GraphicLayout(IReadOnlyList<GraphicElement> Elements)
{
    public (double Left, double Top, double Right, double Bottom) Bounds =>
        Elements.Count == 0
            ? (0, 0, 0, 0)
            : (Elements.Min(e => e.X), Elements.Min(e => e.Y), Elements.Max(e => e.X + e.Width), Elements.Max(e => e.Y + e.Height));
}

/// <summary>
/// Plantillas de grafismo. Cada tipo produce siempre las mismas piezas (mismas claves) para que editar
/// campos, colores o posición se aplique en vivo sin recrear nada en el motor. Las medidas se diseñan para
/// 1080 px de lado corto y escalan con el lienzo y con <see cref="Graphic.Scale"/>.
/// </summary>
public static partial class GraphicTemplates
{
    public const double Margin = 96;

    public static IReadOnlyList<GraphicKind> Kinds { get; } = Enum.GetValues<GraphicKind>();

    public static string DisplayName(GraphicKind kind) => kind switch
    {
        GraphicKind.LowerThird => "Rótulo (lower third)",
        GraphicKind.Headline => "Titular / última hora",
        GraphicKind.Ticker => "Cintillo de noticias",
        GraphicKind.Countdown => "Cuenta atrás",
        GraphicKind.Clock => "Reloj",
        GraphicKind.Scoreboard => "Marcador deportivo",
        GraphicKind.LiveBadge => "Mosca EN VIVO",
        GraphicKind.Alert => "Alerta",
        GraphicKind.ChatOverlay => "Chat en directo",
        GraphicKind.Custom => "Diseño libre",
        _ => kind.ToString(),
    };

    /// <summary>Filtro de archivo (formato de libobs) para logos e imágenes de gráficos.</summary>
    public const string ImageFilter = "Imágenes (*.png *.jpg *.jpeg *.webp *.gif);;Todos (*.*)";

    public static IReadOnlyList<GraphicFieldDefinition> Fields(GraphicKind kind) => kind switch
    {
        GraphicKind.LowerThird => [new("name", "Nombre", "Nombre Apellido"), new("title", "Cargo / descripción", "Cargo"),
            new("logo", "Logo (imagen, opcional)", "", FileFilter: ImageFilter)],
        GraphicKind.Headline => [new("label", "Etiqueta", "ÚLTIMA HORA"), new("headline", "Titular", "Titular de la noticia")],
        GraphicKind.Ticker => [new("label", "Etiqueta", "NOTICIAS"), new("items", "Noticias (una por línea)", "Primera noticia\nSegunda noticia", true),
            new("speed", "Velocidad (px/s)", "120")],
        GraphicKind.Countdown => [new("label", "Etiqueta", "EMPEZAMOS EN"), new("duration", "Duración (mm:ss)", "05:00"),
            new("finished", "Texto al terminar", "¡YA!"), new("endsAt", "Hora de fin (hh:mm, opcional; sustituye a la duración)", "")],
        GraphicKind.Clock => [new("format", "Formato (.NET)", "HH:mm")],
        GraphicKind.Scoreboard => [new("home", "Local", "LOCAL"), new("away", "Visitante", "VISITANTE"), new("homeScore", "Tanteo local", "0"),
            new("awayScore", "Tanteo visitante", "0"), new("period", "Periodo", "1T"), new("clock", "Tiempo", "00:00")],
        GraphicKind.LiveBadge => [new("text", "Texto", "● EN VIVO")],
        GraphicKind.Alert => [new("title", "Título", "¡Nuevo seguidor!"), new("message", "Mensaje", "Gracias por seguir el canal")],
        GraphicKind.ChatOverlay => [new("line1", "Línea 1", "Alex: ¡Buen directo!"), new("line2", "Línea 2", "Sofi: Hola a todos"), new("line3", "Línea 3", "Carlos: Saludos"), new("line4", "Línea 4", "")],
        _ => [],
    };

    /// <summary>
    /// Campos de un gráfico concreto. En un diseño libre son los marcadores {campo} de sus textos, más los de la
    /// cuenta atrás si algún texto la muestra.
    /// </summary>
    public static IReadOnlyList<GraphicFieldDefinition> FieldsOf(Graphic g)
    {
        if (g.Kind != GraphicKind.Custom)
            return Fields(g.Kind);
        var keys = (g.Design ?? []).Where(e => e.Kind == DesignElementKind.Text && e.Binding == DesignBinding.None)
            .SelectMany(e => TokenPattern().Matches(e.Text).Select(m => m.Groups[1].Value)).Distinct().ToList();
        var defs = keys.Select(k => new GraphicFieldDefinition(k, k, "")).ToList();
        if (HasCountdown(g))
            defs.AddRange(Fields(GraphicKind.Countdown).Where(f => f.Key != "label" && !keys.Contains(f.Key)));
        return defs;
    }

    /// <summary>El gráfico tiene una cuenta atrás que se puede iniciar, pausar y reiniciar.</summary>
    public static bool HasCountdown(Graphic g) =>
        g.Kind == GraphicKind.Countdown || g.Design?.Any(e => e.Binding == DesignBinding.Countdown && e.Visible) == true;

    /// <summary>Claves de las piezas cuyo texto calcula el estudio en cada momento (reloj, cuenta atrás).</summary>
    public static IEnumerable<string> LiveKeys(Graphic g) => g.Kind switch
    {
        GraphicKind.Countdown or GraphicKind.Clock => ["time"],
        GraphicKind.Custom => (g.Design ?? []).Where(e => e.Visible && e.Binding != DesignBinding.None).Select(e => e.Key),
        _ => [],
    };

    /// <summary>Sustituye {campo} por su valor; un marcador sin campo se deja tal cual.</summary>
    public static string ReplaceTokens(string text, IReadOnlyDictionary<string, string> fields) =>
        TokenPattern().Replace(text, m => fields.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    [System.Text.RegularExpressions.GeneratedRegex(@"\{([a-zA-Z][a-zA-Z0-9_]{0,39})\}")]
    private static partial System.Text.RegularExpressions.Regex TokenPattern();

    /// <summary>Gráfico nuevo con valores por defecto (o los de <paramref name="brand"/>), colocado según su tipo.</summary>
    public static Graphic Create(GraphicKind kind, string name, CanvasSettings canvas, Guid? id = null, BrandKit? brand = null)
    {
        var fields = Fields(kind).ToDictionary(f => f.Key, f => f.Default);
        var k = Unit(canvas);
        var (x, y) = kind switch
        {
            GraphicKind.LowerThird => (Margin * k, canvas.Height - 240 * k),
            GraphicKind.Headline => (Margin * k, canvas.Height - 200 * k),
            GraphicKind.Ticker => (0, canvas.Height - 64 * k),
            // Esquina superior derecha: mosca arriba; reloj y cuenta atrás debajo. El marcador, arriba a la izquierda.
            GraphicKind.LiveBadge => (canvas.Width - (Margin + 200) * k, 64 * k),
            GraphicKind.Countdown => (canvas.Width - (Margin + 360) * k, 200 * k),
            GraphicKind.Clock => (canvas.Width - (Margin + 220) * k, 200 * k),
            GraphicKind.Scoreboard => (Margin * k, 64 * k),
            GraphicKind.Alert => ((canvas.Width - 720 * k) / 2, canvas.Height * 0.12),
            GraphicKind.ChatOverlay => (Margin * k, canvas.Height - 340 * k),
            _ => (Margin * k, Margin * k),
        };
        var (animIn, animOut) = kind switch
        {
            GraphicKind.LowerThird or GraphicKind.Headline => (GraphicAnimation.Wipe, GraphicAnimation.Fade),
            GraphicKind.Ticker => (GraphicAnimation.SlideFromBottom, GraphicAnimation.SlideFromBottom),
            GraphicKind.Alert => (GraphicAnimation.Pop, GraphicAnimation.Fade),
            GraphicKind.ChatOverlay => (GraphicAnimation.SlideFromBottom, GraphicAnimation.Fade),
            GraphicKind.Scoreboard => (GraphicAnimation.SlideFromTop, GraphicAnimation.SlideFromTop),
            _ => (GraphicAnimation.Fade, GraphicAnimation.Fade),
        };
        IReadOnlyList<DesignElement>? design = null;
        if (kind == GraphicKind.Custom)
        {
            // Punto de partida editable: un panel y un título enlazado al campo {titulo}.
            var style = brand?.Style ?? new GraphicStyle();
            fields["titulo"] = "Título";
            design =
            [
                new DesignElement("panel", "Panel", DesignElementKind.Rect, 0, 0, Math.Round(800 * k), Math.Round(140 * k), Color: style.SecondaryColor),
                new DesignElement("titulo", "Título", DesignElementKind.Text, Math.Round(32 * k), 0, Math.Round(740 * k), Math.Round(140 * k),
                    Color: style.TextColor, Text: "{titulo}", FontFace: style.FontFace, FontSize: Math.Round(56 * k), Bold: true),
            ];
            (x, y) = (Margin * k, canvas.Height - 260 * k);
        }
        var graphic = new Graphic(id ?? Guid.NewGuid(), name, kind, fields, new GraphicStyle(), Math.Round(x), Math.Round(y),
            AnimationIn: animIn, AnimationOut: animOut, AutoHideSeconds: kind == GraphicKind.Alert ? 6 : 0, Design: design);
        return brand is null ? graphic : brand.ApplyTo(graphic);
    }

    /// <summary>
    /// Piezas del gráfico. <paramref name="text"/> permite sustituir el texto de una pieza (cuenta atrás,
    /// reloj) sin cambiar los campos guardados.
    /// </summary>
    public static GraphicLayout Layout(Graphic g, CanvasSettings canvas, Func<string, string?>? text = null)
    {
        var k = Unit(canvas) * g.Scale;
        var s = g.Style;
        var list = new List<GraphicElement>();

        void Rect(string key, double x, double y, double w, double h, string color) =>
            list.Add(new GraphicElement(key, GraphicElementKind.Rect, g.X + x * k, g.Y + y * k, Math.Max(1, w * k), Math.Max(1, h * k), color));

        void Text(string key, double x, double y, double w, double h, string color, string value, double size, bool bold = false,
            string align = "left", GraphicElementKind kind = GraphicElementKind.Text) =>
            list.Add(new GraphicElement(key, kind, g.X + x * k, g.Y + y * k, Math.Max(1, w * k), Math.Max(1, h * k), color,
                text?.Invoke(key) ?? value, size * k, bold, align));

        switch (g.Kind)
        {
            case GraphicKind.Custom:
                foreach (var e in g.Design ?? [])
                {
                    if (!e.Visible)
                        continue;
                    var kind = e.Kind switch
                    {
                        DesignElementKind.Rect => GraphicElementKind.Rect,
                        DesignElementKind.Image => GraphicElementKind.Image,
                        DesignElementKind.Video => GraphicElementKind.Video,
                        _ => GraphicElementKind.Text,
                    };
                    var content = e.Kind is DesignElementKind.Image or DesignElementKind.Video ? e.Path ?? ""
                        : text?.Invoke(e.Key) ?? ReplaceTokens(e.Text, g.Fields);
                    var size = g.Scale;
                    // La sombra es una pieza más, debajo y desplazada (libobs no tiene sombra en texto GDI+ ni en formas).
                    if (e.ShadowColor is { } shadow && e.Kind is DesignElementKind.Rect or DesignElementKind.Text)
                        list.Add(new GraphicElement(e.Key + "-shadow", kind, g.X + (e.X + e.ShadowX) * size, g.Y + (e.Y + e.ShadowY) * size,
                            e.Width * size, e.Height * size, shadow, content, e.FontSize * size, e.Bold, e.Align, e.Rotation,
                            e with { Color = shadow, GradientColor = null, OutlineColor = null, OutlineSize = 0 }));
                    list.Add(new GraphicElement(e.Key, kind, g.X + e.X * size, g.Y + e.Y * size, e.Width * size, e.Height * size,
                        e.Color, content, e.FontSize * size, e.Bold, e.Align, e.Rotation, e));
                }
                break;

            case GraphicKind.LowerThird:
            {
                // La pieza del logo existe siempre (las piezas no cambian en vivo); sin logo es un punto vacío.
                var logo = g.Field("logo").Trim();
                var dx = logo.Length > 0 ? 132 : 0;
                list.Add(new GraphicElement("logo", GraphicElementKind.Image, g.X, g.Y, logo.Length > 0 ? 120 * k : 1, logo.Length > 0 ? 120 * k : 1,
                    "#00000000", logo));
                Rect("accent", dx, 0, 12, 120, s.PrimaryColor);
                // El logo ocupa parte del ancho de las cajas: el rótulo mide lo mismo y sigue cabiendo en vertical.
                Rect("nameBox", dx + 12, 0, 820 - dx, 72, s.SecondaryColor);
                Text("name", dx + 40, 0, 780 - dx, 72, s.TextColor, g.Field("name"), 44, bold: true);
                Rect("titleBox", dx + 12, 72, 700 - dx, 48, s.PrimaryColor);
                Text("title", dx + 40, 72, 660 - dx, 48, s.AccentTextColor, g.Field("title"), 28);
                break;
            }

            case GraphicKind.Headline:
            {
                // Llega hasta el margen derecho del lienzo.
                var width = Math.Max(600, (canvas.Width - g.X - Margin * Unit(canvas)) / k);
                Rect("labelBox", 0, 0, 300, 90, s.PrimaryColor);
                Text("label", 0, 0, 300, 90, s.AccentTextColor, g.Field("label"), 34, bold: true, align: "center");
                Rect("headlineBox", 300, 0, width - 300, 90, s.SecondaryColor);
                Text("headline", 330, 0, width - 360, 90, s.TextColor, g.Field("headline"), 40, bold: true);
                break;
            }

            case GraphicKind.Ticker:
            {
                var width = Math.Max(600, (canvas.Width - g.X) / k);
                Rect("labelBox", 0, 0, 260, 64, s.PrimaryColor);
                Text("label", 0, 0, 260, 64, s.AccentTextColor, g.Field("label"), 30, bold: true, align: "center");
                Rect("band", 260, 0, width - 260, 64, s.SecondaryColor);
                var items = g.Field("items").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // Separador final: el desplazamiento repite el texto en bucle.
                var line = items.Length == 0 ? " " : string.Join("     •     ", items) + "     •     ";
                Text("items", 280, 0, width - 300, 64, s.TextColor, line, 30, kind: GraphicElementKind.Ticker);
                break;
            }

            case GraphicKind.Countdown:
                Rect("box", 0, 0, 360, 140, s.SecondaryColor);
                Rect("accent", 0, 132, 360, 8, s.PrimaryColor);
                Text("label", 0, 6, 360, 40, s.TextColor, g.Field("label"), 24, bold: true, align: "center");
                Text("time", 0, 42, 360, 88, s.TextColor, g.Field("duration"), 64, bold: true, align: "center");
                break;

            case GraphicKind.Clock:
                Rect("box", 0, 0, 220, 72, s.SecondaryColor);
                Rect("accent", 0, 66, 220, 6, s.PrimaryColor);
                Text("time", 0, 0, 220, 66, s.TextColor, DateTime.Now.ToString(SafeFormat(g.Field("format"))), 40, bold: true, align: "center");
                break;

            case GraphicKind.Scoreboard:
                Rect("homeBox", 0, 0, 260, 64, s.SecondaryColor);
                Text("home", 16, 0, 236, 64, s.TextColor, g.Field("home"), 30, bold: true);
                Rect("homeScoreBox", 260, 0, 80, 64, s.PrimaryColor);
                Text("homeScore", 260, 0, 80, 64, s.AccentTextColor, g.Field("homeScore"), 38, bold: true, align: "center");
                Rect("awayScoreBox", 344, 0, 80, 64, s.PrimaryColor);
                Text("awayScore", 344, 0, 80, 64, s.AccentTextColor, g.Field("awayScore"), 38, bold: true, align: "center");
                Rect("awayBox", 424, 0, 260, 64, s.SecondaryColor);
                Text("away", 440, 0, 236, 64, s.TextColor, g.Field("away"), 30, bold: true, align: "right");
                Rect("infoBox", 222, 64, 240, 40, s.SecondaryColor);
                Text("info", 222, 64, 240, 40, s.TextColor, $"{g.Field("period")}  {g.Field("clock")}".Trim(), 24, align: "center");
                break;

            case GraphicKind.LiveBadge:
                Rect("box", 0, 0, 200, 56, s.PrimaryColor);
                Text("text", 0, 0, 200, 56, s.AccentTextColor, g.Field("text"), 28, bold: true, align: "center");
                break;

            case GraphicKind.Alert:
                Rect("box", 0, 0, 720, 160, s.SecondaryColor);
                Rect("accent", 0, 0, 720, 10, s.PrimaryColor);
                Text("title", 32, 22, 656, 60, s.TextColor, g.Field("title"), 40, bold: true, align: "center");
                Text("message", 32, 86, 656, 56, s.TextColor, g.Field("message"), 28, align: "center");
                break;
        }
        return new GraphicLayout(list);
    }

    /// <summary>
    /// Píxeles del lienzo por unidad de diseño: el lado corto entre 1080. En 1920x1080 es 1 y en vertical
    /// (1080x1920) también, para que los gráficos quepan a lo ancho del formato móvil.
    /// </summary>
    public static double Unit(CanvasSettings canvas) => Math.Min(canvas.Width, canvas.Height) / 1080.0;

    /// <summary>Formato de hora utilizable; uno inválido cae a HH:mm en vez de romper el render.</summary>
    public static string SafeFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return "HH:mm";
        try
        {
            _ = DateTime.Now.ToString(format);
            return format;
        }
        catch (FormatException)
        {
            return "HH:mm";
        }
    }

    /// <summary>Duración "mm:ss", "hh:mm:ss" o segundos. null si no es válida.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        text = text.Trim();
        if (int.TryParse(text, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(p => !int.TryParse(p, out var n) || n < 0))
            return null;
        var values = parts.Select(int.Parse).ToArray();
        return parts.Length == 2
            ? new TimeSpan(0, values[0], values[1])
            : new TimeSpan(values[0], values[1], values[2]);
    }

    /// <summary>"" = sin hora de fin; null = texto no válido.</summary>
    public static TimeOnly? ParseEndsAt(string text) =>
        TimeOnly.TryParseExact(text.Trim(), ["H:mm", "H:mm:ss"], out var t) ? t : null;

    /// <summary>Tiempo hasta la próxima vez que el reloj marque <paramref name="endsAt"/>.</summary>
    public static TimeSpan UntilEndsAt(TimeOnly endsAt, DateTime now)
    {
        var remaining = now.Date + endsAt.ToTimeSpan() - now;
        // Pasada hace menos de 12 h: ya terminó (muestra el texto final). Si hace más, es la de mañana.
        return remaining < TimeSpan.FromHours(-12) ? remaining + TimeSpan.FromDays(1) : remaining;
    }

    public static string FormatRemaining(TimeSpan remaining)
    {
        var total = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60:00}:{total % 60:00}";
    }

    public static string? ValidateFields(Graphic g)
    {
        switch (g.Kind)
        {
            case GraphicKind.Custom when HasCountdown(g) && ParseDuration(g.Field("duration")) is null:
                return "Duración de la cuenta atrás no válida: usa mm:ss, hh:mm:ss o segundos.";
            case GraphicKind.Custom when HasCountdown(g) && g.Field("endsAt").Trim().Length > 0 && ParseEndsAt(g.Field("endsAt")) is null:
                return "Hora de fin no válida: usa hh:mm (p. ej. 19:30) o déjala vacía.";
            case GraphicKind.Countdown when ParseDuration(g.Field("duration")) is null:
                return "Duración no válida: usa mm:ss, hh:mm:ss o segundos.";
            case GraphicKind.Countdown when g.Field("endsAt").Trim().Length > 0 && ParseEndsAt(g.Field("endsAt")) is null:
                return "Hora de fin no válida: usa hh:mm (p. ej. 19:30) o déjala vacía.";
            case GraphicKind.Ticker when !double.TryParse(g.Field("speed"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed) || speed is < 10 or > 500:
                return "La velocidad del cintillo debe estar entre 10 y 500 px/s.";
        }
        return null;
    }
}
