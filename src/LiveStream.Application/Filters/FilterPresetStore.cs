using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveStream.Application.Filters;

public sealed record FilterPresetItem(string Name, string TypeId, string SettingsJson, bool Enabled = true);

public sealed record FilterPreset(
    string Name,
    string Description,
    bool IsAudio,
    IReadOnlyList<FilterPresetItem> Filters,
    bool IsBuiltIn = false);

/// <summary>
/// Gestión de preajustes de filtros (cadenas predefinidas y personalizadas del usuario).
/// Permite aplicar cadenas profesionales completas o filtros individuales con un solo clic.
/// </summary>
public static class FilterPresetStore
{
    private static readonly List<FilterPreset> _customPresets = [];
    private static readonly object _lock = new();

    /// <summary>Preajustes profesionales integrados de serie en LiveStream.</summary>
    public static readonly IReadOnlyList<FilterPreset> BuiltInPresets =
    [
        // Audio: Micrófono profesional para streaming
        new(
            "Cadena Vocal Pro (Streaming / Podcast)",
            "Cadena completa: Puerta de ruido + Supresión RNNoise + Compresor dinámico + Limitador anti-saturación.",
            IsAudio: true,
            Filters:
            [
                new("Puerta de ruido", "noise_gate_filter", """{"open_threshold":-32.0,"close_threshold":-38.0,"attack_time":25,"hold_time":200,"release_time":400}"""),
                new("Supresión de ruido RNNoise", "noise_suppress_filter_v2", """{"method":"rnnoise"}"""),
                new("Compresor vocal", "compressor_filter", """{"ratio":4.0,"threshold":-18.0,"attack_time":6,"release_time":60,"output_gain":2.0}"""),
                new("Limitador de seguridad", "limiter_filter", """{"threshold":-1.5,"release_time":60}""")
            ],
            IsBuiltIn: true),

        // Audio: Solo Supresión + Limitador
        new(
            "Anti-Ruido y Protección Básica",
            "Supresión suave y limitador para evitar saturaciones sin alterar el tono de la voz.",
            IsAudio: true,
            Filters:
            [
                new("Supresión de ruido", "noise_suppress_filter_v2", """{"method":"rnnoise"}"""),
                new("Limitador -2 dB", "limiter_filter", """{"threshold":-2.0,"release_time":60}""")
            ],
            IsBuiltIn: true),

        // Video: Chroma Key profesional
        new(
            "Chroma Key Estudio (Fondo verde)",
            "Calibración precisa para tela verde iluminada con reducción de vertido y suavizado de bordes.",
            IsAudio: false,
            Filters:
            [
                new("Clave cromática verde", "chroma_key_filter_v2", """{"key_color_type":"green","similarity":400,"smoothness":80,"spill":100}"""),
                new("Corrección de color", "color_filter_v2", """{"contrast":0.05,"brightness":0.02,"saturation":0.08}""")
            ],
            IsBuiltIn: true),

        // Video: Look Cámara Cinematográfica
        new(
            "Cámara Cinematográfica (Contraste y Nitidez)",
            "Realce de contraste sutil, temperatura de color equilibrada y nitidez para webcams USB.",
            IsAudio: false,
            Filters:
            [
                new("Corrección de color", "color_filter_v2", """{"contrast":0.08,"brightness":0.01,"saturation":0.06,"gamma":-0.03}"""),
                new("Nitidez fina", "sharpness_filter_v2", """{"sharpness":0.12}""")
            ],
            IsBuiltIn: true),

        // Video: Recorte de bordes 16:9 centrado
        new(
            "Recorte de márgenes (Clean Border)",
            "Elimina bordes negros o artefactos de captura en los laterales.",
            IsAudio: false,
            Filters:
            [
                new("Recorte de márgenes", "crop_filter", """{"left":0,"right":0,"top":0,"bottom":0}""")
            ],
            IsBuiltIn: true)
    ];

    public static IReadOnlyList<FilterPreset> GetAllPresets()
    {
        lock (_lock)
        {
            var list = new List<FilterPreset>(BuiltInPresets);
            list.AddRange(_customPresets);
            return list;
        }
    }

    public static IReadOnlyList<FilterPreset> GetPresetsForSource(bool isAudio)
    {
        lock (_lock)
        {
            return GetAllPresets().Where(p => p.IsAudio == isAudio).ToList();
        }
    }

    public static void SaveCustomPreset(string name, string description, bool isAudio, IReadOnlyList<FilterPresetItem> filters)
    {
        lock (_lock)
        {
            _customPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _customPresets.Add(new FilterPreset(name, description, isAudio, filters.ToList(), IsBuiltIn: false));
        }
    }

    public static bool DeleteCustomPreset(string name)
    {
        lock (_lock)
        {
            return _customPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !p.IsBuiltIn) > 0;
        }
    }

    /// <summary>Copia una lista de filtros a formato JSON transportable para portapapeles.</summary>
    public static string ExportFiltersToJson(IReadOnlyList<FilterPresetItem> filters) =>
        JsonSerializer.Serialize(filters, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Importa filtros desde formato JSON del portapapeles.</summary>
    public static IReadOnlyList<FilterPresetItem>? ImportFiltersFromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<FilterPresetItem>>(json);
        }
        catch
        {
            return null;
        }
    }
}
