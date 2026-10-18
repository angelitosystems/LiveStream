using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.Animation;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;
using StreamPilot.Native.Handles;
using StreamPilot.Native.Interop;

namespace StreamPilot.Native.Engine;

/// <summary>Implementación de <see cref="IStreamingEngine"/> sobre streampilot_bridge/libobs.</summary>
public sealed class LibObsEngine : IStreamingEngine, IEngineDisplays
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
                error = $"ABI del puente {version}, se esperaba {NativeMethods.ExpectedAbiVersion}. Ejecuta scripts/build-native.ps1.";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (DllNotFoundException)
        {
            error = $"No se encontró {NativeMethods.Library}.dll. Ejecuta scripts/build-native.ps1.";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---- Motor ---------------------------------------------------------------------------------

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
        var dataDir = settings.DataDirectory ?? Path.Combine(appDir, "data");

        // libobs y varios plugins (p. ej. los hooks de win-capture) resuelven "..\..\data" y
        // "..\..\obs-plugins\64bit" respecto al directorio de trabajo, porque OBS se ejecuta desde
        // bin\64bit. Se fija un directorio ancla dos niveles por debajo de la raíz de la app para
        // reproducir esa estructura. La app solo usa rutas absolutas, así que el cambio es seguro.
        var useDefaultLayout = settings.ModulesDirectory is null && settings.DataDirectory is null;
        if (useDefaultLayout)
        {
            var anchor = Directory.CreateDirectory(Path.Combine(appDir, "obs-anchor", "64bit")).FullName;
            Directory.SetCurrentDirectory(anchor);
        }

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
            AdapterIndex = (uint)settings.AdapterIndex,
            // Con la estructura por defecto libobs ya registra estas rutas; añadirlas otra vez duplicaría módulos.
            ModuleBinDir = useDefaultLayout ? 0 : pin.Add(ObsPath(settings.ModulesDirectory ?? Path.Combine(appDir, "obs-plugins", "64bit"))),
            ModuleDataDir = useDefaultLayout ? 0 : pin.Add(ObsPath(Path.Combine(dataDir, "obs-plugins", "%module%"))),
            LibobsDataDir = pin.Add(ObsPath(Path.Combine(dataDir, "libobs")) + "/"),
            ModuleConfigDir = pin.Add(ObsPath(Directory.CreateDirectory(settings.ConfigDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamPilot", "plugin_config")).FullName)),
        };

        Check(NativeMethods.EngineCreate(in config, out var engine), "sp_engine_create");
        _engine = engine;
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

    public Task<EngineMetrics> GetMetricsAsync(CancellationToken ct) => Run(e =>
    {
        Check(NativeMethods.MetricsGet(e, out var m), "sp_metrics_get");
        return new EngineMetrics(m.ActiveFps, m.AverageFrameRenderMs, m.TotalFrames, m.LaggedFrames, m.SkippedEncoderFrames,
            m.IsRecording != 0, m.CpuUsagePercent, m.MemoryBytes, m.EncodedFrames, m.IsStreaming != 0, m.StreamReconnecting != 0,
            m.StreamDroppedFrames, m.StreamTotalFrames, m.StreamTotalBytes, m.RecordingTotalBytes);
    }, ct);

    public Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync(CancellationToken ct) => Run<IReadOnlyList<CaptureDevice>>(e =>
    {
        var devices = new List<CaptureDevice>();
        var handle = GCHandle.Alloc(devices);
        try
        {
            unsafe
            {
                Check(NativeMethods.DevicesEnumerate(e, &OnDevice, GCHandle.ToIntPtr(handle)), "sp_devices_enumerate");
            }
        }
        finally
        {
            handle.Free();
        }
        return devices;
    }, ct);

    public Task<IReadOnlyList<VideoEncoderInfo>> ListVideoEncodersAsync(CancellationToken ct) => Run<IReadOnlyList<VideoEncoderInfo>>(e =>
    {
        var encoders = new List<VideoEncoderInfo>();
        var handle = GCHandle.Alloc(encoders);
        try
        {
            unsafe
            {
                Check(NativeMethods.VideoEncodersEnumerate(e, &OnEncoder, GCHandle.ToIntPtr(handle)), "sp_video_encoders_enumerate");
            }
        }
        finally
        {
            handle.Free();
        }
        return encoders;
    }, ct);

    // ---- Escenas ---------------------------------------------------------------------------------

    public Task CreateSceneAsync(Guid sceneId, string name, CancellationToken ct) =>
        Run(e => Check(NativeMethods.SceneCreate(e, Id(sceneId), name), "sp_scene_create"), ct);

    public Task RemoveSceneAsync(Guid sceneId, CancellationToken ct) =>
        Run(e => Check(NativeMethods.SceneRemove(e, Id(sceneId)), "sp_scene_remove"), ct);

    public Task RenameSceneAsync(Guid sceneId, string name, CancellationToken ct) =>
        Run(e => Check(NativeMethods.SceneRename(e, Id(sceneId), name), "sp_scene_rename"), ct);

    public Task SetProgramSceneAsync(Guid sceneId, CancellationToken ct) =>
        Run(e => Check(NativeMethods.SceneSetProgram(e, Id(sceneId)), "sp_scene_set_program"), ct);

    public Task SetTransitionAsync(TransitionSettings transition, CancellationToken ct) =>
        Run(e => Check(NativeMethods.TransitionSet(e, transition.TypeId, (uint)Math.Max(0, transition.DurationMs),
            string.IsNullOrWhiteSpace(transition.SettingsJson) ? "{}" : transition.SettingsJson), "sp_transition_set"), ct);

    // ---- Fuentes ---------------------------------------------------------------------------------

    public Task CreateSourceAsync(SourceDefinition source, CancellationToken ct) => Run(e =>
    {
        if (!SourceTypeMap.IsAvailable(source.Kind))
            throw new NotSupportedException($"El tipo de fuente {source.Kind} no está disponible en este runtime.");
        Check(NativeMethods.SourceCreate(e, Id(source.Id), SourceTypeMap.ToObsId(source.Kind), source.Name, source.SettingsJson),
            "sp_source_create");
    }, ct);

    public Task RemoveSourceAsync(Guid sourceId, CancellationToken ct) =>
        Run(e => Check(NativeMethods.SourceRemove(e, Id(sourceId)), "sp_source_remove"), ct);

    public Task RenameSourceAsync(Guid sourceId, string name, CancellationToken ct) =>
        Run(e => Check(NativeMethods.SourceRename(e, Id(sourceId), name), "sp_source_rename"), ct);

    public Task UpdateSourceSettingsAsync(Guid sourceId, string settingsJson, CancellationToken ct) =>
        Run(e => Check(NativeMethods.SourceUpdate(e, Id(sourceId), settingsJson), "sp_source_update"), ct);

    public Task<string> GetSourceSettingsAsync(Guid sourceId, CancellationToken ct) => Run(e =>
    {
        var id = Id(sourceId);
        unsafe
        {
            Check(NativeMethods.ReadString((byte* b, int s, out int r) => NativeMethods.SourceGetSettings(e, id, b, s, out r), out var json),
                "sp_source_get_settings");
            return json;
        }
    }, ct);

    public Task<IReadOnlyList<SourceProperty>> GetSourcePropertiesAsync(Guid sourceId, CancellationToken ct) => Run(e =>
    {
        var id = Id(sourceId);
        unsafe
        {
            Check(NativeMethods.ReadString((byte* b, int s, out int r) => NativeMethods.SourceGetProperties(e, id, b, s, out r), out var json),
                "sp_source_get_properties");
            return PropertyJsonParser.Parse(json);
        }
    }, ct);

    public Task<(int Width, int Height)> GetSourceSizeAsync(Guid sourceId, CancellationToken ct) => Run(e =>
    {
        Check(NativeMethods.SourceGetSize(e, Id(sourceId), out var w, out var h), "sp_source_get_size");
        return ((int)w, (int)h);
    }, ct);

    public Task SetGlobalAudioChannelAsync(Guid sourceId, int channel, CancellationToken ct) =>
        Run(e => Check(NativeMethods.SourceSetOutputChannel(e, Id(sourceId), (uint)channel), "sp_source_set_output_channel"), ct);

    // ---- Audio -----------------------------------------------------------------------------------

    public Task SetVolumeAsync(Guid sourceId, double multiplier, CancellationToken ct) =>
        Run(e => Check(NativeMethods.AudioSetVolume(e, Id(sourceId), (float)multiplier), "sp_audio_set_volume"), ct);

    public Task SetMutedAsync(Guid sourceId, bool muted, CancellationToken ct) =>
        Run(e => Check(NativeMethods.AudioSetMuted(e, Id(sourceId), muted ? 1 : 0), "sp_audio_set_muted"), ct);

    public Task SetMonitoringAsync(Guid sourceId, AudioMonitoring monitoring, CancellationToken ct) =>
        Run(e => Check(NativeMethods.AudioSetMonitoring(e, Id(sourceId), (int)monitoring), "sp_audio_set_monitoring"), ct);

    public Task SetSyncOffsetAsync(Guid sourceId, int offsetMs, CancellationToken ct) =>
        Run(e => Check(NativeMethods.AudioSetSyncOffset(e, Id(sourceId), offsetMs), "sp_audio_set_sync_offset"), ct);

    public Task<AudioLevels?> GetAudioLevelsAsync(Guid sourceId, CancellationToken ct) => Run<AudioLevels?>(e =>
    {
        var rc = NativeMethods.AudioGetLevels(e, Id(sourceId), out var l);
        if (rc == SpResult.NotFound)
            return null;
        Check(rc, "sp_audio_get_levels");
        unsafe
        {
            return new AudioLevels(l.PeakDb[0], l.PeakDb[1], l.MagnitudeDb[0], l.MagnitudeDb[1], (int)l.Channels);
        }
    }, ct);

    // ---- Elementos -------------------------------------------------------------------------------

    // ---- Filtros ---------------------------------------------------------------------------------

    public Task<IReadOnlyList<EngineTypeInfo>> ListFilterTypesAsync(CancellationToken ct) => Run<IReadOnlyList<EngineTypeInfo>>(e =>
    {
        var types = new List<EngineTypeInfo>();
        var handle = GCHandle.Alloc(types);
        try
        {
            unsafe
            {
                Check(NativeMethods.FilterTypesEnumerate(e, &OnType, GCHandle.ToIntPtr(handle)), "sp_filter_types_enumerate");
            }
        }
        finally
        {
            handle.Free();
        }
        return types;
    }, ct);

    public Task<IReadOnlyList<EngineTypeInfo>> ListTransitionTypesAsync(CancellationToken ct) => Run<IReadOnlyList<EngineTypeInfo>>(e =>
    {
        var types = new List<EngineTypeInfo>();
        var handle = GCHandle.Alloc(types);
        try
        {
            unsafe
            {
                Check(NativeMethods.TransitionTypesEnumerate(e, &OnType, GCHandle.ToIntPtr(handle)), "sp_transition_types_enumerate");
            }
        }
        finally
        {
            handle.Free();
        }
        return types;
    }, ct);

    public Task AddFilterAsync(Guid sourceId, Guid filterId, string typeId, string name, string settingsJson, CancellationToken ct) =>
        Run(e => Check(NativeMethods.FilterAdd(e, Id(sourceId), Id(filterId), typeId, name,
            string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson), "sp_filter_add"), ct);

    public Task RemoveFilterAsync(Guid filterId, CancellationToken ct) =>
        Run(e => Check(NativeMethods.FilterRemove(e, Id(filterId)), "sp_filter_remove"), ct);

    public Task UpdateFilterAsync(Guid filterId, string settingsJson, CancellationToken ct) =>
        Run(e => Check(NativeMethods.FilterUpdate(e, Id(filterId), settingsJson), "sp_filter_update"), ct);

    public Task SetFilterEnabledAsync(Guid filterId, bool enabled, CancellationToken ct) =>
        Run(e => Check(NativeMethods.FilterSetEnabled(e, Id(filterId), enabled ? 1 : 0), "sp_filter_set_enabled"), ct);

    // ---- Animación -------------------------------------------------------------------------------

    public Task PlayAnimationAsync(Guid itemId, AnimationClip clip, AnimationFlags flags, CancellationToken ct)
    {
        if (clip.Validate() is { } error)
            throw new ArgumentException(error, nameof(clip));
        var json = AnimationClipJson.Serialize(clip);
        return Run(e => Check(NativeMethods.AnimPlay(e, Id(itemId), json, (int)flags), "sp_anim_play"), ct);
    }

    public Task StopAnimationAsync(Guid itemId, CancellationToken ct) =>
        Run(e => Check(NativeMethods.AnimStop(e, Id(itemId)), "sp_anim_stop"), ct);

    public Task<bool> IsAnimationPlayingAsync(Guid itemId, CancellationToken ct) => Run(e =>
    {
        Check(NativeMethods.AnimIsPlaying(e, Id(itemId), out var playing), "sp_anim_is_playing");
        return playing != 0;
    }, ct);

    // ---- Elementos -------------------------------------------------------------------------------

    public Task AddItemAsync(Guid sceneId, Guid itemId, Guid sourceId, CancellationToken ct) =>
        Run(e => Check(NativeMethods.ItemAdd(e, Id(sceneId), Id(itemId), Id(sourceId)), "sp_item_add"), ct);

    public Task RemoveItemAsync(Guid itemId, CancellationToken ct) =>
        Run(e => Check(NativeMethods.ItemRemove(e, Id(itemId)), "sp_item_remove"), ct);

    public Task SetItemTransformAsync(Guid itemId, Transform t, CancellationToken ct) => Run(e =>
    {
        var native = new SpTransform
        {
            PosX = (float)t.X, PosY = (float)t.Y, ScaleX = (float)t.ScaleX, ScaleY = (float)t.ScaleY, RotationDeg = (float)t.RotationDegrees,
            CropLeft = t.CropLeft, CropTop = t.CropTop, CropRight = t.CropRight, CropBottom = t.CropBottom,
        };
        Check(NativeMethods.ItemSetTransform(e, Id(itemId), in native), "sp_item_set_transform");
    }, ct);

    public Task<Transform> GetItemTransformAsync(Guid itemId, CancellationToken ct) => Run(e =>
    {
        Check(NativeMethods.ItemGetTransform(e, Id(itemId), out var t), "sp_item_get_transform");
        return new Transform(t.PosX, t.PosY, t.ScaleX, t.ScaleY, t.RotationDeg, t.CropLeft, t.CropTop, t.CropRight, t.CropBottom);
    }, ct);

    public Task SetItemVisibleAsync(Guid itemId, bool visible, CancellationToken ct) =>
        Run(e => Check(NativeMethods.ItemSetVisible(e, Id(itemId), visible ? 1 : 0), "sp_item_set_visible"), ct);

    public Task SetItemLockedAsync(Guid itemId, bool locked, CancellationToken ct) =>
        Run(e => Check(NativeMethods.ItemSetLocked(e, Id(itemId), locked ? 1 : 0), "sp_item_set_locked"), ct);

    public Task SetItemOrderAsync(Guid itemId, int index, CancellationToken ct) =>
        Run(e => Check(NativeMethods.ItemSetOrder(e, Id(itemId), index), "sp_item_set_order"), ct);

    // ---- Salidas ---------------------------------------------------------------------------------

    public Task StartRecordingAsync(RecordingSettings settings, CancellationToken ct) => Run(e =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.OutputPath))!);
        using var pin = new Utf8Pin();
        var config = new SpRecordingConfig { OutputPath = pin.Add(ObsPath(settings.OutputPath)), Encoder = Encoder(pin, settings.Encoder) };
        Check(NativeMethods.RecordingStart(e, in config), "sp_recording_start");
    }, ct);

    public Task StopRecordingAsync(CancellationToken ct) =>
        Run(e => Check(NativeMethods.RecordingStop(e), "sp_recording_stop"), ct);

    public Task StartStreamAsync(StreamTarget target, CancellationToken ct) => Run(e =>
    {
        using var pin = new Utf8Pin(); // borra la clave de la memoria no administrada al terminar
        var config = new SpStreamConfig { Server = pin.Add(target.Server), Key = pin.Add(target.Key), Encoder = Encoder(pin, target.Encoder) };
        Check(NativeMethods.StreamStart(e, in config), "sp_stream_start");
    }, ct);

    public Task StopStreamAsync(CancellationToken ct) =>
        Run(e => Check(NativeMethods.StreamStop(e), "sp_stream_stop"), ct);

    public Task<OutputStopInfo> GetLastStopAsync(OutputKind kind, CancellationToken ct) => Run(e =>
    {
        var code = 0;
        unsafe
        {
            Check(NativeMethods.ReadString((byte* b, int s, out int r) => NativeMethods.OutputLastStop(e, (int)kind, out code, b, s, out r), out var message),
                "sp_output_last_stop");
            return new OutputStopInfo(code, message);
        }
    }, ct);

    // ---- Displays --------------------------------------------------------------------------------

    public Task CreateDisplayAsync(Guid displayId, nint nativeWindow, int width, int height, CancellationToken ct = default) =>
        Run(e => Check(NativeMethods.DisplayCreate(e, Id(displayId), nativeWindow, (uint)Math.Max(1, width), (uint)Math.Max(1, height)), "sp_display_create"), ct);

    public Task DestroyDisplayAsync(Guid displayId, CancellationToken ct = default) => _dispatcher.InvokeAsync(() =>
    {
        // Idempotente: la ventana puede destruirse después de apagar el motor.
        if (IsInitialized)
            Check(NativeMethods.DisplayDestroy(_engine!, Id(displayId)), "sp_display_destroy");
    }, ct);

    public Task ResizeDisplayAsync(Guid displayId, int width, int height, CancellationToken ct = default) =>
        Run(e => Check(NativeMethods.DisplayResize(e, Id(displayId), (uint)Math.Max(1, width), (uint)Math.Max(1, height)), "sp_display_resize"), ct);

    public Task SetDisplaySourceAsync(Guid displayId, Guid? sceneId, CancellationToken ct = default) =>
        Run(e => Check(NativeMethods.DisplaySetSource(e, Id(displayId), sceneId is null ? SpDisplayMode.Program : SpDisplayMode.Scene,
            sceneId is { } s ? Id(s) : null), "sp_display_set_source"), ct);

    public Task SetDisplayViewAsync(Guid displayId, double zoom, double offsetX, double offsetY, CancellationToken ct = default) =>
        Run(e => Check(NativeMethods.DisplaySetView(e, Id(displayId), (float)zoom, (float)offsetX, (float)offsetY), "sp_display_set_view"), ct);

    public Task SetDisplayOverlayAsync(Guid displayId, IReadOnlyList<Guid> selectedItems, IReadOnlyList<double> guidesX,
        IReadOnlyList<double> guidesY, CancellationToken ct = default) => Run(e =>
    {
        using var pin = new Utf8Pin();
        var ids = selectedItems.Select(i => pin.Add(Id(i))).ToArray();
        var gx = guidesX.Select(v => (float)v).ToArray();
        var gy = guidesY.Select(v => (float)v).ToArray();
        unsafe
        {
            fixed (nint* pIds = ids)
            fixed (float* pX = gx)
            fixed (float* pY = gy)
                Check(NativeMethods.DisplaySetOverlay(e, Id(displayId), pIds, ids.Length, pX, gx.Length, pY, gy.Length), "sp_display_set_overlay");
        }
    }, ct);

    // ---- Utilidades ------------------------------------------------------------------------------

    /// <summary>
    /// libobs solo reconoce '/' como separador al resolver directorios (p. ej. los #include de los .effect),
    /// así que toda ruta que se le entregue debe usar barras normales.
    /// </summary>
    internal static string ObsPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private static string Id(Guid id) => id.ToString("D");

    private static SpEncoderConfig Encoder(Utf8Pin pin, EncoderSettings encoder) => new()
    {
        VideoEncoderId = pin.Add(encoder.VideoEncoderId),
        VideoBitrateKbps = (uint)Math.Max(0, encoder.VideoBitrateKbps),
        AudioBitrateKbps = (uint)Math.Max(0, encoder.AudioBitrateKbps),
        KeyframeIntervalSec = (uint)Math.Max(0, encoder.KeyframeIntervalSec),
    };

    private Task Run(Action<EngineSafeHandle> action, CancellationToken ct) =>
        _dispatcher.InvokeAsync(() => action(RequireEngine()), ct);

    private Task<T> Run<T>(Func<EngineSafeHandle, T> func, CancellationToken ct) =>
        _dispatcher.InvokeAsync(() => func(RequireEngine()), ct);

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
    private static unsafe void OnEncoder(byte* id, byte* name, byte* codec, nint userData)
    {
        var encoders = (List<VideoEncoderInfo>)GCHandle.FromIntPtr(userData).Target!;
        encoders.Add(new VideoEncoderInfo(
            Marshal.PtrToStringUTF8((nint)id) ?? string.Empty,
            Marshal.PtrToStringUTF8((nint)name) ?? string.Empty,
            Marshal.PtrToStringUTF8((nint)codec) ?? string.Empty));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnType(byte* id, byte* name, uint flags, nint userData)
    {
        var types = (List<EngineTypeInfo>)GCHandle.FromIntPtr(userData).Target!;
        types.Add(new EngineTypeInfo(
            Marshal.PtrToStringUTF8((nint)id) ?? string.Empty,
            Marshal.PtrToStringUTF8((nint)name) ?? string.Empty,
            (flags & 1) != 0,
            (flags & 2) != 0));
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
