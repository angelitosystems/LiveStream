namespace LiveStream.Application.Extensions;

/// <summary>
/// Base opcional: guarda el contexto y deshace en <see cref="StopAsync"/> todo lo registrado con <see cref="Track"/>
/// (suscripciones al bus o a eventos de servicios), así una extensión desactivada no deja manejadores colgados.
/// </summary>
public abstract class StudioExtensionBase : IStudioExtension
{
    private readonly List<IDisposable> _running = [];
    private IExtensionContext? _context;

    public abstract ExtensionManifest Manifest { get; }

    protected IExtensionContext Context => _context ?? throw new InvalidOperationException("La extensión aún no se registró.");

    public void Register(IExtensionContext context, IExtensionServices services)
    {
        _context = context;
        OnRegister(services);
    }

    public Task StartAsync(CancellationToken ct) => OnStartAsync(ct);

    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            await OnStopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            IDisposable[] running;
            lock (_running)
            {
                running = [.. _running];
                _running.Clear();
            }
            foreach (var item in running.Reverse())
                item.Dispose();
        }
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected abstract void OnRegister(IExtensionServices services);

    protected virtual Task OnStartAsync(CancellationToken ct) => Task.CompletedTask;

    protected virtual Task OnStopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Se deshace al detener la extensión.</summary>
    protected void Track(IDisposable running)
    {
        lock (_running)
            _running.Add(running);
    }

    /// <summary>Suscripción a un evento .NET que se quita al detener la extensión.</summary>
    protected void Track(Action subscribe, Action unsubscribe)
    {
        subscribe();
        Track(new Undo(unsubscribe));
    }

    protected void On<T>(Action<T> handler) where T : class => Track(Context.Events.Subscribe(Manifest.Id, handler));

    protected static ExtensionManifest Describe(string id, string name, string description, string[]? dependencies = null,
        params string[] permissions) =>
        new(id, name, System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0), description,
            dependencies ?? [], permissions);

    private sealed class Undo(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
