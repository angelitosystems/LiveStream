using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;
using StreamPilot.Native.Engine;

namespace StreamPilot.Desktop.ViewModels;

/// <summary>
/// Pantalla del spike de la Fase 1: inicializar libobs, enumerar dispositivos, armar una escena
/// con cámara y micrófono, vista previa y grabación de 30 segundos.
/// </summary>
public sealed partial class EngineSpikeViewModel : ObservableObject
{
    private static readonly TimeSpan RecordingLength = TimeSpan.FromSeconds(30);

    private readonly LibObsEngine _engine;
    private (nint Hwnd, uint W, uint H)? _pendingPreview;
    private Guid? _sceneId;

    public EngineSpikeViewModel(LibObsEngine engine)
    {
        _engine = engine;
        LibObsEngine.Log += (level, message) =>
        {
            if (level is "Error" or "Warning")
                Dispatcher.UIThread.Post(() => AppendLog($"[obs {level}] {message}"));
        };
        BridgeAvailable = LibObsEngine.TryProbeBridge(out var error);
        Status = BridgeAvailable ? "Puente nativo detectado. Pulsa Inicializar." : error;
    }

    public ObservableCollection<CaptureDevice> Devices { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty] private bool _bridgeAvailable;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _metricsText = "—";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildSceneCommand), nameof(RecordTestCommand))]
    private bool _isInitialized;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordTestCommand))]
    private bool _hasScene;

    [RelayCommand]
    private Task InitializeAsync() => RunAsync("Inicializando libobs…", async () =>
    {
        await _engine.InitializeAsync(new EngineSettings(), CancellationToken.None);
        IsInitialized = true;

        Devices.Clear();
        foreach (var device in await _engine.ListDevicesAsync(CancellationToken.None))
            Devices.Add(device);

        if (_pendingPreview is { } p)
            await _engine.AttachPreviewAsync(p.Hwnd, p.W, p.H);

        _ = PollMetricsAsync();
        return $"Motor inicializado. {Devices.Count} dispositivos encontrados.";
    });

    [RelayCommand(CanExecute = nameof(IsInitialized))]
    private Task BuildSceneAsync() => RunAsync("Creando escena…", async () =>
    {
        var camera = Devices.FirstOrDefault(d => d.Kind == CaptureDeviceKind.Camera);
        var mic = Devices.FirstOrDefault(d => d.Kind == CaptureDeviceKind.Microphone);

        var sceneId = Guid.NewGuid();
        await _engine.CreateSceneAsync(sceneId, "Spike", CancellationToken.None);

        if (camera is not null)
            await _engine.AddSourceAsync(sceneId, new SourceDefinition(Guid.NewGuid(), "Cámara", SourceKind.VideoCaptureDevice,
                new Dictionary<string, string> { ["video_device_id"] = camera.Id }), CancellationToken.None);
        else
            await _engine.AddSourceAsync(sceneId, new SourceDefinition(Guid.NewGuid(), "Pantalla", SourceKind.DisplayCapture,
                new Dictionary<string, string>()), CancellationToken.None);

        if (mic is not null)
            await _engine.AddSourceAsync(sceneId, new SourceDefinition(Guid.NewGuid(), "Micrófono", SourceKind.AudioInput,
                new Dictionary<string, string> { ["device_id"] = mic.Id }), CancellationToken.None);

        await _engine.SetProgramSceneAsync(sceneId, CancellationToken.None);
        _sceneId = sceneId;
        HasScene = true;
        return $"Escena creada con {(camera?.Name ?? "captura de pantalla")} y {(mic?.Name ?? "sin micrófono")}.";
    });

    private bool CanRecord() => IsInitialized && HasScene;

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private Task RecordTestAsync() => RunAsync("Grabando 30 s…", async () =>
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "StreamPilot",
            $"spike-{DateTime.Now:yyyyMMdd-HHmmss}.mkv");
        await _engine.StartRecordingAsync(new RecordingSettings(path), CancellationToken.None);
        await Task.Delay(RecordingLength);
        await _engine.StopRecordingAsync(CancellationToken.None);
        return $"Grabación guardada en {path}";
    });

    public async Task OnPreviewCreatedAsync(nint hwnd, uint width, uint height)
    {
        _pendingPreview = (hwnd, width, height);
        if (_engine.IsInitialized)
            await RunAsync("Conectando vista previa…", async () =>
            {
                await _engine.AttachPreviewAsync(hwnd, width, height);
                return "Vista previa conectada.";
            });
    }

    public async Task OnPreviewResizedAsync(uint width, uint height)
    {
        if (_pendingPreview is { } p)
            _pendingPreview = (p.Hwnd, width, height);
        if (_engine.IsInitialized)
            await _engine.ResizePreviewAsync(width, height);
    }

    public async Task OnPreviewDestroyingAsync()
    {
        _pendingPreview = null;
        await _engine.DetachPreviewAsync();
    }

    private async Task PollMetricsAsync()
    {
        while (_engine.IsInitialized)
        {
            try
            {
                var m = await _engine.GetMetricsAsync(CancellationToken.None);
                MetricsText = $"{m.ActiveFps:F1} FPS · render {m.AverageFrameRenderMs:F2} ms · " +
                              $"lag {m.LaggedFrames}/{m.TotalFrames} · encoder omitidos {m.SkippedEncoderFrames}" +
                              (m.IsRecording ? " · ● REC" : string.Empty);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NativeEngineException)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task RunAsync(string busyText, Func<Task<string>> action)
    {
        Status = busyText;
        try
        {
            Status = await action();
            AppendLog(Status);
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            AppendLog(Status);
        }
    }

    private void AppendLog(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
        while (LogLines.Count > 200)
            LogLines.RemoveAt(0);
    }
}
