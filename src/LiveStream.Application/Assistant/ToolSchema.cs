using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveStream.Application.Assistant;

/// <summary>
/// Esquemas JSON de los argumentos de las herramientas y su validación. Se usa el mismo esquema para
/// declarar la herramienta al modelo (modo estricto) y para validar lo que llega antes de ejecutar nada:
/// el modelo propone, la aplicación comprueba.
/// </summary>
public static class ToolSchema
{
    public static JsonObject Str(string description, IEnumerable<string>? options = null, int maxLength = 500)
    {
        var s = new JsonObject { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };
        if (options is not null)
            s["enum"] = new JsonArray(options.Select(o => (JsonNode)o).ToArray());
        return s;
    }

    public static JsonObject Num(string description, double min, double max) =>
        new() { ["type"] = "number", ["description"] = description, ["minimum"] = min, ["maximum"] = max };

    public static JsonObject Int(string description, int min, int max) =>
        new() { ["type"] = "integer", ["description"] = description, ["minimum"] = min, ["maximum"] = max };

    public static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    /// <summary>Lista de pares campo/valor (en modo estricto no se admiten objetos con claves libres).</summary>
    public static JsonObject Fields(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["maxItems"] = 20,
        ["items"] = Object(
            ("key", Str("Nombre del campo."), true),
            ("value", Str("Valor del campo.", maxLength: 2000), true)),
    };

    public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema, _) in properties)
            props[name] = schema;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(properties.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
            ["additionalProperties"] = false,
        };
    }

    /// <summary>Valida un valor contra el subconjunto de JSON Schema que generan estos ayudantes. null si es válido.</summary>
    public static string? Validate(JsonObject schema, JsonElement value, string path = "entrada")
    {
        var type = schema["type"]?.GetValue<string>();
        switch (type)
        {
            case "object":
            {
                if (value.ValueKind != JsonValueKind.Object)
                    return $"{path} debe ser un objeto.";
                var props = schema["properties"] as JsonObject ?? [];
                foreach (var required in (schema["required"] as JsonArray ?? []).Select(r => r!.GetValue<string>()))
                    if (!value.TryGetProperty(required, out var v) || v.ValueKind == JsonValueKind.Null)
                        return $"Falta {path}.{required}.";
                foreach (var property in value.EnumerateObject())
                {
                    if (props[property.Name] is not JsonObject child)
                        return $"{path}.{property.Name} no es un argumento admitido.";
                    if (property.Value.ValueKind == JsonValueKind.Null)
                        continue;
                    if (Validate(child, property.Value, $"{path}.{property.Name}") is { } error)
                        return error;
                }
                return null;
            }
            case "array":
            {
                if (value.ValueKind != JsonValueKind.Array)
                    return $"{path} debe ser una lista.";
                if (schema["maxItems"]?.GetValue<int>() is { } max && value.GetArrayLength() > max)
                    return $"{path} admite como máximo {max} elementos.";
                var i = 0;
                foreach (var item in value.EnumerateArray())
                    if (Validate((JsonObject)schema["items"]!, item, $"{path}[{i++}]") is { } error)
                        return error;
                return null;
            }
            case "string":
            {
                if (value.ValueKind != JsonValueKind.String)
                    return $"{path} debe ser texto.";
                var text = value.GetString()!;
                if (schema["maxLength"]?.GetValue<int>() is { } maxLength && text.Length > maxLength)
                    return $"{path} es demasiado largo (máximo {maxLength}).";
                if (schema["enum"] is JsonArray options && options.All(o => o!.GetValue<string>() != text))
                    return $"{path} debe ser uno de: {string.Join(", ", options.Select(o => o!.GetValue<string>()))}.";
                return null;
            }
            case "number" or "integer":
            {
                if (value.ValueKind != JsonValueKind.Number)
                    return $"{path} debe ser un número.";
                var n = value.GetDouble();
                if (!double.IsFinite(n) || (type == "integer" && n != Math.Floor(n)))
                    return $"{path} debe ser un {(type == "integer" ? "entero" : "número")} válido.";
                if (schema["minimum"]?.GetValue<double>() is { } min && n < min)
                    return $"{path} debe ser al menos {min}.";
                if (schema["maximum"]?.GetValue<double>() is { } max && n > max)
                    return $"{path} debe ser como mucho {max}.";
                return null;
            }
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"{path} debe ser verdadero o falso.";
            default:
                return null;
        }
    }
}
