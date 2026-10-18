using StreamPilot.Application.Abstractions;
using StreamPilot.Application.Commands;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.Application.Studio;

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

    public StudioSession(IStreamingEngine engine, IProjectRepository repository, ISecretStore secrets, StudioProject project,
        string projectPath, TimeSpan? autosaveDelay = null)
    {
        Engine = engine;
        _repository = repository;
        _secrets = secrets;
        ProjectPath = projectPath;
        _autosaveDelay = autosaveDelay ?? TimeSpan.FromSeconds(2);
        Executor = new CommandExecutor(new StudioContext(project, engine));
        Executor.StateChanged += (_, _) => ScheduleAutosave();
    }

    public IStreamingEngine Engine { get; }
    public CommandExecutor Executor { get; }
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
        await EngineSync.LoadProjectAsync(Engine, Project, ct).ConfigureAwait(false);
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
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "StreamPilot")
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

    /// <summary>Valida el destino y descifra la clave justo antes de iniciar. La clave no se guarda en memoria del proyecto.</summary>
    public async Task<string?> ValidateStreamAsync(CancellationToken ct = default)
    {
        var server = Project.Stream.Server.Trim();
        if (server.Length == 0)
            return "Configura el servidor RTMP en Ajustes > Transmisión.";
        if (!server.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) && !server.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
            return "El servidor debe empezar por rtmp:// o rtmps://.";
        var key = await _secrets.GetAsync(Project.Stream.CredentialName, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(key) ? "Falta la clave de transmisión en Ajustes > Transmisión." : null;
    }

    public async Task StartStreamAsync(CancellationToken ct = default)
    {
        if (await ValidateStreamAsync(ct).ConfigureAwait(false) is { } error)
            throw new InvalidOperationException(error);
        var key = await _secrets.GetAsync(Project.Stream.CredentialName, ct).ConfigureAwait(false);
        await Engine.StartStreamAsync(new StreamTarget(Project.Stream.Server.Trim(), key!, Project.Stream.EffectiveEncoder), ct).ConfigureAwait(false);
    }

    public Task SetStreamKeyAsync(string key, CancellationToken ct = default) =>
        _secrets.SetAsync(Project.Stream.CredentialName, key, ct);

    public async Task<bool> HasStreamKeyAsync(CancellationToken ct = default) =>
        !string.IsNullOrEmpty(await _secrets.GetAsync(Project.Stream.CredentialName, ct).ConfigureAwait(false));

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
