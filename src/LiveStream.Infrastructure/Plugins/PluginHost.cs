using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveStream.Application.Assistant;
using LiveStream.Infrastructure.Diagnostics;
using LiveStream.PluginSdk;

namespace LiveStream.Infrastructure.Plugins;

public enum PluginRunState { Stopped, Starting, Running, Failed }

/// <summary>
/// Ejecuta un plugin en su propio proceso y le da acceso a la API de LiveStream (las herramientas tipadas del
/// estudio) según los permisos aceptados. Si el plugin se cuelga o se cierra, LiveStream sigue funcionando: se
/// reinicia con espera y, tras varios fallos seguidos, queda detenido con el error visible.
/// </summary>
public sealed class PluginHost : IAsyncDisposable
{
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);
    private const int MaxCrashes = 3;
    private const int MaxConcurrentCalls = 4;

    private readonly InstalledPlugin _plugin;
    private readonly AssistantToolbox _toolbox;
    private readonly Func<IReadOnlyDictionary<string, string>> _settings;
    private readonly string _dataDirectory;
    private readonly HashSet<string> _granted;
    private readonly SemaphoreSlim _calls = new(MaxConcurrentCalls, MaxConcurrentCalls);
    private readonly Queue<DateTime> _crashes = new();
    private readonly object _lock = new();
    private Process? _process;
    private JsonRpcPeer? _peer;
    private CancellationTokenSource? _cts;
    private bool _stopping;

    public PluginHost(InstalledPlugin plugin, AssistantToolbox toolbox, Func<IReadOnlyDictionary<string, string>> settings, string dataDirectory)
    {
        _plugin = plugin;
        _toolbox = toolbox;
        _settings = settings;
        _dataDirectory = dataDirectory;
        _granted = plugin.Manifest.PermissionList.ToHashSet();
    }

    public string Id => _plugin.Id;
    public PluginRunState State { get; private set; } = PluginRunState.Stopped;
    public string? LastError { get; private set; }
    /// <summary>Proceso del plugin (diagnóstico); null si no está en marcha.</summary>
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;
    public event EventHandler? StateChanged;

    public static string PermissionFor(ToolPermission permission) => permission switch
    {
        ToolPermission.Read => PluginPermissions.StudioRead,
        ToolPermission.Modify => PluginPermissions.StudioModify,
        _ => PluginPermissions.StudioLive,
    };

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (State is PluginRunState.Starting or PluginRunState.Running)
                return;
            _stopping = false;
        }
        SetState(PluginRunState.Starting, null);
        try
        {
            await LaunchAsync(ct).ConfigureAwait(false);
            SetState(PluginRunState.Running, null);
            Log("info", "iniciado");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await KillAsync().ConfigureAwait(false);
            SetState(PluginRunState.Failed, $"No se pudo iniciar: {ex.Message}");
            Log("error", LastError!);
        }
    }

    public async Task StopAsync()
    {
        lock (_lock)
            _stopping = true;
        if (_peer is { } peer && _process is { HasExited: false })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await peer.RequestAsync(PluginProtocol.Shutdown, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonRpcException or ObjectDisposedException)
            {
                // No respondió a tiempo: se cierra igualmente.
            }
        }
        await KillAsync().ConfigureAwait(false);
        SetState(PluginRunState.Stopped, null);
    }

    public Task NotifyAsync(string method, JsonNode? parameters)
    {
        if (State != PluginRunState.Running || _peer is not { } peer)
            return Task.CompletedTask;
        return SafeAsync(() => peer.NotifyAsync(method, parameters, _cts?.Token ?? CancellationToken.None));
    }

    private async Task LaunchAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_dataDirectory);
        var psi = new ProcessStartInfo(_plugin.EntryPath)
        {
            WorkingDirectory = _dataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        // El plugin no hereda secretos de LiveStream.
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment["LIVESTREAM_PLUGIN_ID"] = _plugin.Id;
        psi.Environment["LIVESTREAM_PLUGIN_DATA"] = _dataDirectory;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Windows no inició el proceso.");
        try
        {
            PluginJob.Assign(process);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        _cts = new CancellationTokenSource();
        var peer = new JsonRpcPeer(process.StandardOutput, process.StandardInput) { OnRequest = HandleAsync, OnNotification = HandleNotification };
        _process = process;
        _peer = peer;
        var token = _cts.Token;
        _ = Task.Run(() => PumpAsync(process, peer, token), CancellationToken.None);
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                Log("stderr", line.Length > 500 ? line[..500] : line);
        }, CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InitializeTimeout);
        try
        {
            await peer.RequestAsync(PluginProtocol.Initialize, new JsonObject
            {
                ["apiVersion"] = _plugin.Manifest.ApiVersion,
                ["appVersion"] = Updates.UpdateClient.CurrentVersion.ToString(3),
                ["pluginId"] = _plugin.Id,
                ["permissions"] = new JsonArray([.. _granted.Select(p => (JsonNode)p)]),
                ["settings"] = SettingsNode(),
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("el plugin no respondió a initialize en 10 s.");
        }
    }

    public JsonObject SettingsNode() => new([.. _settings().Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))]);

    private async Task PumpAsync(Process process, JsonRpcPeer peer, CancellationToken ct)
    {
        string? error = null;
        try
        {
            await peer.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }
        bool stopping;
        lock (_lock)
            stopping = _stopping || ct.IsCancellationRequested;
        if (stopping)
            return;

        // Salida inesperada (cierre, cuelgue de la tubería o protocolo roto).
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var reason = error ?? $"el proceso terminó (código {process.ExitCode})";
        Log("error", $"se cerró inesperadamente: {reason}");

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            _crashes.Enqueue(now);
            while (_crashes.Count > 0 && now - _crashes.Peek() > CrashWindow)
                _crashes.Dequeue();
        }
        if (_crashes.Count >= MaxCrashes)
        {
            SetState(PluginRunState.Failed, $"Se cerró {MaxCrashes} veces en {CrashWindow.TotalMinutes:0} minutos ({reason}). Queda detenido; reactívalo cuando esté corregido.");
            return;
        }
        SetState(PluginRunState.Starting, $"Reiniciando tras un cierre ({reason})…");
        await Task.Delay(TimeSpan.FromSeconds(2 * _crashes.Count), CancellationToken.None).ConfigureAwait(false);
        lock (_lock)
            if (_stopping)
                return;
        State = PluginRunState.Stopped;
        await StartAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task<JsonNode?> HandleAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        switch (method)
        {
            case PluginProtocol.ToolsList:
                return new JsonObject
                {
                    ["tools"] = new JsonArray([.. _toolbox.Tools.Where(Allowed).Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.InputSchema.DeepClone(),
                        ["permission"] = PermissionFor(t.Permission),
                    })]),
                };

            case PluginProtocol.ToolsCall:
            {
                var name = parameters?["name"]?.GetValue<string>() ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "Falta name.");
                var tool = _toolbox.Tools.FirstOrDefault(t => t.Name == name)
                    ?? throw new JsonRpcException(JsonRpcException.MethodNotFound, $"No existe la herramienta {name}.");
                if (!Allowed(tool))
                    throw new JsonRpcException(JsonRpcException.PermissionDenied, $"El plugin no tiene el permiso {PermissionFor(tool.Permission)}.");
                var arguments = JsonSerializer.SerializeToElement(parameters?["arguments"] ?? new JsonObject());
                await _calls.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await _toolbox.ExecuteAsync(name, arguments, ct).ConfigureAwait(false);
                    Log(result.IsError ? "warn" : "info", $"{name}: {result.Summary}");
                    return new JsonObject { ["content"] = result.Content, ["isError"] = result.IsError, ["summary"] = result.Summary };
                }
                finally
                {
                    _calls.Release();
                }
            }

            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Método desconocido: {method}");
        }
    }

    private bool Allowed(AssistantTool tool) => _granted.Contains(PermissionFor(tool.Permission));

    private void HandleNotification(string method, JsonNode? parameters)
    {
        if (method == PluginProtocol.Log)
        {
            var message = parameters?["message"]?.GetValue<string>() ?? "";
            Log(parameters?["level"]?.GetValue<string>() ?? "info", message.Length > 2000 ? message[..2000] : message);
        }
    }

    private void Log(string level, string message)
    {
        var category = $"plugin:{_plugin.Id}";
        switch (level)
        {
            case "error":
                AppLog.Error(category, message);
                break;
            case "warn":
                AppLog.Warn(category, message);
                break;
            default:
                AppLog.Info(category, message);
                break;
        }
    }

    private void SetState(PluginRunState state, string? error)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task KillAsync()
    {
        _cts?.Cancel();
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Close();
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
            }
            process.Dispose();
        }
        _process = null;
        if (_peer is { } peer)
            await peer.DisposeAsync().ConfigureAwait(false);
        _peer = null;
    }

    private static async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // El plugin se está cerrando; el bucle de lectura gestiona el reinicio.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _calls.Dispose();
    }
}
