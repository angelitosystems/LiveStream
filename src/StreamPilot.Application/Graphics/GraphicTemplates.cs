using StreamPilot.Domain.Entities;
using StreamPilot.Domain.Graphics;

namespace StreamPilot.Application.Graphics;

/// <summary>Campo editable de una plantilla.</summary>
public sealed record GraphicFieldDefinition(string Key, string Label, string Default, bool Multiline = false);

public enum GraphicElementKind
{
    /// <summary>Rectángulo de color sólido (color_source_v3).</summary>
    Rect,
    /// <summary>Texto en una caja fija (text_gdiplus_v3 con extents).</summary>
    Text,
    /// <summary>Texto de ancho natural que se desplaza dentro de su caja (scroll_filter).</summary>
    Ticker,
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
    string Align = "left");

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
public static class GraphicTemplates
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
        _ => kind.ToString(),
    };

    public static IReadOnlyList<GraphicFieldDefinition> Fields(GraphicKind kind) => kind switch
    {
        GraphicKind.LowerThird => [new("name", "Nombre", "Nombre Apellido"), new("title", "Cargo / descripción", "Cargo")],
        GraphicKind.Headline => [new("label", "Etiqueta", "ÚLTIMA HORA"), new("headline", "Titular", "Titular de la noticia")],
        GraphicKind.Ticker => [new("label", "Etiqueta", "NOTICIAS"), new("items", "Noticias (una por línea)", "Primera noticia\nSegunda noticia", true),
            new("speed", "Velocidad (px/s)", "120")],
        GraphicKind.Countdown => [new("label", "Etiqueta", "EMPEZAMOS EN"), new("duration", "Duración (mm:ss)", "05:00"),
            new("finished", "Texto al terminar", "¡YA!")],
        GraphicKind.Clock => [new("format", "Formato (.NET)", "HH:mm")],
        GraphicKind.Scoreboard => [new("home", "Local", "LOCAL"), new("away", "Visitante", "VISITANTE"), new("homeScore", "Tanteo local", "0"),
            new("awayScore", "Tanteo visitante", "0"), new("period", "Periodo", "1T"), new("clock", "Tiempo", "00:00")],
        GraphicKind.LiveBadge => [new("text", "Texto", "● EN VIVO")],
        GraphicKind.Alert => [new("title", "Título", "¡Nuevo seguidor!"), new("message", "Mensaje", "Gracias por seguir el canal")],
        _ => [],
    };

    /// <summary>Gráfico nuevo con valores por defecto, colocado según su tipo.</summary>
    public static Graphic Create(GraphicKind kind, string name, CanvasSettings canvas, Guid? id = null)
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
            _ => (Margin * k, Margin * k),
        };
        var (animIn, animOut) = kind switch
        {
            GraphicKind.LowerThird or GraphicKind.Headline => (GraphicAnimation.Wipe, GraphicAnimation.Fade),
            GraphicKind.Ticker => (GraphicAnimation.SlideFromBottom, GraphicAnimation.SlideFromBottom),
            GraphicKind.Alert => (GraphicAnimation.Pop, GraphicAnimation.Fade),
            GraphicKind.Scoreboard => (GraphicAnimation.SlideFromTop, GraphicAnimation.SlideFromTop),
            _ => (GraphicAnimation.Fade, GraphicAnimation.Fade),
        };
        return new Graphic(id ?? Guid.NewGuid(), name, kind, fields, new GraphicStyle(), Math.Round(x), Math.Round(y),
            AnimationIn: animIn, AnimationOut: animOut, AutoHideSeconds: kind == GraphicKind.Alert ? 6 : 0);
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
            case GraphicKind.LowerThird:
                Rect("accent", 0, 0, 12, 120, s.PrimaryColor);
                Rect("nameBox", 12, 0, 820, 72, s.SecondaryColor);
                Text("name", 40, 0, 780, 72, s.TextColor, g.Field("name"), 44, bold: true);
                Rect("titleBox", 12, 72, 700, 48, s.PrimaryColor);
                Text("title", 40, 72, 660, 48, s.AccentTextColor, g.Field("title"), 28);
                break;

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

    public static string FormatRemaining(TimeSpan remaining)
    {
        var total = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60:00}:{total % 60:00}";
    }

    public static string? ValidateFields(Graphic g)
    {
        switch (g.Kind)
        {
            case GraphicKind.Countdown when ParseDuration(g.Field("duration")) is null:
                return "Duración no válida: usa mm:ss, hh:mm:ss o segundos.";
            case GraphicKind.Ticker when !double.TryParse(g.Field("speed"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed) || speed is < 10 or > 500:
                return "La velocidad del cintillo debe estar entre 10 y 500 px/s.";
        }
        return null;
    }
}
