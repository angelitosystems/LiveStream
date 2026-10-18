using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.ValueObjects;
using StreamPilot.Native.Handles;
using StreamPilot.Native.Interop;

namespace StreamPilot.Native.Engine;

/// <summary>Implementación de <see cref="IStreamingEngine"/> sobre streampilot_bridge/libobs.</summary>
public sealed class LibObsEngine : IStreamingEngine
{
    private readonly NativeDispatcher _dispatcher = new("StreamPilot.LibObs");
    private EngineSafeHandle? _engine;

    /// <summary>Mensajes de log de libobs. Se invoca desde hilos nativos: no tocar la UI directamente.</summary>
    public static event Action<string, string>? Log;

    public bool IsInitialized => _engine is { IsInvalid: false, IsClosed: false };

    /// <summary>Comprueba que la DLL del puente existe y su ABI coincide, sin inicializar libobs.</summary>
    public static bool TryProbeBridge(out string error)
    {
        try
        {
            var version = NativeMethods.AbiVersion();
            if (version != NativeMethods.ExpectedAbiVersion)
            {
                error = $"ABI del puente {version}, se esperaba {NativeMethods.ExpectedAbiVersion}.";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (DllNotFoundException)
        {
            error = $"No se encontró {NativeMethods.Library}.dll. Compila native/streampilot-bridge.";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public Task InitializeAsync(EngineSettings settings, CancellationToken ct) => _dispatcher.InvokeAsync(() =>
    {
        if (IsInitialized)
            throw new InvalidOperationException("El motor ya está inicializado.");
        if (!TryProbeBridge(out var probeError))
            throw new InvalidOperationException(probeError);

        unsafe
        {
            NativeMethods.SetLogCallback(&OnNativeLog, 0);
        }

        var appDir = AppContext.BaseDirectory;
        using var pin = new Utf8Pin();
        var config = new SpEngineConfig
        {
            BaseWidth = (uint)settings.BaseWidth,
            BaseHeight = (uint)settings.BaseHeight,
            OutputWidth = (uint)settings.OutputWidth,
            OutputHeight = (uint)settings.OutputHeight,
            FpsNum = (uint)settings.FpsNumerator,
            FpsDen = (uint)settings.FpsDenominator,
            AudioSampleRate = (uint)settings.AudioSampleRate,
            ModuleBinDir = pin.Add(settings.ModulesDirectory ?? Path.Combine(appDir, "obs-plugins", "64bit")),
            ModuleDataDir = pin.Add(Path.Combine(settings.DataDirectory ?? Path.Combine(appDir, "data"), "obs-plugins", "%module%")),
            LibobsDataDir = pin.Add(Path.Combine(settings.DataDirectory ?? Path.Combine(appDir, "data"), "libobs") + Path.DirectorySeparatorChar),
        };

        Check(NativeMethods.EngineCreate(in config, out var engine), "sp_engine_create");
        _engine = engine;
    }, ct);

    public Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync(CancellationToken ct) => _dispatcher.InvokeAsync<IReadOnlyList<CaptureDevice>>(() =>
    {
        var devices = new List<CaptureDevice>();
        var handle = GCHandle.Alloc(devices);
        try
        {
            unsafe
            {
                Check(NativeMethods.DevicesEnumerate(RequireEngine(), &OnDevice, GCHandle.ToIntPtr(handle)), "sp_devices_enumerate");
            }
        }
        finally
        {
            handle.Free();
        }
        return devices;
    }, ct);

    public Task CreateSceneAsync(Guid sceneId, string name, CancellationToken ct) => _dispatcher.InvokeAsync(() =>
        Check(NativeMethods.SceneCreate(RequireEngine(), sceneId.ToString("D"), name), "sp_scene_create"), ct);

    public Task SetProgramSceneAsync(Guid sceneId, CancellationToken ct) => _dispatcher.InvokeAsync(() =>
        Check(NativeMethods.SceneSetProgram(RequireEngine(), sceneId.ToString("D")), "sp_scene_set_program"), ct);

    public Task AddSourceAsync(Guid sceneId, SourceDefinition source, CancellationToken ct) => _dispatcher.InvokeAsync(() =>
    {
        var json = JsonSerializer.Serialize(source.Settings);
        Check(NativeMethods.SceneAddSource(
            RequireEngine(), sceneId.ToString("D"), source.Id.ToString("D"), SourceTypeMap.ToObsId(source.Kind), source.Name, json),
            "sp_scene_add_source");
    }, ct);

    public Task StartRecordingAsync(RecordingSettings settings, CancellationToken ct) => _dispatcher.InvokeAsync(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.OutputPath))!);
        using var pin = new Utf8Pin();
        var config = new SpRecordingConfig
        {
            OutputPath = pin.Add(Path.GetFullPath(settings.OutputPath)),
            VideoBitrateKbps = (uint)settings.VideoBitrateKbps,
            AudioBitrateKbps = (uint)settings.AudioBitrateKbps,
            VideoEncoderId = pin.Add(settings.Encoder),
        };
        Check(NativeMethods.RecordingStart(RequireEngine(), in config), "sp_recording_start");
    }, ct);

