using System.Text.Json;
using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Scenes;

/// <summary>
/// Transiciones del módulo obs-transitions que ofrece el estudio, con los ajustes que entiende cada una.
/// Los ids y claves son los de libobs 32.
/// </summary>
public static class TransitionCatalog
{
    public const string Cut = "cut_transition";
    public const string Fade = "fade_transition";
    public const string Swipe = "swipe_transition";
    public const string Slide = "slide_transition";
    public const string FadeToColor = "fade_to_color_transition";
    public const string LumaWipe = "wipe_transition";
    public const string Stinger = "obs_stinger_transition";

    public static IReadOnlyDictionary<string, string> Names { get; } = new Dictionary<string, string>
    {
        [Cut] = "Corte",
        [Fade] = "Fundido",
        [Swipe] = "Barrido",
        [Slide] = "Deslizamiento",
        [FadeToColor] = "Fundido a color",
        [LumaWipe] = "Cortinilla (luma)",
        [Stinger] = "Stinger (video)",
    };

    /// <summary>Direcciones de barrido y deslizamiento (clave "direction").</summary>
    public static IReadOnlyList<string> Directions { get; } = ["left", "right", "up", "down"];

    /// <summary>Imágenes de luma incluidas en obs-transitions (clave "luma_image", ruta relativa a su carpeta de datos).</summary>
    public static IReadOnlyList<string> LumaImages { get; } =
    [
        "linear-h.png", "linear-v.png", "linear-topleft.png", "linear-topright.png", "barndoor-h.png", "barndoor-v.png",
        "barndoor-topleft.png", "barndoor-botleft.png", "box-topleft.png", "box-topright.png", "box-botleft.png",
        "box-botright.png", "blinds-h.png", "burst.png", "checkerboard-small.png", "circles.png", "clock.png", "cloud.png",
        "curtain.png", "fan.png", "fractal.png", "iris.png", "parallel-zigzag-h.png", "parallel-zigzag-v.png", "sinus9.png",
        "spiral.png", "square.png", "squares.png", "stripes.png", "strips-h.png", "strips-v.png", "watercolor.png",
        "zigzag-h.png", "zigzag-v.png",
    ];

    /// <summary>
    /// Modos del punto de corte del stinger ("tp_type"): 0 = tiempo en ms, 1 = fotograma.
    /// El stinger fija su propia duración (la del video), por eso ignora la duración configurada.
    /// </summary>
    public const int StingerPointTime = 0;

    public static string? Validate(TransitionSettings transition)
    {
        if (string.IsNullOrEmpty(transition.TypeId) || !Names.ContainsKey(transition.TypeId))
            return $"Transición desconocida: {transition.TypeId}.";
        if (transition.DurationMs is < 0 or > 20_000)
            return "La duración debe estar entre 0 y 20000 ms.";

        JsonElement settings;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(transition.SettingsJson) ? "{}" : transition.SettingsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "Los ajustes de la transición deben ser un objeto JSON.";
            settings = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return "Los ajustes de la transición no son JSON válido.";
        }

        switch (transition.TypeId)
        {
            case Stinger:
                if (!settings.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(path.GetString()))
                    return "El stinger necesita un archivo de video.";
                if (!File.Exists(path.GetString()))
                    return $"No se encuentra el video del stinger: {path.GetString()}";
                break;
            case Swipe or Slide:
                if (settings.TryGetProperty("direction", out var direction) &&
                    !Directions.Contains(direction.GetString() ?? ""))
                    return "Dirección no válida.";
                break;
        }
        return null;
    }
}
