using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using StreamPilot.Application.Commands;
using StreamPilot.Domain.Animation;
using StreamPilot.Domain.Graphics;

namespace StreamPilot.Application.Graphics;

/// <summary>
/// Operación en directo de los gráficos: lanzar al aire y retirar con animación, salida automática,
/// cuentas atrás, reloj y datos en vivo desde archivo. Lo que se emite no entra en el historial de deshacer:
/// lo que el operador edita (campos, estilo) sí, mediante comandos.
/// </summary>
public sealed class GraphicsService : IAsyncDisposable
{
    private readonly CommandExecutor _executor;
    private readonly ConcurrentDictionary<Guid, DateTime?> _onAir = new(); // valor: salida automática
    private readonly ConcurrentDictionary<Guid, CountdownState> _countdowns = new();
    private readonly ConcurrentDictionary<(Guid, string), string> _pushedText = new();
    private readonly ConcurrentDictionary<Guid, DataBinding> _bindings = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public GraphicsService(CommandExecutor executor)
    {
        _executor = executor;
        Context.GraphicText = LiveText;
        executor.StateChanged += (_, _) => Reconcile();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private StudioContext Context => _executor.Context;

    /// <summary>Cambió qué gráficos están al aire o el estado de una cuenta atrás. Se invoca desde hilos de fondo.</summary>
    public event EventHandler? Changed;

    /// <summary>Error en una operación de fondo (archivo de datos, reloj). Se invoca desde hilos de fondo.</summary>
    public event EventHandler<string>? Error;

    public bool IsOnAir(Guid graphicId) => _onAir.ContainsKey(graphicId);

    public async Task TakeInAsync(Guid graphicId, CancellationToken ct = default)
    {
        var graphic = Context.Project.GetGraphic(graphicId);
        if (!Context.Engine.IsInitialized)
            throw new InvalidOperationException("El motor no está iniciado.");
        // El texto vivo se actualiza antes de entrar para que no se vea un valor viejo durante la animación.
        await PushLiveTextAsync(graphic, force: true, ct).ConfigureAwait(false);
        await Context.Engine.PlayAnimationAsync(GraphicLayer.OverlayItemId(graphicId),
            GraphicAnimations.In(graphic, Context.Project.Canvas), AnimationFlags.ShowAtStart, ct).ConfigureAwait(false);
        _onAir[graphicId] = graphic.AutoHideSeconds > 0
            ? DateTime.UtcNow.AddSeconds(graphic.AnimationMs / 1000.0 + graphic.AutoHideSeconds)
            : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task TakeOutAsync(Guid graphicId, CancellationToken ct = default)
    {
        if (!_onAir.TryRemove(graphicId, out _))
            return;
        Changed?.Invoke(this, EventArgs.Empty);
        if (Context.Project.FindGraphic(graphicId) is not { } graphic || !Context.Engine.IsInitialized)
            return;
        await Context.Engine.PlayAnimationAsync(GraphicLayer.OverlayItemId(graphicId),
            GraphicAnimations.Out(graphic, Context.Project.Canvas), AnimationFlags.HideAtEnd, ct).ConfigureAwait(false);
    }

    public Task ToggleAsync(Guid graphicId, CancellationToken ct = default) =>
        IsOnAir(graphicId) ? TakeOutAsync(graphicId, ct) : TakeInAsync(graphicId, ct);

    /// <summary>Retira todos los gráficos al aire (tecla de pánico del operador).</summary>
    public async Task TakeAllOutAsync(CancellationToken ct = default)
    {
        foreach (var id in _onAir.Keys.ToList())
            await TakeOutAsync(id, ct).ConfigureAwait(false);
    }

    // ---- Cuenta atrás -------------------------------------------------------------------------

    public sealed record CountdownStatus(TimeSpan Remaining, bool Running);

    public CountdownStatus GetCountdown(Guid graphicId)
    {
        var state = CountdownFor(graphicId);
        lock (state)
            return new CountdownStatus(state.Remaining, state.Clock.IsRunning);
    }

    public void StartCountdown(Guid graphicId) => WithCountdown(graphicId, s =>
    {
        if (s.Remaining > TimeSpan.Zero)
            s.Clock.Start();
    });

    public void PauseCountdown(Guid graphicId) => WithCountdown(graphicId, s =>
    {
        s.Offset = s.Remaining;
        s.Clock.Reset();
    });

    public void ResetCountdown(Guid graphicId) => WithCountdown(graphicId, s =>
    {
        s.Clock.Reset();
        s.Offset = s.Duration = DurationOf(graphicId);
    });

    private void WithCountdown(Guid graphicId, Action<CountdownState> action)
    {
        var state = CountdownFor(graphicId);
        lock (state)
            action(state);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private CountdownState CountdownFor(Guid graphicId) =>
        _countdowns.GetOrAdd(graphicId, id =>
        {
            var duration = DurationOf(id);
            return new CountdownState { Offset = duration, Duration = duration };
        });

    private TimeSpan DurationOf(Guid graphicId) =>
        Context.Project.FindGraphic(graphicId) is { } g ? GraphicTemplates.ParseDuration(g.Field("duration")) ?? TimeSpan.Zero : TimeSpan.Zero;

    private sealed class CountdownState
    {
        public Stopwatch Clock { get; } = new();
        public TimeSpan Offset { get; set; }
        /// <summary>Duración configurada cuando se preparó: si el operador la cambia en reposo, se recarga.</summary>
        public TimeSpan Duration { get; set; }
        public TimeSpan Remaining => Offset - Clock.Elapsed > TimeSpan.Zero ? Offset - Clock.Elapsed : TimeSpan.Zero;
    }

    // ---- Texto vivo ---------------------------------------------------------------------------

    private string? LiveText(Graphic graphic, string key)
    {
        switch (graphic.Kind)
        {
            case GraphicKind.Countdown when key == "time":
                var remaining = GetCountdown(graphic.Id).Remaining;
                return remaining <= TimeSpan.Zero ? graphic.Field("finished") : GraphicTemplates.FormatRemaining(remaining);
            case GraphicKind.Clock when key == "time":
                return DateTime.Now.ToString(GraphicTemplates.SafeFormat(graphic.Field("format")));
            default:
                return null;
        }
    }

    private async Task PushLiveTextAsync(Graphic graphic, bool force, CancellationToken ct)
    {
        if (graphic.Kind is not (GraphicKind.Countdown or GraphicKind.Clock))
            return;
        var text = LiveText(graphic, "time") ?? "";
        var key = (graphic.Id, "time");
        if (!force && _pushedText.TryGetValue(key, out var last) && last == text)
            return;
        await GraphicLayer.UpdateTextAsync(Context.Engine, Context.Project, graphic, "time", text, ct).ConfigureAwait(false);
        _pushedText[key] = text;
    }

    // ---- Bucle --------------------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                if (!Context.Engine.IsInitialized)
                    continue;
                foreach (var graphic in Snapshot())
                {
                    await PushLiveTextAsync(graphic, force: false, ct).ConfigureAwait(false);
                    if (graphic.Kind == GraphicKind.Countdown)
                        StopFinishedCountdown(graphic.Id);
                }
                foreach (var (id, deadline) in _onAir)
                    if (deadline is { } d && DateTime.UtcNow >= d)
                        await TakeOutAsync(id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex.Message);
            }
        }
    }

