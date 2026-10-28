using LiveStream.Application.Abstractions;
using LiveStream.Application.Studio;

namespace LiveStream.Application.Extensions;

/// <summary>
/// Carga, ordena por dependencias, arranca, detiene y aísla las extensiones del estudio. Un fallo en una extensión
/// (al registrarse, al arrancar o repetido en sus manejadores) la deja en <see cref="ExtensionState.Faulted"/> y
/// bloquea a las que dependen de ella; el núcleo y las demás siguen funcionando.
/// </summary>
public sealed class ExtensionHost : IAsyncDisposable
{
    /// <summary>Errores de manejador tolerados dentro de <see cref="FaultWindow"/> antes de detener la extensión.</summary>
    public const int MaxFaults = 3;

    public static readonly TimeSpan FaultWindow = TimeSpan.FromMinutes(10);

    private readonly StudioSession _session;
    private readonly ISecretStore _secrets;
    private readonly IExtensionStateStore _store;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<Type, (string Owner, object Service)> _services = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _registered;

    public ExtensionHost(StudioSession session, ISecretStore secrets, IExtensionStateStore store)
    {
        _session = session;
        _secrets = secrets;
        _store = store;
        Events.HandlerFaulted += (id, ex) => _ = ReportFaultAsync(id, ex);
    }

    public StudioEventBus Events { get; } = new();

    public event EventHandler? Changed;

    public IReadOnlyList<ExtensionStatus> Statuses
    {
        get
        {
            lock (_entries)
                return [.. _entries.Select(e => new ExtensionStatus(e.Extension.Manifest, e.State, _store.IsEnabled(e.Id), e.Error))];
        }
    }

    public ExtensionState StateOf(string id) => Find(id)?.State ?? ExtensionState.Blocked;

    public void Add(IStudioExtension extension)
    {
        if (_registered)
            throw new InvalidOperationException("Las extensiones se añaden antes de registrarlas.");
        if (Find(extension.Manifest.Id) is not null)
            throw new InvalidOperationException($"Extensión duplicada: {extension.Manifest.Id}.");
        lock (_entries)
            _entries.Add(new Entry(extension));
    }

    /// <summary>Servicio publicado por una extensión registrada, o null si no existe (extensión ausente o fallida).</summary>
    public T? Get<T>() where T : class
    {
        lock (_services)
            return _services.TryGetValue(typeof(T), out var s) ? (T)s.Service : null;
    }

