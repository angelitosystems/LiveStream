using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveStream.PluginSdk;

/// <summary>
/// Métodos del protocolo de plugins v1 (JSON-RPC 2.0, un mensaje JSON por línea en stdin/stdout del plugin).
/// stderr del plugin se guarda en el registro de LiveStream.
/// </summary>
public static class PluginProtocol
{
    // LiveStream → plugin
    /// <summary>Petición. params: apiVersion, appVersion, pluginId, permissions, settings. El plugin responde {} al estar listo.</summary>
    public const string Initialize = "initialize";
    /// <summary>Petición. El plugin libera recursos y responde; después se cierra su stdin y debe salir.</summary>
    public const string Shutdown = "shutdown";
    /// <summary>Notificación. params: name (p. ej. «studio.changed»).</summary>
    public const string Event = "event";
    /// <summary>Notificación. params: settings (objeto clave → texto).</summary>
    public const string SettingsChanged = "settings/changed";

    // plugin → LiveStream
    /// <summary>Petición. Devuelve {tools:[{name, description, inputSchema, permission}]} solo con las permitidas.</summary>
    public const string ToolsList = "tools/list";
    /// <summary>Petición. params: name, arguments. Devuelve {content, isError, summary}.</summary>
    public const string ToolsCall = "tools/call";
    /// <summary>Notificación. params: level (info|warn|error), message.</summary>
    public const string Log = "log";

    /// <summary>Eventos que emite LiveStream.</summary>
    public const string StudioChangedEvent = "studio.changed";

    public const int MaxMessageChars = 1024 * 1024;
}

public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int PermissionDenied = -32001;
}

/// <summary>
/// Extremo JSON-RPC 2.0 simétrico sobre dos flujos de texto (una línea por mensaje). Lo usan LiveStream y los plugins:
/// cada lado puede enviar peticiones y notificaciones y atender las del otro.
/// </summary>
public sealed class JsonRpcPeer(TextReader input, TextWriter output) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _nextId;

    /// <summary>Atiende una petición entrante: devuelve el resultado o lanza <see cref="JsonRpcException"/>.</summary>
    public Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? OnRequest { get; set; }

    public Action<string, JsonNode?>? OnNotification { get; set; }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, ct).ConfigureAwait(false);
            await using (ct.Register(() => tcs.TrySetCanceled(ct)))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, ct);

    /// <summary>Lee mensajes hasta que se cierra la entrada. Las peticiones se atienden en paralelo a la lectura.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await input.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;
                // shortcut: la línea se lee entera antes de medirla; si un plugin abusa, leer con límite por bloques.
                if (line.Length > PluginProtocol.MaxMessageChars)
                    throw new InvalidDataException("Mensaje demasiado grande.");
                JsonObject message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject ?? throw new JsonException();
                }
                catch (JsonException)
                {
                    throw new InvalidDataException("Mensaje que no es JSON-RPC.");
                }
                Dispatch(message, ct);
            }
        }
        finally
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("La conexión se cerró."));
        }
    }

    private void Dispatch(JsonObject message, CancellationToken ct)
    {
        var method = message["method"]?.GetValue<string>();
        var idNode = message["id"];
        if (method is null)
        {
            // Respuesta a una petición nuestra.
            if (idNode is not null && _pending.TryGetValue(idNode.GetValue<long>(), out var tcs))
            {
                if (message["error"] is JsonObject error)
                    tcs.TrySetException(new JsonRpcException(error["code"]?.GetValue<int>() ?? JsonRpcException.InternalError,
                        error["message"]?.GetValue<string>() ?? "Error"));
                else
                    tcs.TrySetResult(message["result"]?.DeepClone());
            }
            return;
        }
        var parameters = message["params"]?.DeepClone();
        if (idNode is null)
        {
            OnNotification?.Invoke(method, parameters);
            return;
        }
        var id = idNode.DeepClone();
        _ = Task.Run(async () =>
        {
            JsonObject response;
            try
            {
                var handler = OnRequest ?? throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Método desconocido: {method}");
                var result = await handler(method, parameters, ct).ConfigureAwait(false);
                response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
            }
            catch (JsonRpcException ex)
            {
                response = Error(id, ex.Code, ex.Message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                response = Error(id, JsonRpcException.InternalError, ex.Message);
            }
            try
            {
                await SendAsync(response, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // El otro lado se fue: nada que responder.
            }
        }, ct);
    }

    private static JsonObject Error(JsonNode id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var line = message.ToJsonString();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
