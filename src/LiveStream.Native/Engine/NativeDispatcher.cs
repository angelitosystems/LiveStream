using System.Collections.Concurrent;

namespace LiveStream.Native.Engine;

/// <summary>
/// Hilo dedicado por el que pasan TODAS las llamadas a libobs. Garantiza ejecución en serie
/// y una afinidad de hilo estable (libobs crea el contexto gráfico en el hilo que llama obs_reset_video).
/// </summary>
internal sealed class NativeDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public NativeDispatcher(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        // libobs inicializa COM en modo STA (como el hilo principal de OBS); en MTA obs_startup
        // falla con RPC_E_CHANGED_MODE.
        if (OperatingSystem.IsWindows())
            _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, CancellationToken.None);
        return tcs.Task;
    }

    public Task InvokeAsync(Action action, CancellationToken ct = default) =>
        InvokeAsync<object?>(() => { action(); return null; }, ct);

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
            work();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsCurrentThread)
            _thread.Join(TimeSpan.FromSeconds(15));
        _queue.Dispose();
    }
}
