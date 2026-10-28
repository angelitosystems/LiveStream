using LiveStream.Application.Abstractions;
using LiveStream.Application.Commands;
using LiveStream.Application.Extensions;
using LiveStream.Application.Graphics;
using LiveStream.Application.Streaming;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.Application.Studio;

/// <summary>
/// Sesión de trabajo: proyecto abierto + motor + historial + autoguardado. Es lo que consumen la UI,
/// el asistente y el optimizador.
/// </summary>
public sealed class StudioSession : IAsyncDisposable
{
    private readonly IProjectRepository _repository;
    private readonly ISecretStore _secrets;
    private readonly TimeSpan _autosaveDelay;
    private CancellationTokenSource? _autosaveCts;

    // El proyecto no es seguro entre hilos: lo que llega de conectores y temporizadores se lleva al hilo que creó la sesión (la UI).
    private readonly SynchronizationContext? _owner = SynchronizationContext.Current;

    public StudioSession(IStreamingEngine engine, IProjectRepository repository, ISecretStore secrets, StudioProject project,
        string projectPath, TimeSpan? autosaveDelay = null, IExtensionStateStore? extensionState = null)
    {
        Engine = engine;
        _repository = repository;
        _secrets = secrets;
        ProjectPath = projectPath;
        _autosaveDelay = autosaveDelay ?? TimeSpan.FromSeconds(2);
        Executor = new CommandExecutor(new StudioContext(project, engine));
        Executor.StateChanged += (_, _) => ScheduleAutosave();
        Graphics = new GraphicsService(Executor);
        Destinations = new StreamSupervisor(this);

        Extensions = new ExtensionHost(this, secrets, extensionState ?? new InMemoryExtensionStateStore());
    }