    private void StopFinishedCountdown(Guid graphicId)
    {
        if (!_countdowns.TryGetValue(graphicId, out var state))
            return;
        bool changed;
        lock (state)
        {
            changed = state.Clock.IsRunning && state.Remaining <= TimeSpan.Zero;
            if (changed)
            {
                state.Clock.Reset();
                state.Offset = TimeSpan.Zero;
            }
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copia de la lista de gráficos; si un comando la está modificando se reintenta en el siguiente tic.</summary>
    private IReadOnlyList<Graphic> Snapshot()
    {
        try
        {
            return Context.Project.Graphics.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Tras cada comando: olvida gráficos eliminados y ajusta los enlaces a archivos de datos.</summary>
    private void Reconcile()
    {
        var graphics = Snapshot().ToDictionary(g => g.Id);
        if (graphics.Count == 0 && Context.Project.Graphics.Count > 0)
            return;
        var removed = false;
        foreach (var id in _onAir.Keys)
            removed |= !graphics.ContainsKey(id) && _onAir.TryRemove(id, out _);
        foreach (var id in _countdowns.Keys.Where(id => !graphics.ContainsKey(id)))
            _countdowns.TryRemove(id, out _);
        foreach (var (id, state) in _countdowns)
        {
            var duration = DurationOf(id);
            lock (state)
            {
                if (!state.Clock.IsRunning && state.Duration != duration && state.Offset == state.Duration)
                    state.Offset = state.Duration = duration;
            }
        }
        foreach (var key in _pushedText.Keys.Where(k => !graphics.ContainsKey(k.Item1)))
            _pushedText.TryRemove(key, out _);

        foreach (var (id, binding) in _bindings)
        {
            if (!graphics.TryGetValue(id, out var g) || !string.Equals(g.DataFile, binding.Path, StringComparison.OrdinalIgnoreCase))
            {
                binding.Dispose();
                _bindings.TryRemove(id, out _);
            }
        }
        foreach (var g in graphics.Values)
            if (g.DataFile is { Length: > 0 } path && !_bindings.ContainsKey(g.Id))
                _bindings[g.Id] = new DataBinding(this, g.Id, path);

        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Lee ya los archivos de datos enlazados (se llama al abrir el proyecto).</summary>
    public void Start() => Reconcile();

    // ---- Datos en vivo desde archivo ----------------------------------------------------------

    /// <summary>
    /// Vigila un archivo JSON ({"campo": "valor", ...}) y aplica sus valores al gráfico cuando cambia. Así un
    /// programa externo (marcador, sistema de redacción) actualiza el rótulo en directo.
    /// </summary>
    private sealed class DataBinding : IDisposable
    {
        private readonly GraphicsService _owner;
        private readonly Guid _graphicId;
        private readonly FileSystemWatcher? _watcher;
        private readonly Timer _debounce;
        private string? _lastContent;

        public DataBinding(GraphicsService owner, Guid graphicId, string path)
        {
            _owner = owner;
            _graphicId = graphicId;
            Path = path;
            _debounce = new Timer(_ => _ = ApplyAsync(), null, Timeout.Infinite, Timeout.Infinite);
            var directory = System.IO.Path.GetDirectoryName(path);
            if (directory is null || !Directory.Exists(directory))
            {
                owner.Error?.Invoke(owner, $"No existe la carpeta del archivo de datos: {path}");
                return;
            }
            _watcher = new FileSystemWatcher(directory, System.IO.Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += (_, _) => Schedule();
            _watcher.Created += (_, _) => Schedule();
            _watcher.Renamed += (_, _) => Schedule();
            _watcher.EnableRaisingEvents = true;
            Schedule();
        }

        public string Path { get; }

        // Los editores escriben en varias pasadas: se espera a que el archivo se quede quieto.
        private void Schedule() => _debounce.Change(150, Timeout.Infinite);

        private async Task ApplyAsync()
        {
            try
            {
                if (!File.Exists(Path))
                    return;
                string content;
                await using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    content = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (content == _lastContent)
                    return;
                _lastContent = content;

                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("El archivo de datos debe contener un objeto JSON.");
                var values = new Dictionary<string, string>();
                foreach (var property in doc.RootElement.EnumerateObject())
                    values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();

                var result = await _owner._executor.ExecuteAsync(new ApplyGraphicDataCommand(_graphicId, values)).ConfigureAwait(false);
                if (!result.Success)
                    _owner.Error?.Invoke(_owner, $"Datos de {System.IO.Path.GetFileName(Path)}: {result.Error}");
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                _owner.Error?.Invoke(_owner, $"Datos de {System.IO.Path.GetFileName(Path)}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _debounce.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        foreach (var binding in _bindings.Values)
            binding.Dispose();
        _bindings.Clear();
        if (Context.GraphicText == LiveText)
            Context.GraphicText = null;
        _cts.Dispose();
    }
}
