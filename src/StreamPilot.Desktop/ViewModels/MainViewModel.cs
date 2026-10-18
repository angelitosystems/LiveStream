using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamPilot.Application.Abstractions;
using StreamPilot.Application.Audio;
using StreamPilot.Application.Commands;
using StreamPilot.Application.Scenes;
using StreamPilot.Application.Sources;
using StreamPilot.Application.Studio;
using StreamPilot.Desktop.Services;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;
using StreamPilot.Native.Engine;

namespace StreamPilot.Desktop.ViewModels;

/// <summary>
/// Estado del estudio para la UI. Toda mutación pasa por <see cref="CommandExecutor"/>; la UI se reconstruye desde
/// el proyecto cuando el ejecutor avisa de un cambio, así deshacer/rehacer y el asistente quedan reflejados igual.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherTimer _metricsTimer;
    private readonly DispatcherTimer _meterTimer;
    private readonly Stopwatch _streamClock = new();
    private readonly Stopwatch _recordClock = new();
    private ulong _lastStreamBytes;
    private DateTime _lastStreamSample;
    private bool _wasStreaming;
    private bool _refreshing;
    private bool _metersBusy;

    public MainViewModel(StudioSession session, LibObsEngine engine, DialogService dialogs)
    {
        Session = session;
        Engine = engine;
        Dialogs = dialogs;
        Executor.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Session.AutosaveFailed += (_, ex) => Dispatcher.UIThread.Post(() => StatusMessage = $"No se pudo guardar: {ex.Message}");
        Session.Saved += (_, _) => Dispatcher.UIThread.Post(() => LastSaved = DateTime.Now.ToString("HH:mm:ss"));

        _metricsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await PollMetricsAsync());
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, async (_, _) => await PollMetersAsync());
        Refresh();
    }

    public StudioSession Session { get; }
    public LibObsEngine Engine { get; }
    public DialogService Dialogs { get; }
    public CommandExecutor Executor => Session.Executor;
    public StudioProject Project => Session.Project;

    public ObservableCollection<SceneRowViewModel> Scenes { get; } = [];
    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];
    public ObservableCollection<MixerChannelViewModel> Mixer { get; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> Transitions { get; } = SetTransitionCommand.Available.ToList();

    /// <summary>Elementos seleccionados en el editor (puede haber varios con Ctrl).</summary>
    public HashSet<Guid> SelectedItems { get; } = [];

    public event EventHandler? SelectionChanged;
    public event EventHandler? EditSceneChanged;
    public event EventHandler? StudioModeChanged;

    [ObservableProperty] private SceneRowViewModel? _selectedScene;
    [ObservableProperty] private SourceRowViewModel? _selectedSource;
    [ObservableProperty] private bool _engineReady;
    [ObservableProperty] private string? _engineError;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _lastSaved = "—";
    [ObservableProperty] private bool _studioMode;
    [ObservableProperty] private string _transitionId = "fade_transition";
    [ObservableProperty] private int _transitionDurationMs = 300;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _streamReconnecting;
    [ObservableProperty] private string _streamTime = "00:00:00";
    [ObservableProperty] private string _recordTime = "00:00:00";
    [ObservableProperty] private string _fpsText = "— FPS";
    [ObservableProperty] private string _cpuText = "CPU —";
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private string _droppedText = "Perdidos 0 (0.0%)";
    [ObservableProperty] private string _bitrateText = "0 kb/s";
    [ObservableProperty] private string _renderText = "—";
    [ObservableProperty] private bool _snapEnabled = true;
    [ObservableProperty] private string _zoomText = "Ajustar";
    [ObservableProperty] private string? _lastRecordingPath;

    public string Title => $"StreamPilot — {Project.Name}{(Project.Canvas.IsVertical ? " (vertical)" : "")}";
    public string CanvasText => $"{Project.Canvas.Width}x{Project.Canvas.Height} · {Project.Canvas.Fps} FPS";
    public bool CanUndo => Executor.CanUndo;
    public bool CanRedo => Executor.CanRedo;
    public string UndoText => Executor.UndoDescription is { } d ? $"Deshacer: {d}" : "Deshacer";
    public string RedoText => Executor.RedoDescription is { } d ? $"Rehacer: {d}" : "Rehacer";

    /// <summary>Escena que se edita: la de vista previa en modo estudio, la de programa fuera de él.</summary>
    public Guid? EditSceneId => Project.StudioMode ? Project.PreviewSceneId : Project.ActiveSceneId;

    // ---- Arranque -------------------------------------------------------------------------------

    public async Task StartAsync()
    {
        if (!LibObsEngine.TryProbeBridge(out var probeError))
        {
            EngineError = probeError + " Puedes editar el proyecto; la vista previa, grabación y transmisión requieren el motor.";
            return;
        }
        try
        {
            StatusMessage = "Iniciando motor…";
            await Session.StartEngineAsync();
            EngineReady = true;
            StatusMessage = "Motor listo.";
            _metricsTimer.Start();
            _meterTimer.Start();
            Refresh();
        }
        catch (Exception ex)
        {
            EngineError = $"No se pudo iniciar el motor: {ex.Message}";
        }
    }

    // ---- Reconstrucción desde el proyecto -------------------------------------------------------

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            var previousEdit = SelectedScene?.Id;
            Sync(Scenes, Project.Scenes.Select(s => s.Id).ToList(), id => new SceneRowViewModel(id, ""), r => r.Id);
            foreach (var row in Scenes)
            {
                var scene = Project.GetScene(row.Id);
                row.Name = scene.Name;
                row.IsProgram = Project.ActiveSceneId == row.Id;
                row.IsPreview = Project.StudioMode && Project.PreviewSceneId == row.Id;
            }
            SelectedScene = Scenes.FirstOrDefault(s => s.Id == EditSceneId);

            var editScene = EditSceneId is { } id ? Project.GetScene(id) : null;
            var items = editScene?.Items.Reverse().ToList() ?? []; // arriba = capa superior, como en OBS
            Sync(Sources, items.Select(i => i.Id).ToList(), itemId =>
            {
                var item = items.First(i => i.Id == itemId);
                var source = Project.GetSource(item.SourceId);
                return new SourceRowViewModel(item.Id, source.Id, source.Kind);
            }, r => r.ItemId);
            foreach (var row in Sources)
            {
                var item = items.First(i => i.Id == row.ItemId);
                row.Name = Project.GetSource(row.SourceId).Name;
                row.Visible = item.Visible;
                row.Locked = item.Locked;
            }

            SelectedItems.IntersectWith(Sources.Select(s => s.ItemId));
            SelectedSource = Sources.FirstOrDefault(s => SelectedItems.Contains(s.ItemId));

            RefreshMixer(editScene);

            TransitionId = Project.Transition.TypeId;
            TransitionDurationMs = Project.Transition.DurationMs;
            if (StudioMode != Project.StudioMode)
            {
                StudioMode = Project.StudioMode;
                StudioModeChanged?.Invoke(this, EventArgs.Empty);
            }

            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(CanvasText));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(UndoText));
            OnPropertyChanged(nameof(RedoText));
            if (previousEdit != EditSceneId)
                EditSceneChanged?.Invoke(this, EventArgs.Empty);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshMixer(Scene? editScene)
    {
        // Canales globales y fuentes de audio de la escena de programa (lo que realmente suena), como en OBS.
        var program = Project.ActiveSceneId is { } p ? Project.GetScene(p) : null;
        var ids = Project.Sources.Where(s => s.GlobalAudioChannel > 0).Select(s => s.Id)
            .Concat((program?.Items ?? []).Select(i => Project.GetSource(i.SourceId)).Where(HasAudio).Select(s => s.Id))
            .Concat((editScene?.Items ?? []).Select(i => Project.GetSource(i.SourceId)).Where(HasAudio).Select(s => s.Id))
            .Distinct().ToList();
        Sync(Mixer, ids, id => new MixerChannelViewModel(id), c => c.SourceId);
        foreach (var channel in Mixer)
        {
            var source = Project.GetSource(channel.SourceId);
            channel.Syncing = true;
            channel.Name = source.Name;
            channel.Muted = source.Muted;
            channel.VolumeDb = MixerChannelViewModel.ToDb(source.Volume);
            channel.Monitoring = source.Monitoring;
            channel.IsGlobal = source.GlobalAudioChannel > 0;
            channel.Syncing = false;
        }
    }

    private static bool HasAudio(Source s) =>
        s.Kind is SourceKind.AudioInput or SourceKind.AudioOutput or SourceKind.Media or SourceKind.VideoCaptureDevice;

    /// <summary>Actualiza una colección conservando las instancias existentes (y con ello la selección y el foco).</summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<Guid> ids, Func<Guid, T> create, Func<T, Guid> key)
    {
        for (var i = target.Count - 1; i >= 0; i--)
            if (!ids.Contains(key(target[i])))
                target.RemoveAt(i);
        for (var i = 0; i < ids.Count; i++)
        {
            var existing = target.Select((t, index) => (t, index)).FirstOrDefault(x => key(x.t) == ids[i]);
            if (existing.t is null)
                target.Insert(i, create(ids[i]));
            else if (existing.index != i)
                target.Move(existing.index, i);
        }
    }

    // ---- Selección ------------------------------------------------------------------------------

    partial void OnSelectedSceneChanged(SceneRowViewModel? value)
    {
        if (_refreshing || value is null || value.Id == EditSceneId)
            return;
        _ = Project.StudioMode ? Run(new SetPreviewSceneCommand(value.Id)) : Run(new ActivateSceneCommand(value.Id));
    }

    partial void OnSelectedSourceChanged(SourceRowViewModel? value)
    {
        if (_refreshing)
            return;
        SelectedItems.Clear();
        if (value is not null)
            SelectedItems.Add(value.ItemId);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selección desde el lienzo.</summary>
    public void SelectFromCanvas(Guid? itemId, bool additive)
    {
        if (!additive)
            SelectedItems.Clear();
        if (itemId is { } id && !SelectedItems.Remove(id))
            SelectedItems.Add(id);
        _refreshing = true;
        SelectedSource = Sources.FirstOrDefault(s => SelectedItems.Contains(s.ItemId));
        _refreshing = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Ejecución de comandos ------------------------------------------------------------------

    public async Task<CommandResult> Run(IStudioCommand command)
    {
        var result = await Executor.ExecuteAsync(command);
        if (!result.Success)
            StatusMessage = result.Error;
        return result;
    }

    // ---- Escenas --------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddScene()
    {
        var name = await Dialogs.PromptAsync("Nueva escena", "Nombre de la escena:", Project.NextSceneName("Escena"));
        if (name is null)
            return;
        var command = new CreateSceneCommand(name);
        if ((await Run(command)).Success && Project.StudioMode && command.CreatedSceneId is { } id)
            await Run(new SetPreviewSceneCommand(id));
    }

    [RelayCommand]
    private async Task RemoveScene()
    {
        if (SelectedScene is not { } scene)
            return;
        if (await Dialogs.ConfirmAsync("Eliminar escena", $"¿Eliminar la escena «{scene.Name}»? Puedes deshacerlo con Ctrl+Z.", "Eliminar", destructive: true))
            await Run(new RemoveSceneCommand(scene.Id));
    }

    [RelayCommand]
    private async Task RenameScene()
    {
        if (SelectedScene is not { } scene)
            return;
        if (await Dialogs.PromptAsync("Renombrar escena", "Nuevo nombre:", scene.Name) is { } name)
            await Run(new RenameSceneCommand(scene.Id, name));
    }

    [RelayCommand]
    private Task DuplicateScene() => SelectedScene is { } s ? Run(new DuplicateSceneCommand(s.Id)) : Task.CompletedTask;

    [RelayCommand]
    private Task MoveSceneUp() => SelectedScene is { } s ? Run(new MoveSceneCommand(s.Id, Project.IndexOfScene(s.Id) - 1)) : Task.CompletedTask;

    [RelayCommand]
    private Task MoveSceneDown() => SelectedScene is { } s ? Run(new MoveSceneCommand(s.Id, Project.IndexOfScene(s.Id) + 1)) : Task.CompletedTask;

    /// <summary>Atajo Ctrl+1..9: cambia a la escena N (en modo estudio, a la vista previa).</summary>
    public Task SwitchToSceneIndex(int index)
    {
        if (index < 0 || index >= Project.Scenes.Count)
            return Task.CompletedTask;
        var id = Project.Scenes[index].Id;
        return Project.StudioMode ? Run(new SetPreviewSceneCommand(id)) : Run(new ActivateSceneCommand(id));
    }

    // ---- Fuentes --------------------------------------------------------------------------------

    [RelayCommand]
    private async Task RemoveSource()
    {
        var targets = SelectedItems.ToList();
        if (targets.Count == 0)
            return;
        var label = targets.Count == 1 ? $"«{Sources.FirstOrDefault(s => s.ItemId == targets[0])?.Name}»" : $"{targets.Count} elementos";
        if (!await Dialogs.ConfirmAsync("Eliminar", $"¿Quitar {label} de la escena? Puedes deshacerlo con Ctrl+Z.", "Eliminar", destructive: true))
            return;
        foreach (var item in targets)
            await Run(new RemoveItemCommand(item));
    }

    [RelayCommand]
    private async Task RenameSource()
    {
        if (SelectedSource is not { } row)
            return;
        if (await Dialogs.PromptAsync("Renombrar fuente", "Nuevo nombre:", row.Name) is { } name)
            await Run(new RenameSourceCommand(row.SourceId, name));
    }

    [RelayCommand]
    private Task MoveSourceUp() => MoveSelected(+1);

    [RelayCommand]
    private Task MoveSourceDown() => MoveSelected(-1);

    private async Task MoveSelected(int delta)
    {
        if (SelectedSource is not { } row)
            return;
        var (scene, _) = Project.FindItem(row.ItemId);
        await Run(new MoveItemCommand(row.ItemId, scene.IndexOf(row.ItemId) + delta));
    }

    [RelayCommand]
    private Task ToggleVisible(SourceRowViewModel row) => Run(new SetItemVisibleCommand(row.ItemId, !row.Visible));

    [RelayCommand]
    private Task ToggleLocked(SourceRowViewModel row) => Run(new SetItemLockedCommand(row.ItemId, !row.Locked));

    [RelayCommand]
    private async Task FitToScreen()
    {
        foreach (var item in SelectedItems.ToList())
        {
            var (_, sceneItem) = Project.FindItem(item);
            var (w, h) = EngineReady ? await Engine.GetSourceSizeAsync(sceneItem.SourceId, CancellationToken.None) : (0, 0);
            var t = sceneItem.Transform;
            var fitted = StudioSession.FitToCanvas(w - t.CropLeft - t.CropRight, h - t.CropTop - t.CropBottom, Project.Canvas);
            await Run(new SetItemTransformCommand(item, fitted with { CropLeft = t.CropLeft, CropTop = t.CropTop, CropRight = t.CropRight, CropBottom = t.CropBottom }));
        }
    }

    [RelayCommand]
    private async Task ResetTransform()
    {
        foreach (var item in SelectedItems.ToList())
            await Run(new SetItemTransformCommand(item, Transform.Identity));
    }

    [RelayCommand]
    private async Task CenterSelection()
    {
        foreach (var item in SelectedItems.ToList())
        {
            var (_, sceneItem) = Project.FindItem(item);
            var (w, h) = EngineReady ? await Engine.GetSourceSizeAsync(sceneItem.SourceId, CancellationToken.None) : (0, 0);
            var t = sceneItem.Transform;
            var width = (w - t.CropLeft - t.CropRight) * t.ScaleX;
            var height = (h - t.CropTop - t.CropBottom) * t.ScaleY;
            await Run(new SetItemTransformCommand(item, t.WithPosition((Project.Canvas.Width - width) / 2, (Project.Canvas.Height - height) / 2)));
        }
    }

    [RelayCommand]
    private async Task Rotate(string degrees)
    {
        foreach (var item in SelectedItems.ToList())
        {
            var t = Project.FindItem(item).Item.Transform;
            await Run(new SetItemTransformCommand(item, t with { RotationDegrees = ((t.RotationDegrees + double.Parse(degrees)) % 360 + 360) % 360 }));
        }
    }

    [RelayCommand]
    private async Task Flip(string axis)
    {
        foreach (var item in SelectedItems.ToList())
        {
            var t = Project.FindItem(item).Item.Transform;
            await Run(new SetItemTransformCommand(item, axis == "h" ? t with { ScaleX = -t.ScaleX } : t with { ScaleY = -t.ScaleY }));
        }
    }

    /// <summary>Desplaza la selección con las flechas (Shift = 10 px).</summary>
    public async Task Nudge(double dx, double dy)
    {
        foreach (var item in SelectedItems.ToList())
        {
            var (_, sceneItem) = Project.FindItem(item);
            if (!sceneItem.Locked)
                await Run(new SetItemTransformCommand(item, sceneItem.Transform.WithPosition(sceneItem.Transform.X + dx, sceneItem.Transform.Y + dy)));
        }
    }

    // ---- Historial ------------------------------------------------------------------------------

    [RelayCommand]
    private async Task Undo()
    {
        var result = await Executor.UndoAsync();
        if (!result.Success)
            StatusMessage = result.Error;
    }

    [RelayCommand]
    private async Task Redo()
    {
        var result = await Executor.RedoAsync();
        if (!result.Success)
            StatusMessage = result.Error;
    }

    // ---- Modo estudio y transiciones -------------------------------------------------------------

    [RelayCommand]
    private Task ToggleStudioMode() => Run(new SetStudioModeCommand(!Project.StudioMode));

    [RelayCommand]
    private Task TransitionToProgram() => Run(new TransitionToProgramCommand());

    partial void OnTransitionIdChanged(string value) => ApplyTransition();

    partial void OnTransitionDurationMsChanged(int value) => ApplyTransition();

    private void ApplyTransition()
    {
        if (_refreshing)
            return;
        var settings = new TransitionSettings(TransitionId, TransitionDurationMs);
        if (settings != Project.Transition)
            _ = Run(new SetTransitionCommand(settings));
    }

    // ---- Mezclador ------------------------------------------------------------------------------

    public Task SetChannelVolume(MixerChannelViewModel channel, double db) =>
        channel.Syncing ? Task.CompletedTask : Run(new SetVolumeCommand(channel.SourceId, MixerChannelViewModel.ToMultiplier(db)));

    [RelayCommand]
    private Task ToggleMute(MixerChannelViewModel channel) => Run(new SetMutedCommand(channel.SourceId, !channel.Muted));

    [RelayCommand]
    private Task SetMonitoring(object? parameter) =>
        parameter is object[] { Length: 2 } args && args[0] is MixerChannelViewModel channel && args[1] is AudioMonitoring m
            ? Run(new SetMonitoringCommand(channel.SourceId, m))
            : Task.CompletedTask;

    private async Task PollMetersAsync()
    {
        if (!EngineReady || _metersBusy || Mixer.Count == 0)
            return;
        _metersBusy = true;
        try
        {
            foreach (var channel in Mixer.ToList())
            {
                var levels = await Engine.GetAudioLevelsAsync(channel.SourceId, CancellationToken.None);
                if (levels is null)
                    continue;
                channel.PeakLeft = levels.PeakLeftDb;
                channel.PeakRight = levels.Channels > 1 ? levels.PeakRightDb : levels.PeakLeftDb;
                channel.MagnitudeLeft = levels.MagnitudeLeftDb;
                channel.MagnitudeRight = levels.Channels > 1 ? levels.MagnitudeRightDb : levels.MagnitudeLeftDb;
            }
        }
        catch (Exception ex) when (ex is NativeEngineException or InvalidOperationException)
        {
            // Una fuente puede eliminarse entre la lista y la consulta; el siguiente ciclo lo corrige.
        }
        finally
        {
            _metersBusy = false;
        }
    }

    // ---- Salidas --------------------------------------------------------------------------------

    [RelayCommand]
    private async Task ToggleStream()
    {
        if (!EngineReady)
            return;
        try
        {
            if (IsStreaming)
            {
                if (!await Dialogs.ConfirmAsync("Detener transmisión", "¿Detener la transmisión en directo?", "Detener", destructive: true))
                    return;
                StatusMessage = "Deteniendo transmisión…";
                await Engine.StopStreamAsync(CancellationToken.None);
                StatusMessage = "Transmisión detenida.";
                return;
            }

            if (await Session.ValidateStreamAsync() is { } problem)
            {
                await Dialogs.InfoAsync("Transmisión", problem);
                return;
            }
            if (!await Dialogs.ConfirmAsync("Iniciar transmisión", $"Se emitirá en directo a {MaskServer(Project.Stream.Server)}. ¿Continuar?", "Transmitir"))
                return;
            StatusMessage = "Conectando…";
            await Session.StartStreamAsync();
            _streamClock.Restart();
            _lastStreamBytes = 0;
            _lastStreamSample = DateTime.UtcNow;
            _wasStreaming = true;
            IsStreaming = true;
            StatusMessage = "Transmisión iniciada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Transmisión: {ex.Message}";
            await Dialogs.InfoAsync("Transmisión", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ToggleRecord()
    {
        if (!EngineReady)
            return;
        try
        {
            if (IsRecording)
            {
                StatusMessage = "Finalizando grabación…";
                await Engine.StopRecordingAsync(CancellationToken.None);
                IsRecording = false;
                _recordClock.Reset();
                StatusMessage = $"Grabación guardada: {LastRecordingPath}";
                return;
            }
            var folder = Path.GetDirectoryName(Session.NextRecordingPath(DateTime.Now))!;
            Directory.CreateDirectory(folder);
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
            if (free < 2L * 1024 * 1024 * 1024 &&
                !await Dialogs.ConfirmAsync("Poco espacio", $"Quedan {free / 1048576} MB libres en la unidad de grabación. ¿Grabar igualmente?", "Grabar"))
                return;
            LastRecordingPath = await Session.StartRecordingAsync();
            _recordClock.Restart();
            IsRecording = true;
            StatusMessage = $"Grabando en {LastRecordingPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Grabación: {ex.Message}";
            await Dialogs.InfoAsync("Grabación", ex.Message);
        }
    }

    private static string MaskServer(string server) => server.Length > 60 ? server[..60] + "…" : server;

    private async Task PollMetricsAsync()
    {
        if (!EngineReady)
            return;
        try
        {
            var m = await Engine.GetMetricsAsync(CancellationToken.None);
            FpsText = $"{m.ActiveFps:0.00} FPS";
            CpuText = $"CPU {m.CpuUsagePercent:0.0}%";
            MemoryText = $"{m.MemoryBytes / 1048576} MB";
            RenderText = $"Render {m.AverageFrameRenderMs:0.0} ms · Lag {m.LaggedFrames}";
            IsRecording = m.IsRecording;
            if (IsRecording)
                RecordTime = _recordClock.Elapsed.ToString(@"hh\:mm\:ss");

            StreamReconnecting = m.StreamReconnecting;
            if (m.IsStreaming)
            {
                var now = DateTime.UtcNow;
                var seconds = (now - _lastStreamSample).TotalSeconds;
                if (seconds > 0 && m.StreamTotalBytes >= _lastStreamBytes)
                    BitrateText = $"{(m.StreamTotalBytes - _lastStreamBytes) * 8 / 1000 / seconds:0} kb/s";
                _lastStreamBytes = m.StreamTotalBytes;
                _lastStreamSample = now;
                var pct = m.StreamTotalFrames == 0 ? 0 : 100.0 * m.StreamDroppedFrames / m.StreamTotalFrames;
                DroppedText = $"Perdidos {m.StreamDroppedFrames} ({pct:0.0}%)";
                StreamTime = _streamClock.Elapsed.ToString(@"hh\:mm\:ss");
            }
            IsStreaming = m.IsStreaming;

            // La conexión puede caer sola (red, clave inválida): se informa con el motivo real.
            if (_wasStreaming && !m.IsStreaming)
            {
                _wasStreaming = false;
                _streamClock.Reset();
                BitrateText = "0 kb/s";
                var stop = await Engine.GetLastStopAsync(OutputKind.Stream, CancellationToken.None);
                if (stop.IsError)
                {
                    StatusMessage = OutputErrors.Describe(stop);
                    await Dialogs.InfoAsync("La transmisión se detuvo", OutputErrors.Describe(stop));
                }
            }
        }
        catch (Exception ex) when (ex is NativeEngineException or InvalidOperationException)
        {
            StatusMessage = ex.Message;
        }
    }

    // ---- Proyecto -------------------------------------------------------------------------------

    [RelayCommand]
    private async Task SaveProject()
    {
        try
        {
            await Session.SaveAsync();
            StatusMessage = $"Guardado en {Session.ProjectPath}";
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync("Guardar", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveProjectAs()
    {
        var path = await Dialogs.SaveFileAsync("Guardar proyecto como", Project.Name, "spproj");
        if (path is null)
            return;
        await Session.SaveAsAsync(path);
        StatusMessage = $"Guardado en {path}";
    }

    /// <summary>
    /// libobs no puede reiniciarse dentro del proceso, así que abrir otro proyecto (o cambiar el lienzo) relanza la
    /// aplicación con ese proyecto tras guardar el actual.
    /// </summary>
    public async Task RelaunchWithProjectAsync(string path)
    {
        if (IsStreaming || IsRecording)
        {
            await Dialogs.InfoAsync("Reinicio necesario", "Detén la transmisión y la grabación antes de cambiar de proyecto o de lienzo.");
            return;
        }
        await Session.SaveAsync();
        var exe = Environment.ProcessPath!;
        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--project", path }, UseShellExecute = false });
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        var path = await Dialogs.PickFileAsync("Abrir proyecto", "Proyectos StreamPilot (*.spproj)");
        if (path is not null && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(Session.ProjectPath), StringComparison.OrdinalIgnoreCase))
            await RelaunchWithProjectAsync(path);
    }

    [RelayCommand]
    private async Task NewProject(string orientation)
    {
        var name = await Dialogs.PromptAsync("Nuevo proyecto", "Nombre del proyecto:", orientation == "vertical" ? "Estudio vertical" : "Nuevo estudio");
        if (name is null)
            return;
        var canvas = orientation == "vertical" ? new CanvasSettings(1080, 1920, 30) : new CanvasSettings(1920, 1080, 30);
        var project = StudioSession.CreateDefaultProject(name, canvas);
        var repo = new Infrastructure.Persistence.JsonProjectRepository();
        var path = repo.DefaultPathFor(project);
        if (File.Exists(path))
            path = Path.Combine(repo.ProjectsDirectory, $"{name} {DateTime.Now:yyyyMMdd-HHmmss}.spproj");
        await repo.SaveAsync(project, path, CancellationToken.None);
        await RelaunchWithProjectAsync(path);
    }

    public async ValueTask DisposeAsync()
    {
        _metricsTimer.Stop();
        _meterTimer.Stop();
        await Session.DisposeAsync();
    }
}

/// <summary>Mensajes legibles para los códigos de detención de salidas de libobs (OBS_OUTPUT_*).</summary>
public static class OutputErrors
{
    public static string Describe(OutputStopInfo stop)
    {
        var reason = stop.Code switch
        {
            -1 => "Ruta o URL de servidor inválida.",
            -2 => "No se pudo conectar con el servidor. Revisa la URL, el firewall y tu conexión.",
            -3 => "El servidor rechazó la transmisión. Revisa que la clave de transmisión sea correcta y esté activa.",
            -4 => "Error de la salida.",
            -5 => "Se perdió la conexión con el servidor y no se pudo recuperar.",
            -6 => "El servidor no admite la configuración enviada (códec o formato).",
            -7 => "No queda espacio en disco.",
            -8 => "Error del codificador. Prueba otro codificador o un bitrate menor.",
            _ => $"Error desconocido ({stop.Code}).",
        };
        return string.IsNullOrWhiteSpace(stop.Message) ? reason : $"{reason} Detalle: {stop.Message}";
    }
}
