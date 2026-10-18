using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StreamPilot.Native.Handles;

namespace StreamPilot.Native.Interop;

/// <summary>Espejo exacto de native/streampilot-bridge/include/streampilot_bridge.h. Mantener sincronizados.</summary>
internal static partial class NativeMethods
{
    public const string Library = "streampilot_bridge";
    public const uint ExpectedAbiVersion = 1;

    [LibraryImport(Library, EntryPoint = "sp_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "sp_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial int LastError(byte* buffer, int bufferSize);

    [LibraryImport(Library, EntryPoint = "sp_set_log_callback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial void SetLogCallback(delegate* unmanaged[Cdecl]<SpLogLevel, byte*, nint, void> callback, nint userData);

    [LibraryImport(Library, EntryPoint = "sp_engine_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult EngineCreate(in SpEngineConfig config, out EngineSafeHandle engine);

    [LibraryImport(Library, EntryPoint = "sp_engine_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void EngineDestroy(nint engine);

    [LibraryImport(Library, EntryPoint = "sp_devices_enumerate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial SpResult DevicesEnumerate(
        EngineSafeHandle engine, delegate* unmanaged[Cdecl]<SpDeviceKind, byte*, byte*, nint, void> callback, nint userData);

    [LibraryImport(Library, EntryPoint = "sp_scene_create", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SceneCreate(EngineSafeHandle engine, string sceneId, string name);

    [LibraryImport(Library, EntryPoint = "sp_scene_set_program", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SceneSetProgram(EngineSafeHandle engine, string sceneId);

    [LibraryImport(Library, EntryPoint = "sp_scene_add_source", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult SceneAddSource(
        EngineSafeHandle engine, string sceneId, string sourceId, string sourceType, string name, string settingsJson);

    [LibraryImport(Library, EntryPoint = "sp_recording_start")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult RecordingStart(EngineSafeHandle engine, in SpRecordingConfig config);

    [LibraryImport(Library, EntryPoint = "sp_recording_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult RecordingStop(EngineSafeHandle engine);

    [LibraryImport(Library, EntryPoint = "sp_metrics_get")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult MetricsGet(EngineSafeHandle engine, out SpMetrics metrics);

    [LibraryImport(Library, EntryPoint = "sp_preview_attach")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult PreviewAttach(EngineSafeHandle engine, nint nativeWindow, uint width, uint height);

    [LibraryImport(Library, EntryPoint = "sp_preview_resize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult PreviewResize(EngineSafeHandle engine, uint width, uint height);

    [LibraryImport(Library, EntryPoint = "sp_preview_detach")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SpResult PreviewDetach(EngineSafeHandle engine);

    public static unsafe string GetLastError()
    {
        var required = LastError(null, 0);
        if (required <= 1)
            return "Error nativo sin detalle.";
        var buffer = new byte[required];
        fixed (byte* p = buffer)
            LastError(p, buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, required - 1);
    }
}
