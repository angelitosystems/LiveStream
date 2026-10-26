using LiveStream.Domain.Graphics;

namespace LiveStream.Application.Graphics;

/// <summary>Identidad visual por defecto de los gráficos: colores, tipografía y logo (ruta local; vacía = sin logo).</summary>
public sealed record BrandKit(string Name, GraphicStyle Style, string Logo = "")
{
    public static BrandKit Default { get; } = new("Mi marca", new GraphicStyle());

    public string? Validate() => string.IsNullOrWhiteSpace(Name) ? "El kit de marca necesita un nombre." : Style.Validate();

    /// <summary>El gráfico con los colores y la tipografía de la marca, y su logo si el gráfico tiene campo de logo.</summary>
    public Graphic ApplyTo(Graphic g)
    {
        var fields = g.Fields;
        if (Logo.Length > 0 && GraphicTemplates.Fields(g.Kind).Any(f => f.Key == "logo"))
            fields = new Dictionary<string, string>(g.Fields) { ["logo"] = Logo };
        return g with { Style = Style, Fields = fields };
    }
}
