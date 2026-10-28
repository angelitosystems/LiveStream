using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Assistant;
using LiveStream.Application.Audio;
using LiveStream.Application.Commands;
using LiveStream.Application.Graphics;
using LiveStream.Application.Optimization;
using LiveStream.Application.Scenes;
using LiveStream.Application.Sources;
using LiveStream.Application.Streaming;
using LiveStream.Application.Studio;
using LiveStream.Desktop.Services;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Domain.ValueObjects;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Plugins;
using LiveStream.Infrastructure.Updates;
using LiveStream.PluginSdk;
using LiveStream.Native.Engine;
using LiveStream.Application.DualStreaming;
using LiveStream.Domain.Compositions;

namespace LiveStream.Desktop.ViewModels;

public enum StreamOutputMode
{
    Horizontal,
    Vertical,
    Dual
}

/// <summary>
/// Estado del estudio para la UI. Toda mutación pasa por <see cref="CommandExecutor"/>; la UI se reconstruye desde
/// el proyecto cuando el ejecutor avisa de un cambio, así deshacer/rehacer y el asistente quedan reflejados igual.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable, IOutputController
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

    public MainViewModel(StudioSession session, LibObsEngine engine, DialogService dialogs, ISecretStore secrets)
    {
        Session = session;
        Engine = engine;
        Dialogs = dialogs;
        Machine = new MachineSettingsStore();
        Optimizer = new PerformanceOptimizer(session) { AdaptiveEnabled = Machine.Load().AdaptiveBitrate };
        Assistant = new AssistantViewModel(this, secrets);
        Plugins = new PluginManager(
            new PluginStore(UpdateClient.CurrentVersion, PluginTrust.EmbeddedPublicKey()) { DeveloperMode = Machine.Load().PluginDeveloperMode },
            plugin => new AssistantToolbox(Session, this, c => Dispatcher.UIThread.InvokeAsync(() => Dialogs.ConfirmAsync(
                $"{plugin.Manifest.Name}: {c.Title}", $"El plugin «{plugin.Manifest.Name}» pide: {c.Message}", c.ConfirmText, destructive: true)),
                Optimizer.Report, Brand.Load));
        Executor.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Executor.StateChanged += (_, _) => PublishStudioChanged();
        Session.AutosaveFailed += (_, ex) => Dispatcher.UIThread.Post(() => StatusMessage = $"No se pudo guardar: {ex.Message}");
        Session.Saved += (_, _) => Dispatcher.UIThread.Post(() => LastSaved = DateTime.Now.ToString("HH:mm:ss"));
        Session.Graphics.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshGraphicsLive);
        Session.Graphics.Error += (_, message) => Dispatcher.UIThread.Post(() => StatusMessage = message);

        _metricsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await PollMetricsAsync());
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, async (_, _) => await PollMetersAsync());
        Chat = new ChatViewModel(session.Chat, session.ChatOverlay, session.ChatTts);
        Alerts = new AlertsViewModel(session);
        Integrations = new IntegrationsViewModel(session, secrets);
        Tts = new TtsViewModel(session.ChatTts);
        MonetaryAlerts = new MonetaryAlertsViewModel(session.MonetaryAlerts, session.ChannelPoints);
        DualStream = session.DualStream;
        DualStream.ActiveFormatChanged += (_, format) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(ActiveCompositionFormat));
                OnPropertyChanged(nameof(IsHorizontalComposition));
                OnPropertyChanged(nameof(IsVerticalComposition));
                OnPropertyChanged(nameof(CompositionFormatLabel));
                OnPropertyChanged(nameof(CanvasText));
                OnPropertyChanged(nameof(Title));
                Refresh();
            });
        };
        Mixer.CollectionChanged += (_, _) => HasMixerChannels = Mixer.Count > 0;
        Refresh();
        HasMixerChannels = Mixer.Count > 0;
    }

    public StudioSession Session { get; }
    public LibObsEngine Engine { get; }
    public DialogService Dialogs { get; }

    /// <summary>Panel del asistente de IA.</summary>
    public AssistantViewModel Assistant { get; }

    /// <summary>Plugins instalados (cada uno en su propio proceso).</summary>
    public PluginManager Plugins { get; }

    /// <summary>Panel y servicio de chat unificado.</summary>
    public ChatViewModel Chat { get; }

    /// <summary>Centro y cola de alertas y eventos.</summary>
    public AlertsViewModel Alerts { get; }

    /// <summary>Gestor de integraciones y conexiones oficiales de plataformas.</summary>
    public IntegrationsViewModel Integrations { get; }

    /// <summary>Subsistema de lectura de comentarios y síntesis de voz (TTS).</summary>
    public TtsViewModel Tts { get; }

    /// <summary>Panel y configuración de alertas monetarias, donaciones y puntos de canal.</summary>
    public MonetaryAlertsViewModel MonetaryAlerts { get; }

    /// <summary>Coordinador de emisión y composición dual simultánea.</summary>
    public DualStreamCoordinator DualStream { get; }

    public CompositionFormat ActiveCompositionFormat => DualStream.ActiveFormat;
    public bool IsHorizontalComposition => ActiveCompositionFormat == CompositionFormat.Horizontal;
    public bool IsVerticalComposition => ActiveCompositionFormat == CompositionFormat.Vertical;
    public string CompositionFormatLabel => IsVerticalComposition ? "Vertical (9:16)" : "Horizontal (16:9)";

    private bool _isVerticalFormatActive;
    /// <summary>
    /// Indica si el formato y marco de referencia vertical (9:16) está activado en el estudio.
    /// Al activarse, muestra guías superpuestas en el lienzo (Preview y Programa) y habilita modos 9:16/Dual.
    /// </summary>
    public bool IsVerticalFormatActive
    {
        get => _isVerticalFormatActive;
        set
        {
            if (SetProperty(ref _isVerticalFormatActive, value))
            {
                OnPropertyChanged(nameof(VerticalFrameGuideText));
                OnPropertyChanged(nameof(FormatBadgeText));
                if (!value && SelectedStreamMode != StreamOutputMode.Horizontal)
                    SelectedStreamMode = StreamOutputMode.Horizontal;
                else if (value && SelectedStreamMode == StreamOutputMode.Horizontal)
                    SelectedStreamMode = StreamOutputMode.Dual;
            }
        }
    }

    public string VerticalFrameGuideText => IsVerticalFormatActive ? "Guía 9:16 Activa" : "Activar 9:16";

    public string FormatBadgeText => SelectedStreamMode switch
    {
        StreamOutputMode.Dual => "Marco 9:16 Dual Activo",
        StreamOutputMode.Vertical => "Formato Vertical (9:16)",
        _ => "Marco 9:16 Activo"
    };

    private StreamOutputMode _selectedStreamMode = StreamOutputMode.Horizontal;
    public StreamOutputMode SelectedStreamMode
    {
        get => _selectedStreamMode;
        set
        {
            if (SetProperty(ref _selectedStreamMode, value))
            {
                OnPropertyChanged(nameof(IsStreamModeHorizontal));
                OnPropertyChanged(nameof(IsStreamModeVertical));
                OnPropertyChanged(nameof(IsStreamModeDual));
                OnPropertyChanged(nameof(StreamButtonText));
            }
        }
    }

    public bool IsStreamModeHorizontal => SelectedStreamMode == StreamOutputMode.Horizontal;
    public bool IsStreamModeVertical => SelectedStreamMode == StreamOutputMode.Vertical;
    public bool IsStreamModeDual => SelectedStreamMode == StreamOutputMode.Dual;

    public string StreamButtonText => IsStreaming
        ? "Detener transmisión"
        : SelectedStreamMode switch
        {
            StreamOutputMode.Horizontal => "Iniciar transmisión (16:9)",
            StreamOutputMode.Vertical => "Iniciar transmisión (9:16)",
            _ => "Iniciar transmisión dual"
        };

    [RelayCommand]
    public void SetStreamMode(StreamOutputMode mode)
    {
        if (mode != StreamOutputMode.Horizontal && !IsVerticalFormatActive)
            IsVerticalFormatActive = true;
        SelectedStreamMode = mode;
        OnPropertyChanged(nameof(FormatBadgeText));
    }

    [RelayCommand]
    public void ToggleVerticalFormat()
    {
        IsVerticalFormatActive = !IsVerticalFormatActive;
        OnPropertyChanged(nameof(FormatBadgeText));
    }

    [RelayCommand]
    public async Task AdaptSelectedToVerticalAsync()
    {
        try
        {
            var canvas = Project.Canvas;
            var centerX = canvas.Width / 2.0;
            var items = SelectedItems.Count > 0
                ? SelectedItems.ToList()
                : (EditSceneId.HasValue && Project.Scenes.Any(s => s.Id == EditSceneId.Value)
                    ? Project.GetScene(EditSceneId.Value).Items.Select(i => i.Id).ToList()
                    : []);
            if (items.Count == 0) return;

            foreach (var itemId in items)
            {
                var (scene, sceneItem) = Project.FindItem(itemId);
                if (sceneItem is null) continue;
                var (w, h) = EngineReady ? await Engine.GetSourceSizeAsync(sceneItem.SourceId, CancellationToken.None) : (0, 0);
                var t = sceneItem.Transform;
                var uncroppedW = w > 0 ? w - t.CropLeft - t.CropRight : (canvas.Width / 2.0);
                var itemW = uncroppedW * t.ScaleX;
                var newX = centerX - (itemW / 2.0);
                await Run(new SetItemTransformCommand(itemId, t.WithPosition(Math.Round(newX), t.Y)));
            }
            StatusMessage = "Elementos centrados en el marco vertical 9:16.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Adaptar a 9:16: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SwitchCompositionFormatAsync(CompositionFormat format) => await DualStream.SwitchFormatAsync(format);

    [RelayCommand]
    public async Task ToggleCompositionFormatAsync()
    {
        var target = IsHorizontalComposition ? CompositionFormat.Vertical : CompositionFormat.Horizontal;
        await DualStream.SwitchFormatAsync(target);
    }

    [RelayCommand]
    public void CreateVerticalComposition()
    {
        if (SelectedScene is null) return;
        var scene = Project.GetScene(SelectedScene.Id);
        var verticalScene = DualStream.CreateVerticalCompositionFrom(scene);
        Refresh();
        SelectedScene = Scenes.FirstOrDefault(s => s.Id == verticalScene.Id);
    }

    [RelayCommand]
    public async Task StartHorizontalStreamAsync()
    {
        var results = await DualStream.StartHorizontalOnlyAsync();
        var failed = results.Where(r => r.Error is not null).ToList();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (results.Any(r => r.Error is null))
            {
                _streamClock.Restart();
                _lastStreamBytes = 0;
                _lastStreamSample = DateTime.UtcNow;
                _wasStreaming = true;
                IsStreaming = true;
                OnPropertyChanged(nameof(StreamButtonText));
            }
            StatusMessage = results.Any(r => r.Error is null)
                ? $"Emisión horizontal iniciada ({results.Count(r => r.Error is null)} destino{(results.Count == 1 ? "" : "s")})."
                : $"No se pudo iniciar la emisión horizontal: {string.Join("; ", failed.Select(f => f.Error))}";
        });
    }

    [RelayCommand]
    public async Task StartVerticalStreamAsync()
    {
        var results = await DualStream.StartVerticalOnlyAsync();
        var failed = results.Where(r => r.Error is not null).ToList();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (results.Any(r => r.Error is null))
            {
                _streamClock.Restart();
                _lastStreamBytes = 0;
                _lastStreamSample = DateTime.UtcNow;
                _wasStreaming = true;
                IsStreaming = true;
                OnPropertyChanged(nameof(StreamButtonText));
            }
            StatusMessage = results.Any(r => r.Error is null)
                ? $"Emisión vertical iniciada ({results.Count(r => r.Error is null)} destino{(results.Count == 1 ? "" : "s")})."
                : $"No se pudo iniciar la emisión vertical: {string.Join("; ", failed.Select(f => f.Error))}";
        });
    }

    [RelayCommand]
    public async Task StartSimultaneousStreamAsync()
    {
        var results = await DualStream.StartSimultaneousAsync();
        var failed = results.Where(r => r.Error is not null).ToList();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (results.Any(r => r.Error is null))
            {
                _streamClock.Restart();
                _lastStreamBytes = 0;
                _lastStreamSample = DateTime.UtcNow;
                _wasStreaming = true;
                IsStreaming = true;
                OnPropertyChanged(nameof(StreamButtonText));
            }
            StatusMessage = results.Any(r => r.Error is null)
                ? $"Emisión dual simultánea iniciada ({results.Count(r => r.Error is null)} destino{(results.Count == 1 ? "" : "s")})."
                : $"Fallo al iniciar emisión simultánea: {string.Join("; ", failed.Select(f => f.Error))}";
        });
    }

    private int _pluginEventPending;

    /// <summary>Agrupa ráfagas de cambios (p. ej. al arrastrar) en un solo evento cada 250 ms.</summary>
    private void PublishStudioChanged()
    {
        if (Interlocked.Exchange(ref _pluginEventPending, 1) == 1)
            return;
        _ = Task.Delay(250).ContinueWith(_ =>
        {
            Volatile.Write(ref _pluginEventPending, 0);
            Plugins.Publish(PluginProtocol.StudioChangedEvent);
        }, TaskScheduler.Default);
    }

    /// <summary>Optimizador en directo (bitrate adaptativo, avisos de sobrecarga, auditoría).</summary>
    public PerformanceOptimizer Optimizer { get; }

    /// <summary>Ajustes de este equipo (adaptador gráfico, bitrate adaptativo).</summary>
    public MachineSettingsStore Machine { get; }
    public CommandExecutor Executor => Session.Executor;
    public StudioProject Project => Session.Project;

    public ObservableCollection<SceneRowViewModel> Scenes { get; } = [];
    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];
    /// <summary>Filas visibles tras el filtro de búsqueda (las mismas instancias: la selección sigue valiendo).</summary>
    public ObservableCollection<SceneRowViewModel> VisibleScenes { get; } = [];
    public ObservableCollection<SourceRowViewModel> VisibleSources { get; } = [];
    [ObservableProperty] private string _sceneFilter = "";
    [ObservableProperty] private string _sourceFilter = "";

    partial void OnSceneFilterChanged(string value) => ApplyFilters();

    partial void OnSourceFilterChanged(string value) => ApplyFilters();

    private void ApplyFilters()
    {
        Sync(VisibleScenes, Filter(Scenes, SceneFilter).Select(r => r.Id).ToList(), id => Scenes.First(r => r.Id == id), r => r.Id);
        Sync(VisibleSources, Filter(Sources, SourceFilter).Select(r => r.ItemId).ToList(), id => Sources.First(r => r.ItemId == id), r => r.ItemId);
    }

    private static IEnumerable<T> Filter<T>(IEnumerable<T> rows, string text) =>
        string.IsNullOrWhiteSpace(text)
            ? rows
            : rows.Where(r => r?.ToString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);

    public ObservableCollection<MixerChannelViewModel> Mixer { get; } = [];
    [ObservableProperty] private bool _hasMixerChannels;
    public IReadOnlyList<KeyValuePair<string, string>> Transitions { get; } = SetTransitionCommand.Available.ToList();
    public ObservableCollection<GraphicRowViewModel> Graphics { get; } = [];
    public IReadOnlyList<GraphicKindOption> GraphicKinds { get; } =
        GraphicTemplates.Kinds.Select(k => new GraphicKindOption(k, GraphicTemplates.DisplayName(k)))
            .Append(new GraphicKindOption(null, "Importar gráfico (.lsgfx)…"))
            .ToList();
    public IGraphicTemplateLibrary Templates { get; } = new GraphicTemplateLibrary();
    public BrandKitStore Brand { get; } = new();

    /// <summary>Imágenes y videos compartidos entre proyectos (%APPDATA%\LiveStream\library).</summary>
    public ResourceLibrary Library { get; } = new();

    /// <summary>Se eligió una transición que necesita ajustes (p. ej. el video del stinger) antes de aplicarse.</summary>
    public event EventHandler<string>? TransitionNeedsSettings;

    /// <summary>Elementos seleccionados en el editor (puede haber varios con Ctrl).</summary>
    public HashSet<Guid> SelectedItems { get; } = [];

    public event EventHandler? SelectionChanged;
    public event EventHandler? EditSceneChanged;
    public event EventHandler? StudioModeChanged;

    [ObservableProperty] private SceneRowViewModel? _selectedScene;
    [ObservableProperty] private SourceRowViewModel? _selectedSource;
    [ObservableProperty] private GraphicRowViewModel? _selectedGraphic;
    [ObservableProperty] private bool _engineReady;
    [ObservableProperty] private string? _engineError;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _lastSaved = "—";
    [ObservableProperty] private bool _studioMode;
    [ObservableProperty] private string _transitionId = "fade_transition";
    [ObservableProperty] private int _transitionDurationMs = 300;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamButtonText))]
    private bool _isStreaming;
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

    public string Title => $"LiveStream — {Project.Name}{(Project.Canvas.IsVertical ? " (vertical)" : "")}";
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
            var machine = Machine.Load();
            var adapters = LibObsEngine.ListAdapters();
            var adapter = adapters.Any(a => a.Index == machine.RenderAdapterIndex) ? machine.RenderAdapterIndex : 0;
            try
            {
                await Session.StartEngineAsync(adapter);
            }
            catch when (adapter != 0)
            {
                // libobs no se puede reiniciar en el mismo proceso: se vuelve al adaptador 0 para el próximo arranque.
                Machine.Save(machine with { RenderAdapterIndex = 0 });
                throw new InvalidOperationException(
                    $"El motor no pudo iniciar con el adaptador {adapter}. Se volvió al adaptador principal: reinicia LiveStream.");
            }
            EngineReady = true;
            StatusMessage = "Motor listo.";
            _ = Plugins.StartEnabledAsync(CancellationToken.None);
            _metricsTimer.Start();
            _meterTimer.Start();
            Refresh();
        }
        catch (Exception ex)
        {
            EngineError = $"No se pudo iniciar el motor: {ex.Message}";
            Infrastructure.Diagnostics.AppLog.Error("motor", "Fallo al iniciar", ex);
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
            RefreshGraphics();

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
            ApplyFilters();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void RefreshMixer() => RefreshMixer(EditSceneId is { } id ? Project.GetScene(id) : null);

    private void RefreshMixer(Scene? editScene)
    {
        // Canales globales y fuentes de audio de la escena de programa (lo que realmente suena), como en OBS.
        var program = Project.ActiveSceneId is { } p ? Project.GetScene(p) : null;
        var ids = Project.Sources.Where(s => s.GlobalAudioChannel > 0).Select(s => s.Id)
            .Concat((program?.Items ?? []).Select(i => Project.GetSource(i.SourceId)).Where(HasAudio).Select(s => s.Id))
            .Concat((editScene?.Items ?? []).Select(i => Project.GetSource(i.SourceId)).Where(HasAudio).Select(s => s.Id))
            .Distinct().ToList();
        Sync(Mixer, ids, id => new MixerChannelViewModel(id)
        {
            VolumeRequested = (c, db) => _ = SetChannelVolume(c, db),
            MonitoringRequested = (c, m) => _ = Run(new SetMonitoringCommand(c.SourceId, m)),
        }, c => c.SourceId);
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
        HasMixerChannels = Mixer.Count > 0;
    }

    private static bool HasAudio(Source s) => s.HasActiveAudio;

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

    /// <summary>Selección desde el panel de fuentes (multi-selección: varias filas del ListBox).</summary>
    public void SelectRows(IReadOnlyList<SourceRowViewModel> rows)
    {
        SelectedItems.Clear();
        foreach (var row in rows)
            SelectedItems.Add(row.ItemId);
        _refreshing = true;
        SelectedSource = rows.Count > 0 ? rows[^1] : null;
        _refreshing = false;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Ejecución de comandos ------------------------------------------------------------------

    public async Task<CommandResult> Run(IStudioCommand command)
    {
        var result = await Executor.ExecuteAsync(command);
        if (!result.Success)
        {
            StatusMessage = result.Error;
            Infrastructure.Diagnostics.AppLog.Warn("comando", $"{command.Description}: {result.Error}");
        }
        return result;
    }

    // ---- Escenas --------------------------------------------------------------------------------

    private SceneRowViewModel? Resolve(SceneRowViewModel? row) => row ?? SelectedScene;

    /// <summary>Se pidió la creación de escenas (MainWindow abre el diálogo con plantillas).</summary>
    public event EventHandler? CreateSceneRequested;

    [RelayCommand]
    private void AddScene() => CreateSceneRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Crea una escena con nombre y plantilla (vía el diálogo nuevo o el asistente).</summary>
    public async Task<Guid?> AddSceneWithTemplateAsync(string name, SceneTemplateKind kind)
    {
        var (sceneId, error) = await SceneTemplates.CreateAsync(Executor.Context, Executor, name, kind);
        if (error is not null)
        {
            StatusMessage = error;
            return null;
        }
        var id = sceneId!.Value;
        if (Project.StudioMode)
            await Run(new SetPreviewSceneCommand(id));
        return id;
    }

    [RelayCommand]
    private async Task RemoveScene(SceneRowViewModel? row)
    {
        if (Resolve(row) is not { } scene)
            return;
        if (await Dialogs.ConfirmAsync("Eliminar escena", $"¿Eliminar la escena «{scene.Name}»? Puedes deshacerlo con Ctrl+Z.", "Eliminar", destructive: true))
            await Run(new RemoveSceneCommand(scene.Id));
    }

    [RelayCommand]
    private async Task RenameScene(SceneRowViewModel? row)
    {
        if (Resolve(row) is not { } scene)
            return;
        if (await Dialogs.PromptAsync("Renombrar escena", "Nuevo nombre:", scene.Name) is { } name)
            await Run(new RenameSceneCommand(scene.Id, name));
    }

    [RelayCommand]
    private Task DuplicateScene(SceneRowViewModel? row) => Resolve(row) is { } s ? Run(new DuplicateSceneCommand(s.Id)) : Task.CompletedTask;

    /// <summary>Saltar a una escena concreta (menú contextual de una fila que no está seleccionada).</summary>
    public Task SwitchToScene(SceneRowViewModel row) =>
        Project.StudioMode ? Run(new SetPreviewSceneCommand(row.Id)) : Run(new ActivateSceneCommand(row.Id));

    [RelayCommand]
    private Task MoveSceneUp(SceneRowViewModel? row) => Resolve(row) is { } s ? Run(new MoveSceneCommand(s.Id, Project.IndexOfScene(s.Id) - 1)) : Task.CompletedTask;

    [RelayCommand]
    private Task MoveSceneDown(SceneRowViewModel? row) => Resolve(row) is { } s ? Run(new MoveSceneCommand(s.Id, Project.IndexOfScene(s.Id) + 1)) : Task.CompletedTask;

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

    /// <summary>Duplica los elementos seleccionados en su escena: comparten fuente y quedan 24 px abajo a la derecha.</summary>
    [RelayCommand]
    private async Task DuplicateSource()
    {
        var targets = SelectedItems.ToList();
        if (targets.Count == 0)
            return;
        Guid? last = null;
        foreach (var item in targets)
        {
            var (scene, sceneItem) = Project.FindItem(item);
            var add = new AddExistingSourceCommand(scene.Id, sceneItem.SourceId);
            var result = await Run(add);
            if (!result.Success || add.ItemId is not { } newItem)
                continue;
            var transform = sceneItem.Transform.WithPosition(sceneItem.Transform.X + 24, sceneItem.Transform.Y + 24);
            await Run(new SetItemTransformCommand(newItem, transform));
            last = newItem;
        }
        if (last is { } created)
            SelectFromCanvas(created, false);
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

    partial void OnTransitionIdChanged(string value)
    {
        // El ComboBox escribe null mientras se inicializa su lista: se ignora y se restaura el valor del proyecto.
        if (string.IsNullOrEmpty(value))
        {
            Dispatcher.UIThread.Post(() => TransitionId = Project.Transition.TypeId);
            return;
        }
        ApplyTransition();
    }

    partial void OnTransitionDurationMsChanged(int value) => ApplyTransition();

    private void ApplyTransition()
    {
        if (_refreshing)
            return;
        var json = TransitionId == Project.Transition.TypeId ? Project.Transition.SettingsJson : "{}";
        var settings = new TransitionSettings(TransitionId, TransitionDurationMs, json);
        if (settings == Project.Transition)
            return;
        if (TransitionId == TransitionCatalog.Stinger && TransitionCatalog.Validate(settings) is not null)
        {
            // Sin video no hay stinger: se vuelve a la transición actual y se abren sus ajustes.
            var requested = TransitionId;
            Dispatcher.UIThread.Post(() =>
            {
                TransitionId = Project.Transition.TypeId;
                TransitionNeedsSettings?.Invoke(this, requested);
            });
            return;
        }
        _ = Run(new SetTransitionCommand(settings));
    }

    // ---- Gráficos -------------------------------------------------------------------------------

    private void RefreshGraphics()
    {
        var ids = Project.Graphics.Reverse().Select(g => g.Id).ToList(); // arriba = capa superior
        Sync(Graphics, ids, id => new GraphicRowViewModel(id, Project.GetGraphic(id).Kind), r => r.Id);
        foreach (var row in Graphics)
            row.Name = Project.GetGraphic(row.Id).Name;
        RefreshGraphicsLive();
    }

    /// <summary>Estado en directo: al aire, cuenta atrás y marcador. Barato: se llama también desde el temporizador.</summary>
    public void RefreshGraphicsLive()
    {
        foreach (var row in Graphics)
        {
            if (Project.FindGraphic(row.Id) is not { } g)
                continue;
            row.OnAir = Session.Graphics.IsOnAir(row.Id);
            row.IsCountdown = GraphicTemplates.HasCountdown(g);
            switch (g.Kind)
            {
                case var _ when row.IsCountdown:
                    var status = Session.Graphics.GetCountdown(row.Id);
                    row.CountdownRunning = status.Running;
                    row.Detail = GraphicTemplates.FormatRemaining(status.Remaining) + (status.Running ? " ▶" : "");
                    break;
                case GraphicKind.Scoreboard:
                    row.Detail = $"{g.Field("home")} {g.Field("homeScore")} - {g.Field("awayScore")} {g.Field("away")}";
                    break;
                default:
                    row.Detail = row.KindLabel;
                    break;
            }
        }
    }

    private string NextGraphicName(string baseName)
    {
        if (!Project.HasGraphicNamed(baseName))
            return baseName;
        for (var i = 2; ; i++)
            if (!Project.HasGraphicNamed($"{baseName} {i}"))
                return $"{baseName} {i}";
    }

    [RelayCommand]
    private async Task AddGraphic(GraphicKind? kind)
    {
        if (kind is null)
        {
            await ImportGraphic();
            return;
        }
        var graphic = GraphicTemplates.Create(kind.Value, NextGraphicName(GraphicTemplates.DisplayName(kind.Value)), Project.Canvas, brand: Brand.Load());
        if ((await Run(new AddGraphicCommand(graphic))).Success)
            SelectGraphic(graphic.Id);
    }

    /// <summary>El refresco tras un comando llega por el despachador: se adelanta para poder seleccionar la fila nueva.</summary>
    private void SelectGraphic(Guid id)
    {
        RefreshGraphics();
        SelectedGraphic = Graphics.FirstOrDefault(g => g.Id == id);
    }

    public async Task<bool> AddGraphicFromTemplateAsync(string templateName)
    {
        var graphic = await Templates.InstantiateAsync(templateName, CancellationToken.None);
        graphic = graphic with { Name = NextGraphicName(graphic.Name) };
        var ok = (await Run(new AddGraphicCommand(graphic))).Success;
        if (ok)
            SelectGraphic(graphic.Id);
        return ok;
    }

    [RelayCommand]
    private async Task RemoveGraphic()
    {
        if (SelectedGraphic is not { } row)
            return;
        if (row.OnAir && !await Dialogs.ConfirmAsync("Eliminar gráfico", $"«{row.Name}» está al aire. ¿Retirarlo y eliminarlo?", "Eliminar", destructive: true))
            return;
        await Run(new RemoveGraphicCommand(row.Id));
    }

    [RelayCommand]
    private async Task DuplicateGraphic()
    {
        if (SelectedGraphic is not { } row || Project.FindGraphic(row.Id) is not { } g)
            return;
        var copy = g with { Id = Guid.NewGuid(), Name = NextGraphicName(g.Name), DataFile = null };
        if ((await Run(new AddGraphicCommand(copy))).Success)
            SelectGraphic(copy.Id);
    }

    [RelayCommand]
    private Task MoveGraphicUp() => MoveGraphic(+1);

    [RelayCommand]
    private Task MoveGraphicDown() => MoveGraphic(-1);

    private Task MoveGraphic(int delta)
    {
        if (SelectedGraphic is not { } row)
            return Task.CompletedTask;
        var index = Project.IndexOfGraphic(row.Id) + delta;
        return index < 0 || index >= Project.Graphics.Count ? Task.CompletedTask : Run(new MoveGraphicCommand(row.Id, index));
    }

    [RelayCommand]
    private async Task ToggleGraphic(GraphicRowViewModel row)
    {
        try
        {
            await Session.Graphics.ToggleAsync(row.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo lanzar «{row.Name}»: {ex.Message}";
            Infrastructure.Diagnostics.AppLog.Warn("gráficos", StatusMessage);
        }
        RefreshGraphicsLive();
    }

    [RelayCommand]
    private async Task TakeAllGraphicsOut()
    {
        await Session.Graphics.TakeAllOutAsync();
        RefreshGraphicsLive();
    }

    [RelayCommand]
    private void CountdownStartPause(GraphicRowViewModel row)
    {
        if (Session.Graphics.GetCountdown(row.Id).Running)
            Session.Graphics.PauseCountdown(row.Id);
        else
            Session.Graphics.StartCountdown(row.Id);
    }

    [RelayCommand]
    private void CountdownReset(GraphicRowViewModel row) => Session.Graphics.ResetCountdown(row.Id);

    [RelayCommand]
    private Task ScoreHome(GraphicRowViewModel row) => AddScore(row, "homeScore");

    [RelayCommand]
    private Task ScoreAway(GraphicRowViewModel row) => AddScore(row, "awayScore");

    /// <summary>Suma un tanto (deshacer lo corrige si fue un error del operador).</summary>
    private Task AddScore(GraphicRowViewModel row, string key)
    {
        if (Project.FindGraphic(row.Id) is not { } g)
            return Task.CompletedTask;
        var current = int.TryParse(g.Field(key), out var n) ? n : 0;
        var fields = new Dictionary<string, string>(g.Fields) { [key] = (current + 1).ToString() };
        return Run(new UpdateGraphicCommand(g with { Fields = fields }));
    }

    // ---- Diseño libre del editor visual --------------------------------------------------------

    /// <summary>
    /// Convierte una plantilla en diseño libre para el editor: las piezas pasan a ser elementos editables y los
    /// textos que mostraban un campo siguen como marcadores {campo}. Se deshace de una vez con Ctrl+Z.
    /// </summary>
    public async Task<bool> ConvertToDesignAsync(Guid graphicId)
    {
        if (Project.FindGraphic(graphicId) is not { } graphic)
            return false;
        if (graphic.Kind == GraphicKind.Custom)
            return true;
        if (Session.Graphics.IsOnAir(graphicId))
        {
            StatusMessage = "Retira el gráfico del aire antes de convertirlo en diseño libre.";
            return false;
        }
        // FromTemplate crea una copia con id nueva: la conversión se aplica al mismo gráfico.
        var converted = DesignOps.FromTemplate(graphic, Project.Canvas, graphic.Name) with { Id = graphic.Id };
        var result = await Run(new UpdateGraphicCommand(converted, allowKindChange: true));
        if (!result.Success)
            StatusMessage = result.Error;
        return result.Success;
    }

    [RelayCommand]
    private async Task ImportGraphic()
    {
        var path = await Dialogs.PickFileAsync("Importar gráfico", GraphicPackage.FileFilter);
        if (path is null)
            return;
        try
        {
            var imported = GraphicPackage.Import(path, Library);
            imported = imported with { Name = NextGraphicName(imported.Name) };
            if ((await Run(new AddGraphicCommand(imported))).Success)
                SelectGraphic(imported.Id);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException
                or NotSupportedException or UnauthorizedAccessException)
        {
            await Dialogs.InfoAsync("Importar gráfico", ex.Message);
        }
    }

    /// <summary>Exporta un gráfico con sus recursos a un archivo .lsgfx, para usarlo en otro equipo o proyecto.</summary>
    public async Task ExportGraphicAsync(Guid graphicId)
    {
        if (Project.FindGraphic(graphicId) is not { } graphic)
            return;
        var path = await Dialogs.SaveFileAsync("Exportar gráfico", graphic.Name, GraphicPackage.Extension.TrimStart('.'));
        if (path is null)
            return;
        try
        {
            GraphicPackage.Export(graphic, path);
            StatusMessage = "Gráfico exportado: " + path;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException
                or UnauthorizedAccessException)
        {
            await Dialogs.InfoAsync("Exportar gráfico", ex.Message);
        }
    }

    // ---- Mezclador ------------------------------------------------------------------------------

    public Task SetChannelVolume(MixerChannelViewModel channel, double db) =>
        channel.Syncing ? Task.CompletedTask : Run(new SetVolumeCommand(channel.SourceId, MixerChannelViewModel.ToMultiplier(db)));

    [RelayCommand]
    private Task ToggleMute(MixerChannelViewModel channel) => Run(new SetMutedCommand(channel.SourceId, !channel.Muted));

    private int _meterTicks;

    private async Task PollMetersAsync()
    {
        if (++_meterTicks % 6 == 0 && Graphics.Any(g => g.IsCountdown))
            RefreshGraphicsLive();
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
                if (await Dialogs.ConfirmAsync("Detener transmisión", "¿Detener la transmisión en directo?", "Detener", destructive: true))
                    await StopStreamAsync(CancellationToken.None);
                return;
            }

            IEnumerable<StreamDestination> targets = SelectedStreamMode switch
            {
                StreamOutputMode.Horizontal => DualStream.GetDestinationsFor(CompositionFormat.Horizontal),
                StreamOutputMode.Vertical => DualStream.GetDestinationsFor(CompositionFormat.Vertical),
                _ => Session.EnabledDestinations
            };
            var targetList = targets.Where(d => d.Enabled).ToList();
            if (targetList.Count == 0)
            {
                var modeDesc = SelectedStreamMode switch
                {
                    StreamOutputMode.Horizontal => "horizontal (16:9)",
                    StreamOutputMode.Vertical => "vertical (9:16)",
                    _ => "dual (16:9 + 9:16)"
                };
                await Dialogs.InfoAsync("Transmisión", $"No hay destinos activos compatibles con el modo de emisión {modeDesc}.\n\nRevisa los destinos configurados en el panel «Destinos».");
                return;
            }

            var names = string.Join(", ", targetList.Select(d => d.Name));
            var (confirmTitle, confirmMsg) = SelectedStreamMode switch
            {
                StreamOutputMode.Horizontal => ("Iniciar emisión 16:9", $"Se emitirá en directo en formato Horizontal (16:9) a:\n{names}\n\n¿Continuar?"),
                StreamOutputMode.Vertical => ("Iniciar emisión 9:16", $"Se emitirá en directo en formato Vertical (9:16) a:\n{names}\n\n¿Continuar?"),
                _ => ("Iniciar emisión Dual simultánea", $"Se emitirá en directo en formato Dual (16:9 + 9:16) a:\n{names}\n\n¿Continuar?")
            };

            if (await Dialogs.ConfirmAsync(confirmTitle, confirmMsg, "Transmitir"))
            {
                if (SelectedStreamMode == StreamOutputMode.Horizontal)
                    await StartHorizontalStreamAsync();
                else if (SelectedStreamMode == StreamOutputMode.Vertical)
                    await StartVerticalStreamAsync();
                else
                    await StartStreamAsync(CancellationToken.None);
            }
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
                await StopRecordingAsync(CancellationToken.None);
                return;
            }
            var folder = Path.GetDirectoryName(Session.NextRecordingPath(DateTime.Now))!;
            Directory.CreateDirectory(folder);
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
            if (free < 2L * 1024 * 1024 * 1024 &&
                !await Dialogs.ConfirmAsync("Poco espacio", $"Quedan {free / 1048576} MB libres en la unidad de grabación. ¿Grabar igualmente?", "Grabar"))
                return;
            await StartRecordingAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Grabación: {ex.Message}";
            await Dialogs.InfoAsync("Grabación", ex.Message);
        }
    }

    // IOutputController: el mismo camino para los botones y para el asistente (que confirma por su cuenta).
    // Puede llamarse desde otro hilo: el estado de la UI se actualiza en el hilo de UI.

    public async Task StartStreamAsync(CancellationToken ct)
    {
        if (!EngineReady)
            throw new InvalidOperationException("El motor no está iniciado.");
        await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "Conectando…");
        var results = await Session.StartStreamAsync(ct);
        var failed = results.Where(r => r.Error is not null).ToList();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _streamClock.Restart();
            _lastStreamBytes = 0;
            _lastStreamSample = DateTime.UtcNow;
            _wasStreaming = true;
            IsStreaming = true;
            StatusMessage = failed.Count == 0
                ? $"Transmisión iniciada ({results.Count} destino{(results.Count == 1 ? "" : "s")})."
                : $"Transmisión iniciada; no arrancaron: {string.Join("; ", failed.Select(f => f.Error))}";
        });
    }

    public async Task StopStreamAsync(CancellationToken ct)
    {
        await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "Deteniendo transmisión…");
        await Session.StopStreamAsync(ct);
        await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "Transmisión detenida.");
    }

    public async Task<string> StartRecordingAsync(CancellationToken ct)
    {
        if (!EngineReady)
            throw new InvalidOperationException("El motor no está iniciado.");
        var path = await Session.StartRecordingAsync(ct);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            LastRecordingPath = path;
            _recordClock.Restart();
            IsRecording = true;
            StatusMessage = $"Grabando en {path}";
        });
        return path;
    }

    public async Task StopRecordingAsync(CancellationToken ct)
    {
        await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "Finalizando grabación…");
        await Engine.StopRecordingAsync(ct);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsRecording = false;
            _recordClock.Reset();
            StatusMessage = $"Grabación guardada: {LastRecordingPath}";
        });
    }

    private async Task PollMetricsAsync()
    {
        if (!EngineReady)
            return;
        try
        {
            var m = await Engine.GetMetricsAsync(CancellationToken.None);
            await Session.Destinations.TickAsync(DateTime.UtcNow);
            await Optimizer.TickAsync(m, DateTime.UtcNow, Session.Destinations.Statuses);
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
            // Mientras un destino conecta, libobs aún no lo da por activo: cuenta como emisión en curso.
            var live = m.IsStreaming || Session.Destinations.AnyLive;
            IsStreaming = live;

            // Todos los destinos cayeron solos (red, clave inválida): se informa con el motivo real de cada uno.
            if (_wasStreaming && !live)
            {
                _wasStreaming = false;
                _streamClock.Reset();
                BitrateText = "0 kb/s";
                var failures = Session.Destinations.Statuses.Where(s => s.Error is not null).Select(s => $"{s.Name}: {s.Error}").ToList();
                if (failures.Count > 0)
                {
                    StatusMessage = string.Join(" · ", failures);
                    await Dialogs.InfoAsync("La transmisión se detuvo", string.Join("\n\n", failures));
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
        var path = await Dialogs.PickFileAsync("Abrir proyecto", "Proyectos LiveStream (*.spproj)");
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
        Assistant.Cancel();
        _metricsTimer.Stop();
        _meterTimer.Stop();
        await Plugins.DisposeAsync();
        await Session.DisposeAsync();
    }
}