    /// <summary>Ejecuta <paramref name="action"/> en el hilo dueño del proyecto; un fallo se registra y no tumba la app.</summary>
    public void OnOwnerThread(string what, Func<Task> action)
    {
        async void Run()
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppLog.Error("session", what, ex);
            }
        }

        if (_owner is null || SynchronizationContext.Current == _owner)
            Run();
        else
            _owner.Post(_ => Run(), null);
    }

    public IStreamingEngine Engine { get; }
    public CommandExecutor Executor { get; }
    public ISecretStore Secrets => _secrets;

    /// <summary>Funcionalidades desacopladas del núcleo (alertas, chat, TTS, donaciones…): las añade la composición de la app.</summary>
    public ExtensionHost Extensions { get; }

    /// <summary>Operación en directo de los gráficos (al aire, cuentas atrás, datos en vivo).</summary>
    public GraphicsService Graphics { get; }

    public StudioProject Project => Executor.Context.Project;
    public string ProjectPath { get; private set; }

    public event EventHandler<Exception>? AutosaveFailed;
    public event EventHandler? Saved;

    /// <summary>Crea un proyecto por defecto: una escena, audio de escritorio y micrófono en canales globales (como OBS).</summary>
    public static StudioProject CreateDefaultProject(string name = "Mi estudio", CanvasSettings? canvas = null)
    {
        var project = new StudioProject(Guid.NewGuid(), name) { Canvas = canvas ?? new CanvasSettings() };
        project.AddScene("Escena");
        var desktop = new Source(Guid.NewGuid(), "Audio del escritorio", SourceKind.AudioOutput, """{"device_id":"default"}""");
        desktop.SetGlobalAudioChannel(1);
        var mic = new Source(Guid.NewGuid(), "Micrófono", SourceKind.AudioInput, """{"device_id":"default"}""");
        mic.SetGlobalAudioChannel(3);
        project.AddSource(desktop);
        project.AddSource(mic);
        return project;
    }

    /// <summary>Inicializa el motor con el lienzo del proyecto y vuelca todo el proyecto en él.</summary>
    public async Task StartEngineAsync(int adapterIndex = 0, CancellationToken ct = default)
    {
        await Engine.InitializeAsync(EngineSettings.From(Project.Canvas, adapterIndex), ct).ConfigureAwait(false);
        await EngineSync.LoadProjectAsync(Engine, Project, ct, Executor.Context.GraphicText).ConfigureAwait(false);
        Executor.Context.AppliedTransition = Project.Transition;
        Graphics.Start();
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _repository.SaveAsync(Project, ProjectPath, ct).ConfigureAwait(false);
        _repository.LastOpenedPath = ProjectPath;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsAsync(string path, CancellationToken ct = default)
    {
        ProjectPath = path;
        await SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Calcula la ruta de la próxima grabación según las preferencias del proyecto.</summary>
    public string NextRecordingPath(DateTime now)
    {
        var folder = string.IsNullOrWhiteSpace(Project.Recording.Folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LiveStream")
            : Project.Recording.Folder;
        var container = RecordingPreferences.SupportedContainers.Contains(Project.Recording.Container) ? Project.Recording.Container : "mkv";
        return Path.Combine(folder, $"{now:yyyy-MM-dd HH-mm-ss}.{container}");
    }

    public async Task<string> StartRecordingAsync(CancellationToken ct = default)
    {
        var path = NextRecordingPath(DateTime.Now);
        await Engine.StartRecordingAsync(new RecordingSettings(path, Project.Recording.EffectiveEncoder), ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Supervisión por destino (estado, reconexiones, errores).</summary>
    public StreamSupervisor Destinations { get; private set; } = null!;

    public sealed record DestinationStartResult(StreamDestination Destination, string? Error);

    public IEnumerable<StreamDestination> EnabledDestinations => Project.Destinations.Where(d => d.Enabled);

    /// <summary>Problema que impide emitir a un destino (servidor, plataforma o clave); null si está listo.</summary>
    public async Task<string?> ValidateDestinationAsync(StreamDestination d, CancellationToken ct = default)
    {
        if (PlatformCatalog.Validate(d, d.EffectiveEncoder(Project.Stream)) is { } error)
            return error;
        var key = await _secrets.GetAsync(d.CredentialName, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(key) ? $"«{d.Name}»: falta la clave de transmisión." : null;
    }

    /// <summary>Comprueba que haya algo a lo que emitir. Los destinos con problemas se describen todos juntos.</summary>
    public async Task<string?> ValidateStreamAsync(CancellationToken ct = default)
    {
        var enabled = EnabledDestinations.ToList();
        if (enabled.Count == 0)
            return Project.Destinations.Count == 0
                ? "No hay destinos de emisión. Añade uno en Emisión → Destinos."
                : "Todos los destinos están desactivados.";
        var problems = new List<string>();
        foreach (var d in enabled)
            if (await ValidateDestinationAsync(d, ct).ConfigureAwait(false) is { } p)
                problems.Add(p);
        return problems.Count == enabled.Count ? string.Join("\n", problems) : null;
    }

    /// <summary>
    /// Inicia la emisión a todos los destinos activados que estén listos. Un destino que falla no impide los demás;
    /// el resultado dice qué pasó con cada uno. Falla solo si no arranca ninguno.
    /// </summary>
    public async Task<IReadOnlyList<DestinationStartResult>> StartStreamAsync(CancellationToken ct = default)
    {
        var results = new List<DestinationStartResult>();
        foreach (var d in EnabledDestinations.ToList())
        {
            if (Destinations.IsLive(d.Id))
                continue;
            try
            {
                await StartDestinationAsync(d.Id, ct).ConfigureAwait(false);
                results.Add(new(d, null));
            }
            catch (InvalidOperationException ex)
            {
                results.Add(new(d, ex.Message));
            }
        }
        if (results.Count > 0 && results.All(r => r.Error is not null))
            throw new InvalidOperationException(string.Join("\n", results.Select(r => r.Error)));
        return results;
    }

    /// <summary>Inicia un destino concreto (aunque esté desactivado para el inicio general).</summary>
    public async Task StartDestinationAsync(Guid id, CancellationToken ct = default)
    {
        var d = Project.FindDestination(id) ?? throw new InvalidOperationException("El destino no existe.");
        if (await ValidateDestinationAsync(d, ct).ConfigureAwait(false) is { } problem)
        {
            Destinations.MarkFailedToStart(d, problem);
            throw new InvalidOperationException(problem);
        }
        var key = await _secrets.GetAsync(d.CredentialName, ct).ConfigureAwait(false);
        Destinations.MarkStarting(d.Id);
        try
        {
            await Engine.StartDestinationAsync(d.Id, new StreamTarget(d.Server.Trim(), key!, d.EffectiveEncoder(Project.Stream)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Destinations.MarkFailedToStart(d, ex.Message);
            throw new InvalidOperationException($"«{d.Name}»: {ex.Message}", ex);
        }
    }

    public async Task StopDestinationAsync(Guid id, CancellationToken ct = default)
    {
        var d = Project.FindDestination(id) ?? throw new InvalidOperationException("El destino no existe.");
        await Engine.StopDestinationAsync(id, ct).ConfigureAwait(false);
        Destinations.MarkStopped(d);
    }

    /// <summary>Detiene todos los destinos que están emitiendo o reconectando.</summary>
    public async Task StopStreamAsync(CancellationToken ct = default)
    {
        foreach (var d in Project.Destinations.ToList())
        {
            var stats = await Engine.GetDestinationStatsAsync(d.Id, ct).ConfigureAwait(false);
            if (stats is { Active: true } || Destinations.IsLive(d.Id))
            {
                try
                {
                    await StopDestinationAsync(d.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Ya se había caído: no hay nada que detener.
                    Destinations.MarkStopped(d);
                }
            }
        }
    }

    public Task SetDestinationKeyAsync(Guid id, string key, CancellationToken ct = default) =>
        _secrets.SetAsync((Project.FindDestination(id) ?? throw new InvalidOperationException("El destino no existe.")).CredentialName, key, ct);

    public async Task<bool> HasDestinationKeyAsync(Guid id, CancellationToken ct = default) =>
        Project.FindDestination(id) is { } d && !string.IsNullOrEmpty(await _secrets.GetAsync(d.CredentialName, ct).ConfigureAwait(false));

    /// <summary>Borra la clave cifrada de un destino (al eliminarlo definitivamente).</summary>
    public Task DeleteDestinationKeyAsync(string credentialName, CancellationToken ct = default) => _secrets.DeleteAsync(credentialName, ct);

    /// <summary>Calcula una transformación que encaja una fuente en el lienzo conservando su relación de aspecto.</summary>
    public static Transform FitToCanvas(int sourceWidth, int sourceHeight, CanvasSettings canvas)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return Transform.Identity;
        var scale = Math.Min((double)canvas.Width / sourceWidth, (double)canvas.Height / sourceHeight);
        var w = sourceWidth * scale;
        var h = sourceHeight * scale;
        return new Transform((canvas.Width - w) / 2, (canvas.Height - h) / 2, scale, scale, 0);
    }

    private void ScheduleAutosave()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _autosaveCts, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_autosaveDelay, cts.Token).ConfigureAwait(false);
                await SaveAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Otro cambio reprogramó el guardado.
            }
            catch (Exception ex)
            {
                AutosaveFailed?.Invoke(this, ex);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Extensions.DisposeAsync().ConfigureAwait(false);
        await Graphics.DisposeAsync().ConfigureAwait(false);
        _autosaveCts?.Cancel();
        try
        {
            await SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AutosaveFailed?.Invoke(this, ex);
        }
    }
}