    public Task StopRecordingAsync(CancellationToken ct) => _dispatcher.InvokeAsync(() =>
        Check(NativeMethods.RecordingStop(RequireEngine()), "sp_recording_stop"), ct);

    public Task<EngineMetrics> GetMetricsAsync(CancellationToken ct) => _dispatcher.InvokeAsync(() =>
    {
        Check(NativeMethods.MetricsGet(RequireEngine(), out var m), "sp_metrics_get");
        return new EngineMetrics(m.ActiveFps, m.AverageFrameRenderMs, m.TotalFrames, m.LaggedFrames, m.SkippedEncoderFrames, m.IsRecording != 0);
    }, ct);

    public Task AttachPreviewAsync(nint nativeWindow, uint width, uint height, CancellationToken ct = default) => _dispatcher.InvokeAsync(() =>
        Check(NativeMethods.PreviewAttach(RequireEngine(), nativeWindow, width, height), "sp_preview_attach"), ct);

    public Task ResizePreviewAsync(uint width, uint height, CancellationToken ct = default) => _dispatcher.InvokeAsync(() =>
        Check(NativeMethods.PreviewResize(RequireEngine(), width, height), "sp_preview_resize"), ct);

    public Task DetachPreviewAsync(CancellationToken ct = default) => _dispatcher.InvokeAsync(() =>
    {
        if (IsInitialized)
            Check(NativeMethods.PreviewDetach(_engine!), "sp_preview_detach");
    }, ct);

    /// <summary>Apaga libobs en el mismo hilo que lo inicializó.</summary>
    public Task ShutdownAsync(CancellationToken ct) => _dispatcher.InvokeAsync(() =>
    {
        _engine?.Dispose();
        _engine = null;
    }, ct);

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        _dispatcher.Dispose();
    }

    private EngineSafeHandle RequireEngine() =>
        IsInitialized ? _engine! : throw new InvalidOperationException("El motor no está inicializado.");

    private static void Check(SpResult result, string operation)
    {
        if (result != SpResult.Ok)
            throw new NativeEngineException(operation, (int)result, NativeMethods.GetLastError());
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnDevice(SpDeviceKind kind, byte* id, byte* name, nint userData)
    {
        var devices = (List<CaptureDevice>)GCHandle.FromIntPtr(userData).Target!;
        devices.Add(new CaptureDevice(
            Marshal.PtrToStringUTF8((nint)id) ?? string.Empty,
            Marshal.PtrToStringUTF8((nint)name) ?? string.Empty,
            (CaptureDeviceKind)kind));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativeLog(SpLogLevel level, byte* message, nint userData)
    {
        try
        {
            Log?.Invoke(level.ToString(), Marshal.PtrToStringUTF8((nint)message) ?? string.Empty);
        }
        catch
        {
            // Nunca propagar excepciones administradas a través del límite nativo.
        }
    }
}
