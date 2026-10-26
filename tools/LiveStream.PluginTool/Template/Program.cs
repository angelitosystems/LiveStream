using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStream.PluginSdk;

// __NAME__ — plugin de ejemplo para LiveStream. stdout es el canal del protocolo: nunca escribas otra cosa en él
// (usa «log» o stderr; stderr lo guarda el registro de LiveStream). Documentación completa:
// https://github.com/angelitosystems/LiveStream/blob/main/docs/plugins/SDK.md
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);

var peer = new JsonRpcPeer(Console.In, Console.Out);
var shutdown = new TaskCompletionSource();
var greeting = "Hola";

peer.OnRequest = async (method, parameters, ct) =>
{
    switch (method)
    {
        // Llega al arrancar, con 10 s para responder {}. Ajustes y permisos ya aceptados por el usuario.
        case PluginProtocol.Initialize:
            greeting = parameters?["settings"]?["greeting"]?.GetValue<string>() ?? greeting;
            await LogAsync($"{greeting} desde LiveStream {parameters?["appVersion"]}", ct);
            return new JsonObject();
        // Llega al detener el plugin: responde y termina limpio.
        case PluginProtocol.Shutdown:
            shutdown.TrySetResult();
            return new JsonObject();
        default:
            throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Método desconocido: {method}");
    }
};

peer.OnNotification = (method, parameters) =>
{
    if (method == PluginProtocol.SettingsChanged)
        greeting = parameters?["settings"]?["greeting"]?.GetValue<string>() ?? greeting;
    else if (method == PluginProtocol.Event && parameters?["name"]?.GetValue<string>() == PluginProtocol.StudioChangedEvent)
        _ = ReportScenesAsync();
};

var reading = peer.RunAsync(CancellationToken.None);
await Task.WhenAny(reading, shutdown.Task);
return 0;

// Ejemplo: llama a una herramienta del estudio. Requiere el permiso studio.read (plugin.json).
async Task ReportScenesAsync()
{
    try
    {
        var result = await peer.RequestAsync(PluginProtocol.ToolsCall,
            new JsonObject { ["name"] = "get_studio_state", ["arguments"] = new JsonObject() }, CancellationToken.None);
        var state = JsonNode.Parse(result?["content"]?.GetValue<string>() ?? "{}");
        var scenes = state?["scenes"]?.AsArray().Count ?? 0;
        await LogAsync($"{greeting}: el estudio tiene {scenes} escena(s).", CancellationToken.None);
    }
    catch (Exception ex) when (ex is JsonRpcException or IOException or JsonException)
    {
        Console.Error.WriteLine($"No se pudo leer el estado: {ex.Message}");
    }
}

Task LogAsync(string message, CancellationToken ct) =>
    peer.NotifyAsync(PluginProtocol.Log, new JsonObject { ["level"] = "info", ["message"] = message }, ct);
