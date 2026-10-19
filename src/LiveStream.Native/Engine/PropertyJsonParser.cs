using System.Text.Json;
using LiveStream.Application.Abstractions;

namespace LiveStream.Native.Engine;

/// <summary>Convierte el JSON de sp_source_get_properties en el modelo de la capa de aplicación.</summary>
internal static class PropertyJsonParser
{
    public static IReadOnlyList<SourceProperty> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseArray(doc.RootElement);
    }

    private static List<SourceProperty> ParseArray(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Select(ParseOne).ToList() : [];

    private static SourceProperty ParseOne(JsonElement e) => new(
        Name: Str(e, "name") ?? string.Empty,
        Description: Str(e, "description") ?? string.Empty,
        Type: (Str(e, "type") ?? "") switch
        {
            "bool" => SourcePropertyType.Bool,
            "int" => SourcePropertyType.Int,
            "float" => SourcePropertyType.Float,
            "text" => SourcePropertyType.Text,
            "path" => SourcePropertyType.Path,
            "list" => SourcePropertyType.List,
            "color" => SourcePropertyType.Color,
            "color_alpha" => SourcePropertyType.ColorAlpha,
            "font" => SourcePropertyType.Font,
            "button" => SourcePropertyType.Button,
            "editable_list" => SourcePropertyType.EditableList,
            "frame_rate" => SourcePropertyType.FrameRate,
            "group" => SourcePropertyType.Group,
            _ => SourcePropertyType.Unknown,
        },
        Enabled: Bool(e, "enabled", true),
        Tooltip: Str(e, "tooltip"),
        Min: Num(e, "min"),
        Max: Num(e, "max"),
        Step: Num(e, "step"),
        Slider: Bool(e, "slider"),
        Multiline: Bool(e, "multiline"),
        Password: Bool(e, "password"),
        Info: Bool(e, "info"),
        PathType: Str(e, "pathType"),
        Filter: Str(e, "filter"),
        ListFormat: Str(e, "format"),
        Editable: Bool(e, "editable"),
        Options: e.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(i => new SourcePropertyOption(
                Str(i, "name") ?? string.Empty,
                i.TryGetProperty("value", out var v) ? Value(v) : null,
                Bool(i, "disabled"))).ToList()
            : null,
        Children: e.TryGetProperty("properties", out var children) ? ParseArray(children) : null);

    private static object? Value(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number when v.TryGetInt64(out var l) => l,
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool Bool(JsonElement e, string name, bool fallback = false) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True : fallback;
}
