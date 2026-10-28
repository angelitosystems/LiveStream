using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace LiveStream.PluginSdk;

/// <summary>Respuesta de una herramienta de LiveStream.</summary>
public sealed record ToolResult(string Content, bool IsError, string Summary);

/// <summary>
/// Lado del plugin del protocolo, listo para usar: responde a <c>initialize</c> y <c>shutdown</c>, guarda los ajustes y
/// entrega los eventos. Los manejadores se ejecutan de uno en uno, fuera del hilo de lectura, así que pueden llamar a
/// herramientas sin bloquear el protocolo. LiveStream registra cada herramienta que se usa en su log.
/// </summary>
public sealed class PluginClient
{
    private readonly JsonRpcPeer _peer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _handlers = new(1, 1);
    private volatile IReadOnlyDictionary<string, string> _settings = new Dictionary<string, string>();

    public PluginClient(TextReader input, TextWriter output)
    {
        _peer = new JsonRpcPeer(input, output) { OnRequest = HandleRequestAsync, OnNotification = HandleNotification };
    }

    /// <summary>Cliente sobre stdin/stdout en UTF-8. stdout queda reservado al protocolo.</summary>
    public static PluginClient FromConsole()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        return new PluginClient(Console.In, Console.Out);
    }

    /// <summary>Se cancela al recibir <c>shutdown</c> o al cerrarse la conexión: úsalo en los bucles del plugin.</summary>
    public CancellationToken Stopping => _stopping.Token;

    public IReadOnlyDictionary<string, string> Settings => _settings;

    /// <summary>Tras <c>initialize</c> y cada vez que el usuario cambia los ajustes.</summary>
    public Func<Task>? SettingsApplied { get; set; }

    /// <summary>Evento del estudio (por ejemplo <see cref="PluginProtocol.StudioChangedEvent"/>).</summary>
    public Func<string, Task>? EventReceived { get; set; }

    /// <summary>Ajuste de texto; vacío o ausente devuelve <paramref name="fallback"/>.</summary>
    public string Setting(string key, string fallback = "") =>
        _settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;

    /// <summary>Ajuste numérico acotado a [min, max]; si no es un número, <paramref name="fallback"/>.</summary>
    public double Number(string key, double fallback, double min, double max) =>
        double.TryParse(Setting(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
            ? Math.Clamp(n, min, max)
            : fallback;

    /// <summary>Atiende el protocolo hasta <c>shutdown</c> o hasta que LiveStream cierre la conexión.</summary>
    public async Task RunAsync()
    {
        var reading = _peer.RunAsync(CancellationToken.None);
        var stopped = Task.Delay(Timeout.Infinite, Stopping).ContinueWith(_ => { }, TaskScheduler.Default);
        try
        {
            await await Task.WhenAny(reading, stopped).ConfigureAwait(false);
        }
        finally
        {
            _stopping.Cancel();
        }
    }

    /// <summary>Llama a una herramienta. Un error de la herramienta llega en <see cref="ToolResult.IsError"/>.</summary>
    public async Task<ToolResult> CallToolAsync(string name, JsonObject? arguments = null, CancellationToken ct = default)
    {
        var result = await _peer.RequestAsync(PluginProtocol.ToolsCall,
            new JsonObject { ["name"] = name, ["arguments"] = arguments ?? new JsonObject() }, ct).ConfigureAwait(false);
        return new ToolResult(result?["content"]?.GetValue<string>() ?? "", result?["isError"]?.GetValue<bool>() ?? false,
            result?["summary"]?.GetValue<string>() ?? "");
    }

    /// <summary>Estado del estudio (<c>get_studio_state</c>) ya interpretado.</summary>
    public async Task<JsonObject> StudioStateAsync(CancellationToken ct = default) =>
        JsonNode.Parse((await CallToolAsync("get_studio_state", ct: ct).ConfigureAwait(false)).Content) as JsonObject ?? [];

    /// <summary>Crea el gráfico (<c>create_graphic</c>) si no hay ninguno con ese nombre. Devuelve true si lo creó.</summary>
    public async Task<bool> EnsureGraphicAsync(string name, string kind, JsonArray? fields = null, CancellationToken ct = default)
    {
        var state = await StudioStateAsync(ct).ConfigureAwait(false);
        if (state["graphics"] is JsonArray graphics
            && graphics.Any(g => string.Equals(g?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)))
            return false;
        var created = await CallToolAsync("create_graphic",
            new JsonObject { ["kind"] = kind, ["name"] = name, ["fields"] = fields ?? [] }, ct).ConfigureAwait(false);
        if (created.IsError)
            throw new InvalidOperationException(created.Content);
        return true;
    }

    /// <summary>Campos de una herramienta de gráficos: [{key, value}, …].</summary>
    public static JsonArray Fields(params (string Key, string Value)[] fields) =>
        [.. fields.Select(f => (JsonNode)new JsonObject { ["key"] = f.Key, ["value"] = f.Value })];

    /// <summary>Escribe en el registro de LiveStream (<c>[plugin:&lt;id&gt;]</c>).</summary>
    public Task LogAsync(string message, string level = "info") =>
        _peer.NotifyAsync(PluginProtocol.Log, new JsonObject { ["level"] = level, ["message"] = message }, CancellationToken.None);

    private Task<JsonNode?> HandleRequestAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        switch (method)
        {
            case PluginProtocol.Initialize:
                ApplySettings(parameters);
                return Task.FromResult<JsonNode?>(new JsonObject());
            case PluginProtocol.Shutdown:
                _stopping.Cancel();
                return Task.FromResult<JsonNode?>(new JsonObject());
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Método desconocido: {method}");
        }
    }

    private void HandleNotification(string method, JsonNode? parameters)
    {
        if (method == PluginProtocol.SettingsChanged)
            ApplySettings(parameters);
        else if (method == PluginProtocol.Event && parameters?["name"]?.GetValue<string>() is { } name && EventReceived is { } handler)
            Dispatch(() => handler(name));
    }

    private void ApplySettings(JsonNode? parameters)
    {
        _settings = (parameters?["settings"] as JsonObject ?? [])
            .ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
        if (SettingsApplied is { } handler)
            Dispatch(handler);
    }

    private void Dispatch(Func<Task> handler) => _ = Task.Run(async () =>
    {
        try
        {
            await _handlers.WaitAsync(Stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await handler().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Un fallo de un manejador no debe tumbar el plugin: queda en stderr, que LiveStream guarda en su registro.
            Console.Error.WriteLine($"Error en el plugin: {ex.Message}");
        }
        finally
        {
            _handlers.Release();
        }
    });
}
