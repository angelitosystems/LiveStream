using System.Collections.Concurrent;

namespace LiveStream.Application.Extensions;

/// <summary>
/// Bus de eventos tipado entre el núcleo y las extensiones. Se entrega en el hilo de quien publica (el suscriptor
/// lleva al hilo de UI lo que lo necesite). Un manejador que lanza no afecta a los demás: se registra y se le
/// atribuye el fallo a su extensión, que el host puede detener si se repite.
/// </summary>
public sealed class StudioEventBus
{
    private readonly ConcurrentDictionary<Type, Subscription[]> _subscriptions = new();
    private readonly object _writeLock = new();

    /// <summary>Un manejador de la extensión indicada lanzó una excepción.</summary>
    public event Action<string, Exception>? HandlerFaulted;

    public IDisposable Subscribe<T>(string ownerId, Action<T> handler) where T : class
    {
        var subscription = new Subscription(ownerId, e => handler((T)e));
        lock (_writeLock)
            _subscriptions[typeof(T)] = [.. _subscriptions.GetValueOrDefault(typeof(T), []), subscription];
        return new Unsubscriber(() =>
        {
            lock (_writeLock)
                _subscriptions[typeof(T)] = [.. _subscriptions.GetValueOrDefault(typeof(T), []).Where(s => s != subscription)];
        });
    }

    public void Publish<T>(T message) where T : class
    {
        // Copia inmutable: publicar mientras alguien se suscribe o se va no necesita bloqueo.
        foreach (var subscription in _subscriptions.GetValueOrDefault(typeof(T), []))
        {
            try
            {
                subscription.Handler(message);
            }
            catch (Exception ex)
            {
                AppLog.Error($"ext:{subscription.OwnerId}", $"Manejador de {typeof(T).Name}", ex);
                HandlerFaulted?.Invoke(subscription.OwnerId, ex);
            }
        }
    }

    public int SubscriberCount<T>() => _subscriptions.GetValueOrDefault(typeof(T), []).Length;

    private sealed record Subscription(string OwnerId, Action<object> Handler);

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