    /// <summary>Registra (si falta) y arranca, en orden de dependencias, las activas cuyas dependencias funcionan.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Register();
            foreach (var entry in Ordered())
                await TryStartAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        OnChanged();
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var entry = Find(id) ?? throw new KeyNotFoundException($"No existe la extensión {id}.");
        if (!enabled && !entry.Extension.Manifest.CanDisable)
            throw new InvalidOperationException($"«{entry.Extension.Manifest.Name}» no se puede desactivar.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _store.SetEnabled(id, enabled);
            if (enabled)
            {
                lock (entry.Faults)
                    entry.Faults.Clear();
                if (!entry.Registered)
                    return; // falló al registrarse: hace falta reiniciar la app
                foreach (var e in Ordered())
                    if (e.State is not ExtensionState.Running)
                        await TryStartAsync(e, ct).ConfigureAwait(false);
            }
            else
            {
                await StopWithDependentsAsync(entry, ExtensionState.Disabled, null, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
        OnChanged();
    }

    /// <summary>Atribuye un error a una extensión; al repetirse, se detiene para proteger al resto.</summary>
    public async Task ReportFaultAsync(string id, Exception ex)
    {
        if (Find(id) is not { } entry)
            return;
        var now = DateTime.UtcNow;
        lock (entry.Faults)
        {
            entry.Faults.Add(now);
            entry.Faults.RemoveAll(t => now - t > FaultWindow);
            if (entry.Faults.Count < MaxFaults)
                return;
            entry.Faults.Clear();
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.State is ExtensionState.Running)
                await StopWithDependentsAsync(entry, ExtensionState.Faulted, $"Detenida tras {MaxFaults} errores: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        OnChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var entry in Ordered().Reverse())
            {
                if (entry.State is ExtensionState.Running)
                    await TryStopAsync(entry, ExtensionState.Registered, null, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await entry.Extension.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"ext:{entry.Id}", "Liberar la extensión", ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Interno ----------------------------------------------------------------------------------------

    /// <summary>Crea y publica los servicios de todas (sin arrancarlas); idempotente. La UI puede resolverlos después.</summary>
    public void Register()
    {
        if (_registered)
            return;
        _registered = true;
        foreach (var entry in Ordered())
        {
            if (entry.State is ExtensionState.Faulted or ExtensionState.Blocked)
                continue; // ciclo o dependencia ausente, ya marcado por Ordered
            if (Dependencies(entry).FirstOrDefault(d => !d.Registered) is { } missing)
            {
                Mark(entry, ExtensionState.Blocked, $"Su dependencia «{missing.Id}» no se pudo cargar.");
                continue;
            }
            try
            {
                entry.Extension.Register(new Context(this, entry.Extension.Manifest), new Services(this, entry.Extension.Manifest));
                entry.Registered = true;
                Mark(entry, ExtensionState.Registered, null);
            }
            catch (Exception ex)
            {
                AppLog.Error($"ext:{entry.Id}", "Registrar la extensión", ex);
                Mark(entry, ExtensionState.Faulted, ex.Message);
            }
        }
    }

    private async Task TryStartAsync(Entry entry, CancellationToken ct)
    {
        if (!entry.Registered || entry.State is ExtensionState.Running)
            return;
        if (!_store.IsEnabled(entry.Id))
        {
            Mark(entry, ExtensionState.Disabled, null);
            return;
        }
        if (Dependencies(entry).FirstOrDefault(d => d.State is not ExtensionState.Running) is { } blocker)
        {
            Mark(entry, ExtensionState.Blocked, $"Necesita «{blocker.Extension.Manifest.Name}» activa.");
            return;
        }
        try
        {
            await entry.Extension.StartAsync(ct).ConfigureAwait(false);
            Mark(entry, ExtensionState.Running, null);
            AppLog.Info($"ext:{entry.Id}", $"Activa ({entry.Extension.Manifest.Version})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            AppLog.Error($"ext:{entry.Id}", "Arrancar la extensión", ex);
            Mark(entry, ExtensionState.Faulted, ex.Message);
            // Deshace lo que llegara a arrancar antes de fallar.
            await TryStopAsync(entry, ExtensionState.Faulted, ex.Message, ct).ConfigureAwait(false);
        }
    }

    private async Task StopWithDependentsAsync(Entry entry, ExtensionState state, string? error, CancellationToken ct)
    {
        // Primero las que dependen de esta (en orden inverso), luego ella.
        foreach (var dependent in Ordered().Reverse().Where(e => e != entry && DependsOn(e, entry.Id)))
            if (dependent.State is ExtensionState.Running)
                await TryStopAsync(dependent, ExtensionState.Blocked, $"Necesita «{entry.Extension.Manifest.Name}» activa.", ct).ConfigureAwait(false);
        await TryStopAsync(entry, state, error, ct).ConfigureAwait(false);
    }

    private static async Task TryStopAsync(Entry entry, ExtensionState state, string? error, CancellationToken ct)
    {
        try
        {
            await entry.Extension.StopAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error($"ext:{entry.Id}", "Detener la extensión", ex);
            error ??= ex.Message;
            state = ExtensionState.Faulted;
        }
        Mark(entry, state, error);
    }

    private static void Mark(Entry entry, ExtensionState state, string? error)
    {
        entry.State = state;
        entry.Error = error;
    }

    /// <summary>Orden topológico estable; marca los ciclos y las dependencias que no existen.</summary>
    private IReadOnlyList<Entry> Ordered()
    {
        List<Entry> entries;
        lock (_entries)
            entries = [.. _entries];
        var result = new List<Entry>();
        var visiting = new HashSet<string>();
        var done = new HashSet<string>();

        void Visit(Entry e)
        {
            if (done.Contains(e.Id))
                return;
            if (!visiting.Add(e.Id))
            {
                Mark(e, ExtensionState.Faulted, "Dependencia circular.");
                return;
            }
            foreach (var depId in e.Extension.Manifest.Dependencies)
            {
                if (entries.FirstOrDefault(x => x.Id == depId) is { } dep)
                    Visit(dep);
                else if (!e.Registered)
                    Mark(e, ExtensionState.Blocked, $"Falta la extensión «{depId}».");
            }
            visiting.Remove(e.Id);
            done.Add(e.Id);
            result.Add(e);
        }

        foreach (var e in entries)
            Visit(e);
        return result;
    }

    private IEnumerable<Entry> Dependencies(Entry entry) =>
        entry.Extension.Manifest.Dependencies.Select(Find).Where(e => e is not null)!;

    private bool DependsOn(Entry entry, string id, HashSet<string>? seen = null)
    {
        seen ??= [];
        return seen.Add(entry.Id) &&
               (entry.Extension.Manifest.Dependencies.Contains(id) || Dependencies(entry).Any(d => DependsOn(d, id, seen)));
    }

    private Entry? Find(string id)
    {
        lock (_entries)
            return _entries.FirstOrDefault(e => e.Id == id);
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Entry(IStudioExtension extension)
    {
        public IStudioExtension Extension { get; } = extension;
        public string Id => Extension.Manifest.Id;
        public ExtensionState State { get; set; } = ExtensionState.Registered;
        public string? Error { get; set; }
        public bool Registered { get; set; }
        public List<DateTime> Faults { get; } = [];
    }

    private sealed class Context(ExtensionHost host, ExtensionManifest manifest) : IExtensionContext
    {
        public ExtensionManifest Manifest => manifest;

        public StudioSession Session => Demand(ExtensionPermissions.Studio, host._session);

        public ISecretStore Secrets => Demand(ExtensionPermissions.Secrets, host._secrets);

        public StudioEventBus Events => host.Events;

        public IReadOnlyDictionary<string, string> Settings => host._store.LoadSettings(manifest.Id);

        public void SaveSettings(IReadOnlyDictionary<string, string> values) => host._store.SaveSettings(manifest.Id, values);

        public void Log(string level, string message, Exception? ex = null)
        {
            var category = $"ext:{manifest.Id}";
            switch (level)
            {
                case "error":
                    AppLog.Error(category, message, ex);
                    break;
                case "warn":
                    AppLog.Warn(category, ex is null ? message : $"{message}: {ex.Message}");
                    break;
                default:
                    AppLog.Info(category, message);
                    break;
            }
        }

        private T Demand<T>(string permission, T value) =>
            manifest.Permissions.Contains(permission) ? value : throw new ExtensionPermissionException(manifest.Id, permission);
    }

    private sealed class Services(ExtensionHost host, ExtensionManifest manifest) : IExtensionServices
    {
        public void Publish<T>(T service) where T : class
        {
            lock (host._services)
            {
                if (host._services.TryGetValue(typeof(T), out var existing))
                    throw new InvalidOperationException($"{typeof(T).Name} ya lo publica «{existing.Owner}».");
                host._services[typeof(T)] = (manifest.Id, service);
            }
        }

        public T Require<T>() where T : class
        {
            lock (host._services)
            {
                if (!host._services.TryGetValue(typeof(T), out var s))
                    throw new InvalidOperationException($"Nadie publica {typeof(T).Name}.");
                // Solo se consume lo que publica una dependencia declarada: el contrato queda explícito en el manifiesto.
                if (s.Owner != manifest.Id && !manifest.Dependencies.Contains(s.Owner))
                    throw new InvalidOperationException($"«{manifest.Id}» usa {typeof(T).Name} de «{s.Owner}» sin declararla como dependencia.");
                return (T)s.Service;
            }
        }
    }
}
